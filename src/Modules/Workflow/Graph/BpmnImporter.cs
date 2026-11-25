using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TeamBeats.Modules.Workflow.Graph;

/// <summary>One line of an import report: the element it is about (empty for the whole file), a key and an optional detail such as an attribute name.</summary>
public sealed record ImportItem(string ElementId, string Key, string? Detail = null);

/// <summary>The model as TeamBeats BPMN, what was converted, and what TeamBeats cannot represent (an import goes ahead only when that is empty).</summary>
public sealed record ImportResult(string? Xml, IReadOnlyList<ImportItem> Converted, IReadOnlyList<ImportItem> Unsupported);

/// <summary>
/// Imports a BPMN 2.0 file exported from Camunda Modeler (Camunda 7 and 8) or another tool (spec 024 R3, US7). Standard
/// BPMN is kept as it is. Vendor extensions that have a TeamBeats meaning are converted to the <c>teambeats</c>
/// namespace: assignee, candidate users and groups, due dates in days, retries, called process, message correlation,
/// input and output mappings and multi-instance collections. Anything else of a vendor, and every element outside the
/// subset, is listed as unsupported. The result is then read by <see cref="BpmnReader"/>: its structural problems are
/// unsupported too, while ordinary validation problems (a step with nobody to do it, a service step without a connector)
/// stay with the draft for the designer to fix.
/// </summary>
public static partial class BpmnImporter
{
    public const string Camunda7Ns = "http://camunda.org/schema/1.0/bpmn";
    public const string ZeebeNs = "http://camunda.org/schema/zeebe/1.0";
    public const string ActivitiNs = "http://activiti.org/bpmn";
    public const string FlowableNs = "http://flowable.org/bpmn";
    private const string XsiNs = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly XNamespace Bpmn = BpmnReader.BpmnNs;
    private static readonly XNamespace Tb = BpmnReader.TeamBeatsNs;
    private static readonly XNamespace Camunda = Camunda7Ns;
    private static readonly XNamespace Zeebe = ZeebeNs;

    /// <summary>Namespaces whose attributes and elements describe how another engine runs the model.</summary>
    private static readonly HashSet<string> Vendors = new(StringComparer.Ordinal) { Camunda7Ns, ZeebeNs, ActivitiNs, FlowableNs };

    /// <summary>Vendor attributes that only tune the other engine (transactions, history, priorities): dropped with a note.</summary>
    private static readonly HashSet<string> Ignorable = new(StringComparer.Ordinal)
    {
        "asyncBefore", "asyncAfter", "exclusive", "jobPriority", "taskPriority", "versionTag", "historyTimeToLive", "isStartableInTasklist", "priority",
    };

    /// <summary>Problems of the reader that mean TeamBeats cannot represent the model, rather than that the draft is unfinished.</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "validation.process.xml", "validation.process.too_large", "validation.process.one_process", "validation.process.unsupported_element",
        "validation.process.too_many_elements",
    };

    public const string Prefix = "process.import.";

    [GeneratedRegex(@"^P(\d{1,3})D$")]
    private static partial Regex DaysPattern();

    [GeneratedRegex(@"^R(\d{1,2})/")]
    private static partial Regex RetryCyclePattern();

    [GeneratedRegex(@"^\$\{\s*([a-z][A-Za-z0-9_]{0,40})\s*\}$")]
    private static partial Regex JuelVariablePattern();

    public static ImportResult Import(string key, string xml)
    {
        var converted = new List<ImportItem>();
        var unsupported = new List<ImportItem>();
        if (xml.Length > BpmnReader.MaxBytes)
        {
            return new ImportResult(null, converted, [new ImportItem("", "validation.process.too_large")]);
        }
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = BpmnReader.MaxBytes };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            return new ImportResult(null, converted, [new ImportItem("", "validation.process.xml")]);
        }
        var root = doc.Root;
        if (root?.Name != Bpmn + "definitions")
        {
            return new ImportResult(null, converted, [new ImportItem("", "validation.process.xml")]);
        }
        foreach (var collaboration in root.Elements(Bpmn + "collaboration"))
        {
            // A single pool is just a frame around the process; several pools or message flows between them are not in the subset.
            if (collaboration.Elements(Bpmn + "participant").Count() > 1 || collaboration.Elements(Bpmn + "messageFlow").Any())
            {
                unsupported.Add(new ImportItem((string?)collaboration.Attribute("id") ?? "", Prefix + "collaboration"));
            }
            collaboration.Remove();
        }
        var processes = root.Elements(Bpmn + "process").ToList();
        if (processes.Count != 1)
        {
            return new ImportResult(null, converted, [.. unsupported, new ImportItem("", "validation.process.one_process")]);
        }
        var process = processes[0];

        // Message correlation lives on the message in Camunda 8; TeamBeats puts it on each element that waits for the message.
        var correlations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var message in root.Elements(Bpmn + "message"))
        {
            var subscription = message.Descendants(Zeebe + "subscription").FirstOrDefault();
            if (subscription?.Attribute("correlationKey") is { } correlation && (string?)message.Attribute("id") is { } id)
            {
                correlations[id] = Feel(correlation.Value);
            }
            message.Element(Bpmn + "extensionElements")?.Remove();
        }

        foreach (var element in process.DescendantsAndSelf().Where(e => e.Name.Namespace == Bpmn).ToList())
        {
            if (element.Parent is { } parent && parent.Name == Bpmn + "extensionElements")
            {
                continue;
            }
            Convert(element, correlations, converted, unsupported);
        }
        foreach (var attribute in root.Attributes().Where(a => !a.IsNamespaceDeclaration && Vendors.Contains(a.Name.NamespaceName)).ToList())
        {
            attribute.Remove();
        }
        foreach (var declaration in root.Descendants().Prepend(root).SelectMany(e => e.Attributes()).Where(a => a.IsNamespaceDeclaration && Vendors.Contains(a.Value)).ToList())
        {
            declaration.Remove();
        }
        if (root.GetPrefixOfNamespace(Tb) is null)
        {
            root.SetAttributeValue(XNamespace.Xmlns + "tb", BpmnReader.TeamBeatsNs);
        }

        var result = doc.Declaration is null ? doc.ToString(SaveOptions.DisableFormatting) : doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
        var (_, problems) = BpmnReader.Read(key, result);
        unsupported.AddRange(problems.Where(p => Structural.Contains(p.Key)).Select(p => new ImportItem(p.ElementId, p.Key)));
        return new ImportResult(result, converted, unsupported.DistinctBy(u => (u.ElementId, u.Key, u.Detail)).ToList());
    }

    /// <summary>A Camunda 8 expression (<c>=total</c>) or a Camunda 7 one (<c>${total}</c>) as FEEL text.</summary>
    private static string Feel(string value)
    {
        value = value.Trim();
        if (value.StartsWith('='))
        {
            return value[1..].Trim();
        }
        if (value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}'))
        {
            return value[2..^1].Trim().Replace("&&", " and ", StringComparison.Ordinal).Replace("||", " or ", StringComparison.Ordinal)
                .Replace("==", "=", StringComparison.Ordinal);
        }
        return value;
    }

    private static XElement Extensions(XElement element)
    {
        if (element.Element(Bpmn + "extensionElements") is { } existing)
        {
            return existing;
        }
        var created = new XElement(Bpmn + "extensionElements");
        // extensionElements comes first among the children of a BPMN element.
        var documentation = element.Elements(Bpmn + "documentation").LastOrDefault();
        if (documentation is not null)
        {
            documentation.AddAfterSelf(created);
        }
        else
        {
            element.AddFirst(created);
        }
        return created;
    }

    private static void Convert(XElement element, Dictionary<string, string> correlations, List<ImportItem> converted, List<ImportItem> unsupported)
    {
        var id = (string?)element.Attribute("id") ?? "";
        var local = element.Name.LocalName;
        var extensions = element.Element(Bpmn + "extensionElements");
        var vendorElements = extensions?.Elements().Where(e => e.Name.Namespace != Tb).ToList() ?? [];
        var vendorAttributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name.Namespace != XNamespace.None && a.Name.Namespace != XNamespace.Xml
            && a.Name.NamespaceName != XsiNs).ToList();
        var handled = new HashSet<object>();
        var assignment = new Dictionary<string, string>(StringComparer.Ordinal);

        string? Attr(XNamespace ns, string name)
        {
            var a = element.Attribute(ns + name);
            if (a is not null)
            {
                handled.Add(a);
            }
            return a?.Value;
        }
        XElement? Ext(XNamespace ns, string name)
        {
            var e = vendorElements.FirstOrDefault(x => x.Name == ns + name);
            if (e is not null)
            {
                handled.Add(e);
            }
            return e;
        }
        void People(string? list, string kind, bool allowExpression)
        {
            if (string.IsNullOrWhiteSpace(list))
            {
                return;
            }
            var value = list.Trim();
            if (allowExpression && (value.StartsWith('=') || JuelVariablePattern().IsMatch(value)))
            {
                assignment["assignee"] = value.StartsWith('=') ? Feel(value) : JuelVariablePattern().Match(value).Groups[1].Value;
                converted.Add(new ImportItem(id, Prefix + "assignee_expression"));
                return;
            }
            var ids = new List<Guid>();
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (Guid.TryParse(part, out var guid))
                {
                    ids.Add(guid);
                }
                else
                {
                    // A user name or group name of the other tool is not a TeamBeats ID; the designer chooses who instead.
                    unsupported.Add(new ImportItem(id, Prefix + (kind == "groups" ? "group_name" : "user_name"), part));
                }
            }
            if (ids.Count > 0)
            {
                assignment[kind] = string.Join(',', (assignment.TryGetValue(kind, out var known) ? known.Split(',').Select(Guid.Parse) : []).Concat(ids).Distinct());
                converted.Add(new ImportItem(id, Prefix + kind));
            }
        }

        if (local == "userTask")
        {
            var zeebe = Ext(Zeebe, "assignmentDefinition");
            People((string?)zeebe?.Attribute("assignee") ?? Attr(Camunda, "assignee"), "users", true);
            People((string?)zeebe?.Attribute("candidateUsers") ?? Attr(Camunda, "candidateUsers"), "users", false);
            People((string?)zeebe?.Attribute("candidateGroups") ?? Attr(Camunda, "candidateGroups"), "groups", false);
            Ext(Zeebe, "userTask");
            var due = (string?)Ext(Zeebe, "taskSchedule")?.Attribute("dueDate") ?? Attr(Camunda, "dueDate");
            if (due is not null)
            {
                var text = Feel(due).Trim('"');
                if (DaysPattern().Match(text) is { Success: true } days && int.Parse(days.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0)
                {
                    Extensions(element).Add(new XElement(Tb + "due", new XAttribute("workingDays", days.Groups[1].Value)));
                    converted.Add(new ImportItem(id, Prefix + "due_working_days", days.Groups[1].Value));
                }
                else
                {
                    unsupported.Add(new ImportItem(id, Prefix + "due_date", due));
                }
            }
            if (assignment.Count > 0 && extensions?.Element(Tb + "assignment") is null)
            {
                Extensions(element).Add(new XElement(Tb + "assignment", assignment.Select(a => new XAttribute(a.Key, a.Value))));
            }
        }

        if (local is "serviceTask" or "sendTask" or "businessRuleTask")
        {
            var definition = Ext(Zeebe, "taskDefinition");
            var retries = (string?)definition?.Attribute("retries");
            if (Ext(Camunda, "failedJobRetryTimeCycle") is { } cycle && RetryCyclePattern().Match(cycle.Value.Trim()) is { Success: true } match)
            {
                retries = match.Groups[1].Value;
            }
            foreach (var worker in new[] { Attr(Camunda, "class"), Attr(Camunda, "delegateExpression"), Attr(Camunda, "expression"), Attr(Camunda, "topic"),
                (string?)definition?.Attribute("type") })
            {
                if (worker is not null)
                {
                    // The other engine's worker has no TeamBeats meaning: the designer binds a connector or webhook instead.
                    converted.Add(new ImportItem(id, Prefix + "worker_dropped", worker));
                }
            }
            Attr(Camunda, "type");
            if (local == "serviceTask" && extensions?.Elements().Any(e => e.Name.Namespace == Tb && e.Name.LocalName is "connector" or "webhook" or "product") != true)
            {
                var connector = new XElement(Tb + "connector");
                if (retries is not null && int.TryParse(retries, out var count) && count is >= 0 and <= 10)
                {
                    connector.SetAttributeValue("retries", count);
                    converted.Add(new ImportItem(id, Prefix + "retries", retries));
                }
                Extensions(element).Add(connector);
            }
        }

        if (local == "callActivity" && Ext(Zeebe, "calledElement") is { } called && (string?)called.Attribute("processId") is { } processId)
        {
            element.SetAttributeValue("calledElement", Feel(processId));
            converted.Add(new ImportItem(id, Prefix + "called_element"));
        }

        if (Ext(Zeebe, "ioMapping") is { } io)
        {
            foreach (var input in io.Elements(Zeebe + "input"))
            {
                Extensions(element).Add(new XElement(Tb + "input", new XAttribute("name", (string?)input.Attribute("target") ?? ""), new XAttribute("source", Feel((string?)input.Attribute("source") ?? ""))));
            }
            foreach (var output in io.Elements(Zeebe + "output"))
            {
                Extensions(element).Add(new XElement(Tb + "output", new XAttribute("variable", (string?)output.Attribute("target") ?? ""), new XAttribute("source", Feel((string?)output.Attribute("source") ?? ""))));
            }
            converted.Add(new ImportItem(id, Prefix + "mappings"));
        }

        if (element.Element(Bpmn + "multiInstanceLoopCharacteristics") is { } loop)
        {
            var zeebeLoop = loop.Descendants(Zeebe + "loopCharacteristics").FirstOrDefault();
            var collection = (string?)zeebeLoop?.Attribute("inputCollection") ?? (string?)loop.Attribute(Camunda + "collection");
            var variable = (string?)zeebeLoop?.Attribute("inputElement") ?? (string?)loop.Attribute(Camunda + "elementVariable");
            if (zeebeLoop is not null || loop.Attribute(Camunda + "collection") is not null)
            {
                loop.Element(Bpmn + "extensionElements")?.Remove();
                loop.Attribute(Camunda + "collection")?.Remove();
                loop.Attribute(Camunda + "elementVariable")?.Remove();
                if (collection is not null && extensions?.Element(Tb + "loop") is null)
                {
                    Extensions(element).Add(new XElement(Tb + "loop", new XAttribute("collection", Feel(collection)), new XAttribute("elementVariable", variable ?? "item")));
                    converted.Add(new ImportItem(id, Prefix + "loop"));
                }
            }
        }

        var messageRef = (string?)element.Attribute("messageRef") ?? (string?)element.Elements(Bpmn + "messageEventDefinition").FirstOrDefault()?.Attribute("messageRef");
        if (messageRef is not null && correlations.TryGetValue(messageRef, out var correlationKey) && extensions?.Element(Tb + "correlation") is null
            && local is "receiveTask" or "intermediateCatchEvent" or "boundaryEvent" or "startEvent")
        {
            Extensions(element).Add(new XElement(Tb + "correlation", new XAttribute("key", correlationKey)));
            converted.Add(new ImportItem(id, Prefix + "correlation"));
        }

        if (local == "conditionExpression" && (element.Value.TrimStart().StartsWith('=') || element.Value.TrimStart().StartsWith("${", StringComparison.Ordinal)))
        {
            element.Value = Feel(element.Value);
            converted.Add(new ImportItem((string?)element.Parent?.Attribute("id") ?? "", Prefix + "condition"));
        }

        foreach (var attribute in vendorAttributes)
        {
            if (!handled.Contains(attribute))
            {
                if (Vendors.Contains(attribute.Name.NamespaceName) && Ignorable.Contains(attribute.Name.LocalName))
                {
                    converted.Add(new ImportItem(id, Prefix + "dropped", attribute.Name.LocalName));
                }
                else
                {
                    unsupported.Add(new ImportItem(id, Prefix + "vendor_attribute", attribute.Name.LocalName));
                }
            }
            attribute.Remove();
        }
        foreach (var vendor in vendorElements)
        {
            if (!handled.Contains(vendor))
            {
                // Forms, listeners, properties and anything else of the other tool: TeamBeats cannot run them.
                unsupported.Add(new ImportItem(id, Prefix + "vendor_extension", vendor.Name.LocalName));
            }
            vendor.Remove();
        }
        if (element.Element(Bpmn + "extensionElements") is { HasElements: false } empty)
        {
            empty.Remove();
        }
    }
}
