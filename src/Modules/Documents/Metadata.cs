using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>A value people pick from a choice field: a stable value and its name per locale (a managed term).</summary>
public sealed record FieldOption(string Value, IReadOnlyDictionary<string, string> Names);

/// <summary>
/// A typed metadata field of a document type (FR-002): text, number, date, choice from a managed term set, or yes/no.
/// A default fills the field when the uploader leaves it out; a required field without a value keeps the upload a draft.
/// </summary>
public sealed record FieldDefinition(
    string Key,
    IReadOnlyDictionary<string, string> Names,
    string Kind,
    bool Required = false,
    JsonElement? Default = null,
    IReadOnlyList<FieldOption>? Options = null);

public static class FieldKinds
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Date = "date";
    public const string Choice = "choice";
    public const string Boolean = "boolean";

    public static readonly IReadOnlyList<string> All = [Text, Number, Date, Choice, Boolean];
}

/// <summary>Checks field definitions and the values a document carries for them.</summary>
public static partial class MetadataRules
{
    public const int MaxFields = 30;
    public const int MaxOptions = 200;
    public const int MaxTextLength = 500;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex KeyPattern();

    public static bool IsKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>Validates definitions and returns them with defaults normalized.</summary>
    public static IReadOnlyList<FieldDefinition> Definitions(IReadOnlyList<FieldDefinition>? fields, ValidationErrors errors)
    {
        var list = fields ?? [];
        if (list.Count > MaxFields)
        {
            errors.Add("fields", "validation.documents.too_many_fields");
            return [];
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FieldDefinition>();
        foreach (var f in list)
        {
            if (!IsKey(f.Key) || !keys.Add(f.Key))
            {
                errors.Add("fields", "validation.documents.field_key");
                continue;
            }
            if (f.Names is null || f.Names.Count == 0 || f.Names.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 100))
            {
                errors.Add("fields", "validation.documents.field_name");
                continue;
            }
            if (!FieldKinds.All.Contains(f.Kind))
            {
                errors.Add("fields", "validation.documents.field_kind");
                continue;
            }
            var options = f.Kind == FieldKinds.Choice ? f.Options ?? [] : null;
            if (options is not null && (options.Count == 0 || options.Count > MaxOptions
                || options.Any(o => string.IsNullOrWhiteSpace(o.Value) || o.Value.Length > 100 || o.Names is null || o.Names.Count == 0)
                || options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count))
            {
                errors.Add("fields", "validation.documents.field_options");
                continue;
            }
            var definition = f with { Options = options, Default = null };
            if (f.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } d)
            {
                if (Normalize(definition, d) is not { } value)
                {
                    errors.Add("fields", "validation.documents.field_default");
                    continue;
                }
                definition = definition with { Default = JsonSerializer.SerializeToElement(value) };
            }
            result.Add(definition);
        }
        return result;
    }

    /// <summary>
    /// Checks the values given for a document against its type's fields, fills defaults and says whether every required
    /// field has a value. Values for unknown fields are refused, so a typo is not silently dropped.
    /// </summary>
    public static (Dictionary<string, object> Values, bool Complete) Values(IReadOnlyList<FieldDefinition> fields, IReadOnlyDictionary<string, JsonElement>? given,
        ValidationErrors errors)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var key in (given ?? new Dictionary<string, JsonElement>()).Keys.Where(k => fields.All(f => f.Key != k)))
        {
            errors.Add($"metadata.{key}", "validation.documents.unknown_field");
        }
        var complete = true;
        foreach (var f in fields)
        {
            JsonElement? raw = given is not null && given.TryGetValue(f.Key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;
            if (raw is { ValueKind: JsonValueKind.String } s && string.IsNullOrWhiteSpace(s.GetString()))
            {
                raw = null;
            }
            raw ??= f.Default;
            if (raw is null)
            {
                complete &= !f.Required;
                continue;
            }
            if (Normalize(f, raw.Value) is { } value)
            {
                values[f.Key] = value;
            }
            else
            {
                errors.Add($"metadata.{f.Key}", "validation.documents.field_value");
            }
        }
        return (values, complete);
    }

    /// <summary>Whether stored values still fill every required field (a type may gain a required field later).</summary>
    public static bool Complete(IReadOnlyList<FieldDefinition> fields, IReadOnlyDictionary<string, JsonElement> stored) =>
        fields.Where(f => f.Required).All(f => stored.TryGetValue(f.Key, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));

    /// <summary>The stored form of one value, or null when it does not fit the field.</summary>
    internal static object? Normalize(FieldDefinition f, JsonElement raw)
    {
        switch (f.Kind)
        {
            case FieldKinds.Text when raw.ValueKind == JsonValueKind.String:
                var text = raw.GetString()!.Trim();
                return text.Length is > 0 and <= MaxTextLength ? text : null;
            case FieldKinds.Number when raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out var number):
                return number;
            case FieldKinds.Number when raw.ValueKind == JsonValueKind.String
                && decimal.TryParse(raw.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            case FieldKinds.Date when raw.ValueKind == JsonValueKind.String
                && DateOnly.TryParseExact(raw.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            case FieldKinds.Choice when raw.ValueKind == JsonValueKind.String && (f.Options ?? []).Any(o => o.Value == raw.GetString()):
                return raw.GetString()!;
            case FieldKinds.Boolean when raw.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return raw.GetBoolean();
            default:
                return null;
        }
    }
}
