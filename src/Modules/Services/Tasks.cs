using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

public sealed record TaskDecision(string? Outcome, string? Comment);

/// <summary>One line of the task inbox. Request fields are set for tasks of form services.</summary>
public sealed record InboxItem(
    Guid TaskId,
    string Source,
    string Name,
    IReadOnlyDictionary<string, string>? Step,
    Guid? RequestId,
    string? Number,
    Guid? ServiceId,
    IReadOnlyDictionary<string, string>? ServiceNames,
    PersonRef? Requester,
    PersonRef? RequestedFor,
    PersonRef? OnBehalfOf,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DueAt);

public sealed record InboxCount(int Open);

/// <summary>
/// The task inbox of spec 016 US3 (FR-007): every task waiting for the person, from form services and from the
/// engine's other sources, with a count for the header badge; and approve, reject with a reason, or return. Acting
/// needs a current assignment (directly or as a delegate) and <c>workflow.approve</c> at the request's unit, held by the
/// person whose authority is used: the approver, or the person a delegate acts for. A manager holds that approval for
/// requests of their direct and indirect reports through the reporting line (spec 016 roles).
/// </summary>
public sealed class Tasks(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning grants,
    IEntitlementService entitlements,
    IUserDirectory users,
    IReportingLine reportingLine,
    IProcessEngine engine,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    Requests requests)
{
    public const int CommentMax = 2000;
    public const int InboxMax = 500;

    private const string ServicePrefix = "service.";

    /// <summary>The request a task of a form service belongs to: its business key starts with the request ID.</summary>
    internal static Guid? RequestIdOf(UserTaskInfo task) => RequestIdOf(task.DefinitionKey, task.BusinessKey);

    internal static Guid? RequestIdOf(string definitionKey, string businessKey) =>
        definitionKey.StartsWith(ServicePrefix, StringComparison.Ordinal) && businessKey.Length >= 32
            && Guid.TryParseExact(businessKey[..32], "N", out var id) ? id : null;

    private readonly Dictionary<(Guid, Guid?), bool> approves = [];

    private async Task<bool> MayApproveAsync(Guid authority, RequestRow? request, Guid? unit, CancellationToken ct)
    {
        if (request is not null && await reportingLine.IsManagerOfAsync(authority, request.SubjectId, ct))
        {
            return true;
        }
        if (!approves.TryGetValue((authority, unit), out var allowed))
        {
            var target = unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;
            allowed = authority == context.RequireUser()
                ? (await authz.CheckAsync(Permissions.WorkflowApprove, target, ct)).Allowed
                : await grants.UserHoldsAsync(authority, Permissions.WorkflowApprove, target, ct);
            approves[(authority, unit)] = allowed;
        }
        return allowed;
    }

    private readonly Dictionary<Guid, PersonRef> people = [];

    private async Task<PersonRef> PersonAsync(Guid id, CancellationToken ct)
    {
        if (!people.TryGetValue(id, out var person))
        {
            people[id] = person = new PersonRef(id, (await users.GetAsync(id, ct))?.DisplayName ?? string.Empty);
        }
        return person;
    }

    private async Task<Guid?> UnitOfAsync(UserTaskInfo task, RequestRow? request, CancellationToken ct) =>
        request is not null ? request.OrgUnitId : (await engine.GetInstanceAsync(task.InstanceId, ct))?.OrgUnitId;

    private async Task<IReadOnlyList<(UserTaskInfo Task, RequestRow? Request)>> ActionableAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var list = new List<(UserTaskInfo, RequestRow?)>();
        foreach (var task in await engine.MyOpenTasksAsync(InboxMax, ct))
        {
            var request = RequestIdOf(task) is { } requestId ? await requests.RowAsync(requestId, false, ct) : null;
            if (await MayApproveAsync(task.OnBehalfOf ?? user, request, await UnitOfAsync(task, request, ct), ct))
            {
                list.Add((task, request));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<InboxItem>> InboxAsync(Guid? serviceId, CancellationToken ct)
    {
        var items = new List<InboxItem>();
        foreach (var (task, request) in await ActionableAsync(ct))
        {
            if (serviceId is { } only && request?.ServiceId != only)
            {
                continue;
            }
            IReadOnlyDictionary<string, string>? step = null, names = null;
            if (request is not null)
            {
                names = Catalogue.Json<Dictionary<string, string>>(await session.ScalarAsync<string>("select names::text from services.service where id = @id",
                    new { id = request.ServiceId }, ct) ?? "{}");
                var version = await Versions.GetAsync(session, request.ServiceId, request.ServiceVersion, ct);
                step = version?.Route.Approval?.Names is { Count: > 0 } stepNames ? stepNames : names;
            }
            items.Add(new InboxItem(task.Id, request is null ? task.DefinitionKey : "service_request", task.Name, step, request?.Id, request?.Number,
                request?.ServiceId, names, request is null ? null : await PersonAsync(request.RequesterId, ct),
                request?.OnBehalfOfId is { } subject ? await PersonAsync(subject, ct) : null,
                task.OnBehalfOf is { } delegator ? await PersonAsync(delegator, ct) : null, task.CreatedAt, task.DueAt));
        }
        return items;
    }

    public async Task<InboxCount> CountAsync(CancellationToken ct) => new((await ActionableAsync(ct)).Count);

    /// <summary>Approves, rejects (a reason is required) or returns a task, and moves its request on.</summary>
    public async Task<UserTaskInfo> DecideAsync(Guid taskId, TaskDecision decision, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var outcome = decision.Outcome ?? string.Empty;
        var comment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim();
        var errors = new ValidationErrors();
        if (!TaskOutcomes.All.Contains(outcome))
        {
            errors.Add("outcome", "validation.workflow.outcome");
        }
        if (outcome == TaskOutcomes.Reject && comment is null)
        {
            errors.Add("comment", "validation.services.reason_required");
        }
        if (comment is { Length: > CommentMax })
        {
            errors.Add("comment", "validation.workflow.comment");
        }
        errors.ThrowIfAny();

        // Not a candidate: 403 for a task of this tenant (crafted call, US3 scenario 3), 404 for anything else.
        var task = await engine.MyTaskAsync(taskId, ct) ?? throw (await engine.GetTaskAsync(taskId, ct) is null ? AppException.NotFound() : AppException.Denied());
        var request = RequestIdOf(task) is { } requestId ? await requests.RowAsync(requestId, true, ct) : null;
        if (task.Status != "open")
        {
            throw AppException.Conflict("conflict.workflow.task_closed");
        }
        var onBehalfOf = task.OnBehalfOf;
        if (!await MayApproveAsync(onBehalfOf ?? user, request, await UnitOfAsync(task, request, ct), ct))
        {
            throw AppException.Denied();
        }
        if (request is not null && request.Status != RequestStatuses.InApproval)
        {
            throw AppException.Conflict("conflict.services.request_closed");
        }

        var done = await engine.CompleteTaskAsync(taskId, outcome, comment, ct);
        var action = outcome switch
        {
            TaskOutcomes.Approve => AuditActions.TaskApproved,
            TaskOutcomes.Reject => AuditActions.TaskRejected,
            _ => AuditActions.TaskReturned,
        };
        await audit.WriteAsync(new AuditEntry(action, "user_task", taskId.ToString(), TargetOrgUnitId: request?.OrgUnitId,
            Changes: new { request = request?.Id, request?.Number, element = task.ElementId, onBehalfOf, comment = comment is not null }), ct);
        if (request is not null)
        {
            await MoveRequestAsync(request, task, done, ct);
        }
        return done;
    }

    private async Task MoveRequestAsync(RequestRow request, UserTaskInfo task, UserTaskInfo done, CancellationToken ct)
    {
        var version = await Versions.GetAsync(session, request.ServiceId, request.ServiceVersion, ct);
        var step = version?.Route.Approval?.Names is { Count: > 0 } names ? ServiceAdmin.Preferred(names) ?? task.Name : task.Name;
        var eventName = done.Outcome switch
        {
            TaskOutcomes.Approve => "approved",
            TaskOutcomes.Reject => "rejected",
            _ => "returned",
        };
        await requests.EventAsync(request.Id, eventName, step, done.CompletedBy, done.OnBehalfOf, done.Comment, ct);

        var instance = await engine.GetInstanceAsync(task.InstanceId, ct);
        if (instance is not { Status: "completed" })
        {
            return; // A later step is open (R2 multi-step routes).
        }
        var status = instance.Outcome switch
        {
            InstanceOutcomes.Approved => RequestStatuses.Approved,
            InstanceOutcomes.Rejected => RequestStatuses.Rejected,
            _ => RequestStatuses.Returned,
        };
        var now = clock.UtcNow;
        await session.ExecAsync("update services.request set status = @status, closed_at = @closed where id = @id",
            new { id = request.Id, status, closed = status == RequestStatuses.Returned ? (DateTimeOffset?)null : now }, ct);
        if (status != RequestStatuses.Returned)
        {
            await outbox.EnqueueAsync(new RequestCompleted(request.Id, request.ServiceId, status), ct);
        }
        else
        {
            await outbox.EnqueueAsync(new RequestReturned(request.Id, request.ServiceId), ct);
        }
    }
}
