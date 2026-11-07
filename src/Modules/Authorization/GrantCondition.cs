using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Authorization;

/// <summary>
/// A condition on a role assignment or resource permission (spec 004 FR-014, FR-015). Conditions are data, parsed
/// strictly from JSON: a comparison is <c>{"attribute": "job_grade", "operator": "gte", "value": 7}</c>, and comparisons
/// combine with <c>{"all": [...]}</c> or <c>{"any": [...]}</c> to a depth of two. Anything else is rejected.
/// A condition only narrows its own grant. There is no negation of a group, and every comparison on an attribute the
/// user, resource or request does not have makes the whole condition false (fail closed).
/// </summary>
public abstract record GrantCondition
{
    public const int MaxDepth = 2;
    public const int MaxChildren = 20;
    public const int MaxListValues = 100;
    public const int MaxValueLength = 200;

    public static readonly IReadOnlyList<string> Operators = ["eq", "ne", "in", "not_in", "gte", "lte"];

    public abstract IEnumerable<Comparison> Comparisons();

    public abstract JsonNode ToJson();

    public string ToCanonicalJson() => ToJson().ToJsonString();

    /// <summary>Parses and validates a condition. Errors are resource keys under the <c>condition</c> field.</summary>
    public static GrantCondition? Parse(JsonElement? json, ValidationErrors errors, string field = "condition")
    {
        if (json is not { } element || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        var parsed = ParseNode(element, 0, errors, field);
        return errors.Any ? null : parsed;
    }

    /// <summary>Reads a condition stored by <see cref="ToCanonicalJson"/>. Stored conditions were validated on write.</summary>
    public static GrantCondition? FromStored(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var errors = new ValidationErrors();
        var parsed = ParseNode(doc.RootElement, 0, errors, "condition");
        // A stored condition that no longer validates (for example after a catalogue change) never grants.
        return errors.Any ? Never.Instance : parsed;
    }

    private static GrantCondition? ParseNode(JsonElement e, int depth, ValidationErrors errors, string field)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            errors.Add(field, "validation.condition.shape");
            return null;
        }
        var names = e.EnumerateObject().Select(p => p.Name).ToList();
        if (names.Count == 1 && names[0] is "all" or "any")
        {
            if (depth >= MaxDepth)
            {
                errors.Add(field, "validation.condition.too_deep");
                return null;
            }
            var list = e.GetProperty(names[0]);
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0 || list.GetArrayLength() > MaxChildren)
            {
                errors.Add(field, "validation.condition.group_size");
                return null;
            }
            var children = new List<GrantCondition>();
            foreach (var child in list.EnumerateArray())
            {
                if (ParseNode(child, depth + 1, errors, field) is { } c)
                {
                    children.Add(c);
                }
            }
            return new Group(names[0] == "all", children);
        }

        if (names.Count != 3 || !names.Contains("attribute") || !names.Contains("operator") || !names.Contains("value"))
        {
            errors.Add(field, "validation.condition.shape");
            return null;
        }
        var attrKey = e.GetProperty("attribute").ValueKind == JsonValueKind.String ? e.GetProperty("attribute").GetString()! : string.Empty;
        if (!ConditionAttributes.All.TryGetValue(attrKey, out var attribute))
        {
            errors.Add(field, "validation.condition.unknown_attribute");
            return null;
        }
        var op = e.GetProperty("operator").ValueKind == JsonValueKind.String ? e.GetProperty("operator").GetString()! : string.Empty;
        if (!Operators.Contains(op))
        {
            errors.Add(field, "validation.condition.unknown_operator");
            return null;
        }
        if (op is "gte" or "lte" && attribute.Kind != AttributeKind.Number)
        {
            errors.Add(field, "validation.condition.operator_not_allowed");
            return null;
        }
        if (op is "in" or "not_in" && attribute.Kind == AttributeKind.Flag)
        {
            errors.Add(field, "validation.condition.operator_not_allowed");
            return null;
        }

        var raw = e.GetProperty("value");
        var isList = op is "in" or "not_in";
        if (isList != (raw.ValueKind == JsonValueKind.Array))
        {
            errors.Add(field, "validation.condition.value_type");
            return null;
        }
        var rawValues = isList ? raw.EnumerateArray().ToList() : [raw];
        if (rawValues.Count == 0 || rawValues.Count > MaxListValues)
        {
            errors.Add(field, "validation.condition.value_type");
            return null;
        }
        var values = new List<string>();
        foreach (var v in rawValues)
        {
            if (NormalizeValue(attribute, v) is not { } normalized)
            {
                errors.Add(field, "validation.condition.value_type");
                return null;
            }
            values.Add(normalized);
        }
        return new Comparison(attribute, op, values.Distinct(StringComparer.Ordinal).ToList());
    }

    private static string? NormalizeValue(ConditionAttribute attribute, JsonElement v)
    {
        switch (attribute.Kind)
        {
            case AttributeKind.Number:
                return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d.ToString(CultureInfo.InvariantCulture) : null;
            case AttributeKind.Flag:
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? (v.GetBoolean() ? "true" : "false") : null;
            case AttributeKind.OrgUnit:
                return v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g.ToString() : null;
            case AttributeKind.Choice:
                var choice = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim().ToLowerInvariant() : null;
                return choice is not null && attribute.Choices!.Contains(choice) ? choice : null;
            default:
                var text = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
                return string.IsNullOrEmpty(text) || text.Length > MaxValueLength ? null : text;
        }
    }

    /// <summary>Evaluates the condition. <paramref name="lookup"/> returns null for an attribute that is absent.</summary>
    public ConditionTrace Evaluate(Func<string, string?> lookup)
    {
        var clauses = new List<ConditionClause>();
        var missing = new List<string>();
        foreach (var c in Comparisons())
        {
            var actual = lookup(c.Attribute.Key);
            if (string.IsNullOrEmpty(actual))
            {
                actual = null;
                if (!missing.Contains(c.Attribute.Key))
                {
                    missing.Add(c.Attribute.Key);
                }
            }
            clauses.Add(new ConditionClause(c.Attribute.Key, c.Operator, c.Values, actual, actual is not null && c.Holds(actual)));
        }
        var met = missing.Count == 0 && Holds(clauses);
        return new ConditionTrace(met, clauses, missing);
    }

    /// <summary>Whether the condition holds given the clause results, in <see cref="Comparisons"/> order.</summary>
    protected abstract bool HoldsFrom(IEnumerator<ConditionClause> clauses);

    private bool Holds(IReadOnlyList<ConditionClause> clauses)
    {
        using var e = clauses.GetEnumerator();
        return HoldsFrom(e);
    }

    public sealed record Comparison(ConditionAttribute Attribute, string Operator, IReadOnlyList<string> Values) : GrantCondition
    {
        public override IEnumerable<Comparison> Comparisons() => [this];

        protected override bool HoldsFrom(IEnumerator<ConditionClause> clauses) => clauses.MoveNext() && clauses.Current.Met;

        public bool Holds(string actual)
        {
            if (!Attribute.MultiValued)
            {
                return HoldsOne(actual);
            }
            var values = actual.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Operator is "ne" or "not_in" ? values.All(HoldsOne) : values.Any(HoldsOne);
        }

        private bool HoldsOne(string actual) => Operator switch
        {
            "eq" => Values.Any(v => Same(actual, v)),
            "ne" => !Values.Any(v => Same(actual, v)),
            "in" => Values.Any(v => Same(actual, v)),
            "not_in" => !Values.Any(v => Same(actual, v)),
            "gte" => Number(actual) is { } a && Number(Values[0]) is { } b && a >= b,
            "lte" => Number(actual) is { } a2 && Number(Values[0]) is { } b2 && a2 <= b2,
            _ => false,
        };

        private bool Same(string actual, string expected) => Attribute.Kind switch
        {
            AttributeKind.Number => Number(actual) is { } a && Number(expected) is { } b && a == b,
            AttributeKind.OrgUnit => Guid.TryParse(actual, out var a) && Guid.TryParse(expected, out var b) && a == b,
            _ => string.Equals(actual.Trim(), expected, StringComparison.OrdinalIgnoreCase),
        };

        private static decimal? Number(string s) =>
            decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

        public override JsonNode ToJson()
        {
            JsonNode Value(string v) => Attribute.Kind switch
            {
                AttributeKind.Number => JsonValue.Create(decimal.Parse(v, CultureInfo.InvariantCulture)),
                AttributeKind.Flag => JsonValue.Create(v == "true"),
                _ => JsonValue.Create(v),
            };
            return new JsonObject
            {
                ["attribute"] = Attribute.Key,
                ["operator"] = Operator,
                ["value"] = Operator is "in" or "not_in" ? new JsonArray(Values.Select(Value).ToArray()) : Value(Values[0]),
            };
        }
    }

    public sealed record Group(bool All, IReadOnlyList<GrantCondition> Children) : GrantCondition
    {
        public override IEnumerable<Comparison> Comparisons() => Children.SelectMany(c => c.Comparisons());

        protected override bool HoldsFrom(IEnumerator<ConditionClause> clauses)
        {
            // Consume every child so the enumerator stays aligned with Comparisons().
            var results = Children.Select(c => c.HoldsFrom(clauses)).ToList();
            return All ? results.All(r => r) : results.Any(r => r);
        }

        public override JsonNode ToJson() => new JsonObject { [All ? "all" : "any"] = new JsonArray(Children.Select(c => c.ToJson()).ToArray()) };
    }

    /// <summary>A stored condition that no longer validates. It never holds.</summary>
    public sealed record Never : GrantCondition
    {
        public static readonly Never Instance = new();

        public override IEnumerable<Comparison> Comparisons() => [];

        protected override bool HoldsFrom(IEnumerator<ConditionClause> clauses) => false;

        public override JsonNode ToJson() => new JsonObject { ["invalid"] = true };
    }
}
