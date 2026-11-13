using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using TeamBeats.Modules.Workflow.Feel;
using TeamBeats.Modules.Workflow.Graph;

namespace TeamBeats.Modules.Workflow.Decisions;

/// <summary>DMN hit policies of spec 024 FR-007.</summary>
public static class HitPolicies
{
    public const string Unique = "UNIQUE";
    public const string First = "FIRST";
    public const string Any = "ANY";
    public const string Priority = "PRIORITY";
    public const string Collect = "COLLECT";
    public const string RuleOrder = "RULE ORDER";
    public const string OutputOrder = "OUTPUT ORDER";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Unique, First, Any, Priority, Collect, RuleOrder, OutputOrder };
}

public sealed record DecisionInput(string Id, string Label, string Expression);

/// <summary>An output column. <see cref="Values"/> are its allowed values in priority order, for the priority and output order policies.</summary>
public sealed record DecisionOutput(string Id, string Name, IReadOnlyList<string>? Values);

public sealed record DecisionRule(string Id, IReadOnlyList<string> Inputs, IReadOnlyList<string> Outputs, string? Description);

/// <summary>A compiled decision table (DMN 1.3, decision tables only).</summary>
public sealed record DecisionTable(
    string Key,
    IReadOnlyDictionary<string, string> Names,
    string HitPolicy,
    string? Aggregation,
    IReadOnlyList<DecisionInput> Inputs,
    IReadOnlyList<DecisionOutput> Outputs,
    IReadOnlyList<DecisionRule> Rules)
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxRules = 1000;
    public const int MaxColumns = 30;

    private static readonly XNamespace[] Namespaces =
    [
        "https://www.omg.org/spec/DMN/20191111/MODEL/", "http://www.omg.org/spec/DMN/20180521/MODEL/", "https://www.omg.org/spec/DMN/20230324/MODEL/",
    ];

    public string ToJson() => JsonSerializer.Serialize(this, ProcessGraph.Json);

    public static DecisionTable FromJson(string json) => JsonSerializer.Deserialize<DecisionTable>(json, ProcessGraph.Json)
        ?? throw new InvalidOperationException("Compiled decision is empty.");

    /// <summary>Reads the one decision table of a DMN file, and lists its problems by element ID (FR-007).</summary>
    public static (DecisionTable? Table, IReadOnlyList<ModelProblem> Problems) Read(string key, string xml)
    {
        if (xml.Length > MaxBytes)
        {
            return (null, [new ModelProblem("", "validation.decision.too_large")]);
        }
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return (null, [new ModelProblem("", "validation.decision.xml")]);
        }
        var ns = Namespaces.FirstOrDefault(n => doc.Root?.Name == n + "definitions");
        if (ns is null)
        {
            return (null, [new ModelProblem("", "validation.decision.xml")]);
        }
        var decisions = doc.Root!.Elements(ns + "decision").ToList();
        if (decisions.Count != 1 || decisions[0].Element(ns + "decisionTable") is not { } table)
        {
            return (null, [new ModelProblem("", "validation.decision.one_table")]);
        }
        var problems = new List<ModelProblem>();
        var decision = decisions[0];
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if ((string?)decision.Attribute("name") is { Length: > 0 and <= 200 } name)
        {
            names["en"] = name;
        }
        foreach (var n in decision.Elements().Where(e => e.Name.LocalName == "name" && e.Name.NamespaceName == BpmnReader.TeamBeatsNs))
        {
            if (n.Attribute(XNamespace.Xml + "lang")?.Value is { Length: > 0 } lang && n.Value.Trim() is { Length: > 0 and <= 200 } localized)
            {
                names[lang] = localized;
            }
        }
        if (names.Count == 0)
        {
            problems.Add(new ModelProblem((string?)decision.Attribute("id") ?? "", "validation.decision.name"));
        }
        var policy = ((string?)table.Attribute("hitPolicy") ?? HitPolicies.Unique).Trim();
        var aggregation = ((string?)table.Attribute("aggregation"))?.Trim();
        var tableId = (string?)table.Attribute("id") ?? "";
        if (!HitPolicies.All.Contains(policy) || (aggregation is not null && (policy != HitPolicies.Collect || aggregation is not ("SUM" or "MIN" or "MAX" or "COUNT"))))
        {
            problems.Add(new ModelProblem(tableId, "validation.decision.hit_policy"));
        }
        var inputs = table.Elements(ns + "input").Select((i, n) => new DecisionInput((string?)i.Attribute("id") ?? $"input{n + 1}", (string?)i.Attribute("label") ?? "",
            i.Element(ns + "inputExpression")?.Element(ns + "text")?.Value.Trim() ?? "")).ToList();
        var outputs = table.Elements(ns + "output").Select((o, n) => new DecisionOutput((string?)o.Attribute("id") ?? $"output{n + 1}", (string?)o.Attribute("name") ?? "",
            o.Element(ns + "outputValues")?.Element(ns + "text")?.Value is { Length: > 0 } values ? SplitValues(values) : null)).ToList();
        if (inputs.Count is 0 or > MaxColumns || outputs.Count is 0 or > MaxColumns)
        {
            problems.Add(new ModelProblem(tableId, "validation.decision.columns"));
        }
        foreach (var input in inputs.Where(i => i.Expression.Length == 0 || FeelEngine.CheckExpression(i.Expression) is not null))
        {
            problems.Add(new ModelProblem(input.Id, "validation.decision.expression"));
        }
        if (outputs.Count > 1 && outputs.Any(o => !BpmnReader.IsVariable(o.Name)) || outputs.Select(o => o.Name).Distinct().Count() != outputs.Count)
        {
            problems.Add(new ModelProblem(tableId, "validation.decision.output_name"));
        }
        if (policy is HitPolicies.Priority or HitPolicies.OutputOrder && outputs.FirstOrDefault()?.Values is not { Count: > 0 })
        {
            problems.Add(new ModelProblem(tableId, "validation.decision.priority_values"));
        }
        if (aggregation is "SUM" or "MIN" or "MAX" && outputs.Count != 1)
        {
            problems.Add(new ModelProblem(tableId, "validation.decision.hit_policy"));
        }
        var rules = new List<DecisionRule>();
        foreach (var (rule, n) in table.Elements(ns + "rule").Select((r, n) => (r, n)))
        {
            var id = (string?)rule.Attribute("id") ?? $"rule{n + 1}";
            var ins = rule.Elements(ns + "inputEntry").Select(e => e.Element(ns + "text")?.Value.Trim() is { Length: > 0 } t ? t : "-").ToList();
            var outs = rule.Elements(ns + "outputEntry").Select(e => e.Element(ns + "text")?.Value.Trim() ?? "").ToList();
            if (ins.Count != inputs.Count || outs.Count != outputs.Count)
            {
                problems.Add(new ModelProblem(id, "validation.decision.rule_columns"));
                continue;
            }
            if (ins.Any(t => FeelEngine.CheckUnaryTests(t) is not null) || outs.Any(t => t.Length == 0 || FeelEngine.CheckExpression(t) is not null))
            {
                problems.Add(new ModelProblem(id, "validation.decision.expression"));
            }
            rules.Add(new DecisionRule(id, ins, outs, rule.Element(ns + "description")?.Value.Trim()));
        }
        if (rules.Count > MaxRules)
        {
            problems.Add(new ModelProblem(tableId, "validation.decision.too_many_rules"));
        }
        var compiled = new DecisionTable(key, names, policy, aggregation, inputs, outputs, rules);
        if (problems.Count == 0 && policy == HitPolicies.Unique)
        {
            problems.AddRange(Overlaps(compiled));
        }
        return problems.Count > 0 ? (null, problems.DistinctBy(p => (p.ElementId, p.Key)).ToList()) : (compiled, problems);
    }

    /// <summary>Output values in priority order, each normalized to its JSON form so results compare with them.</summary>
    private static List<string> SplitValues(string text) => SplitRaw(text).Select(Normalize).ToList();

    private static string Normalize(string literal)
    {
        try
        {
            return FeelEngine.Evaluate(literal, new Dictionary<string, object?>(), DateTimeOffset.UnixEpoch) is { } value ? ValueKey(value) : literal;
        }
        catch (Exception ex) when (ex is FeelEvaluationException or FeelSyntaxException)
        {
            return literal;
        }
    }

    private static List<string> SplitRaw(string text)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
            }
            if (ch == ',' && !quoted)
            {
                values.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.ToString().Trim() is { Length: > 0 } last)
        {
            values.Add(last);
        }
        return values;
    }

    // ---- Overlaps under UNIQUE ----

    /// <summary>A simple cell as a set: any value, a list of literals, or an interval. Anything else is not compared.</summary>
    private sealed record Cell(bool Any, IReadOnlyList<string>? Literals, decimal? Low, bool LowOpen, decimal? High, bool HighOpen);

    private static Cell? Simplify(string text)
    {
        text = text.Trim();
        if (text == "-")
        {
            return new Cell(true, null, null, false, null, false);
        }
        var ops = new[] { ">=", "<=", ">", "<" };
        foreach (var op in ops)
        {
            if (text.StartsWith(op, StringComparison.Ordinal) && decimal.TryParse(text[op.Length..].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
            {
                return op switch
                {
                    ">=" => new Cell(false, null, n, false, null, false),
                    ">" => new Cell(false, null, n, true, null, false),
                    "<=" => new Cell(false, null, null, false, n, false),
                    _ => new Cell(false, null, null, false, n, true),
                };
            }
        }
        if (text.Length > 4 && (text[0] is '[' or '(' or ']') && (text[^1] is ']' or ')' or '[') && text.Contains(".."))
        {
            var parts = text[1..^1].Split("..");
            if (parts.Length == 2 && decimal.TryParse(parts[0].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var lo)
                && decimal.TryParse(parts[1].Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var hi))
            {
                return new Cell(false, null, lo, text[0] != '[', hi, text[^1] != ']');
            }
        }
        var literals = new List<string>();
        foreach (var part in SplitRaw(text))
        {
            if (part.StartsWith('"') && part.EndsWith('"') || decimal.TryParse(part, NumberStyles.Number, CultureInfo.InvariantCulture, out _) || part is "true" or "false")
            {
                literals.Add(part);
            }
            else
            {
                return null;
            }
        }
        return literals.Count > 0 ? new Cell(false, literals, null, false, null, false) : null;
    }

    private static bool Meet(Cell a, Cell b)
    {
        if (a.Any || b.Any)
        {
            return true;
        }
        if (a.Literals is { } la && b.Literals is { } lb)
        {
            return la.Intersect(lb).Any();
        }
        if (a.Literals is { } lits && b.Literals is null)
        {
            return lits.Any(l => decimal.TryParse(l, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && Contains(b, v));
        }
        if (b.Literals is not null)
        {
            return Meet(b, a);
        }
        var low = Max(a.Low, a.LowOpen, b.Low, b.LowOpen);
        var high = Min(a.High, a.HighOpen, b.High, b.HighOpen);
        return low.Value is null || high.Value is null || low.Value < high.Value || (low.Value == high.Value && !low.Open && !high.Open);
    }

    private static bool Contains(Cell c, decimal v) =>
        (c.Low is null || v > c.Low || (v == c.Low && !c.LowOpen)) && (c.High is null || v < c.High || (v == c.High && !c.HighOpen));

    private static (decimal? Value, bool Open) Max(decimal? a, bool ao, decimal? b, bool bo) =>
        a is null ? (b, bo) : b is null ? (a, ao) : a > b ? (a, ao) : b > a ? (b, bo) : (a, ao || bo);

    private static (decimal? Value, bool Open) Min(decimal? a, bool ao, decimal? b, bool bo) =>
        a is null ? (b, bo) : b is null ? (a, ao) : a < b ? (a, ao) : b < a ? (b, bo) : (a, ao || bo);

    /// <summary>Pairs of rules that can both match under UNIQUE, found where every cell is simple; complex cells are not compared.</summary>
    private static IEnumerable<ModelProblem> Overlaps(DecisionTable table)
    {
        var cells = table.Rules.Select(r => r.Inputs.Select(Simplify).ToList()).ToList();
        for (var i = 0; i < table.Rules.Count; i++)
        {
            for (var j = i + 1; j < table.Rules.Count; j++)
            {
                var a = cells[i];
                var b = cells[j];
                if (a.All(c => c is not null) && b.All(c => c is not null) && a.Zip(b).All(p => Meet(p.First!, p.Second!)))
                {
                    yield return new ModelProblem(table.Rules[j].Id, "validation.decision.overlap");
                }
            }
        }
    }

    // ---- Evaluation ----

    private static string ValueKey(object? value) => FeelEngine.ToJson(value)?.ToJsonString() ?? "null";

    /// <summary>
    /// Evaluates the table for the variables: inputs are FEEL expressions, cells are unary tests, outputs FEEL. Returns
    /// the result (a value for one output, an object for several, a list for collect policies) and the matched rule
    /// numbers, from 1. Throws <see cref="DecisionException"/> when UNIQUE or ANY is broken.
    /// </summary>
    public (JsonNode? Result, IReadOnlyList<int> Rules) Evaluate(IReadOnlyDictionary<string, object?> variables, DateTimeOffset now)
    {
        var values = Inputs.Select(i => FeelEngine.Evaluate(i.Expression, variables, now)).ToList();
        var matched = new List<int>();
        for (var r = 0; r < Rules.Count; r++)
        {
            var rule = Rules[r];
            var hit = true;
            for (var c = 0; c < values.Count && hit; c++)
            {
                hit = rule.Inputs[c] == "-" || FeelEngine.Test(rule.Inputs[c], values[c], variables, now);
            }
            if (hit)
            {
                matched.Add(r);
                if (HitPolicy == HitPolicies.First)
                {
                    break;
                }
            }
        }
        JsonNode? Output(int rule)
        {
            if (Outputs.Count == 1)
            {
                return FeelEngine.ToJson(FeelEngine.Evaluate(Rules[rule].Outputs[0], variables, now));
            }
            var row = new JsonObject();
            for (var o = 0; o < Outputs.Count; o++)
            {
                row[Outputs[o].Name] = FeelEngine.ToJson(FeelEngine.Evaluate(Rules[rule].Outputs[o], variables, now));
            }
            return row;
        }
        int Rank(int rule)
        {
            var first = Outputs[0].Values!;
            var key = ValueKey(FeelEngine.Evaluate(Rules[rule].Outputs[0], variables, now));
            var at = first.ToList().IndexOf(key);
            return at < 0 ? int.MaxValue : at;
        }
        var numbers = matched.Select(r => r + 1).ToList();
        switch (HitPolicy)
        {
            case HitPolicies.Unique:
                if (matched.Count > 1)
                {
                    throw new DecisionException("unique_violated");
                }
                return (matched.Count == 0 ? null : Output(matched[0]), numbers);
            case HitPolicies.First:
                return (matched.Count == 0 ? null : Output(matched[0]), numbers);
            case HitPolicies.Any:
            {
                var outputs = matched.Select(Output).ToList();
                if (outputs.Select(o => o?.ToJsonString()).Distinct().Count() > 1)
                {
                    throw new DecisionException("any_conflict");
                }
                return (outputs.FirstOrDefault(), numbers);
            }
            case HitPolicies.Priority:
            {
                if (matched.Count == 0)
                {
                    return (null, numbers);
                }
                var best = matched.OrderBy(Rank).ThenBy(r => r).First();
                return (Output(best), [best + 1]);
            }
            case HitPolicies.OutputOrder:
            {
                var ordered = matched.OrderBy(Rank).ThenBy(r => r).ToList();
                return (new JsonArray(ordered.Select(Output).ToArray()), ordered.Select(r => r + 1).ToList());
            }
            case HitPolicies.RuleOrder:
                return (new JsonArray(matched.Select(Output).ToArray()), numbers);
            default:
            {
                var outputs = matched.Select(Output).ToList();
                if (Aggregation is null)
                {
                    return (new JsonArray(outputs.ToArray()), numbers);
                }
                if (Aggregation == "COUNT")
                {
                    return (JsonValue.Create(outputs.Count), numbers);
                }
                var nums = outputs.Select(o => o is JsonValue v && v.TryGetValue<decimal>(out var d) ? (decimal?)d : null).ToList();
                if (nums.Any(n => n is null))
                {
                    throw new DecisionException("aggregation_not_number");
                }
                if (nums.Count == 0)
                {
                    return (null, numbers);
                }
                var result = Aggregation switch
                {
                    "SUM" => nums.Sum(n => n!.Value),
                    "MIN" => nums.Min(n => n!.Value),
                    _ => nums.Max(n => n!.Value),
                };
                return (JsonValue.Create(result), numbers);
            }
        }
    }
}
