using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamBeats.Modules.Workflow.Graph;

/// <summary>Element types of the R2 subset (spec 024 section 12). Anything else in a model is refused.</summary>
public static class ElementTypes
{
    public const string StartEvent = "startEvent";
    public const string EndEvent = "endEvent";
    public const string IntermediateCatchEvent = "intermediateCatchEvent";
    public const string IntermediateThrowEvent = "intermediateThrowEvent";
    public const string BoundaryEvent = "boundaryEvent";
    public const string UserTask = "userTask";
    public const string ServiceTask = "serviceTask";
    public const string BusinessRuleTask = "businessRuleTask";
    public const string SendTask = "sendTask";
    public const string ReceiveTask = "receiveTask";
    public const string ManualTask = "manualTask";
    public const string ExclusiveGateway = "exclusiveGateway";
    public const string ParallelGateway = "parallelGateway";
    public const string InclusiveGateway = "inclusiveGateway";
    public const string EventBasedGateway = "eventBasedGateway";
    public const string SubProcess = "subProcess";
    public const string CallActivity = "callActivity";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        StartEvent, EndEvent, IntermediateCatchEvent, IntermediateThrowEvent, BoundaryEvent, UserTask, ServiceTask, BusinessRuleTask, SendTask,
        ReceiveTask, ManualTask, ExclusiveGateway, ParallelGateway, InclusiveGateway, EventBasedGateway, SubProcess, CallActivity,
    };

    /// <summary>Elements that do work and can carry boundary events and multi-instance.</summary>
    public static readonly IReadOnlySet<string> Activities = new HashSet<string>(StringComparer.Ordinal)
    {
        UserTask, ServiceTask, BusinessRuleTask, SendTask, ReceiveTask, ManualTask, SubProcess, CallActivity,
    };

    public static bool IsGateway(string type) => type is ExclusiveGateway or ParallelGateway or InclusiveGateway or EventBasedGateway;
}

/// <summary>The trigger of an event: none, timer, message, error, escalation or terminate.</summary>
public static class EventKinds
{
    public const string None = "none";
    public const string Timer = "timer";
    public const string Message = "message";
    public const string Error = "error";
    public const string Escalation = "escalation";
    public const string Terminate = "terminate";
}

/// <summary>A timer: an ISO 8601 date, duration or cycle (<c>R3/PT1H</c>), or a number of working days, as text or FEEL.</summary>
public sealed record TimerSpec(string? Date = null, string? Duration = null, string? Cycle = null, int? WorkingDays = null);

/// <summary>Who a user task goes to. Expressions are FEEL and give a user ID or a list of them.</summary>
public sealed record Assignment(
    IReadOnlyList<Guid>? Users = null,
    string? Permission = null,
    bool LineManager = false,
    int? ManagerLevel = null,
    string? AssigneeExpression = null,
    IReadOnlyList<Guid>? FallbackUsers = null)
{
    public bool IsEmpty => Users is not { Count: > 0 } && Permission is null && !LineManager && ManagerLevel is null && AssigneeExpression is null;
}

/// <summary>A connector action or a registered outbound webhook, with FEEL input mappings and output mappings to variables.</summary>
public sealed record ServiceCall(
    Guid? ConnectionId,
    string? Action,
    Guid? WebhookId,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string> Outputs,
    int Retries = 3);

/// <summary>Multi-instance: one body per item of <see cref="Collection"/>, one after another or all at once.</summary>
public sealed record MultiInstance(bool Sequential, string Collection, string ElementVariable, string? CompletionCondition);

/// <summary>One element of the executable graph. <see cref="Scope"/> is the sub-process it sits in, or null for the process.</summary>
public sealed record GraphElement(
    string Id,
    string Type,
    IReadOnlyDictionary<string, string> Names,
    string? Scope,
    IReadOnlyList<string> Incoming,
    IReadOnlyList<string> Outgoing,
    string EventKind = EventKinds.None,
    TimerSpec? Timer = null,
    string? MessageName = null,
    string? CorrelationKey = null,
    string? Code = null,
    string? AttachedTo = null,
    bool Interrupting = true,
    string? DefaultFlow = null,
    Assignment? Assignment = null,
    int? DueWorkingDays = null,
    ServiceCall? Call = null,
    string? DecisionKey = null,
    string? ResultVariable = null,
    string? CalledKey = null,
    MultiInstance? Loop = null,
    bool TriggeredByEvent = false,
    IReadOnlyDictionary<string, string>? Outputs = null)
{
    public string Name(string locale) => Names.TryGetValue(locale, out var n) ? n : Names.Values.FirstOrDefault() ?? Id;
}

public sealed record GraphFlow(string Id, string Source, string Target, string? Condition);

/// <summary>The executable graph a published process version runs (spec 024 plan, R2 model).</summary>
public sealed record ProcessGraph(string Key, IReadOnlyDictionary<string, string> Names, IReadOnlyList<GraphElement> Elements, IReadOnlyList<GraphFlow> Flows)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private Dictionary<string, GraphElement>? byId;
    private Dictionary<string, GraphFlow>? flowById;

    public GraphElement Element(string id) => (byId ??= Elements.ToDictionary(e => e.Id, StringComparer.Ordinal))[id];

    public bool Has(string id) => (byId ??= Elements.ToDictionary(e => e.Id, StringComparer.Ordinal)).ContainsKey(id);

    public GraphFlow Flow(string id) => (flowById ??= Flows.ToDictionary(f => f.Id, StringComparer.Ordinal))[id];

    public IEnumerable<GraphElement> InScope(string? scope) => Elements.Where(e => e.Scope == scope);

    public IEnumerable<GraphElement> BoundariesOf(string activityId) => Elements.Where(e => e.Type == ElementTypes.BoundaryEvent && e.AttachedTo == activityId);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static ProcessGraph FromJson(string json) => JsonSerializer.Deserialize<ProcessGraph>(json, Json)
        ?? throw new InvalidOperationException("Compiled process is empty.");
}
