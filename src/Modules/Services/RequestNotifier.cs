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
public sealed class RequestNotifier(IDbSession session, Requests requests, IReportingLine reportingLine, INotificationSender notifications)
    : IMessageHandler<RequestCompleted>, IMessageHandler<RequestReturned>, IMessageHandler<UserTaskCreated>, IMessageHandler<UserTaskCompleted>
{
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
}
