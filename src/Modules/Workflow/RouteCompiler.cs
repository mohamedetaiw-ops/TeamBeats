using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow;

/// <summary>The executable graph of an R1 route: steps in order, each a user task followed by an outcome gateway.</summary>
public sealed record CompiledRoute(string Name, bool ExcludeStarter, IReadOnlyList<CompiledStep> Steps)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CompiledRoute FromJson(string json) => JsonSerializer.Deserialize<CompiledRoute>(json, Json)
        ?? throw new InvalidOperationException("Compiled route is empty.");
}

public sealed record CompiledStep(string Id, string Name, IReadOnlyList<Guid> Users, string? Permission, int? DueWorkingDays);

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
            var users = step.Candidates?.Users ?? [];
            var permission = step.Candidates?.Permission;
            if (users.Count == 0 && string.IsNullOrEmpty(permission))
            {
                // A user task with no assignment cannot be published (US1 scenario 2).
                errors.Add($"{field}.candidates", "validation.workflow.no_candidates");
            }
            if (users.Count > MaxUsersPerStep || users.Any(u => u == Guid.Empty))
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

    private static bool IsReserved(string id) => id is "start" or "end_approved" or "end_rejected" or "end_returned" || id.StartsWith("gw_", StringComparison.Ordinal) || id.StartsWith("flow_", StringComparison.Ordinal);

    public static CompiledRoute Compile(RouteSpec route)
    {
        Validate(route).ThrowIfAny();
        return new CompiledRoute(route.Name.Trim(), route.ExcludeStarter,
            route.Steps.Select(s => new CompiledStep(s.Id, s.Name.Trim(), (s.Candidates.Users ?? []).Distinct().ToList(), s.Candidates.Permission, s.DueWorkingDays)).ToList());
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
            var candidates = new XElement(Tb + "candidates");
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
            Flow(previous, step.Id, previous == "start" ? null : TaskOutcomes.Approve);
            var gateway = "gw_" + step.Id;
            process.Add(new XElement(Bpmn + "exclusiveGateway", new XAttribute("id", gateway)));
            Flow(step.Id, gateway);
            Flow(gateway, "end_rejected", TaskOutcomes.Reject);
            Flow(gateway, "end_returned", TaskOutcomes.Return);
            previous = gateway;
        }
        Flow(previous, "end_approved", TaskOutcomes.Approve);
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
    /// Reads R1 route BPMN back into the executable graph by walking the approve branches from the start event. Anything
    /// outside the R1 subset is refused, so a definition never runs with elements the engine does not support.
    /// </summary>
    public static CompiledRoute FromBpmn(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var process = doc.Root?.Element(Bpmn + "process") ?? throw new FormatException("No BPMN process.");
        var supported = new HashSet<string> { "startEvent", "userTask", "exclusiveGateway", "endEvent", "sequenceFlow" };
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
        var steps = new List<CompiledStep>();
        var current = flows.Single(f => f.From == "start").To;
        var approve = $"=outcome = \"{TaskOutcomes.Approve}\"";
        while (current != "end_approved")
        {
            if (!tasks.TryGetValue(current, out var task) || steps.Count >= MaxSteps)
            {
                throw new FormatException($"Expected a user task at {current}.");
            }
            var extensions = task.Element(Bpmn + "extensionElements");
            var candidates = extensions?.Element(Tb + "candidates");
            var users = candidates?.Elements(Tb + "user").Select(u => Guid.Parse((string)u.Attribute("id")!)).ToList() ?? [];
            var permission = (string?)candidates?.Element(Tb + "permissionAtUnit")?.Attribute("key");
            var due = (int?)extensions?.Element(Tb + "dueDate")?.Attribute("workingDays");
            steps.Add(new CompiledStep(current, (string?)task.Attribute("name") ?? current, users, permission, due));
            var gateway = flows.Single(f => f.From == current).To;
            current = flows.Single(f => f.From == gateway && f.Condition == approve).To;
        }
        var excludeStarter = (string?)process.Attribute(Tb + "excludeStarter") != "false";
        return new CompiledRoute((string?)process.Attribute("name") ?? string.Empty, excludeStarter, steps);
    }
}
