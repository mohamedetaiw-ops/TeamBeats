using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>
/// Leave approvals in the spec 016 task inbox (FR-026): the line manager's task links to the request with its overlap
/// and the team calendar; nobody decides on their own leave, directly or as a delegate (section 6). The replacement's
/// accept task is a personal task of the named person.
/// </summary>
public sealed class LeaveTaskSource(ITenantContext context, IProcessEngine engine, LeaveRequests requests, LeaveConfig config, IReportingLine reporting,
    INotificationSender notifications) : ITaskSource, IPersonalTaskSource, IMessageHandler<UserTaskCreated>
{
    public bool Handles(string definitionKey) => definitionKey == LeaveRequests.ProcessKey;

    private async Task<(LeaveRow Row, bool Change)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (LeaveRequests.Parse(task.BusinessKey) is not var (id, change) || await requests.RowAsync(id, forUpdate, ct) is not { } row)
        {
            return null;
        }
        var waits = change ? row.ChangeInstanceId == task.InstanceId && row.ChangeKind is not null
            : row.InstanceId == task.InstanceId && row.Status == LeaveStatuses.Pending;
        return waits ? (row, change) : null;
    }

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not var (row, change))
        {
            return null;
        }
        var type = await config.TypeAsync(row.TypeId, ct);
        var title = (type?.Names ?? []).ToDictionary(n => n.Key, n => $"{row.Number} {n.Value}");
        return new TaskSubject(change ? "leave_change" : "leave", title, LeaveRequests.Link(row.Id) + "?task=" + task.Id, row.OrgUnitId, row.UserId);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is var (row, _) && authority != row.UserId && authority != row.FiledBy;

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not var (row, change))
        {
            return;
        }
        var instance = await engine.GetInstanceAsync(task.InstanceId, ct);
        if (instance is not { Status: "completed" })
        {
            return;
        }
        await requests.DecidedAsync(row, change, instance, done.CompletedBy ?? context.RequireUser(), ct);
    }

    public Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default) => requests.ReplacementTasksAsync(userId, ct);

    /// <summary>The line manager hears that a request waits for them; HR officers find fallback tasks in their inbox.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!Handles(message.DefinitionKey) || LeaveRequests.Parse(message.BusinessKey) is not var (id, _) || await requests.RowAsync(id, false, ct) is not { } row
            || await reporting.ManagerOfAsync(row.UserId, ct) is not { } manager || manager == row.FiledBy || manager == row.UserId)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest([manager], NotificationCategories.ApprovalWaiting, LeaveRequests.ApprovalWaiting,
            LeaveRequests.Link(row.Id) + "?task=" + message.TaskId, new Dictionary<string, string> { ["number"] = row.Number },
            DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }
}

/// <summary>The Leave service in the one catalogue (FR-026), shown only where the plan has <c>hr.leave</c>.</summary>
public sealed class LeaveModuleServices : IModuleServiceSource
{
    public const string Key = "hr.leave";

    public IReadOnlyList<ModuleServiceRegistration> Services { get; } =
    [
        new(Key, new Dictionary<string, string> { ["en"] = "Leave", ["ar"] = "الإجازات" },
            new Dictionary<string, string> { ["en"] = "See your balance and request time off", ["ar"] = "اطلع على رصيدك واطلب إجازة" },
            "/leave", LeaveRequests.ProcessKey, Capabilities.HrLeave),
    ];
}
