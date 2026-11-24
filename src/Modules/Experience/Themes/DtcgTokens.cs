using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TeamBeats.Modules.Experience.Themes;

/// <summary>
/// Portable tokens (spec 033 R2 T022, FR-014, US8 scenario 2): a brand's tokens as a DTCG 2025.10 token file and back.
/// Token names become nested groups (<c>color.surface.base</c> is the token <c>base</c> in the group <c>surface</c> of the
/// group <c>color</c>), values take the format's typed forms (color objects in sRGB, dimension and duration objects, cubic
/// Bézier arrays, shadow objects) and an alias stays the format's own <c>{group.token}</c> reference. The format has no
/// modes of its own, so the documented mapping keeps the dark, high contrast and compact values under
/// <c>$extensions["com.teambeats"].modes</c>, and a design choice (a keyword from a fixed list) is marked there as
/// <c>type: "choice"</c>. Pure: no I/O, so the round trip is unit tested.
/// </summary>
public static partial class DtcgTokens
{
    public const string Extension = "com.teambeats";
    public const string Schema = "https://www.designtokens.org/schemas/2025.10/format.json";

    private static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>
    {
        [TokenTypes.Color] = "color",
        [TokenTypes.Dimension] = "dimension",
        [TokenTypes.FontFamily] = "fontFamily",
        [TokenTypes.FontWeight] = "fontWeight",
        [TokenTypes.Number] = "number",
        [TokenTypes.Duration] = "duration",
        [TokenTypes.Easing] = "cubicBezier",
        [TokenTypes.Shadow] = "shadow",
    };

    /// <summary>The cubic Bézier points of each easing keyword; <c>linear</c> is the straight curve.</summary>
    private static readonly IReadOnlyDictionary<string, double[]> Curves = TokenSchema.Easings.ToDictionary(e => e.Key, e => e.Value == "linear"
        ? [0d, 0d, 1d, 1d]
        : e.Value["cubic-bezier(".Length..^1].Split(',').Select(p => double.Parse(p.Trim(), CultureInfo.InvariantCulture)).ToArray());

    /// <summary>The token file for a set of token values. Tokens are written in schema order, so the same brand gives the same file.</summary>
    public static JsonObject Export(IReadOnlyDictionary<string, TokenValue> tokens, string brandName)
    {
        var root = new JsonObject
        {
            ["$schema"] = Schema,
            ["$description"] = brandName,
            ["$extensions"] = new JsonObject { [Extension] = new JsonObject { ["format"] = "teambeats-tokens/1" } },
        };
        foreach (var definition in TokenSchema.All)
        {
            if (!tokens.TryGetValue(definition.Name, out var value) || value.Value is null)
            {
                continue;
            }
            var token = new JsonObject();
            if (Types.TryGetValue(definition.Type, out var type))
            {
                token["$type"] = type;
            }
            token["$value"] = ExportValue(definition, value.Value);
            var extension = new JsonObject();
            if (definition.Type == TokenTypes.Choice)
            {
                extension["type"] = "choice";
            }
            if (value.Modes is { Count: > 0 } modes)
            {
                var exported = new JsonObject();
                foreach (var mode in modes.Keys.Order(StringComparer.Ordinal))
                {
                    exported[mode] = ExportValue(definition, modes[mode]);
                }
                extension["modes"] = exported;
            }
            if (extension.Count > 0)
            {
                token["$extensions"] = new JsonObject { [Extension] = extension };
            }
            Group(root, definition.Name)[Leaf(definition.Name)] = token;
        }
        return root;
    }

    /// <summary>
    /// Reads a token file back into token values. Returns the tokens and the problems found, as (token path, resource key);
    /// the caller validates the values against the schema as for any brand edit.
    /// </summary>
    public static (Dictionary<string, TokenValue> Tokens, List<(string Path, string Key)> Problems) Import(JsonNode? file)
    {
        var tokens = new Dictionary<string, TokenValue>(StringComparer.Ordinal);
        var problems = new List<(string, string)>();
        if (file is not JsonObject root)
        {
            problems.Add(("file", "validation.theme.dtcg_invalid"));
            return (tokens, problems);
        }
        Walk(root, "", null, tokens, problems);
        return (tokens, problems);
    }

    private static void Walk(JsonObject group, string path, string? inheritedType, Dictionary<string, TokenValue> tokens, List<(string, string)> problems)
    {
        // A group's $type applies to the tokens inside it that declare none (DTCG 2025.10 type inheritance).
        var groupType = group["$type"] is JsonValue t && t.TryGetValue<string>(out var gt) ? gt : inheritedType;
        foreach (var (key, node) in group)
        {
            if (key.StartsWith('$'))
            {
                continue;
            }
            var name = path.Length == 0 ? key : $"{path}.{key}";
            if (node is not JsonObject child)
            {
                problems.Add((name, "validation.theme.dtcg_invalid"));
                continue;
            }
            if (!child.ContainsKey("$value"))
            {
                Walk(child, name, groupType, tokens, problems);
                continue;
            }
            if (tokens.Count >= TokenSchema.All.Count)
            {
                problems.Add(("file", "validation.theme.too_many_tokens"));
                return;
            }
            if (!TokenSchema.ByName.TryGetValue(name, out var definition))
            {
                problems.Add((name, "validation.theme.token_unknown"));
                continue;
            }
            var declared = child["$type"] is JsonValue dt && dt.TryGetValue<string>(out var d) ? d : groupType;
            if (declared is not null && (!Types.TryGetValue(definition.Type, out var expected) || expected != declared))
            {
                problems.Add((name, "validation.theme.dtcg_type_mismatch"));
                continue;
            }
            var value = ImportValue(definition, child["$value"]);
            if (value is null)
            {
                problems.Add((name, InvalidKey(definition)));
                continue;
            }
            Dictionary<string, string>? modes = null;
            if (child["$extensions"]?[Extension]?["modes"] is JsonObject exported)
            {
                modes = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (mode, modeNode) in exported)
                {
                    if (ImportValue(definition, modeNode) is { } modeValue)
                    {
                        modes[mode] = modeValue;
                    }
                    else
                    {
                        problems.Add(($"{name}.{mode}", InvalidKey(definition)));
                    }
                }
            }
            tokens[name] = new TokenValue(value, modes is { Count: > 0 } ? modes : null);
        }
    }

    private static string InvalidKey(TokenDefinition definition) => definition.Type switch
    {
        TokenTypes.Color => "validation.theme.color_invalid",
        TokenTypes.Dimension => "validation.theme.dimension_invalid",
        TokenTypes.Duration => "validation.theme.duration_invalid",
        TokenTypes.Easing => "validation.theme.easing_invalid",
        TokenTypes.Shadow => "validation.theme.shadow_invalid",
        _ => "validation.theme.token_value_invalid",
    };

    private static JsonObject Group(JsonObject root, string name)
    {
        var node = root;
        var parts = name.Split('.');
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject next)
            {
                next = new JsonObject();
                node[part] = next;
            }
            node = next;
        }
        return node;
    }

    private static string Leaf(string name) => name[(name.LastIndexOf('.') + 1)..];

    private static JsonNode? ExportValue(TokenDefinition definition, string value)
    {
        if (TokenSchema.IsAlias(value, out _))
        {
            return JsonValue.Create(value);
        }
        return definition.Type switch
        {
            TokenTypes.Color => TokenSchema.ParseColor(value) is { } c ? Color(c.R, c.G, c.B, 1) : JsonValue.Create(value),
            TokenTypes.Dimension => Measure(value, DimensionPart()),
            TokenTypes.Duration => Measure(value, DurationPart()),
            TokenTypes.FontWeight or TokenTypes.Number =>
                decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? JsonValue.Create(n) : JsonValue.Create(value),
            TokenTypes.Easing => Curves.TryGetValue(value, out var curve) ? new JsonArray([.. curve.Select(p => (JsonNode?)JsonValue.Create(p))]) : JsonValue.Create(value),
            TokenTypes.Shadow => Shadow(value),
            _ => JsonValue.Create(value),
        };
    }

    private static JsonObject Color(int r, int g, int b, double alpha) => new()
    {
        ["colorSpace"] = "srgb",
        ["components"] = new JsonArray(JsonValue.Create(Math.Round(r / 255d, 4)), JsonValue.Create(Math.Round(g / 255d, 4)), JsonValue.Create(Math.Round(b / 255d, 4))),
        ["alpha"] = Math.Round(alpha, 4),
        ["hex"] = $"#{r:x2}{g:x2}{b:x2}",
    };

    private static JsonNode? Measure(string value, Regex pattern)
    {
        var m = pattern.Match(value);
        return m.Success
            ? new JsonObject { ["value"] = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), ["unit"] = m.Groups[2].Value }
            : JsonValue.Create(value);
    }

    private static JsonNode? Shadow(string value)
    {
        if (value == "none")
        {
            return new JsonObject
            {
                ["color"] = Color(0, 0, 0, 0),
                ["offsetX"] = Px(0), ["offsetY"] = Px(0), ["blur"] = Px(0), ["spread"] = Px(0),
            };
        }
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 || HexAlpha().Match(parts[4]) is not { Success: true } hex)
        {
            return JsonValue.Create(value);
        }
        var h = hex.Groups[1].Value;
        var alpha = h.Length == 8 ? Convert.ToInt32(h[6..], 16) / 255d : 1d;
        return new JsonObject
        {
            ["color"] = Color(Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16), alpha),
            ["offsetX"] = Measure(parts[0], SignedDimension()),
            ["offsetY"] = Measure(parts[1], SignedDimension()),
            ["blur"] = Measure(parts[2], SignedDimension()),
            ["spread"] = Measure(parts[3], SignedDimension()),
        };
    }

    private static JsonObject Px(decimal n) => new() { ["value"] = n, ["unit"] = "px" };

    private static string? ImportValue(TokenDefinition definition, JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var text))
        {
            // An alias, or a value already in the stored form (a keyword, a font family or a hex color), stays as written.
            return text.Trim();
        }
        return definition.Type switch
        {
            TokenTypes.Color => ImportColor(node, alphaAllowed: false),
            TokenTypes.Dimension => ImportMeasure(node, ["px", "rem"], 1),
            TokenTypes.Duration => ImportDuration(node),
            TokenTypes.FontWeight or TokenTypes.Number => node is JsonValue n && n.TryGetValue<decimal>(out var number)
                ? number.ToString(CultureInfo.InvariantCulture) : null,
            TokenTypes.FontFamily => node is JsonArray families && families.FirstOrDefault() is JsonValue first && first.TryGetValue<string>(out var family)
                ? family : null,
            TokenTypes.Easing => ImportCurve(node),
            TokenTypes.Shadow => ImportShadow(node),
            _ => null,
        };
    }

    private static string? ImportColor(JsonNode? node, bool alphaAllowed)
    {
        if (node is not JsonObject color)
        {
            return null;
        }
        var alpha = color["alpha"] is JsonValue a && a.TryGetValue<double>(out var av) ? av : 1d;
        if (alpha is < 0 or > 1 || (!alphaAllowed && alpha < 1))
        {
            return null;
        }
        string? hex = null;
        if (color["hex"] is JsonValue h && h.TryGetValue<string>(out var hv) && HexColor().IsMatch(hv))
        {
            hex = hv.ToLowerInvariant();
        }
        else if (color["colorSpace"] is JsonValue cs && cs.TryGetValue<string>(out var space) && space == "srgb"
            && color["components"] is JsonArray { Count: 3 } components
            && components.All(c => c is JsonValue cv && cv.TryGetValue<double>(out var x) && x is >= 0 and <= 1))
        {
            var rgb = components.Select(c => (int)Math.Round(c!.GetValue<double>() * 255)).ToArray();
            hex = $"#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}";
        }
        if (hex is null)
        {
            return null;
        }
        return alphaAllowed ? hex + ((int)Math.Round(alpha * 255)).ToString("x2", CultureInfo.InvariantCulture) : hex;
    }

    private static string? ImportMeasure(JsonNode? node, string[] units, int sign)
    {
        if (node is not JsonObject m || m["unit"] is not JsonValue u || !u.TryGetValue<string>(out var unit) || !units.Contains(unit)
            || m["value"] is not JsonValue n || !n.TryGetValue<decimal>(out var number) || (sign > 0 && number < 0))
        {
            return null;
        }
        return number.ToString(CultureInfo.InvariantCulture) + unit;
    }

    private static string? ImportDuration(JsonNode? node)
    {
        if (node is not JsonObject m || m["unit"] is not JsonValue u || !u.TryGetValue<string>(out var unit)
            || m["value"] is not JsonValue n || !n.TryGetValue<decimal>(out var number) || number < 0)
        {
            return null;
        }
        var ms = unit switch { "ms" => number, "s" => number * 1000, _ => -1 };
        return ms >= 0 && ms == decimal.Truncate(ms) ? ((int)ms).ToString(CultureInfo.InvariantCulture) + "ms" : null;
    }

    private static string? ImportCurve(JsonNode? node)
    {
        if (node is not JsonArray { Count: 4 } points || !points.All(p => p is JsonValue pv && pv.TryGetValue<double>(out _)))
        {
            return null;
        }
        var curve = points.Select(p => p!.GetValue<double>()).ToArray();
        // Only the approved easings exist, so a curve is matched to the keyword with the same points.
        return Curves.FirstOrDefault(c => c.Value.Zip(curve).All(z => Math.Abs(z.First - z.Second) < 0.001)).Key;
    }

    private static string? ImportShadow(JsonNode? node)
    {
        var shadow = node is JsonArray { Count: 1 } single ? single[0] : node;
        if (shadow is not JsonObject s || (s["inset"] is JsonValue inset && inset.TryGetValue<bool>(out var isInset) && isInset))
        {
            return null;
        }
        var color = ImportColor(s["color"], alphaAllowed: true);
        var parts = new[] { "offsetX", "offsetY", "blur", "spread" }.Select((p, i) => ImportMeasure(s[p], ["px"], i < 2 ? 0 : 1)).ToArray();
        return color is null || parts.Any(p => p is null) ? null : $"{string.Join(' ', parts)} {color}";
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(px|rem)$")]
    private static partial Regex DimensionPart();

    [GeneratedRegex(@"^(-?\d+(?:\.\d+)?)(px)$")]
    private static partial Regex SignedDimension();

    [GeneratedRegex(@"^(\d+)(ms)$")]
    private static partial Regex DurationPart();

    [GeneratedRegex("^#([0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)$")]
    private static partial Regex HexAlpha();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
