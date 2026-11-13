using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TeamBeats.Modules.Workflow.Feel;

namespace TeamBeats.Modules.Workflow.Graph;

/// <summary>A problem in a process model: the element (or flow) it is about, and a resource key under <c>validation.process</c>.</summary>
public sealed record ModelProblem(string ElementId, string Key);

/// <summary>
/// Reads a BPMN 2.0 model with TeamBeats extension elements (spec 024 FR-001) into the executable graph, keeping only the
/// R2 subset of section 12, and validates it (FR-002). XML is parsed with DTDs prohibited and a size limit (ADR-0017).
/// Extension elements, in the <c>teambeats</c> namespace inside <c>bpmn:extensionElements</c>:
/// <c>tb:name xml:lang</c>; <c>tb:assignment users groups permission lineManager managerLevel assignee fallbackUsers</c>;
/// <c>tb:due workingDays</c>; <c>tb:escalation afterWorkingDays users groups</c>; <c>tb:connector connection action
/// retries</c>, <c>tb:webhook id retries</c> or <c>tb:product action</c> with <c>tb:input name source</c> and
/// <c>tb:output variable source</c>; <c>tb:decision key resultVariable</c>; <c>tb:correlation key</c>; <c>tb:loop
/// collection elementVariable</c>; <c>tb:timer workingDays</c>; <c>tb:result value</c> on end events.
/// </summary>
public static partial class BpmnReader
{
    public const string BpmnNs = RouteCompiler.BpmnNs;
    public const string TeamBeatsNs = RouteCompiler.TeamBeatsNs;
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxElements = 500;
    public const int MaxNameLength = 200;
    public static readonly TimeSpan MinCycle = TimeSpan.FromHours(1);

    private static readonly XNamespace Bpmn = BpmnNs;
    private static readonly XNamespace Tb = TeamBeatsNs;
    private static readonly XNamespace XmlNs = XNamespace.Xml;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex ElementIdPattern();

    [GeneratedRegex("^[a-z][a-zA-Z0-9_]{0,40}$")]
    private static partial Regex VariablePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9_.:-]{0,99}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,62}$")]
    private static partial Regex CodePattern();

    /// <summary>BPMN element names this reader knows but that are outside the subset; they are reported, never dropped silently.</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "sequenceFlow", "extensionElements", "documentation", "incoming", "outgoing", "laneSet", "multiInstanceLoopCharacteristics",
        "timerEventDefinition", "messageEventDefinition", "errorEventDefinition", "escalationEventDefinition", "terminateEventDefinition",
        "conditionExpression", "dataInputAssociation", "dataOutputAssociation", "ioSpecification", "property",
    };

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Parses and compiles the model. On problems the graph is null.</summary>
    public static (ProcessGraph? Graph, IReadOnlyList<ModelProblem> Problems) Read(string key, string xml)
    {
        var problems = new List<ModelProblem>();
        if (xml.Length > MaxBytes)
        {
            return (null, [new ModelProblem("", "validation.process.too_large")]);
        }
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return (null, [new ModelProblem("", "validation.process.xml")]);
        }
        var root = doc.Root;
        var processes = root?.Name == Bpmn + "definitions" ? root.Elements(Bpmn + "process").ToList() : [];
        if (processes.Count != 1)
        {
            return (null, [new ModelProblem("", "validation.process.one_process")]);
        }
        var process = processes[0];
        var messages = root!.Elements(Bpmn + "message").Where(m => m.Attribute("id") is not null)
            .ToDictionary(m => (string)m.Attribute("id")!, m => (string?)m.Attribute("name") ?? (string)m.Attribute("id")!, StringComparer.Ordinal);
        var errors = root.Elements(Bpmn + "error").Where(m => m.Attribute("id") is not null)
            .ToDictionary(m => (string)m.Attribute("id")!, m => (string?)m.Attribute("errorCode"), StringComparer.Ordinal);
        var escalations = root.Elements(Bpmn + "escalation").Where(m => m.Attribute("id") is not null)
            .ToDictionary(m => (string)m.Attribute("id")!, m => (string?)m.Attribute("escalationCode"), StringComparer.Ordinal);

        var elements = new List<GraphElement>();
        var flows = new List<GraphFlow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Collect(process, null, elements, flows, ids, messages, errors, escalations, problems);
        if (elements.Count > MaxElements)
        {
            problems.Add(new ModelProblem("", "validation.process.too_many_elements"));
        }
        var names = Names(process, problems, process.Attribute("id")?.Value ?? "");
        // Incoming and outgoing come from the flows, not from the optional bpmn:incoming and bpmn:outgoing children.
        var linked = elements.Select(e => e with
        {
            Incoming = flows.Where(f => f.Target == e.Id).Select(f => f.Id).ToList(),
            Outgoing = flows.Where(f => f.Source == e.Id).Select(f => f.Id).ToList(),
        }).ToList();
        var graph = new ProcessGraph(key, names, linked, flows);
        problems.AddRange(ProcessValidator.Validate(graph));
        return problems.Count > 0 ? (null, problems.DistinctBy(p => (p.ElementId, p.Key)).ToList()) : (graph, problems);
    }

    private static IReadOnlyDictionary<string, string> Names(XElement element, List<ModelProblem> problems, string id)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Extensions(element, "name"))
        {
            var locale = (string?)name.Attribute(XmlNs + "lang");
            if (locale is "en" or "ar" && !string.IsNullOrWhiteSpace(name.Value))
            {
                names[locale] = name.Value.Trim();
            }
        }
        if (!names.ContainsKey("en") && (string?)element.Attribute("name") is { Length: > 0 } plain)
        {
            names["en"] = plain.Trim();
        }
        if (names.Values.Any(n => n.Length > MaxNameLength))
        {
            problems.Add(new ModelProblem(id, "validation.process.name"));
        }
        return names;
    }

    private static IEnumerable<XElement> Extensions(XElement element, string name) =>
        element.Elements(Bpmn + "extensionElements").Elements(Tb + name);

    private static XElement? Extension(XElement element, string name) => Extensions(element, name).FirstOrDefault();

    private static void Collect(XElement container, string? scope, List<GraphElement> elements, List<GraphFlow> flows, HashSet<string> ids,
        IReadOnlyDictionary<string, string> messages, IReadOnlyDictionary<string, string?> errors, IReadOnlyDictionary<string, string?> escalations,
        List<ModelProblem> problems)
    {
        foreach (var child in container.Elements())
        {
            if (child.Name.Namespace != Bpmn)
            {
                continue;
            }
            var local = child.Name.LocalName;
            var id = (string?)child.Attribute("id") ?? "";
            if (local == "sequenceFlow")
            {
                if (!ElementIdPattern().IsMatch(id) || !ids.Add(id))
                {
                    problems.Add(new ModelProblem(id, "validation.process.element_id"));
                    continue;
                }
                var condition = child.Element(Bpmn + "conditionExpression")?.Value.Trim();
                flows.Add(new GraphFlow(id, (string?)child.Attribute("sourceRef") ?? "", (string?)child.Attribute("targetRef") ?? "",
                    string.IsNullOrEmpty(condition) ? null : condition));
                continue;
            }
            if (Structural.Contains(local))
            {
                continue;
            }
            if (!ElementTypes.All.Contains(local))
            {
                problems.Add(new ModelProblem(id, "validation.process.unsupported_element"));
                continue;
            }
            if (!ElementIdPattern().IsMatch(id) || !ids.Add(id))
            {
                problems.Add(new ModelProblem(id, "validation.process.element_id"));
                continue;
            }
            elements.Add(Element(child, local, id, scope, messages, errors, escalations, problems));
            if (local == ElementTypes.SubProcess)
            {
                Collect(child, id, elements, flows, ids, messages, errors, escalations, problems);
            }
        }
    }

    private static GraphElement Element(XElement x, string type, string id, string? scope, IReadOnlyDictionary<string, string> messages,
        IReadOnlyDictionary<string, string?> errors, IReadOnlyDictionary<string, string?> escalations, List<ModelProblem> problems)
    {
        var element = new GraphElement(id, type, Names(x, problems, id), scope, [], []);
        string? Attr(XElement? e, string name) => (string?)e?.Attribute(name) is { Length: > 0 } v ? v.Trim() : null;

        // Event definitions.
        if (x.Element(Bpmn + "timerEventDefinition") is { } timer)
        {
            var days = Attr(Extension(x, "timer"), "workingDays");
            element = element with
            {
                EventKind = EventKinds.Timer,
                Timer = new TimerSpec(timer.Element(Bpmn + "timeDate")?.Value.Trim(), timer.Element(Bpmn + "timeDuration")?.Value.Trim(),
                    timer.Element(Bpmn + "timeCycle")?.Value.Trim(),
                    days is null ? null : int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : -1),
            };
        }
        else if (x.Element(Bpmn + "messageEventDefinition") is { } message)
        {
            element = element with { EventKind = EventKinds.Message, MessageName = Lookup(messages, Attr(message, "messageRef")) };
        }
        else if (x.Element(Bpmn + "errorEventDefinition") is { } error)
        {
            element = element with { EventKind = EventKinds.Error, Code = Attr(error, "errorRef") is { } r ? errors.GetValueOrDefault(r) ?? "" : null };
        }
        else if (x.Element(Bpmn + "escalationEventDefinition") is { } escalation)
        {
            element = element with { EventKind = EventKinds.Escalation, Code = Attr(escalation, "escalationRef") is { } r ? escalations.GetValueOrDefault(r) ?? "" : null };
        }
        else if (x.Element(Bpmn + "terminateEventDefinition") is not null)
        {
            element = element with { EventKind = EventKinds.Terminate };
        }
        if (type is ElementTypes.ReceiveTask or ElementTypes.SendTask)
        {
            element = element with { MessageName = Lookup(messages, Attr(x, "messageRef")) };
        }
        if (Extension(x, "correlation") is { } correlation)
        {
            element = element with { CorrelationKey = Attr(correlation, "key") };
        }

        switch (type)
        {
            case ElementTypes.BoundaryEvent:
                element = element with { AttachedTo = Attr(x, "attachedToRef"), Interrupting = Attr(x, "cancelActivity") != "false" };
                break;
            case ElementTypes.StartEvent:
                element = element with { Interrupting = Attr(x, "isInterrupting") != "false" };
                break;
            case ElementTypes.ExclusiveGateway:
            case ElementTypes.InclusiveGateway:
                element = element with { DefaultFlow = Attr(x, "default") };
                break;
            case ElementTypes.UserTask:
            {
                var a = Extension(x, "assignment");
                var level = Attr(a, "managerLevel");
                element = element with
                {
                    Assignment = new Assignment(Guids(Attr(a, "users"), id, problems), Attr(a, "permission"), Attr(a, "lineManager") == "true",
                        level is null ? null : int.TryParse(level, NumberStyles.None, CultureInfo.InvariantCulture, out var l) ? l : -1,
                        Attr(a, "assignee"), Guids(Attr(a, "fallbackUsers"), id, problems), Guids(Attr(a, "groups"), id, problems)),
                    Escalation = Extension(x, "escalation") is { } esc
                        ? new TaskEscalation(int.TryParse(Attr(esc, "afterWorkingDays"), NumberStyles.None, CultureInfo.InvariantCulture, out var ed) ? ed : -1,
                            Guids(Attr(esc, "users"), id, problems) ?? [], Guids(Attr(esc, "groups"), id, problems) ?? [])
                        : null,
                    DueWorkingDays = Attr(Extension(x, "due"), "workingDays") is { } due ? int.TryParse(due, NumberStyles.None, CultureInfo.InvariantCulture, out var dd) ? dd : -1 : null,
                    Outputs = Mappings(x, "output", "variable"),
                };
                break;
            }
            case ElementTypes.ServiceTask:
            {
                var connector = Extension(x, "connector");
                var webhook = Extension(x, "webhook");
                var product = Extension(x, "product");
                var retries = Attr(connector ?? webhook, "retries");
                Guid? connection = Attr(connector, "connection") is { } c ? Guid.TryParse(c, out var g) ? g : Guid.Empty : null;
                Guid? hook = Attr(webhook, "id") is { } w ? Guid.TryParse(w, out var h) ? h : Guid.Empty : null;
                element = element with
                {
                    Call = connector is null && webhook is null && product is null ? null : new ServiceCall(connection, Attr(connector, "action"), hook,
                        Mappings(x, "input", "name"), Mappings(x, "output", "variable"),
                        retries is null ? 3 : int.TryParse(retries, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1,
                        product is null ? null : Attr(product, "action") ?? ""),
                };
                break;
            }
            case ElementTypes.BusinessRuleTask:
            {
                var decision = Extension(x, "decision");
                element = element with { DecisionKey = Attr(decision, "key"), ResultVariable = Attr(decision, "resultVariable") };
                break;
            }
            case ElementTypes.EndEvent:
                element = element with { Result = Attr(Extension(x, "result"), "value") };
                break;
            case ElementTypes.CallActivity:
                element = element with { CalledKey = Attr(x, "calledElement"), Outputs = Mappings(x, "output", "variable") };
                break;
            case ElementTypes.SubProcess:
                element = element with { TriggeredByEvent = Attr(x, "triggeredByEvent") == "true" };
                break;
        }
        if (x.Element(Bpmn + "multiInstanceLoopCharacteristics") is { } loop)
        {
            var settings = Extension(x, "loop");
            element = element with
            {
                Loop = new MultiInstance(Attr(loop, "isSequential") == "true", Attr(settings, "collection") ?? "", Attr(settings, "elementVariable") ?? "",
                    loop.Element(Bpmn + "completionCondition")?.Value.Trim() is { Length: > 0 } cc ? cc : null),
            };
        }
        return element;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> messages, string? reference) =>
        reference is null ? null : messages.GetValueOrDefault(reference) ?? "";

    private static IReadOnlyList<Guid>? Guids(string? list, string id, List<ModelProblem> problems)
    {
        if (list is null)
        {
            return null;
        }
        var result = new List<Guid>();
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var g))
            {
                problems.Add(new ModelProblem(id, "validation.process.assignment"));
                continue;
            }
            result.Add(g);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> Mappings(XElement x, string name, string keyAttribute)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in Extensions(x, name))
        {
            if ((string?)m.Attribute(keyAttribute) is { Length: > 0 } k && (string?)m.Attribute("source") is { Length: > 0 } source)
            {
                map[k.Trim()] = source.Trim();
            }
        }
        return map;
    }

    internal static bool IsVariable(string? name) => name is not null && VariablePattern().IsMatch(name);

    internal static bool IsCode(string? code) => code is not null && CodePattern().IsMatch(code);

    /// <summary>Checks a timer: exactly one of date, duration, cycle or working days, each well formed; a cycle repeats at most hourly.</summary>
    internal static string? CheckTimer(TimerSpec? timer)
    {
        if (timer is null)
        {
            return "validation.process.timer";
        }
        var set = new object?[] { timer.Date, timer.Duration, timer.Cycle, timer.WorkingDays }.Count(v => v is not null);
        if (set != 1)
        {
            return "validation.process.timer";
        }
        if (timer.WorkingDays is { } days)
        {
            return days is >= 1 and <= 365 ? null : "validation.process.timer";
        }
        if (timer.Date is { } date)
        {
            return DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _) || FeelEngine.CheckExpression(date) is null
                ? null : "validation.process.timer";
        }
        if (timer.Duration is { } duration)
        {
            return FeelEngine.ParseDuration(duration) is { } d && Positive(d) || (duration.Contains('(') && FeelEngine.CheckExpression(duration) is null)
                ? null : "validation.process.timer";
        }
        var cycle = ParseCycle(timer.Cycle!);
        if (cycle is null)
        {
            return "validation.process.timer";
        }
        return cycle.Value.Every.Months == 0 && cycle.Value.Every.Time < MinCycle ? "validation.process.timer_cycle" : null;
    }

    private static bool Positive(FeelDuration d) => d.Months > 0 || d.Time > TimeSpan.Zero;

    /// <summary>An ISO 8601 repeating interval <c>R[n]/duration</c>: how many times (null for unbounded) and how often.</summary>
    public static (int? Times, FeelDuration Every)? ParseCycle(string cycle)
    {
        var parts = cycle.Split('/');
        if (parts.Length != 2 || !parts[0].StartsWith('R'))
        {
            return null;
        }
        int? times = null;
        if (parts[0].Length > 1)
        {
            if (!int.TryParse(parts[0][1..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1)
            {
                return null;
            }
            times = n;
        }
        return FeelEngine.ParseDuration(parts[1]) is { } every && Positive(every) ? (times, every) : null;
    }
}
