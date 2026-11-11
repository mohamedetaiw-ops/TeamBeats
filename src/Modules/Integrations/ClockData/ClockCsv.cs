using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TeamBeats.Modules.Integrations.Contracts;

namespace TeamBeats.Modules.Integrations.ClockData;

/// <summary>
/// Which column holds each field (spec 017 FR-020). With a header row a column is named by its header; without one, by
/// its number counted from 1. Either <see cref="Timestamp"/> or both <see cref="Date"/> and <see cref="Time"/> are set.
/// </summary>
public sealed record ClockColumns(string? EmployeeNumber, string? PunchId, string? Timestamp, string? Date, string? Time, string? Direction, string? Device);

/// <summary>
/// The CSV shape of a clock data source. <see cref="TimestampFormat"/>, <see cref="DateFormat"/> and
/// <see cref="TimeFormat"/> are .NET custom formats; with none, ISO 8601 is read. Times without an offset are in
/// <see cref="TimeZone"/> (IANA). <see cref="InValues"/> and <see cref="OutValues"/> are the source's words for each
/// direction, compared without case.
/// </summary>
public sealed record ClockCsvSettings(
    string Delimiter,
    bool HasHeader,
    ClockColumns Columns,
    string? TimestampFormat,
    string? DateFormat,
    string? TimeFormat,
    string TimeZone,
    IReadOnlyList<string> InValues,
    IReadOnlyList<string> OutValues);

/// <summary>A row that could not become a punch. <see cref="Reason"/> is one of <see cref="ClockRowReasons"/>.</summary>
public sealed record ClockRowProblem(int Line, string? EmployeeNumber, string Reason);

public sealed record ClockRow(int Line, string EmployeeNumber, string PunchId, DateTimeOffset At, string Direction, string? Device);

/// <summary>The rows of one file, or the reason the whole file was refused (a resource key).</summary>
public sealed record ClockParse(IReadOnlyList<ClockRow> Rows, IReadOnlyList<ClockRowProblem> Problems, int RowCount, string? FileError);

public static class ClockRowReasons
{
    public const string UnknownEmployee = "unknown_employee";
    public const string MissingEmployee = "missing_employee";
    public const string BadTime = "bad_time";
    public const string BadDirection = "bad_direction";
    public const string TooLong = "too_long";

    public static readonly IReadOnlyList<string> All = [UnknownEmployee, MissingEmployee, BadTime, BadDirection, TooLong];
}

/// <summary>
/// Reads clock data CSV (RFC 4180 quoting, UTF-8 with or without a byte order mark) into canonical rows. Pure: no I/O.
/// A row with no punch ID gets one derived from employee, time and direction, so the same punch imported twice is
/// still one punch.
/// </summary>
public static class ClockCsv
{
    public const int MaxRows = 500_000;
    public const int MaxField = 200;

    public static readonly IReadOnlyList<string> Delimiters = [",", ";", "\t", "|"];

    public static ClockParse Parse(ReadOnlySpan<byte> content, ClockCsvSettings settings)
    {
        var text = Encoding.UTF8.GetString(content.StartsWith(Encoding.UTF8.Preamble) ? content[3..] : content);
        var zone = FindZone(settings.TimeZone);
        if (zone is null)
        {
            return Refused("integrations.clock.file.time_zone");
        }
        var records = Records(text, settings.Delimiter[0]);
        using var e = records.GetEnumerator();
        var header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (settings.HasHeader)
        {
            if (!e.MoveNext())
            {
                return Refused("integrations.clock.file.empty");
            }
            var names = e.Current.Fields;
            for (var i = 0; i < names.Count; i++)
            {
                header.TryAdd(names[i].Trim(), i);
            }
        }
        int? Index(string? column)
        {
            if (column is null)
            {
                return null;
            }
            if (settings.HasHeader)
            {
                return header.TryGetValue(column.Trim(), out var i) ? i : -1;
            }
            return int.TryParse(column, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 ? n - 1 : -1;
        }
        var c = settings.Columns;
        var employee = Index(c.EmployeeNumber);
        var punch = Index(c.PunchId);
        var stamp = Index(c.Timestamp);
        var date = Index(c.Date);
        var time = Index(c.Time);
        var direction = Index(c.Direction);
        var device = Index(c.Device);
        if (new[] { employee, punch, stamp, date, time, direction, device }.Any(i => i == -1))
        {
            return Refused("integrations.clock.file.missing_column");
        }

        var rows = new List<ClockRow>();
        var problems = new List<ClockRowProblem>();
        var count = 0;
        var ins = new HashSet<string>(settings.InValues.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
        var outs = new HashSet<string>(settings.OutValues.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
        while (e.MoveNext())
        {
            var (line, fields) = e.Current;
            if (fields.Count == 1 && fields[0].Length == 0)
            {
                continue;
            }
            if (++count > MaxRows)
            {
                return Refused("integrations.clock.file.too_many_rows");
            }
            string? Field(int? i) => i is { } x && x < fields.Count && fields[x].Trim() is { Length: > 0 } v ? v : null;
            var number = Field(employee);
            if (number is null)
            {
                problems.Add(new ClockRowProblem(line, null, ClockRowReasons.MissingEmployee));
                continue;
            }
            if (fields.Any(f => f.Length > MaxField))
            {
                problems.Add(new ClockRowProblem(line, number.Length <= MaxField ? number : null, ClockRowReasons.TooLong));
                continue;
            }
            var at = stamp is not null ? ReadTime(Field(stamp), settings.TimestampFormat, zone) : ReadTime(Field(date), Field(time), settings, zone);
            if (at is null)
            {
                problems.Add(new ClockRowProblem(line, number, ClockRowReasons.BadTime));
                continue;
            }
            var way = ClockDirections.Unknown;
            if (direction is not null)
            {
                var value = Field(direction);
                way = value is null ? ClockDirections.Unknown : ins.Contains(value) ? ClockDirections.In : outs.Contains(value) ? ClockDirections.Out : "";
                if (way.Length == 0)
                {
                    problems.Add(new ClockRowProblem(line, number, ClockRowReasons.BadDirection));
                    continue;
                }
            }
            var id = Field(punch) ?? Derived(number, at.Value, way);
            rows.Add(new ClockRow(line, number, id, at.Value, way, Field(device)));
        }
        return new ClockParse(rows, problems, count, null);
    }

    private static ClockParse Refused(string key) => new([], [], 0, key);

    public static TimeZoneInfo? FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    private static string Derived(string number, DateTimeOffset at, string direction)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{number}|{at.UtcDateTime:O}|{direction}"));
        return "h:" + Convert.ToHexStringLower(hash)[..32];
    }

    private static DateTimeOffset? ReadTime(string? value, string? format, TimeZoneInfo zone)
    {
        if (value is null)
        {
            return null;
        }
        if (format is null)
        {
            // ISO 8601; a value with an offset keeps it, one without is local to the source's zone.
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset) && HasOffset(value))
            {
                return withOffset.ToUniversalTime();
            }
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ? Local(local, zone) : null;
        }
        return DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact) ? Local(exact, zone) : null;
    }

    private static DateTimeOffset? ReadTime(string? date, string? time, ClockCsvSettings s, TimeZoneInfo zone)
    {
        if (date is null || time is null)
        {
            return null;
        }
        var d = s.DateFormat is null
            ? DateOnly.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd) ? dd : (DateOnly?)null
            : DateOnly.TryParseExact(date, s.DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var de) ? de : null;
        var t = s.TimeFormat is null
            ? TimeOnly.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.None, out var td) ? td : (TimeOnly?)null
            : TimeOnly.TryParseExact(time, s.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var te) ? te : null;
        return d is { } day && t is { } at ? Local(day.ToDateTime(at), zone) : null;
    }

    private static bool HasOffset(string value) =>
        value.EndsWith('Z') || value.EndsWith('z') || (value.Length > 6 && (value[^6] is '+' or '-') && value[^3] == ':');

    /// <summary>A local time in the source's zone. A time skipped by a daylight saving change cannot have been punched.</summary>
    private static DateTimeOffset? Local(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            return null;
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Records with their first line number; quoted fields may hold the delimiter, quotes and line breaks.</summary>
    internal static IEnumerable<(int Line, List<string> Fields)> Records(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var start = 1;
        var quoted = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    quoted = false;
                }
                else
                {
                    if (ch == '\n')
                    {
                        line++;
                    }
                    if (field.Length <= MaxField)
                    {
                        field.Append(ch);
                    }
                }
                i++;
                continue;
            }
            if (ch == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
                fields.Add(field.ToString());
                field.Clear();
                yield return (start, fields);
                fields = [];
                line++;
                start = line;
            }
            else if (field.Length <= MaxField)
            {
                field.Append(ch);
            }
            i++;
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return (start, fields);
        }
    }
}
