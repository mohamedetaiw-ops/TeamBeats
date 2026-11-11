using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow.Contracts;

/// <summary>Where a route comes from (spec 024 plan): the module that owns its simple route settings.</summary>
public static class RouteSources
{
    public const string ServiceRoute = "service_route";
    public const string ContentRoute = "content_route";
    public const string PolicyRoute = "policy_route";
    public const string DocumentRoute = "document_route";
    public const string LeaveRoute = "leave_route";
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

    /// <summary>An operator cancelled the instance on an incident (spec 024 US4 scenario 2).</summary>
    public const string Cancelled = "cancelled";
}

/// <summary>
/// Who may complete a step: named users, or anyone who holds <see cref="Permission"/> at the instance's org unit through
/// central authorization (so a sibling unit's holder is not a candidate). Role names are never used. With
/// <see cref="LineManager"/>, the current manager of the person the instance is for decides, from the reporting line
/// (spec 010), checked each time so a new manager takes over an open step; when that person has no manager, or the
/// manager may not decide (they filed it), <see cref="Permission"/> holders are the fallback.
/// </summary>
public sealed record CandidateRule(IReadOnlyList<Guid>? Users = null, string? Permission = null, bool LineManager = false);

/// <summary>
/// One step: an approval (user task) by default, or, with <see cref="Action"/>, a service task that calls a connector
/// action (spec 024 FR-010, spec 017 US2). <see cref="DueWorkingDays"/> sets a due time for an approval from the tenant
/// calendar of the instance's unit. An action step has no candidates.
/// </summary>
public sealed record RouteStep(string Id, string Name, CandidateRule Candidates, int? DueWorkingDays = null, ConnectorStep? Action = null);

/// <summary>What a business error of a connector step does: carry on with the next step, or end the instance as rejected.</summary>
public static class BusinessErrorPaths
{
    public const string Continue = "continue";
    public const string Reject = "reject";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Continue, Reject };
}

/// <summary>
/// A service task that calls a connector action by name with mapped fields only (spec 017 FR-009, spec 016 FR-016).
/// <see cref="Inputs"/> maps each action input to an instance variable, such as <c>form.device</c> or
/// <c>requester.email</c>; nothing else is sent. <see cref="OnBusinessError"/> maps the action's declared business error
/// codes to a path (<see cref="BusinessErrorPaths"/>); a code with no path raises an incident like a technical failure.
/// </summary>
public sealed record ConnectorStep(Guid ConnectionId, string Action, IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string>? OnBusinessError = null);

/// <summary>The state of an incident on a step (spec 024 US4): open until an operator retries, skips or cancels.</summary>
public static class IncidentResolutions
{
    public const string Retried = "retried";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// An incident (spec 024 FR-010, FR-013): a service task whose call failed after its retries, whose outcome is unknown,
/// or whose business error has no path. <see cref="ErrorClass"/> is the plain error class of spec 017 section 10, and
/// <see cref="ErrorCode"/> the business error code when there is one. The instance waits on the step until it is resolved.
/// </summary>
public sealed record IncidentInfo(
    Guid Id,
    Guid InstanceId,
    string DefinitionKey,
    string BusinessKey,
    string ElementId,
    string StepName,
    string ErrorClass,
    string? ErrorCode,
    string Status,
    string? Resolution,
    string? Reason,
    Guid? ResolvedBy,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ResolvedAt);

/// <summary>A connector step of an instance: its state and the external reference the system returned.</summary>
public sealed record ServiceTaskInfo(Guid InstanceId, string ElementId, string StepName, string Status, string? ExternalReference, string? ErrorClass,
    string? ErrorCode, DateTimeOffset StartedAt, DateTimeOffset? EndedAt);

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
    /// request was filed for). <paramref name="requestedFor"/> is the person the instance is for, whose line manager a
    /// line manager step asks; the starter when not given.
    /// </summary>
    /// <paramref name="variables"/> are the values connector steps may map, such as <c>form.device</c>; they are kept on
    /// the instance (at most 256 KB) and never sent unless a step maps them.
    Task<ProcessInstanceInfo> StartAsync(string key, string businessKey, Guid? orgUnitId, Guid? startedBy, IReadOnlyCollection<Guid>? alsoExcluded = null,
        Guid? requestedFor = null, IReadOnlyDictionary<string, System.Text.Json.JsonElement>? variables = null, CancellationToken ct = default);

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

    /// <summary>The connector steps of an instance, in the order they started.</summary>
    Task<IReadOnlyList<ServiceTaskInfo>> ServiceTasksAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>The incidents of an instance, newest first.</summary>
    Task<IReadOnlyList<IncidentInfo>> IncidentsAsync(Guid instanceId, CancellationToken ct = default);

    /// <summary>
    /// Resolves an open incident as an operator (spec 024 FR-013): <see cref="IncidentResolutions.Retried"/> runs the step
    /// again with its original idempotency key, <see cref="IncidentResolutions.Skipped"/> moves on to the next step,
    /// <see cref="IncidentResolutions.Cancelled"/> ends the instance. A reason is required; each is audited. The caller
    /// authorizes the operator first. A second resolution of the same incident is refused with 409.
    /// </summary>
    Task<IncidentInfo> ResolveIncidentAsync(Guid incidentId, string resolution, string? reason, CancellationToken ct = default);
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

/// <summary>A connector step succeeded; <paramref name="ExternalReference"/> is the record the system created, if any (spec 017 US2 scenario 1).</summary>
public sealed record ServiceTaskCompleted(Guid InstanceId, string DefinitionKey, string BusinessKey, string ElementId, string? ExternalReference) : IIntegrationEvent;

/// <summary>A step stopped with an incident; the instance waits (spec 016 US8 scenario 5: "automation failed").</summary>
public sealed record ProcessIncidentRaised(Guid IncidentId, Guid InstanceId, string DefinitionKey, string BusinessKey, string ElementId, string ErrorClass) : IIntegrationEvent;

/// <summary>An operator resolved an incident: retried, skipped or cancelled.</summary>
public sealed record ProcessIncidentResolved(Guid IncidentId, Guid InstanceId, string DefinitionKey, string BusinessKey, string ElementId, string Resolution) : IIntegrationEvent;

/// <summary>A user task reached its due time (spec 015 reminders).</summary>
public sealed record UserTaskDue(Guid TaskId, Guid InstanceId, string DefinitionKey, string BusinessKey, DateTimeOffset DueAt) : IIntegrationEvent;

/// <summary>What the task inbox shows for a task of another module: a title per locale, a link and the unit it is for.</summary>
public sealed record TaskSubject(string Source, IReadOnlyDictionary<string, string> Title, string Link, Guid? OrgUnitId, Guid? RequesterId);

/// <summary>
/// A module whose approvals run on the engine and show in the spec 016 task inbox (spec 009 FR-003, spec 011, spec 025).
/// The inbox lists the module's tasks, asks it whether the person whose authority is used may decide (on top of the
/// engine's candidate check), and calls it after the decision in the same transaction so its object moves on.
/// </summary>
public interface ITaskSource
{
    /// <summary>Whether tasks of this process definition key belong to the module.</summary>
    bool Handles(string definitionKey);

    /// <summary>The subject of the task, or null when its object no longer waits for a decision (it is then left out).</summary>
    Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default);

    Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default);

    /// <summary>Called after the engine completed the task; <paramref name="done"/> carries the outcome and comment.</summary>
    Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default);
}

/// <summary>
/// A task in the spec 016 inbox that is not an engine task: something only the person can do themselves, such as a
/// policy attestation (spec 025 US2). It never moves to a delegate and is done from its link, not from the inbox.
/// </summary>
public sealed record PersonalTask(string Source, Guid Id, IReadOnlyDictionary<string, string> Title, string Link, DateTimeOffset CreatedAt, DateTimeOffset? DueAt);

/// <summary>A module that puts personal tasks in the inbox. Asked for the signed-in person only.</summary>
public interface IPersonalTaskSource
{
    Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default);
}
