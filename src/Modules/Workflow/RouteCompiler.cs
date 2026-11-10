using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow;

/// <summary>
/// The executable graph of a route: steps in order, each a user task followed by an outcome gateway, or a service task
/// that calls a connector action (R2 part of spec 024 FR-010).
/// </summary>
public sealed record CompiledRoute(string Name, bool ExcludeStarter, IReadOnlyList<CompiledStep> Steps)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CompiledRoute FromJson(string json) => JsonSerializer.Deserialize<CompiledRoute>(json, Json)
        ?? throw new InvalidOperationException("Compiled route is empty.");
}

public sealed record CompiledStep(string Id, string Name, IReadOnlyList<Guid> Users, string? Permission, int? DueWorkingDays, bool LineManager = false,
    ConnectorStep? Action = null)
{
    public bool IsServiceTask => Action is not null;
}

/// <summary>
/// Compiles simple routes (spec 024 plan) to BPMN 2.0 XML with the <c>teambeats</c> extension namespace and to the
/// executable graph, and reads that XML back, so the R2 modeler and the full subset run the same definitions. The R1
/// subset is: start event, user tasks in sequence, an exclusive gateway on each task outcome, end events.
/// </summary>
public static partial class RouteCompiler
{
    public const string BpmnNs = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    public const string TeamBeatsNs = "https://teambeats.example/schema/bpmn/1.0";
    public const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
    public const int MaxSteps = 10;
    public const int MaxNameLength = 200;
    public const int MaxUsersPerStep = 50;
    public const int MaxDueWorkingDays = 90;

    private static readonly XNamespace Bpmn = BpmnNs;
    private static readonly XNamespace Tb = TeamBeatsNs;
    private static readonly XNamespace Xsi = XsiNs;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,40}$")]
    private static partial Regex ElementId();

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$")]
    private static partial Regex PermissionKey();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,99}$")]
    private static partial Regex ProcessKey();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex ActionName();

    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,40}$")]
    private static partial Regex InputName();

    /// <summary>An instance variable a step may map, such as <c>form.device</c> or <c>requester.email</c>.</summary>
    [GeneratedRegex("^[a-z]{2,20}\\.[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex VariableName();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex ErrorCode();

    public const int MaxMappedInputs = 50;

    public static bool IsValidKey(string? key) => key is not null && ProcessKey().IsMatch(key);

    /// <summary>Validates a route; problems use resource keys under <c>validation.workflow</c>.</summary>
    public static ValidationErrors Validate(RouteSpec? route)
    {
        var errors = new ValidationErrors();
        if (route is null)
        {
            errors.Add("route", "validation.workflow.route_required");
            return errors;
        }
        if (string.IsNullOrWhiteSpace(route.Name) || route.Name.Length > MaxNameLength)
        {
            errors.Add("name", "validation.workflow.name");
        }
        if (route.Steps is not { Count: > 0 } || route.Steps.Count > MaxSteps)
        {
            errors.Add("steps", "validation.workflow.steps");
            return errors;
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < route.Steps.Count; i++)
        {
            var step = route.Steps[i];
            var field = $"steps[{i}]";
            if (step is null)
            {
                errors.Add(field, "validation.workflow.steps");
                continue;
            }
            if (step.Id is null || !ElementId().IsMatch(step.Id) || IsReserved(step.Id) || !ids.Add(step.Id))
            {
                errors.Add($"{field}.id", "validation.workflow.step_id");
            }
            if (string.IsNullOrWhiteSpace(step.Name) || step.Name.Length > MaxNameLength)
            {
                errors.Add($"{field}.name", "validation.workflow.name");
            }
            if (step.Action is { } action)
            {
                ValidateAction(action, field, errors);
                if (step.DueWorkingDays is not null || step.Candidates is { } c && (c.Users is { Count: > 0 } || c.Permission is not null || c.LineManager))
                {
                    errors.Add($"{field}.candidates", "validation.workflow.action_candidates");
                }
                continue;
            }
            var users = step.Candidates?.Users ?? [];
            var permission = step.Candidates?.Permission;
            if (users.Count == 0 && string.IsNullOrEmpty(permission) && step.Candidates?.LineManager != true)
            {
                // A user task with no assignment cannot be published (US1 scenario 2).
                errors.Add($"{field}.candidates", "validation.workflow.no_candidates");
            }
            if (users.Count > MaxUsersPerStep || users.Any(u => u == Guid.Empty) || (step.Candidates?.LineManager == true && users.Count > 0))
            {
                errors.Add($"{field}.candidates.users", "validation.workflow.users");
            }
            if (permission is not null && !PermissionKey().IsMatch(permission))
            {
                errors.Add($"{field}.candidates.permission", "validation.workflow.permission");
            }
            if (step.DueWorkingDays is { } due && (due < 1 || due > MaxDueWorkingDays))
            {
                errors.Add($"{field}.dueWorkingDays", "validation.workflow.due");
            }
        }
        return errors;
    }

    private static void ValidateAction(ConnectorStep action, string field, ValidationErrors errors)
    {
        if (action.ConnectionId == Guid.Empty)
        {
            errors.Add($"{field}.action.connectionId", "validation.workflow.action_connection");
        }
        if (action.Action is null || !ActionName().IsMatch(action.Action))
        {
            errors.Add($"{field}.action.action", "validation.workflow.action_name");
        }
        var inputs = action.Inputs ?? new Dictionary<string, string>();
        if (inputs.Count > MaxMappedInputs || inputs.Any(i => !InputName().IsMatch(i.Key) || i.Value is null || !VariableName().IsMatch(i.Value)))
        {
            errors.Add($"{field}.action.inputs", "validation.workflow.action_inputs");
        }
        if (action.OnBusinessError is { } paths && (paths.Count > 50 || paths.Any(p => !ErrorCode().IsMatch(p.Key) || !BusinessErrorPaths.All.Contains(p.Value ?? string.Empty))))
        {
            errors.Add($"{field}.action.onBusinessError", "validation.workflow.action_errors");
        }
    }

    private static bool IsReserved(string id) => id is "start" or "end_approved" or "end_rejected" or "end_returned" || id.StartsWith("gw_", StringComparison.Ordinal) || id.StartsWith("flow_", StringComparison.Ordinal);

    public static CompiledRoute Compile(RouteSpec route)
    {
        Validate(route).ThrowIfAny();
        return new CompiledRoute(route.Name.Trim(), route.ExcludeStarter,
            route.Steps.Select(s => s.Action is { } a
                ? new CompiledStep(s.Id, s.Name.Trim(), [], null, null, false,
                    new ConnectorStep(a.ConnectionId, a.Action, new SortedDictionary<string, string>(a.Inputs.ToDictionary(), StringComparer.Ordinal),
                        a.OnBusinessError is { Count: > 0 } paths ? new SortedDictionary<string, string>(paths.ToDictionary(), StringComparer.Ordinal) : null))
                : new CompiledStep(s.Id, s.Name.Trim(), (s.Candidates?.Users ?? []).Distinct().ToList(), s.Candidates?.Permission, s.DueWorkingDays,
                    s.Candidates?.LineManager ?? false)).ToList());
    }

    /// <summary>BPMN 2.0 XML for the route. Element IDs are the step IDs, so history and monitoring name the same elements.</summary>
    public static string ToBpmn(string key, CompiledRoute route)
    {
        var process = new XElement(Bpmn + "process", new XAttribute("id", key), new XAttribute("name", route.Name), new XAttribute("isExecutable", "true"),
            new XAttribute(Tb + "excludeStarter", route.ExcludeStarter ? "true" : "false"));
        var flows = new List<XElement>();
        var flowNumber = 0;
        XElement Flow(string from, string to, string? outcome = null)
        {
            var flow = new XElement(Bpmn + "sequenceFlow", new XAttribute("id", $"flow_{++flowNumber}"), new XAttribute("sourceRef", from), new XAttribute("targetRef", to));
            if (outcome is not null)
            {
                flow.Add(new XElement(Bpmn + "conditionExpression", new XAttribute(Xsi + "type", "tFormalExpression"), $"=outcome = \"{outcome}\""));
            }
            flows.Add(flow);
            return flow;
        }

        process.Add(new XElement(Bpmn + "startEvent", new XAttribute("id", "start")));
        var previous = "start";
        foreach (var step in route.Steps)
        {
            if (step.Action is { } action)
            {
                // A service task: the connector action, its input mapping and the paths of its business errors.
                var connector = new XElement(Tb + "connector", new XAttribute("connection", action.ConnectionId.ToString("D")), new XAttribute("action", action.Action));
                foreach (var input in action.Inputs)
                {
                    connector.Add(new XElement(Tb + "input", new XAttribute("name", input.Key), new XAttribute("source", input.Value)));
                }
                foreach (var path in action.OnBusinessError ?? new Dictionary<string, string>())
                {
                    connector.Add(new XElement(Tb + "businessError", new XAttribute("code", path.Key), new XAttribute("path", path.Value)));
                }
                process.Add(new XElement(Bpmn + "serviceTask", new XAttribute("id", step.Id), new XAttribute("name", step.Name), new XElement(Bpmn + "extensionElements", connector)));
                Flow(previous, step.Id, previous.StartsWith("gw_", StringComparison.Ordinal) ? TaskOutcomes.Approve : null);
                previous = step.Id;
                continue;
            }
            var candidates = new XElement(Tb + "candidates");
            if (step.LineManager)
            {
                candidates.Add(new XElement(Tb + "lineManager"));
            }
            foreach (var user in step.Users)
            {
                candidates.Add(new XElement(Tb + "user", new XAttribute("id", user.ToString("D"))));
            }
            if (step.Permission is not null)
            {
                candidates.Add(new XElement(Tb + "permissionAtUnit", new XAttribute("key", step.Permission)));
            }
            var extensions = new XElement(Bpmn + "extensionElements", candidates);
            if (step.DueWorkingDays is { } due)
            {
                extensions.Add(new XElement(Tb + "dueDate", new XAttribute("workingDays", due)));
            }
            process.Add(new XElement(Bpmn + "userTask", new XAttribute("id", step.Id), new XAttribute("name", step.Name), extensions));
            Flow(previous, step.Id, previous.StartsWith("gw_", StringComparison.Ordinal) ? TaskOutcomes.Approve : null);
            var gateway = "gw_" + step.Id;
            process.Add(new XElement(Bpmn + "exclusiveGateway", new XAttribute("id", gateway)));
            Flow(step.Id, gateway);
            Flow(gateway, "end_rejected", TaskOutcomes.Reject);
            Flow(gateway, "end_returned", TaskOutcomes.Return);
            previous = gateway;
        }
        Flow(previous, "end_approved", previous.StartsWith("gw_", StringComparison.Ordinal) ? TaskOutcomes.Approve : null);
        process.Add(new XElement(Bpmn + "endEvent", new XAttribute("id", "end_approved")));
        process.Add(new XElement(Bpmn + "endEvent", new XAttribute("id", "end_rejected")));
        process.Add(new XElement(Bpmn + "endEvent", new XAttribute("id", "end_returned")));
        process.Add(flows);
        var definitions = new XElement(Bpmn + "definitions",
            new XAttribute(XNamespace.Xmlns + "bpmn", BpmnNs),
            new XAttribute(XNamespace.Xmlns + "teambeats", TeamBeatsNs),
            new XAttribute(XNamespace.Xmlns + "xsi", XsiNs),
            new XAttribute("id", "definitions_" + key.Replace('.', '_').Replace(':', '_')),
            new XAttribute("targetNamespace", "https://teambeats.example/processes"),
            process);
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), definitions).ToString();
    }

    /// <summary>
    /// Reads route BPMN back into the executable graph by walking the approve branches and service tasks from the start
    /// event. Anything outside the supported subset is refused, so a definition never runs with elements the engine does not support.
    /// </summary>
    public static CompiledRoute FromBpmn(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var process = doc.Root?.Element(Bpmn + "process") ?? throw new FormatException("No BPMN process.");
        var supported = new HashSet<string> { "startEvent", "userTask", "serviceTask", "exclusiveGateway", "endEvent", "sequenceFlow" };
        var unsupported = process.Elements().Where(e => e.Name.Namespace == Bpmn && !supported.Contains(e.Name.LocalName)).Select(e => e.Name.LocalName).ToList();
        if (unsupported.Count > 0)
        {
            throw new FormatException("Unsupported BPMN elements in R1: " + string.Join(", ", unsupported.Distinct()));
        }
        var flows = process.Elements(Bpmn + "sequenceFlow").Select(f => (
            From: (string)f.Attribute("sourceRef")!,
            To: (string)f.Attribute("targetRef")!,
            Condition: f.Element(Bpmn + "conditionExpression")?.Value.Trim())).ToList();
        var tasks = process.Elements(Bpmn + "userTask").ToDictionary(t => (string)t.Attribute("id")!, StringComparer.Ordinal);
        var services = process.Elements(Bpmn + "serviceTask").ToDictionary(t => (string)t.Attribute("id")!, StringComparer.Ordinal);
        var steps = new List<CompiledStep>();
        var current = flows.Single(f => f.From == "start").To;
        var approve = $"=outcome = \"{TaskOutcomes.Approve}\"";
        while (current != "end_approved")
        {
            if (services.TryGetValue(current, out var service) && steps.Count < MaxSteps)
            {
                var connector = service.Element(Bpmn + "extensionElements")?.Element(Tb + "connector") ?? throw new FormatException($"Service task {current} has no connector.");
                var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var input in connector.Elements(Tb + "input"))
                {
                    inputs[(string)input.Attribute("name")!] = (string)input.Attribute("source")!;
                }
                var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var path in connector.Elements(Tb + "businessError"))
                {
                    paths[(string)path.Attribute("code")!] = (string)path.Attribute("path")!;
                }
                steps.Add(new CompiledStep(current, (string?)service.Attribute("name") ?? current, [], null, null, false,
                    new ConnectorStep(Guid.Parse((string)connector.Attribute("connection")!), (string)connector.Attribute("action")!, inputs, paths.Count > 0 ? paths : null)));
                current = flows.Single(f => f.From == current).To;
                continue;
            }
            if (!tasks.TryGetValue(current, out var task) || steps.Count >= MaxSteps)
            {
                throw new FormatException($"Expected a user task at {current}.");
            }
            var extensions = task.Element(Bpmn + "extensionElements");
            var candidates = extensions?.Element(Tb + "candidates");
            var users = candidates?.Elements(Tb + "user").Select(u => Guid.Parse((string)u.Attribute("id")!)).ToList() ?? [];
            var permission = (string?)candidates?.Element(Tb + "permissionAtUnit")?.Attribute("key");
            var due = (int?)extensions?.Element(Tb + "dueDate")?.Attribute("workingDays");
            var lineManager = candidates?.Element(Tb + "lineManager") is not null;
            steps.Add(new CompiledStep(current, (string?)task.Attribute("name") ?? current, users, permission, due, lineManager));
            var gateway = flows.Single(f => f.From == current).To;
            current = flows.Single(f => f.From == gateway && f.Condition == approve).To;
        }
        var excludeStarter = (string?)process.Attribute(Tb + "excludeStarter") != "false";
        return new CompiledRoute((string?)process.Attribute("name") ?? string.Empty, excludeStarter, steps);
    }
}
