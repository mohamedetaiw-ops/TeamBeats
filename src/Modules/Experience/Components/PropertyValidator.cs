using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Experience.Components;

/// <summary>
/// Validates placement properties against a manifest's typed property schema (spec 032 FR-025) and refuses executable content
/// anywhere in them (FR-035). The schema is a restricted subset of JSON Schema with no raw HTML type, checked here so no JSON
/// Schema library is needed.
/// </summary>
public static partial class PropertyValidator
{
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "string", "text", "number", "integer", "boolean", "enum", "url", "image", "color_token", "list",
    };

    /// <summary>Returns the names of properties that fail, each with a reason code.</summary>
    public static List<(string Property, string Reason)> Validate(IReadOnlyDictionary<string, PropertySchema> schema, JsonElement? props)
    {
        var problems = new List<(string, string)>();
        var values = props is { ValueKind: JsonValueKind.Object } p
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal)
            : [];
        if (props is { } given && given.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
        {
            problems.Add(("props", "not_object"));
            return problems;
        }
        foreach (var name in values.Keys.Where(k => !schema.ContainsKey(k)))
        {
            problems.Add((name, "unknown"));
        }
        foreach (var (name, property) in schema)
        {
            if (!values.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                if (property.Required && property.Default is null)
                {
                    problems.Add((name, "required"));
                }
                continue;
            }
            if (Check(property, value) is { } reason)
            {
                problems.Add((name, reason));
            }
        }
        return problems;
    }

    private static string? Check(PropertySchema s, JsonElement v)
    {
        switch (s.Type)
        {
            case "string":
                return v.ValueKind != JsonValueKind.String ? "type" : Text(s, v.GetString()!);
            case "text":
                // Localized plain text: an object of locale to string.
                if (v.ValueKind != JsonValueKind.Object)
                {
                    return "type";
                }
                foreach (var entry in v.EnumerateObject())
                {
                    if (!LocaleKey().IsMatch(entry.Name) || entry.Value.ValueKind != JsonValueKind.String)
                    {
                        return "type";
                    }
                    if (Text(s, entry.Value.GetString()!) is { } r)
                    {
                        return r;
                    }
                }
                return null;
            case "number" or "integer":
                if (v.ValueKind != JsonValueKind.Number || (s.Type == "integer" && !v.TryGetInt64(out _)))
                {
                    return "type";
                }
                var n = v.GetDouble();
                return n < s.Minimum || n > s.Maximum ? "range" : null;
            case "boolean":
                return v.ValueKind is JsonValueKind.True or JsonValueKind.False ? null : "type";
            case "enum":
                return v.ValueKind == JsonValueKind.String && s.Enum is { } options && options.Contains(v.GetString()!) ? null : "enum";
            case "url":
                return v.ValueKind == JsonValueKind.String && IsSafeUrl(v.GetString()!) ? null : "url";
            case "image":
                // An asset reference, never an inline image or a URL.
                return v.ValueKind == JsonValueKind.String && AssetRef().IsMatch(v.GetString()!) ? null : "image";
            case "color_token":
                return v.ValueKind == JsonValueKind.String && TokenName().IsMatch(v.GetString()!) ? null : "color_token";
            case "list":
                if (v.ValueKind != JsonValueKind.Array || s.Items is null)
                {
                    return "type";
                }
                if (s.MaxItems is { } max && v.GetArrayLength() > max)
                {
                    return "range";
                }
                return v.EnumerateArray().Select(item => Check(s.Items, item)).FirstOrDefault(r => r is not null);
            default:
                return "type";
        }
    }

    private static string? Text(PropertySchema s, string text)
    {
        if (text.Length < (s.MinLength ?? 0) || (s.MaxLength is { } max && text.Length > max))
        {
            return "length";
        }
        return IsExecutable(text) ? "executable" : null;
    }

    /// <summary>Markup tags and event handler attributes (FR-035, spec 026). Text is always rendered as text, never as markup.</summary>
    public static bool IsExecutable(string text) => Markup().IsMatch(text) || Handler().IsMatch(text);

    /// <summary>Only https and site-relative URLs.</summary>
    public static bool IsSafeUrl(string url)
    {
        if (url.Length is 0 or > 2000 || IsExecutable(url) || ScriptScheme().IsMatch(url) || url.Any(char.IsControl) || url.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        if (url.StartsWith('/'))
        {
            return true;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;
    }

    /// <summary>Applies schema defaults for properties not given, so a property added in a new manifest version gets its default.</summary>
    public static Dictionary<string, JsonElement> WithDefaults(IReadOnlyDictionary<string, PropertySchema> schema, JsonElement? props)
    {
        var result = props is { ValueKind: JsonValueKind.Object } p
            ? p.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, property) in schema)
        {
            if (!result.ContainsKey(name) && property.Default is { } d)
            {
                result[name] = d;
            }
        }
        return result;
    }

    [GeneratedRegex(@"<\s*/?\s*[a-zA-Z!?]")]
    private static partial Regex Markup();

    [GeneratedRegex(@"(?i)\b(javascript|vbscript|data|file)\s*:")]
    private static partial Regex ScriptScheme();

    [GeneratedRegex(@"(?i)\bon[a-z]+\s*=")]
    private static partial Regex Handler();

    [GeneratedRegex("^[a-z]{2}(-[A-Z]{2})?$")]
    private static partial Regex LocaleKey();

    [GeneratedRegex("^asset:[0-9a-f-]{36}$")]
    private static partial Regex AssetRef();

    [GeneratedRegex(@"^color\.[a-z]+(\.[a-z]+)*$")]
    private static partial Regex TokenName();
}
