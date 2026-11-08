using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services;

/// <summary>Field types of FR-003. A policy note holds no value; it shows text in the form.</summary>
public static class FieldTypes
{
    public const string Text = "text";
    public const string Number = "number";
    public const string Money = "money";
    public const string Date = "date";
    public const string DateRange = "date_range";
    public const string Choice = "choice";
    public const string MultiChoice = "multi_choice";
    public const string Person = "person";
    public const string OrgUnit = "org_unit";
    public const string Lookup = "lookup";
    public const string Attachment = "attachment";
    public const string PolicyNote = "policy_note";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Text, Number, Money, Date, DateRange, Choice, MultiChoice, Person, OrgUnit, Lookup, Attachment, PolicyNote,
    };

    public static bool HasOptions(string type) => type is Choice or MultiChoice or Lookup;
}

/// <summary>Validation rules (plan, Forms): comparisons with constants and with today plus or minus days. Nothing else.</summary>
public static class RuleKinds
{
    public const string Min = "min";
    public const string Max = "max";
    public const string MinLength = "min_length";
    public const string MaxLength = "max_length";
    public const string MinDaysFromToday = "min_days_from_today";
    public const string MaxDaysFromToday = "max_days_from_today";
    public const string MinItems = "min_items";
    public const string MaxItems = "max_items";

    public static IReadOnlySet<string> For(string type) => type switch
    {
        FieldTypes.Number or FieldTypes.Money => new HashSet<string> { Min, Max },
        FieldTypes.Text => new HashSet<string> { MinLength, MaxLength },
        FieldTypes.Date or FieldTypes.DateRange => new HashSet<string> { MinDaysFromToday, MaxDaysFromToday },
        FieldTypes.MultiChoice or FieldTypes.Attachment or FieldTypes.Person => new HashSet<string> { MinItems, MaxItems },
        _ => new HashSet<string>(),
    };
}

/// <summary>
/// Where a field's starting value comes from (FR-004). Fields from the directory or the account are read-only; the
/// rest start filled and stay editable. More profile fields arrive with spec 010 custom fields.
/// </summary>
public static class PrefillSources
{
    public const string DisplayName = "profile.display_name";
    public const string Email = "profile.email";
    public const string JobTitle = "profile.job_title";
    public const string Department = "profile.department";
    public const string Office = "profile.office";
    public const string WorkPhone = "profile.work_phone";
    public const string MobilePhone = "profile.mobile_phone";
    public const string EmployeeNumber = "profile.employee_number";
    public const string Manager = "profile.manager";
    public const string PrimaryUnit = "org.primary_unit";
    public const string Company = "org.company";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DisplayName, Email, JobTitle, Department, Office, WorkPhone, MobilePhone, EmployeeNumber, Manager, PrimaryUnit, Company,
    };

    /// <summary>The field types a source can fill.</summary>
    public static bool Fits(string source, string type) => source switch
    {
        Manager => type == FieldTypes.Person,
        PrimaryUnit or Company => type == FieldTypes.OrgUnit,
        _ => type == FieldTypes.Text,
    };
}

public sealed record FieldOption(string Value, IReadOnlyDictionary<string, string> Labels);

/// <summary>A rule; <see cref="Message"/> is the owner's text per locale, shown by the form instead of the generic one.</summary>
public sealed record FieldRule(string Kind, decimal? Value = null, IReadOnlyDictionary<string, string>? Message = null);

/// <summary>Shows the field only when an earlier choice field has one of the values.</summary>
public sealed record FieldCondition(string Field, IReadOnlyList<string> Values);

public sealed record FormField(
    string Key,
    string Type,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string>? Help = null,
    bool Required = false,
    IReadOnlyList<FieldOption>? Options = null,
    string? Prefill = null,
    IReadOnlyList<FieldRule>? Rules = null,
    FieldCondition? ShowWhen = null,
    IReadOnlyDictionary<string, string>? Note = null,
    IReadOnlyList<string>? Currencies = null);

public sealed record FormDefinition(IReadOnlyList<FormField> Fields)
{
    public static readonly FormDefinition Empty = new([]);
}

/// <summary>What the submit check found besides errors: people, units and attachments the caller must verify exist.</summary>
public sealed record FormReferences(IReadOnlySet<Guid> People, IReadOnlySet<Guid> Units, IReadOnlySet<Guid> Attachments);

/// <summary>
/// Checks form definitions on save and form data on submit (plan, Forms). The same rules run in both places, the
/// language set is closed, and no value is ever an expression.
/// </summary>
public static partial class FormValidator
{
    public const int MaxFields = 50;
    public const int MaxOptions = 100;
    public const int MaxLabelLength = 200;
    public const int MaxNoteLength = 2000;
    public const int MaxTextLength = 2000;
    public const int MaxItems = 50;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FieldKey();

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex Locale();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex Currency();

    private static bool Texts(IReadOnlyDictionary<string, string>? texts, int max, bool required) =>
        texts is null ? !required
            : (!required || texts.Count > 0) && texts.Count <= 10
              && texts.All(t => Locale().IsMatch(t.Key) && !string.IsNullOrWhiteSpace(t.Value) && t.Value.Trim().Length <= max);

    public static void ValidateDefinition(FormDefinition? form, ValidationErrors errors, string path = "form")
    {
        if (form?.Fields is not { } fields)
        {
            errors.Add(path, "validation.services.form");
            return;
        }
        if (fields.Count > MaxFields)
        {
            errors.Add($"{path}.fields", "validation.services.form_too_many_fields");
            return;
        }
        var seen = new Dictionary<string, FormField>(StringComparer.Ordinal);
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            var at = $"{path}.fields[{i}]";
            if (f is null)
            {
                errors.Add(at, "validation.services.field");
                continue;
            }
            if (f.Key is null || !FieldKey().IsMatch(f.Key) || seen.ContainsKey(f.Key))
            {
                errors.Add($"{at}.key", "validation.services.field_key");
            }
            if (f.Type is null || !FieldTypes.All.Contains(f.Type))
            {
                errors.Add($"{at}.type", "validation.services.field_type");
                continue;
            }
            if (!Texts(f.Labels, MaxLabelLength, required: true))
            {
                errors.Add($"{at}.labels", "validation.services.field_labels");
            }
            if (!Texts(f.Help, MaxNoteLength, required: false) || !Texts(f.Note, MaxNoteLength, required: f.Type == FieldTypes.PolicyNote))
            {
                errors.Add($"{at}.note", "validation.services.field_note");
            }
            if (f.Type == FieldTypes.PolicyNote && (f.Required || f.Prefill is not null || f.Rules is { Count: > 0 }))
            {
                errors.Add(at, "validation.services.policy_note_value");
            }
            if (FieldTypes.HasOptions(f.Type))
            {
                if (f.Options is not { Count: > 0 } options || options.Count > MaxOptions || options.Any(o => o is null
                    || string.IsNullOrWhiteSpace(o.Value) || o.Value.Length > 100 || !Texts(o.Labels, MaxLabelLength, required: true))
                    || options.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count() != options.Count)
                {
                    errors.Add($"{at}.options", "validation.services.field_options");
                }
            }
            else if (f.Options is { Count: > 0 })
            {
                errors.Add($"{at}.options", "validation.services.field_options");
            }
            if (f.Type == FieldTypes.Money && (f.Currencies is not { Count: > 0 } currencies || currencies.Count > 20 || currencies.Any(c => c is null || !Currency().IsMatch(c))))
            {
                errors.Add($"{at}.currencies", "validation.services.field_currencies");
            }
            if (f.Prefill is { } source && (!PrefillSources.All.Contains(source) || !PrefillSources.Fits(source, f.Type)))
            {
                errors.Add($"{at}.prefill", "validation.services.field_prefill");
            }
            var allowed = RuleKinds.For(f.Type);
            var rules = f.Rules ?? [];
            if (rules.Count > 4 || rules.Any(r => r is null || !allowed.Contains(r.Kind) || r.Value is null || !Texts(r.Message, MaxNoteLength, required: false)
                || ((r.Kind.EndsWith("_length", StringComparison.Ordinal) || r.Kind.EndsWith("_items", StringComparison.Ordinal)) && (r.Value < 0 || r.Value != decimal.Truncate(r.Value.Value)))
                || (r.Kind.Contains("days", StringComparison.Ordinal) && (r.Value < -3660 || r.Value > 3660 || r.Value != decimal.Truncate(r.Value.Value))))
                || rules.Select(r => r?.Kind).Distinct().Count() != rules.Count)
            {
                errors.Add($"{at}.rules", "validation.services.field_rules");
            }
            if (f.ShowWhen is { } when)
            {
                // Conditions point back only, so they can never form a loop.
                if (when.Field is null || !seen.TryGetValue(when.Field, out var target) || !FieldTypes.HasOptions(target.Type)
                    || when.Values is not { Count: > 0 } || when.Values.Any(v => target.Options?.Any(o => o.Value == v) != true))
                {
                    errors.Add($"{at}.showWhen", "validation.services.field_condition");
                }
            }
            if (f.Key is not null)
            {
                seen.TryAdd(f.Key, f);
            }
        }
    }

    /// <summary>Whether a field shows for the given data: its condition's field shows and has one of the values.</summary>
    public static bool IsShown(FormDefinition form, FormField field, IReadOnlyDictionary<string, JsonElement> data)
    {
        if (field.ShowWhen is not { } when)
        {
            return true;
        }
        var target = form.Fields.FirstOrDefault(f => f.Key == when.Field);
        if (target is null || !IsShown(form, target, data) || !data.TryGetValue(when.Field, out var value))
        {
            return false;
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => when.Values.Contains(value.GetString()!),
            JsonValueKind.Array => value.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && when.Values.Contains(v.GetString()!)),
            _ => false,
        };
    }

    /// <summary>
    /// Checks submitted data against the form. Values of hidden fields and unknown keys are dropped; the returned data
    /// holds only shown fields with a value. Dates compare with <paramref name="today"/> in the requester's calendar.
    /// </summary>
    public static (Dictionary<string, JsonElement> Data, FormReferences References) ValidateData(
        FormDefinition form, IReadOnlyDictionary<string, JsonElement>? input, DateOnly today, ValidationErrors errors, string path = "data")
    {
        input ??= new Dictionary<string, JsonElement>();
        var clean = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var people = new HashSet<Guid>();
        var units = new HashSet<Guid>();
        var files = new HashSet<Guid>();
        foreach (var field in form.Fields)
        {
            if (field.Type == FieldTypes.PolicyNote || !IsShown(form, field, input))
            {
                continue;
            }
            var at = $"{path}.{field.Key}";
            if (!input.TryGetValue(field.Key, out var value) || IsEmpty(value))
            {
                if (field.Required)
                {
                    errors.Add(at, "validation.services.field_required");
                }
                continue;
            }
            if (!CheckValue(field, value, today, at, errors, people, units, files))
            {
                continue;
            }
            clean[field.Key] = value.Clone();
        }
        return (clean, new FormReferences(people, units, files));
    }

    private static bool IsEmpty(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => true,
        JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()),
        JsonValueKind.Array => v.GetArrayLength() == 0,
        _ => false,
    };

    private static decimal? Rule(FormField f, string kind) => f.Rules?.FirstOrDefault(r => r.Kind == kind)?.Value;

    private static bool CheckValue(FormField f, JsonElement v, DateOnly today, string at, ValidationErrors errors,
        HashSet<Guid> people, HashSet<Guid> units, HashSet<Guid> files)
    {
        bool Fail(string key)
        {
            errors.Add(at, key);
            return false;
        }
        switch (f.Type)
        {
            case FieldTypes.Text:
            {
                if (v.ValueKind != JsonValueKind.String)
                {
                    return Fail("validation.services.field_invalid");
                }
                var length = v.GetString()!.Trim().Length;
                if (length > MaxTextLength || (Rule(f, RuleKinds.MaxLength) is { } max && length > max))
                {
                    return Fail("validation.services.field_too_long");
                }
                return Rule(f, RuleKinds.MinLength) is { } min && length < min ? Fail("validation.services.field_too_short") : true;
            }
            case FieldTypes.Number:
                return v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var number) ? Range(f, number, at, errors) : Fail("validation.services.field_invalid");
            case FieldTypes.Money:
            {
                if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("amount", out var amount) || amount.ValueKind != JsonValueKind.Number
                    || !amount.TryGetDecimal(out var value) || !v.TryGetProperty("currency", out var currency) || currency.ValueKind != JsonValueKind.String
                    || f.Currencies?.Contains(currency.GetString()!) != true || value < 0)
                {
                    return Fail("validation.services.field_invalid");
                }
                return Range(f, value, at, errors);
            }
            case FieldTypes.Date:
                return DateOf(v) is { } date ? Days(f, date, today, at, errors) : Fail("validation.services.field_invalid");
            case FieldTypes.DateRange:
            {
                if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("from", out var fromValue) || !v.TryGetProperty("to", out var toValue)
                    || DateOf(fromValue) is not { } from || DateOf(toValue) is not { } to)
                {
                    return Fail("validation.services.field_invalid");
                }
                if (to < from)
                {
                    return Fail("validation.services.field_range_order");
                }
                return Days(f, from, today, at, errors) && Days(f, to, today, at, errors);
            }
            case FieldTypes.Choice or FieldTypes.Lookup:
                return v.ValueKind == JsonValueKind.String && f.Options!.Any(o => o.Value == v.GetString()) ? true : Fail("validation.services.field_option");
            case FieldTypes.MultiChoice:
            {
                if (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String || f.Options!.All(o => o.Value != i.GetString())))
                {
                    return Fail("validation.services.field_option");
                }
                var values = v.EnumerateArray().Select(i => i.GetString()).ToList();
                return values.Distinct().Count() != values.Count ? Fail("validation.services.field_option") : Items(f, values.Count, at, errors);
            }
            case FieldTypes.Person or FieldTypes.Attachment:
            {
                var ids = Ids(v);
                if (ids is null || ids.Count > MaxItems)
                {
                    return Fail("validation.services.field_invalid");
                }
                (f.Type == FieldTypes.Person ? people : files).UnionWith(ids);
                return Items(f, ids.Count, at, errors);
            }
            case FieldTypes.OrgUnit:
                if (v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var unit) && unit != Guid.Empty)
                {
                    units.Add(unit);
                    return true;
                }
                return Fail("validation.services.field_invalid");
            default:
                return Fail("validation.services.field_invalid");
        }
    }

    /// <summary>One ID as a string, or a list of IDs; anything else is invalid.</summary>
    private static List<Guid>? Ids(JsonElement v)
    {
        var raw = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [v];
        var ids = new List<Guid>();
        foreach (var item in raw)
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var id) || id == Guid.Empty || ids.Contains(id))
            {
                return null;
            }
            ids.Add(id);
        }
        return ids;
    }

    private static DateOnly? DateOf(JsonElement v) =>
        v.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static bool Range(FormField f, decimal value, string at, ValidationErrors errors)
    {
        if (Rule(f, RuleKinds.Min) is { } min && value < min)
        {
            errors.Add(at, "validation.services.field_min");
            return false;
        }
        if (Rule(f, RuleKinds.Max) is { } max && value > max)
        {
            errors.Add(at, "validation.services.field_max");
            return false;
        }
        return true;
    }

    private static bool Days(FormField f, DateOnly date, DateOnly today, string at, ValidationErrors errors)
    {
        if (Rule(f, RuleKinds.MinDaysFromToday) is { } min && date < today.AddDays((int)min))
        {
            errors.Add(at, "validation.services.field_date_too_early");
            return false;
        }
        if (Rule(f, RuleKinds.MaxDaysFromToday) is { } max && date > today.AddDays((int)max))
        {
            errors.Add(at, "validation.services.field_date_too_late");
            return false;
        }
        return true;
    }

    private static bool Items(FormField f, int count, string at, ValidationErrors errors)
    {
        if (Rule(f, RuleKinds.MinItems) is { } min && count < min)
        {
            errors.Add(at, "validation.services.field_too_few");
            return false;
        }
        if (Rule(f, RuleKinds.MaxItems) is { } max && count > max)
        {
            errors.Add(at, "validation.services.field_too_many");
            return false;
        }
        return true;
    }
}
