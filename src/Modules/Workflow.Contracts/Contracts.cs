using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow.Contracts;

/// <summary>Where a route comes from (spec 024 plan): the module that owns its simple route settings.</summary>
public static class RouteSources
{
    public const string ServiceRoute = "service_route";
    public const string ContentRoute = "content_route";
    public const string PolicyRoute = "policy_route";
    public const string DocumentRoute = "document_route";
}

/// <summary>Outcomes a user task can end with. Approve continues to the next step; reject and return end the instance.</summary>
public static class TaskOutcomes
{
    public const string Approve = "approve";
    public const string Reject = "reject";
    public const string Return = "return";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Approve, Reject, Return };
}

/// <summary>How an instance ended: approved after the last step, or the outcome of the step that stopped it.</summary>
public static class InstanceOutcomes
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Returned = "returned";
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// Who may complete a step: named users, or anyone who holds <see cref="Permission"/> at the instance's org unit through
/// central authorization (so a sibling unit's holder is not a candidate). Role names are never used.
/// </summary>
public sealed record CandidateRule(IReadOnlyList<Guid>? Users = null, string? Permission = null);

/// <summary>One approval step. <see cref="DueWorkingDays"/> sets a due time from the tenant calendar of the instance's unit.</summary>
public sealed record RouteStep(string Id, string Name, CandidateRule Candidates, int? DueWorkingDays = null);

/// <summary>
/// The simple route settings that specs 009, 011, 016 and 025 keep, compiled to BPMN 2.0 by the engine. Steps run in
/// order; the starter of an instance is never a candidate when <see cref="ExcludeStarter"/> is set (no self-approval).
/// </summary>
public sealed record RouteSpec(string Name, IReadOnlyList<RouteStep> Steps, bool ExcludeStarter = true);

public sealed record ProcessDefinitionInfo(string Key, int Version, string Name, string Source, DateTimeOffset PublishedAt);

public sealed record ProcessInstanceInfo(
    Guid Id,
    string DefinitionKey,
    int DefinitionVersion,
    string BusinessKey,
    Guid? OrgUnitId,
    Guid? StartedBy,
    string Status,
    string? Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

public sealed record UserTaskInfo(
    Guid Id,
    Guid InstanceId,
    string DefinitionKey,
    string BusinessKey,
    string ElementId,
    string Name,
    int StepIndex,
    string Status,
    string? Outcome,
    string? Comment,
    Guid? CompletedBy,
    Guid? OnBehalfOf,
    DateTimeOffset? DueAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record HistoryEntry(string ElementId, string Event, Guid? ActorId, Guid? OnBehalfOf, DateTimeOffset At);

/// <summary>
/// The engine API of spec 024 (FR-016, R1 part). Calling modules authorize their own action first (for example the
/// permission to act on a request); the engine then checks that the caller is a current candidate of the task. Every
/// call is in the current tenant; IDs from another tenant are not found.
/// </summary>
public interface IProcessEngine
{
    /// <summary>Compiles the route to BPMN 2.0 and the executable graph and stores it as the next immutable version.</summary>
    Task<ProcessDefinitionInfo> PublishRouteAsync(string key, string source, RouteSpec route, CancellationToken ct = default);

    Task<ProcessDefinitionInfo?> GetDefinitionAsync(string key, int? version = null, CancellationToken ct = default);

    /// <summary>The BPMN 2.0 XML of a published version, for export and the R2 modeler.</summary>
    Task<string?> GetBpmnAsync(string key, int version, CancellationToken ct = default);

    /// <summary>
    /// Starts an instance of the latest version. The business key is unique per process key, so starting twice for the
    /// same business object returns the existing instance instead of a second one. When the route excludes the
    /// starter, <paramref name="alsoExcluded"/> names more people who may never be candidates (for example the person a
    /// request was filed for).
    /// </summary>
    Task<ProcessInstanceInfo> StartAsync(string key, string businessKey, Guid? orgUnitId, Guid? startedBy, IReadOnlyCollection<Guid>? alsoExcluded = null,
        CancellationToken ct = default);

    /// <summary>Completes an open task as the current user, who must be a candidate. Refused with 409 once completed.</summary>
    Task<UserTaskInfo> CompleteTaskAsync(Guid taskId, string outcome, string? comment, CancellationToken ct = default);

    /// <summary>Ends an active instance and cancels its open task (the requester withdrew).</summary>
    Task<ProcessInstanceInfo> WithdrawAsync(Guid instanceId, CancellationToken ct = default);

    Task<ProcessInstanceInfo?> GetInstanceAsync(Guid instanceId, CancellationToken ct = default);

    Task<ProcessInstanceInfo?> FindInstanceAsync(string key, string businessKey, CancellationToken ct = default);

    Task<UserTaskInfo?> GetTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// The task when the current user may complete it, directly or as a delegate, whatever its status. A delegated task
    /// carries the person it is done for in <see cref="UserTaskInfo.OnBehalfOf"/>. Null when the user is not a candidate.
    /// </summary>
    Task<UserTaskInfo?> MyTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Open tasks the current user may complete now, oldest first (the spec 016 inbox). Tasks the user holds as a
    /// delegate carry the person they are done for in <see cref="UserTaskInfo.OnBehalfOf"/>.
    /// </summary>
    Task<IReadOnlyList<UserTaskInfo>> MyOpenTasksAsync(int limit = 100, CancellationToken ct = default);

    Task<IReadOnlyList<UserTaskInfo>> InstanceTasksAsync(Guid instanceId, CancellationToken ct = default);

    Task<IReadOnlyList<HistoryEntry>> HistoryAsync(Guid instanceId, CancellationToken ct = default);
}

/// <summary>
/// Who acts for whom while someone is away (spec 016 US4, spec 024 FR-016). The engine asks it when deciding who may
/// complete a task; the module that keeps delegations implements it.
/// </summary>
public interface IDelegations
{
    /// <summary>People who have handed their tasks to <paramref name="delegateId"/> at <paramref name="at"/>, each on their own calendar day.</summary>
    Task<IReadOnlyList<Guid>> DelegatorsOfAsync(Guid delegateId, DateTimeOffset at, CancellationToken ct = default);
}

public sealed record UserTaskCreated(Guid TaskId, Guid InstanceId, string DefinitionKey, string BusinessKey, string ElementId, DateTimeOffset? DueAt) : IIntegrationEvent;

public sealed record UserTaskCompleted(Guid TaskId, Guid InstanceId, string DefinitionKey, string BusinessKey, string ElementId, string Outcome, Guid CompletedBy) : IIntegrationEvent;

public sealed record ProcessInstanceCompleted(Guid InstanceId, string DefinitionKey, string BusinessKey, string Outcome) : IIntegrationEvent;

public sealed record ProcessInstanceWithdrawn(Guid InstanceId, string DefinitionKey, string BusinessKey) : IIntegrationEvent;

/// <summary>A user task reached its due time (spec 015 reminders).</summary>
public sealed record UserTaskDue(Guid TaskId, Guid InstanceId, string DefinitionKey, string BusinessKey, DateTimeOffset DueAt) : IIntegrationEvent;
