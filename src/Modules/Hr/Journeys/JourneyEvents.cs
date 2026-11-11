using System.Globalization;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

/// <summary>
/// Starts journeys from events (spec 022 FR-014), once per template, person and event, only where the plan has
/// <c>hr.journeys</c>. The template that applies is the nearest one up the person's primary unit. A new account starts
/// onboarding only when its anchor is at most <see cref="MaxPastDays"/> before today, so a directory's first sync of
/// existing staff starts nothing. <c>UserDeprovisioned</c> starts nothing: the account is disabled at the end of
/// offboarding (US5 scenario 3), not before it.
/// </summary>
public sealed class JourneyStarter(IEntitlementService entitlements, LeaveLedger people, JourneyTemplates templates, JourneyService journeys, IRequestFacts requests)
    : IMessageHandler<EmployeeHired>, IMessageHandler<EmployeeRehired>, IMessageHandler<UserProvisioned>, IMessageHandler<EmployeeTerminated>,
        IMessageHandler<RequestCompleted>
{
    public const int MaxPastDays = 30;

    private async Task StartAsync(string startOn, Guid? serviceId, Guid userId, DateOnly anchor, string startedBy, string sourceKey, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct) || await people.PersonAsync(userId, ct) is not { Active: true } person
            || await templates.ApplicableAsync(startOn, serviceId, person.Path, ct) is not { } template)
        {
            return;
        }
        await journeys.StartOrMoveAsync(template, userId, anchor, startedBy, sourceKey, null, refuseActive: false, ct);
    }

    public Task HandleAsync(EmployeeHired message, CancellationToken ct) => message.UserId is { } user
        ? StartAsync(JourneyStarts.Hire, null, user, message.StartDate, "hr_hire", $"hr_hire:{message.ChangeId:N}", ct)
        : Task.CompletedTask; // No account yet: the new account starts it with the HR start date.

    public Task HandleAsync(EmployeeRehired message, CancellationToken ct) => message.UserId is { } user
        ? StartAsync(JourneyStarts.Hire, null, user, message.StartDate, "hr_rehire", $"hr_rehire:{message.ChangeId:N}", ct)
        : Task.CompletedTask;

    public Task HandleAsync(EmployeeTerminated message, CancellationToken ct) => message.UserId is { } user
        ? StartAsync(JourneyStarts.Termination, null, user, message.LastWorkingDay, "hr_termination", $"hr_termination:{message.ChangeId:N}", ct)
        : Task.CompletedTask;

    /// <summary>A new account: onboarding from templates started by accounts, and from those started by hires when HR knows the hire date.</summary>
    public async Task HandleAsync(UserProvisioned message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct) || await people.PersonAsync(message.UserId, ct) is not { Active: true } person)
        {
            return;
        }
        var today = await people.TodayAsync(ct);
        var anchor = person.HireDate ?? today;
        if (anchor.DayNumber < today.DayNumber - MaxPastDays)
        {
            return;
        }
        await StartAsync(JourneyStarts.Account, null, message.UserId, anchor, "account", $"account:{message.UserId:N}", ct);
        if (person.HireDate is not null)
        {
            await StartAsync(JourneyStarts.Hire, null, message.UserId, anchor, "account", $"account:{message.UserId:N}", ct);
        }
    }

    /// <summary>An approved request of a service a template names (a resignation) starts offboarding on the date in the template's form field.</summary>
    public async Task HandleAsync(RequestCompleted message, CancellationToken ct)
    {
        if (message.Status is not ("approved" or "completed") || !await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct))
        {
            return;
        }
        var named = (await templates.RowsAsync(ct)).Where(t => t.Active && t.StartOn == JourneyStarts.Service && t.ServiceId == message.ServiceId).ToList();
        if (named.Count == 0 || await requests.GetAsync(message.RequestId, ct) is not { } request
            || await people.PersonAsync(request.SubjectId, ct) is not { Active: true } person
            || await templates.ApplicableAsync(JourneyStarts.Service, message.ServiceId, person.Path, ct) is not { } template
            || template.AnchorField is not { } field || !request.Data.TryGetValue(field, out var value) || DateOf(value) is not { } anchor)
        {
            return;
        }
        await journeys.StartOrMoveAsync(template, request.SubjectId, anchor, "service", $"request:{message.RequestId:N}", null, refuseActive: false, ct);
    }

    private static DateOnly? DateOf(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// Journey steps in the spec 016 task inbox: each task links to the journey. The assignee hears that a step waits for
/// them; a queue (permission) step is found in the inbox. An overdue step publishes <see cref="JourneyStepOverdue"/>.
/// </summary>
public sealed class JourneyTaskSource(ITenantContext context, JourneyService journeys, IReportingLine reporting, IUserDirectory users, INotificationSender notifications,
    IOutbox outbox, IEntitlementService entitlements) : ITaskSource, IMessageHandler<UserTaskCreated>, IMessageHandler<UserTaskDue>, IMessageHandler<PolicyAttested>
{
    public const string TaskWaiting = "hr.journey_task_waiting";

    public bool Handles(string definitionKey) => definitionKey.StartsWith(JourneyTemplates.ProcessPrefix, StringComparison.Ordinal);

    private async Task<(JourneyRow Journey, JourneyStepRow Step)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (!Handles(task.DefinitionKey) || await journeys.StepByInstanceAsync(task.InstanceId, forUpdate, ct) is not { Status: JourneyStepStatuses.Open } step
            || await journeys.RowAsync(step.JourneyId, false, ct) is not { Status: JourneyStatuses.Active } journey)
        {
            return null;
        }
        return (journey, step);
    }

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        // An attestation step shows in the inbox as the policies to attest to (spec 025), not as a task of its own.
        if (await WaitingAsync(task, false, ct) is not var (journey, step) || step.Type == JourneyStepTypes.Attestation)
        {
            return null;
        }
        var person = await users.GetAsync(journey.UserId, ct);
        var title = step.Names.ToDictionary(n => n.Key, n => person is null ? n.Value : $"{n.Value}: {person.DisplayName}");
        return new TaskSubject("journey", title, JourneyService.Link(journey.Id) + "?task=" + task.Id, journey.OrgUnitId, journey.UserId);
    }

    /// <summary>The person decides only their own steps; an attestation step is done only by attesting.</summary>
    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is var (journey, step) && (step.Assignee == JourneyAssignees.Person || authority != journey.UserId)
        && step.Type != JourneyStepTypes.Attestation;

    /// <summary>
    /// A checklist step is done when every line is confirmed, and an exit interview step is approved by recording the
    /// interview (it may still be closed as not needed); a decision before that is refused and rolled back.
    /// </summary>
    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is var (journey, step))
        {
            if (step.Type == JourneyStepTypes.AccessRemoval && await journeys.PendingAsync(journey.Id, step.Key, ct))
            {
                throw AppException.Conflict("conflict.journey.checklist_pending");
            }
            if (step.Type == JourneyStepTypes.ExitInterview && done.Outcome == TaskOutcomes.Approve && !await journeys.ExitRecordedAsync(journey.Id, ct))
            {
                throw AppException.Conflict("conflict.journey.exit_pending");
            }
            await journeys.StepDecidedAsync(step, done with { CompletedBy = done.CompletedBy ?? context.ActorId }, ct);
        }
    }

    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!Handles(message.DefinitionKey) || JourneyService.ParseBusinessKey(message.BusinessKey) is not var (journeyId, key)
            || await journeys.RowAsync(journeyId, false, ct) is not { Status: JourneyStatuses.Active } journey
            || (await journeys.StepsAsync(journeyId, ct)).FirstOrDefault(s => s.Key == key) is not { } step || step.Type == JourneyStepTypes.Attestation)
        {
            // Policies tells the person about their attestations itself.
            return;
        }
        IReadOnlyCollection<Guid> recipients = step.Assignee switch
        {
            JourneyAssignees.Person => [journey.UserId],
            JourneyAssignees.Buddy => journey.BuddyUserId is { } b ? [b] : [],
            JourneyAssignees.Manager => await reporting.ManagerOfAsync(journey.UserId, ct) is { } m && m != journey.UserId ? [m] : [],
            JourneyAssignees.Users => await journeys.StepUsersAsync(journeyId, key, journey.UserId, ct),
            _ => [],
        };
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.RequestUpdate, TaskWaiting,
            JourneyService.Link(journey.Id) + "?task=" + message.TaskId, new Dictionary<string, string> { ["step"] = JourneyTemplates.StepName(step.Names) },
            DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }

    /// <summary>The person attested: their attestation steps with nothing left are done (US4 scenario 4).</summary>
    public async Task HandleAsync(PolicyAttested message, CancellationToken ct)
    {
        if (await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct))
        {
            await journeys.SettleAttestationsAsync(message.UserId, ct);
        }
    }

    public async Task HandleAsync(UserTaskDue message, CancellationToken ct)
    {
        if (Handles(message.DefinitionKey) && JourneyService.ParseBusinessKey(message.BusinessKey) is var (journeyId, key)
            && await journeys.RowAsync(journeyId, false, ct) is { Status: JourneyStatuses.Active } journey)
        {
            await outbox.EnqueueAsync(new JourneyStepOverdue(journeyId, journey.UserId, key), ct);
        }
    }
}
