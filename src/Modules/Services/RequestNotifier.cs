using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Tells people about their requests through spec 015 (spec 016 US3 scenario 2): the person who filed a request and
/// the person it is for learn that it was approved, rejected or returned; a named approver or line manager learns that
/// a request waits for them. Notifications carry the request number only; the details stay behind the request's read
/// check. Steps for holders of a permission at a unit reach their approvers through the inbox badge.
/// </summary>
public sealed class RequestNotifier(IDbSession session, Requests requests, IReportingLine reportingLine, INotificationSender notifications,
    TeamBeats.Authorization.Contracts.IAuthorizationProvisioning grants, IProcessEngine engine)
    : IMessageHandler<RequestCompleted>, IMessageHandler<RequestReturned>, IMessageHandler<UserTaskCreated>, IMessageHandler<UserTaskCompleted>,
      IMessageHandler<UserTaskDue>, IMessageHandler<UserTaskEscalated>, IMessageHandler<ProcessActionRequested>
{
    public const string Reminder = "services.task.reminder";
    public const string Escalated = "services.task.escalated";
    public const string Update = "services.request.update";
    public const string AtRisk = "services.request.at_risk";
    public const string Breached = "services.request.breached";
    public const string FulfilmentFailed = "services.request.fulfilment_failed";

    public const string Approved = "services.request.approved";
    public const string Rejected = "services.request.rejected";
    public const string Returned = "services.request.returned";
    public const string Waiting = "services.task.waiting";

    public static string Link(Guid requestId) => $"/requests/{requestId}";

    public async Task HandleAsync(RequestCompleted message, CancellationToken ct)
    {
        // However it ended (approved, rejected or cancelled by the filer), nobody is still asked to approve it (spec 015 FR-025).
        await ResolveWaitingAsync(message.RequestId, ct);
        await (message.Status switch
        {
            RequestStatuses.Approved or RequestStatuses.Completed => TellRequesterAsync(message.RequestId, Approved, "completed", ct),
            RequestStatuses.Rejected => TellRequesterAsync(message.RequestId, Rejected, "completed", ct),
            _ => Task.CompletedTask, // The filer cancelled it themselves.
        });
    }

    public async Task HandleAsync(RequestReturned message, CancellationToken ct)
    {
        await ResolveWaitingAsync(message.RequestId, ct);
        await TellRequesterAsync(message.RequestId, Returned, "returned", ct);
    }

    /// <summary>A task of a request closed, whoever completed it: its "waiting for you" notification is resolved for every approver (spec 015 FR-025).</summary>
    public Task HandleAsync(UserTaskCompleted message, CancellationToken ct) =>
        Tasks.RequestIdOf(message.DefinitionKey, message.BusinessKey) is { } requestId
            ? notifications.ResolveAsync(NotificationCategories.ApprovalWaiting, TaskLink(requestId, message.TaskId), ct)
            : Task.CompletedTask;

    private Task ResolveWaitingAsync(Guid requestId, CancellationToken ct) =>
        notifications.ResolveAsync(NotificationCategories.ApprovalWaiting, Link(requestId) + "?task=", ct);

    public static string TaskLink(Guid requestId, Guid taskId) => $"{Link(requestId)}?task={taskId}";

    private async Task TellRequesterAsync(Guid requestId, string template, string stage, CancellationToken ct)
    {
        if (await requests.RowAsync(requestId, false, ct) is not { } row)
        {
            return;
        }
        Guid[] recipients = row.OnBehalfOfId is { } other ? [row.RequesterId, other] : [row.RequesterId];
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.RequestUpdate, template, Link(row.Id),
            new Dictionary<string, string> { ["number"] = row.Number },
            DedupeKey: $"request:{row.Id:N}:{stage}:{await RoundAsync(row.Id, ct)}"), ct);
    }

    /// <summary>How many times the request was submitted, so each round of a returned request is told again.</summary>
    private Task<long> RoundAsync(Guid requestId, CancellationToken ct) =>
        session.ScalarAsync<long>("select count(*) from services.request_event where request_id = @requestId and event in ('submitted', 'resubmitted')", new { requestId }, ct);

    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (Tasks.RequestIdOf(message.DefinitionKey, message.BusinessKey) is not { } requestId || await requests.RowAsync(requestId, false, ct) is not { } row)
        {
            return;
        }
        if ((await Versions.GetAsync(session, row.ServiceId, row.ServiceVersion, ct))?.Route.Graph is { } graph)
        {
            await TellCandidatesAsync(row, graph, message.ElementId, message.TaskId, Waiting, "task:" + message.TaskId.ToString("N"), ct);
            return;
        }
        // The route runs on the latest published version (resubmits included), so its rule names the approvers.
        var route = await session.ScalarAsync<string?>(
            "select route::text from services.service_version where service_id = @id and state = 'published' order by version desc limit 1", new { id = row.ServiceId }, ct);
        var rule = route is null ? null : Catalogue.Json<ServiceRoute>(route).Approval?.Approvers;
        IEnumerable<Guid> recipients = rule switch
        {
            { Kind: ApproverKinds.Users, Users: { } named } => named,
            { Kind: ApproverKinds.LineManager } => await reportingLine.ManagerOfAsync(row.SubjectId, ct) is { } manager ? [manager] : [],
            _ => [],
        };
        var list = recipients.Where(p => p != row.RequesterId && p != row.OnBehalfOfId).Distinct().ToArray();
        if (list.Length == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(list, NotificationCategories.ApprovalWaiting, Waiting, TaskLink(row.Id, message.TaskId),
            new Dictionary<string, string> { ["number"] = row.Number },
            DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }

    /// <summary>A step of a step graph route is overdue: the people it waits for are reminded once (US5 scenario 2).</summary>
    public async Task HandleAsync(UserTaskDue message, CancellationToken ct)
    {
        if (Tasks.RequestIdOf(message.DefinitionKey, message.BusinessKey) is { } id && await requests.RowAsync(id, false, ct) is { } request
            && (await Versions.GetAsync(session, request.ServiceId, request.ServiceVersion, ct))?.Route.Graph is { } route)
        {
            await TellCandidatesAsync(request, route, null, message.TaskId, Reminder, $"task:{message.TaskId:N}:due", ct);
        }
    }

    /// <summary>An overdue step also went to its escalation target: the people it now reaches are told (US5 scenario 2).</summary>
    public async Task HandleAsync(UserTaskEscalated message, CancellationToken ct)
    {
        if (Tasks.RequestIdOf(message.DefinitionKey, message.BusinessKey) is not { } requestId || await requests.RowAsync(requestId, false, ct) is not { } row)
        {
            return;
        }
        var list = message.AddedUsers.Where(p => p != row.RequesterId && p != row.OnBehalfOfId).Distinct().ToArray();
        if (list.Length > 0)
        {
            await notifications.SendAsync(new NotificationRequest(list, NotificationCategories.ApprovalWaiting, Escalated, TaskLink(row.Id, message.TaskId),
                new Dictionary<string, string> { ["number"] = row.Number }, DedupeKey: $"task:{message.TaskId:N}:escalated"), ct);
        }
    }

    /// <summary>A notify step of a step graph route (US8 scenario 1): the requester, the person it is for, named users and group members hear about the request.</summary>
    public async Task HandleAsync(ProcessActionRequested message, CancellationToken ct)
    {
        if (message.Action != "notify" || Tasks.RequestIdOf(message.DefinitionKey, message.BusinessKey) is not { } requestId
            || await requests.RowAsync(requestId, false, ct) is not { } row)
        {
            return;
        }
        var recipients = new List<Guid>();
        if (message.Inputs["requester"]?.GetValue<bool>() == true)
        {
            recipients.Add(row.RequesterId);
        }
        if (message.Inputs["subject"]?.GetValue<bool>() == true)
        {
            recipients.Add(row.SubjectId);
        }
        recipients.AddRange(Ids(message.Inputs["users"]?.GetValue<string>()));
        if (Ids(message.Inputs["groups"]?.GetValue<string>()) is { Count: > 0 } groups)
        {
            recipients.AddRange(await grants.GroupMembersAsync(groups, ct));
        }
        if (recipients.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(recipients.Distinct().ToArray(), NotificationCategories.RequestUpdate, Update, Link(row.Id),
                new Dictionary<string, string> { ["number"] = row.Number }, DedupeKey: $"action:{message.ActionId:N}"), ct);
        }
    }

    private static List<Guid> Ids(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();

    /// <summary>
    /// The people a person step of a step graph route waits for: named users, the line manager or the manager at a
    /// level, and members of its groups. Holders of a permission at a unit learn it from the inbox badge, as in R1.
    /// The requester and the person the request is for are never told to approve it.
    /// </summary>
    private async Task TellCandidatesAsync(RequestRow row, RouteGraph graph, string? elementId, Guid taskId, string template, string dedupe, CancellationToken ct)
    {
        elementId ??= (await engine.GetTaskAsync(taskId, ct))?.ElementId;
        if (elementId is null)
        {
            return;
        }
        var list = await CandidatesAsync(row, graph, elementId, ct);
        if (list.Length > 0)
        {
            await notifications.SendAsync(new NotificationRequest(list, NotificationCategories.ApprovalWaiting, template, TaskLink(row.Id, taskId),
                new Dictionary<string, string> { ["number"] = row.Number }, DedupeKey: dedupe), ct);
        }
    }

    /// <summary>The people a person step names, never the requester or the person the request is for.</summary>
    private async Task<Guid[]> CandidatesAsync(RequestRow row, RouteGraph graph, string elementId, CancellationToken ct)
    {
        if (graph.Step(RouteGraph.StepIdOf(elementId)) is not { Assignee: { } assignee })
        {
            return [];
        }
        var recipients = new List<Guid>(assignee.Users ?? []);
        if (assignee.Kind == AssigneeKinds.LineManager && await reportingLine.ManagerOfAsync(row.SubjectId, ct) is { } manager)
        {
            recipients.Add(manager);
        }
        if (assignee.Kind == AssigneeKinds.ManagerLevel && assignee.Level is { } level && await reportingLine.ChainAsync(row.SubjectId, level, ct) is { } chain
            && chain.Count == level)
        {
            recipients.Add(chain[^1]);
        }
        if (assignee.Groups is { Count: > 0 } groups)
        {
            recipients.AddRange(await grants.GroupMembersAsync(groups, ct));
        }
        return recipients.Where(p => p != row.RequesterId && p != row.OnBehalfOfId).Distinct().ToArray();
    }

    /// <summary>
    /// A request went at risk or breached its service level target (US5 scenario 3): the people its open steps wait for
    /// (the queue) and the service's owners (tenant-wide holders of <c>service.manage</c> who hold it at the owning unit)
    /// are told once.
    /// </summary>
    internal async Task AlertServiceLevelAsync(RequestRow row, string template, CancellationToken ct)
    {
        var recipients = new List<Guid>();
        var route = (await Versions.GetAsync(session, row.ServiceId, row.ServiceVersion, ct))?.Route;
        if (row.InstanceId is { } instance)
        {
            foreach (var task in (await engine.InstanceTasksAsync(instance, ct)).Where(t => t.Status == "open"))
            {
                if (route?.Graph is { } graph)
                {
                    recipients.AddRange(await CandidatesAsync(row, graph, task.ElementId, ct));
                }
                else if (route?.Approval?.Approvers is { } rule)
                {
                    recipients.AddRange(rule.Users ?? []);
                    if (rule.Kind == ApproverKinds.LineManager && await reportingLine.ManagerOfAsync(row.SubjectId, ct) is { } manager)
                    {
                        recipients.Add(manager);
                    }
                }
            }
        }
        recipients.AddRange(await OwnersAsync(row.ServiceId, ct));
        await AlertAsync(row, recipients, template, $"request:{row.Id:N}:{template}", ct);
    }

    /// <summary>
    /// An automatic step of a request failed and waits for retry, skip or cancel (US6 scenario 3): the service's
    /// fulfilment queue and its owners are told, once per failure.
    /// </summary>
    internal async Task AlertFulfilmentFailedAsync(RequestRow row, Guid incidentId, CancellationToken ct)
    {
        var recipients = new List<Guid>();
        if ((await Versions.GetAsync(session, row.ServiceId, row.ServiceVersion, ct))?.FulfilmentQueueGroupId is { } queue)
        {
            recipients.AddRange(await grants.GroupMembersAsync([queue], ct));
        }
        recipients.AddRange(await OwnersAsync(row.ServiceId, ct));
        await AlertAsync(row, recipients, FulfilmentFailed, $"request:{row.Id:N}:incident:{incidentId:N}", ct);
    }

    /// <summary>The service's owners: tenant-wide holders of <c>service.manage</c> who hold it at the owning unit.</summary>
    private async Task<List<Guid>> OwnersAsync(Guid serviceId, CancellationToken ct)
    {
        var owners = new List<Guid>();
        var owner = await session.ScalarAsync<Guid?>("select owner_org_unit_id from services.service where id = @id", new { id = serviceId }, ct);
        var target = owner is { } unit ? TeamBeats.Authorization.Contracts.AuthzTarget.OrgUnit(unit) : TeamBeats.Authorization.Contracts.AuthzTarget.Tenant;
        foreach (var holder in await grants.TenantHoldersAsync(TeamBeats.Authorization.Contracts.Permissions.ServiceManage, ct))
        {
            if (await grants.UserHoldsAsync(holder, TeamBeats.Authorization.Contracts.Permissions.ServiceManage, target, ct))
            {
                owners.Add(holder);
            }
        }
        return owners;
    }

    private async Task AlertAsync(RequestRow row, List<Guid> recipients, string template, string dedupe, CancellationToken ct)
    {
        var list = recipients.Where(p => p != row.RequesterId && p != row.OnBehalfOfId).Distinct().ToArray();
        if (list.Length > 0)
        {
            await notifications.SendAsync(new NotificationRequest(list, NotificationCategories.ApprovalEscalation, template, Link(row.Id),
                new Dictionary<string, string> { ["number"] = row.Number }, DedupeKey: dedupe), ct);
        }
    }
}
