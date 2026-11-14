using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services;

/// <summary>The step types of the route designer (spec 016 US8 scenario 1). None runs custom code.</summary>
public static class StepTypes
{
    public const string Approval = "approval";
    public const string Task = "task";
    public const string SetField = "set_field";
    public const string Notify = "notify";
    public const string Connector = "connector";
    public const string Webhook = "webhook";
    public const string Wait = "wait";
    public const string Condition = "condition";
    public const string Split = "split";
    public const string Join = "join";
    public const string StartService = "start_service";
    public const string End = "end";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Approval, Task, SetField, Notify, Connector, Webhook, Wait, Condition, Split, Join, StartService, End,
    };

    /// <summary>Steps a person acts on.</summary>
    public static bool IsHuman(string type) => type is Approval or Task;
}

/// <summary>Who a person step goes to (FR-006): named users, a permission at the requester's unit, the line manager, a manager level, or a group or queue.</summary>
public static class AssigneeKinds
{
    public const string Users = ApproverKinds.Users;
    public const string PermissionAtRequesterUnit = ApproverKinds.PermissionAtRequesterUnit;
    public const string LineManager = ApproverKinds.LineManager;
    public const string ManagerLevel = "manager_level";
    public const string Group = "group";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Users, PermissionAtRequesterUnit, LineManager, ManagerLevel, Group };
}

/// <summary>Request results an end step can give (the request's status when the route ends there).</summary>
public static class RouteResults
{
    public const string Completed = "completed";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Returned = "returned";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Completed, Approved, Rejected };
}

/// <summary>Comparisons a condition may use: a form field against a constant (the forms' fixed rule set, no expressions).</summary>
public static class ConditionOperators
{
    public static readonly IReadOnlyDictionary<string, string> Feel = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eq"] = "=", ["ne"] = "!=", ["gt"] = ">", ["ge"] = ">=", ["lt"] = "<", ["le"] = "<=",
    };
}

public sealed record StepAssignee(string Kind, IReadOnlyList<Guid>? Users = null, string? Permission = null, int? Level = null, IReadOnlyList<Guid>? Groups = null);

public sealed record StepEscalation(int AfterWorkingDays, IReadOnlyList<Guid>? Users = null, IReadOnlyList<Guid>? Groups = null);

/// <summary>Who a notify step tells: the requester, the person the request is for, named users and members of groups.</summary>
public sealed record NotifyTarget(bool Requester = false, bool Subject = false, IReadOnlyList<Guid>? Users = null, IReadOnlyList<Guid>? Groups = null);

/// <summary>A form field compared with a constant.</summary>
public sealed record StepCondition(string Field, string Operator, JsonElement Value);

/// <summary>
/// One step of a route graph. Which settings apply depends on <see cref="Type"/>: assignee, due time and escalation for
/// approvals and tasks; connection, action or webhook and inputs for automatic steps; field and value for set field;
/// notify target; working days for wait; result for end.
/// </summary>
public sealed record RouteNode(
    string Id,
    string Type,
    IReadOnlyDictionary<string, string>? Names = null,
    StepAssignee? Assignee = null,
    int? DueWorkingDays = null,
    StepEscalation? Escalation = null,
    Guid? ConnectionId = null,
    string? Action = null,
    Guid? WebhookId = null,
    IReadOnlyDictionary<string, string>? Inputs = null,
    string? Field = null,
    string? Value = null,
    NotifyTarget? Notify = null,
    int? WaitWorkingDays = null,
    Guid? ServiceId = null,
    string? Result = null,
    IReadOnlyList<StepCondition>? AutoApprove = null);

/// <summary>A path between two steps; from a condition or split, it is taken when all its conditions hold (none: the other paths' default).</summary>
public sealed record RouteLink(string From, string To, IReadOnlyList<StepCondition>? When = null);

/// <summary>The step graph of a service route (spec 016 R2, US5 and US8).</summary>
public sealed record RouteGraph(IReadOnlyList<RouteNode> Steps, IReadOnlyList<RouteLink> Links)
{
    public const int MaxSteps = 60;

    public RouteNode? Step(string id) => Steps.FirstOrDefault(s => s.Id == id);

    /// <summary>The step a compiled element belongs to: helper elements are named <c>{step}__{part}</c>.</summary>
    public static string StepIdOf(string elementId) => elementId.Split("__", 2)[0];

    /// <summary>The package capabilities the graph needs (section 9); approval and task steps in sequence need none beyond the catalogue.</summary>
    public IReadOnlySet<string> Capabilities()
    {
        var needs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in Steps)
        {
            switch (step.Type)
            {
                case StepTypes.Condition or StepTypes.Split or StepTypes.Join:
                    needs.Add(Entitlements.Contracts.Capabilities.WorkflowAdvanced);
                    break;
                case StepTypes.Connector or StepTypes.Webhook:
                    needs.Add(Entitlements.Contracts.Capabilities.WorkflowConnectorFulfilment);
                    break;
                case StepTypes.SetField or StepTypes.Notify or StepTypes.Wait or StepTypes.StartService:
                    needs.Add(Entitlements.Contracts.Capabilities.WorkflowAutomation);
                    break;
            }
            if (step.Escalation is not null)
            {
                needs.Add(Entitlements.Contracts.Capabilities.WorkflowAdvanced);
            }
            if (step.AutoApprove is { Count: > 0 })
            {
                needs.Add(Entitlements.Contracts.Capabilities.WorkflowAutomation); // Auto-approve rules (section 9).
            }
        }
        return needs;
    }
}

/// <summary>
/// Checks the shape of a route graph when a draft is saved: known step types with the settings they need, paths
/// between steps that exist, one first step, and conditions on form fields. Reachability, matched joins and what the
/// steps refer to are checked by the process engine when the compiled model is validated (FR-013).
/// </summary>
public static partial class RouteGraphCheck
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    public static partial Regex StepId();

    /// <summary>Values a step may read: a form field, the request number, or the requester's name or email.</summary>
    [GeneratedRegex("^(form\\.[a-z][a-z0-9_]{0,39}|request\\.number|requester\\.(name|email))$")]
    public static partial Regex Reference();

    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,39}$")]
    private static partial Regex InputName();

    public static void Validate(RouteGraph graph, FormDefinition form, ValidationErrors errors)
    {
        const string at = "route.graph";
        if (graph.Steps is not { Count: > 0 } steps || steps.Count > RouteGraph.MaxSteps || graph.Links is null)
        {
            errors.Add(at, "validation.services.route_graph");
            return;
        }
        var fields = form.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var path = $"{at}.steps[{i}]";
            if (step is null || !StepId().IsMatch(step.Id ?? "") || step.Id!.Contains("__", StringComparison.Ordinal) || !ids.Add(step.Id) || !StepTypes.All.Contains(step.Type ?? ""))
            {
                errors.Add(path, "validation.services.route_step");
                continue;
            }
            if (step.Names is { } names && (names.Count > 10 || names.Any(n => string.IsNullOrWhiteSpace(n.Value) || n.Value.Length > 200)))
            {
                errors.Add($"{path}.names", "validation.services.names");
            }
            if (!StepValid(step))
            {
                errors.Add(path, "validation.services.route_step");
            }
            if (step.AutoApprove is { Count: > 0 } rule && (step.Type != StepTypes.Approval || !ConditionsValid(rule, fields)))
            {
                errors.Add($"{path}.autoApprove", "validation.services.route_condition");
            }
        }
        var links = graph.Links;
        for (var i = 0; i < links.Count; i++)
        {
            var link = links[i];
            if (link is null || !ids.Contains(link.From ?? "") || !ids.Contains(link.To ?? "") || link.From == link.To
                || links.Take(i).Any(l => l is not null && l.From == link.From && l.To == link.To))
            {
                errors.Add($"{at}.links[{i}]", "validation.services.route_link");
                continue;
            }
            var from = graph.Step(link.From!)!;
            if (link.When is { Count: > 0 } when && (from.Type is not (StepTypes.Condition or StepTypes.Split) || !ConditionsValid(when, fields)))
            {
                errors.Add($"{at}.links[{i}].when", "validation.services.route_condition");
            }
        }
        if (errors.Any)
        {
            return;
        }
        // One first step; ends lead nowhere; a condition or split branches; every other step goes on to one step.
        var firsts = steps.Where(s => !links.Any(l => l.To == s.Id)).ToList();
        if (firsts.Count != 1 || firsts[0].Type == StepTypes.End)
        {
            errors.Add(at, "validation.services.route_start");
        }
        for (var i = 0; i < steps.Count; i++)
        {
            var outgoing = links.Count(l => l.From == steps[i].Id);
            var ok = steps[i].Type switch
            {
                StepTypes.End => outgoing == 0,
                StepTypes.Condition => outgoing >= 2 && links.Count(l => l.From == steps[i].Id && l.When is not { Count: > 0 }) <= 1,
                StepTypes.Split => outgoing >= 2,
                _ => outgoing == 1,
            };
            if (!ok)
            {
                errors.Add($"{at}.steps[{i}]", "validation.services.route_paths");
            }
        }
    }

    /// <summary>At most 10 comparisons, each of a form field with a constant.</summary>
    private static bool ConditionsValid(IReadOnlyList<StepCondition> when, IReadOnlyDictionary<string, FormField> fields) =>
        when.Count <= 10 && when.All(c => c is not null && fields.ContainsKey(c.Field ?? "") && ConditionOperators.Feel.ContainsKey(c.Operator ?? "")
            && c.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);

    private static bool Ids(IReadOnlyList<Guid>? ids, int max) => ids is null || (ids.Count <= max && ids.All(g => g != Guid.Empty));

    private static bool StepValid(RouteNode step)
    {
        var inputs = step.Inputs ?? new Dictionary<string, string>();
        if (inputs.Count > 30 || inputs.Any(i => !InputName().IsMatch(i.Key) || !Reference().IsMatch(i.Value ?? "")))
        {
            return false;
        }
        if (step.DueWorkingDays is < 1 or > 90 || (step.Escalation is { } e && (step.DueWorkingDays is null || e.AfterWorkingDays is < 1 or > 90
            || (e.Users?.Count ?? 0) + (e.Groups?.Count ?? 0) == 0 || !Ids(e.Users, 50) || !Ids(e.Groups, 20))))
        {
            return false;
        }
        return step.Type switch
        {
            StepTypes.Approval or StepTypes.Task => step.Assignee is { } a && AssigneeValid(a),
            StepTypes.Connector => step.ConnectionId is { } c && c != Guid.Empty && !string.IsNullOrEmpty(step.Action) && step.WebhookId is null,
            StepTypes.Webhook => step.WebhookId is { } w && w != Guid.Empty && step.ConnectionId is null,
            StepTypes.SetField => step.Field is { } f && InputName().IsMatch(f) && step.Value is { Length: <= 500 } && !Reserved(f),
            StepTypes.Notify => step.Notify is { } n && (n.Requester || n.Subject || n.Users is { Count: > 0 } || n.Groups is { Count: > 0 }) && Ids(n.Users, 50) && Ids(n.Groups, 20),
            StepTypes.Wait => step.WaitWorkingDays is >= 1 and <= 365,
            StepTypes.StartService => step.ServiceId is { } s && s != Guid.Empty,
            StepTypes.End => step.Result is null || RouteResults.All.Contains(step.Result),
            _ => true,
        };
    }

    /// <summary>Variable names a set field step may not take: the request values and what the engine writes.</summary>
    private static bool Reserved(string name) => name is "form" or "request" or "requester" or "tasks" or "outcome" or "result";

    private static bool AssigneeValid(StepAssignee a) => a.Kind switch
    {
        AssigneeKinds.Users => a.Users is { Count: > 0 } && Ids(a.Users, 50) && a.Permission is null,
        AssigneeKinds.PermissionAtRequesterUnit => a.Permission is { } p && Authorization.Contracts.Permissions.All.Contains(p),
        AssigneeKinds.LineManager => a.Permission is null || Authorization.Contracts.Permissions.All.Contains(a.Permission),
        AssigneeKinds.ManagerLevel => a.Level is >= 1 and <= 5,
        AssigneeKinds.Group => a.Groups is { Count: > 0 } && Ids(a.Groups, 20),
        _ => false,
    };

    /// <summary>The request value a reference names, as a FEEL path over the route's variables.</summary>
    public static string Feel(string reference) => reference;
}

/// <summary>
/// Compiles a route graph to BPMN 2.0 with the TeamBeats extensions for the spec 024 engine. Element IDs are the step
/// IDs, so the engine's problems and history name the steps; helper elements are <c>{step}__{part}</c>. An approval
/// is a user task followed by its decision: approve goes on, reject and return end the whole route with that result.
/// A task step goes on when completed and ends the route as rejected when rejected with a reason.
/// </summary>
public static class RouteGraphCompiler
{
    private static readonly XNamespace Bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private static readonly XNamespace Tb = "https://teambeats.example/schema/bpmn/1.0";

    public static string Compile(string processName, RouteGraph graph)
    {
        var process = new XElement(Bpmn + "process", new XAttribute("id", "route"), new XAttribute("name", processName), new XAttribute("isExecutable", "true"));
        var flows = new List<XElement>();
        var n = 0;
        void Flow(string from, string to, string? condition = null, string? id = null)
        {
            var flow = new XElement(Bpmn + "sequenceFlow", new XAttribute("id", id ?? $"flow_{++n}"), new XAttribute("sourceRef", from), new XAttribute("targetRef", to));
            if (condition is not null)
            {
                flow.Add(new XElement(Bpmn + "conditionExpression", condition));
            }
            flows.Add(flow);
        }

        // An approval with an auto-approve rule is entered through its check, so every path into the step meets the rule first.
        string Entry(string stepId) => graph.Step(stepId) is { Type: StepTypes.Approval, AutoApprove.Count: > 0 } ? stepId + "__auto" : stepId;
        var first = graph.Steps.First(s => !graph.Links.Any(l => l.To == s.Id));
        process.Add(new XElement(Bpmn + "startEvent", new XAttribute("id", "start__")));
        Flow("start__", Entry(first.Id));
        var linkIds = graph.Links.Select((l, i) => (l, id: $"link_{i}")).ToDictionary(x => x.l, x => x.id);

        foreach (var step in graph.Steps)
        {
            var outgoing = graph.Links.Where(l => l.From == step.Id).ToList();
            switch (step.Type)
            {
                case StepTypes.Approval:
                case StepTypes.Task:
                {
                    // With a rule, both ways out (decided and auto-approved) meet in a merge first, so a join after the step counts one path.
                    var next = Entry(outgoing[0].To);
                    if (step.AutoApprove is { Count: > 0 } rule)
                    {
                        var merge = step.Id + "__next";
                        process.Add(new XElement(Bpmn + "exclusiveGateway", new XAttribute("id", merge)));
                        Flow(merge, next);
                        next = merge;
                        // The rule holds: the system completes the step ("auto-approved by rule") and the route goes on past it.
                        var check = step.Id + "__auto";
                        var approved = step.Id + "__auto_approved";
                        var manual = $"{check}_manual";
                        process.Add(new XElement(Bpmn + "exclusiveGateway", new XAttribute("id", check), new XAttribute("default", manual)));
                        Flow(check, step.Id, id: manual);
                        Flow(check, approved, Condition(rule));
                        process.Add(new XElement(Bpmn + "serviceTask", new XAttribute("id", approved), new XAttribute("name", Name(step)), Extensions(step,
                            new XElement(Tb + "product", new XAttribute("action", "auto_approve")))));
                        Flow(approved, merge);
                    }
                    process.Add(new XElement(Bpmn + "userTask", Attrs(step), Extensions(step, Assignment(step.Assignee!), Due(step), Escalation(step.Escalation))));
                    var decision = step.Id + "__decision";
                    process.Add(new XElement(Bpmn + "exclusiveGateway", new XAttribute("id", decision), new XAttribute("default", linkIds[outgoing[0]])));
                    Flow(step.Id, decision);
                    flows.Add(new XElement(Bpmn + "sequenceFlow", new XAttribute("id", linkIds[outgoing[0]]), new XAttribute("sourceRef", decision),
                        new XAttribute("targetRef", next)));
                    Stop(step.Id + "__rejected", RouteResults.Rejected);
                    Flow(decision, step.Id + "__rejected", "outcome = \"reject\"");
                    if (step.Type == StepTypes.Approval)
                    {
                        Stop(step.Id + "__returned", RouteResults.Returned);
                        Flow(decision, step.Id + "__returned", "outcome = \"return\"");
                    }
                    continue;
                }
                case StepTypes.Connector:
                    process.Add(new XElement(Bpmn + "serviceTask", Attrs(step), Extensions(step,
                        new XElement(Tb + "connector", new XAttribute("connection", step.ConnectionId!.Value), new XAttribute("action", step.Action!)), Inputs(step))));
                    break;
                case StepTypes.Webhook:
                    process.Add(new XElement(Bpmn + "serviceTask", Attrs(step), Extensions(step,
                        new XElement(Tb + "webhook", new XAttribute("id", step.WebhookId!.Value)), Inputs(step))));
                    break;
                case StepTypes.SetField:
                    process.Add(new XElement(Bpmn + "serviceTask", Attrs(step), Extensions(step,
                        new XElement(Tb + "product", new XAttribute("action", "set")),
                        new XElement(Tb + "input", new XAttribute("name", step.Field!), new XAttribute("source", Literal(step.Value!))))));
                    break;
                case StepTypes.Notify:
                {
                    var target = step.Notify!;
                    process.Add(new XElement(Bpmn + "serviceTask", Attrs(step), Extensions(step,
                        new XElement(Tb + "product", new XAttribute("action", "notify")),
                        Input("requester", target.Requester ? "true" : "false"), Input("subject", target.Subject ? "true" : "false"),
                        Input("users", Literal(string.Join(',', target.Users ?? []))), Input("groups", Literal(string.Join(',', target.Groups ?? []))))));
                    break;
                }
                case StepTypes.StartService:
                    process.Add(new XElement(Bpmn + "serviceTask", Attrs(step), Extensions(step,
                        new XElement(Tb + "product", new XAttribute("action", "start_service")), Input("service", Literal(step.ServiceId!.Value.ToString())))));
                    break;
                case StepTypes.Wait:
                    process.Add(new XElement(Bpmn + "intermediateCatchEvent", Attrs(step), Extensions(step,
                        new XElement(Tb + "timer", new XAttribute("workingDays", step.WaitWorkingDays!.Value.ToString(CultureInfo.InvariantCulture)))),
                        new XElement(Bpmn + "timerEventDefinition")));
                    break;
                case StepTypes.Condition:
                    process.Add(new XElement(Bpmn + "exclusiveGateway", Attrs(step), Default(outgoing, linkIds), Extensions(step)));
                    break;
                case StepTypes.Split:
                    process.Add(new XElement(Bpmn + (outgoing.Any(l => l.When is { Count: > 0 }) ? "inclusiveGateway" : "parallelGateway"), Attrs(step),
                        Default(outgoing, linkIds), Extensions(step)));
                    break;
                case StepTypes.Join:
                    process.Add(new XElement(Bpmn + (JoinsInclusive(graph, step) ? "inclusiveGateway" : "parallelGateway"), Attrs(step), Extensions(step)));
                    break;
                case StepTypes.End:
                    process.Add(new XElement(Bpmn + "endEvent", Attrs(step), Extensions(step,
                        new XElement(Tb + "result", new XAttribute("value", step.Result ?? RouteResults.Completed))), new XElement(Bpmn + "terminateEventDefinition")));
                    continue;
            }
            foreach (var link in outgoing)
            {
                Flow(step.Id, Entry(link.To), link.When is { Count: > 0 } when ? Condition(when) : null, linkIds[link]);
            }
        }
        process.Add(flows);
        var definitions = new XElement(Bpmn + "definitions", new XAttribute(XNamespace.Xmlns + "bpmn", Bpmn), new XAttribute(XNamespace.Xmlns + "tb", Tb),
            new XAttribute("id", "service_route"), process);
        return definitions.ToString(SaveOptions.DisableFormatting);

        void Stop(string id, string result) => process.Add(new XElement(Bpmn + "endEvent", new XAttribute("id", id),
            new XElement(Bpmn + "extensionElements", new XElement(Tb + "result", new XAttribute("value", result))), new XElement(Bpmn + "terminateEventDefinition")));
    }

    private static object[] Attrs(RouteNode step) =>
        [new XAttribute("id", step.Id), new XAttribute("name", Name(step))];

    private static string Name(RouteNode step) =>
        step.Names is { Count: > 0 } names ? names.TryGetValue("en", out var en) ? en : names.Values.First() : step.Id;

    private static XAttribute? Default(IReadOnlyList<RouteLink> outgoing, IReadOnlyDictionary<RouteLink, string> ids) =>
        outgoing.FirstOrDefault(l => l.When is not { Count: > 0 }) is { } fallback && outgoing.Any(l => l.When is { Count: > 0 }) ? new XAttribute("default", ids[fallback]) : null;

    private static XElement Extensions(RouteNode step, params object?[] content)
    {
        var x = new XElement(Bpmn + "extensionElements");
        foreach (var (locale, name) in step.Names ?? new Dictionary<string, string>())
        {
            x.Add(new XElement(Tb + "name", new XAttribute(XNamespace.Xml + "lang", locale), name));
        }
        x.Add(content.Where(c => c is not null).ToArray());
        return x;
    }

    private static XElement Assignment(StepAssignee a)
    {
        var x = new XElement(Tb + "assignment");
        switch (a.Kind)
        {
            case AssigneeKinds.Users:
                x.Add(new XAttribute("users", string.Join(',', a.Users!)));
                break;
            case AssigneeKinds.PermissionAtRequesterUnit:
                x.Add(new XAttribute("permission", a.Permission!));
                break;
            case AssigneeKinds.LineManager:
                // Without a manager the step falls back to the permission at the requester's unit (R1 rule).
                x.Add(new XAttribute("lineManager", "true"), new XAttribute("permission", a.Permission ?? Authorization.Contracts.Permissions.WorkflowApprove));
                break;
            case AssigneeKinds.ManagerLevel:
                x.Add(new XAttribute("managerLevel", a.Level!.Value.ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("permission", a.Permission ?? Authorization.Contracts.Permissions.WorkflowApprove));
                break;
            case AssigneeKinds.Group:
                x.Add(new XAttribute("groups", string.Join(',', a.Groups!)));
                break;
        }
        return x;
    }

    private static XElement? Due(RouteNode step) => step.DueWorkingDays is { } days
        ? new XElement(Tb + "due", new XAttribute("workingDays", days.ToString(CultureInfo.InvariantCulture)))
        : null;

    private static XElement? Escalation(StepEscalation? e)
    {
        if (e is null)
        {
            return null;
        }
        var x = new XElement(Tb + "escalation", new XAttribute("afterWorkingDays", e.AfterWorkingDays.ToString(CultureInfo.InvariantCulture)));
        if (e.Users is { Count: > 0 } users)
        {
            x.Add(new XAttribute("users", string.Join(',', users)));
        }
        if (e.Groups is { Count: > 0 } groups)
        {
            x.Add(new XAttribute("groups", string.Join(',', groups)));
        }
        return x;
    }

    private static IEnumerable<XElement> Inputs(RouteNode step) =>
        (step.Inputs ?? new Dictionary<string, string>()).Select(i => Input(i.Key, RouteGraphCheck.Feel(i.Value)));

    private static XElement Input(string name, string source) => new(Tb + "input", new XAttribute("name", name), new XAttribute("source", source));

    /// <summary>All of the conditions, each a form field compared with a FEEL literal.</summary>
    private static string Condition(IReadOnlyList<StepCondition> when) =>
        string.Join(" and ", when.Select(c => $"form.{c.Field} {ConditionOperators.Feel[c.Operator]} {Literal(c.Value)}"));

    private static string Literal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => Literal(value.GetString()!),
        JsonValueKind.Number => value.GetDecimal().ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "true",
        _ => "false",
    };

    public static string Literal(string text)
    {
        var sb = new StringBuilder("\"");
        foreach (var ch in text)
        {
            sb.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => ch.ToString() });
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A join waits like the split it closes: the nearest split upstream, inclusive when any of its paths has a condition.</summary>
    private static bool JoinsInclusive(RouteGraph graph, RouteNode join)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(graph.Links.Where(l => l.To == join.Id).Select(l => l.From));
        while (queue.TryDequeue(out var id))
        {
            if (!seen.Add(id) || graph.Step(id) is not { } step)
            {
                continue;
            }
            if (step.Type == StepTypes.Split)
            {
                return graph.Links.Any(l => l.From == step.Id && l.When is { Count: > 0 });
            }
            foreach (var link in graph.Links.Where(l => l.To == id))
            {
                queue.Enqueue(link.From);
            }
        }
        return false;
    }
}
