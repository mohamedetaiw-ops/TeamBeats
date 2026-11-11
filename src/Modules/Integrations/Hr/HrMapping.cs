using System.Globalization;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>One HR record the sync refused (FR-014), with a plain reason.</summary>
public sealed record HrProblem(string? RecordId, string Reason);

public static class HrProblemReasons
{
    public const string MissingId = "missing_id";
    public const string DuplicateId = "duplicate_id";
    public const string BadStatus = "bad_status";
    public const string BadDate = "bad_date";
    public const string BadEmail = "bad_email";
    public const string TooLong = "too_long";
    public const string ManagerLoop = "manager_loop";
    public const string SelfManager = "self_manager";

    public static readonly IReadOnlyList<string> All = [MissingId, DuplicateId, BadStatus, BadDate, BadEmail, TooLong, ManagerLoop, SelfManager];
}

/// <summary>What a pack read from the HR system: canonical records and the records it could not read.</summary>
public sealed record HrRead(IReadOnlyList<HrEmployee> Employees, IReadOnlyList<HrProblem> Problems);

/// <summary>
/// Turns the mapped values of one HR record into the canonical employee (spec 019 section 2). Values are trimmed and
/// length checked; dates are ISO dates or the connection's date format; the status is the HR system's own word,
/// mapped to <see cref="HrEmployeeStatuses"/> by the connection's status values.
/// </summary>
public static class HrMapping
{
    public const int MaxRecords = 100_000;
    public const int MaxText = 200;

    private static readonly Dictionary<string, int> Limits = new(StringComparer.Ordinal)
    {
        [HrFields.RecordId] = 100, [HrFields.EmployeeNumber] = 100, [HrFields.WorkEmail] = 254, [HrFields.OrgUnitCode] = 100,
        [HrFields.ManagerEmployeeNumber] = 100, [HrFields.CostCentre] = 100, [HrFields.Grade] = 100, [HrFields.EmploymentType] = 100,
    };

    /// <summary>Checks status words: each canonical status at most 10 distinct words, no word for two statuses, active required.</summary>
    public static Dictionary<string, IReadOnlyList<string>> CheckStatusValues(IReadOnlyDictionary<string, IReadOnlyList<string>>? values, ValidationErrors errors)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (status, words) in values ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            var clean = (words ?? []).Select(w => w?.Trim() ?? "").Where(w => w.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!HrEmployeeStatuses.All.Contains(status) || clean.Count is 0 or > 10 || clean.Any(w => !seen.Add(w)))
            {
                errors.Add("statusValues", "validation.hr.status_values");
                continue;
            }
            result[status] = clean;
        }
        if (!result.ContainsKey(HrEmployeeStatuses.Active) || !result.ContainsKey(HrEmployeeStatuses.Terminated))
        {
            errors.Add("statusValues", "validation.hr.status_values");
        }
        return result;
    }

    /// <summary>A date format is accepted when a sample date written with it reads back.</summary>
    public static bool DateFormatWorks(string format)
    {
        if (format.Length > 30)
        {
            return false;
        }
        try
        {
            var sample = new DateOnly(2026, 3, 15);
            return DateOnly.TryParseExact(sample.ToString(format, CultureInfo.InvariantCulture), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var back)
                && back == sample;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static (HrEmployee? Employee, HrProblem? Problem) Map(IReadOnlyDictionary<string, string?> raw, IReadOnlyDictionary<string, IReadOnlyList<string>> statusValues,
        string? dateFormat)
    {
        string? V(string field)
        {
            var v = raw.TryGetValue(field, out var x) ? x?.Trim() : null;
            return string.IsNullOrEmpty(v) ? null : v;
        }
        var id = V(HrFields.RecordId);
        if (id is null)
        {
            return (null, new HrProblem(null, HrProblemReasons.MissingId));
        }
        foreach (var field in HrFields.All)
        {
            if (V(field) is { } v && (v.Length > Limits.GetValueOrDefault(field, MaxText) || v.Any(char.IsControl)))
            {
                return (null, new HrProblem(id.Length <= 100 ? id : null, HrProblemReasons.TooLong));
            }
        }
        var word = V(HrFields.Status);
        var status = word is null ? null : statusValues.FirstOrDefault(s => s.Value.Contains(word, StringComparer.OrdinalIgnoreCase)).Key;
        if (status is null)
        {
            return (null, new HrProblem(id, HrProblemReasons.BadStatus));
        }
        var email = V(HrFields.WorkEmail)?.ToLowerInvariant();
        if (email is not null && (email.Count(c => c == '@') != 1 || email.StartsWith('@') || email.EndsWith('@') || email.Contains(' ')))
        {
            return (null, new HrProblem(id, HrProblemReasons.BadEmail));
        }
        DateOnly? hire = null, end = null;
        DateOnly? assigned = null;
        if (V(HrFields.HireDate) is { } h && (hire = Date(h, dateFormat)) is null || V(HrFields.TerminationDate) is { } t && (end = Date(t, dateFormat)) is null
            || V(HrFields.AssignmentDate) is { } a && (assigned = Date(a, dateFormat)) is null)
        {
            return (null, new HrProblem(id, HrProblemReasons.BadDate));
        }
        var number = V(HrFields.EmployeeNumber);
        var manager = V(HrFields.ManagerEmployeeNumber);
        if (number is not null && manager == number)
        {
            return (null, new HrProblem(id, HrProblemReasons.SelfManager));
        }
        return (new HrEmployee(id, number, email, V(HrFields.NameEn), V(HrFields.NameAr), status, hire, end, V(HrFields.JobTitle), V(HrFields.Grade),
            V(HrFields.EmploymentType), V(HrFields.CostCentre), V(HrFields.Location), V(HrFields.OrgUnitCode), manager, assigned), null);
    }

    private static DateOnly? Date(string text, string? format)
    {
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }
        if (format is not null && DateOnly.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return d;
        }
        // An ISO time stamp from an API: the date part counts.
        return text.Length > 10 && text[10] == 'T' && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp)
            ? stamp
            : null;
    }

    /// <summary>
    /// Refuses duplicate record IDs and records whose manager chain loops back to them (spec 019 edge cases, FR-014).
    /// Every record in a loop is refused; the rest of the read goes on.
    /// </summary>
    public static HrRead Check(IReadOnlyList<HrEmployee> employees, IReadOnlyList<HrProblem> problems)
    {
        var all = problems.ToList();
        var byId = new Dictionary<string, HrEmployee>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in employees)
        {
            if (!byId.TryAdd(e.RecordId, e))
            {
                duplicates.Add(e.RecordId);
            }
        }
        foreach (var d in duplicates)
        {
            byId.Remove(d);
            all.Add(new HrProblem(d, HrProblemReasons.DuplicateId));
        }
        var byNumber = byId.Values.Where(e => e.EmployeeNumber is not null).GroupBy(e => e.EmployeeNumber!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var looped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in byId.Values)
        {
            var path = new List<string>();
            var onPath = new HashSet<string>(StringComparer.Ordinal);
            var current = start;
            while (current is not null && onPath.Add(current.RecordId))
            {
                path.Add(current.RecordId);
                current = current.ManagerEmployeeNumber is { } m && byNumber.TryGetValue(m, out var next) ? next : null;
            }
            if (current is not null)
            {
                foreach (var id in path.SkipWhile(id => id != current.RecordId))
                {
                    looped.Add(id);
                }
            }
        }
        foreach (var id in looped.Order(StringComparer.Ordinal))
        {
            byId.Remove(id);
            all.Add(new HrProblem(id, HrProblemReasons.ManagerLoop));
        }
        return new HrRead(employees.Where(e => byId.TryGetValue(e.RecordId, out var kept) && ReferenceEquals(kept, e)).ToList(), all);
    }
}
