using TeamBeats.Primitives;

namespace TeamBeats.Modules.Calendar;

/// <summary>
/// The small recurrence subset of FR-017, expanded on local dates so a series keeps its local start time across daylight
/// saving changes: daily, weekly on chosen ISO weekdays (1 Monday to 7 Sunday), or monthly on the start's day of the
/// month (months without that day are skipped). A series ends at <c>Until</c>, after <c>Count</c> occurrences, or at
/// <see cref="MaxOccurrences"/>, whichever comes first, and never runs more than <see cref="MaxYears"/> years.
/// </summary>
public static class Recurrence
{
    public const int MaxOccurrences = 100;
    public const int MaxYears = 5;
    public const int MaxInterval = 12;
    public static readonly string[] Frequencies = ["daily", "weekly", "monthly"];

    public static void Validate(RecurrenceRule? rule, DateOnly start, ValidationErrors errors)
    {
        if (rule is null)
        {
            return;
        }
        if (!Frequencies.Contains(rule.Frequency))
        {
            errors.Add("recurrence.frequency", "validation.calendar_event.recurrence");
        }
        if (rule.Interval is < 1 or > MaxInterval)
        {
            errors.Add("recurrence.interval", "validation.calendar_event.recurrence");
        }
        if (rule.Weekdays is { Count: > 0 } days && (rule.Frequency != "weekly" || days.Any(d => d is < 1 or > 7)))
        {
            errors.Add("recurrence.weekdays", "validation.calendar_event.recurrence");
        }
        if (rule.Count is < 1 or > MaxOccurrences)
        {
            errors.Add("recurrence.count", "validation.calendar_event.recurrence");
        }
        if (rule.Until is { } until && (until < start || until > start.AddYears(MaxYears)))
        {
            errors.Add("recurrence.until", "validation.calendar_event.recurrence");
        }
    }

    /// <summary>The ISO day number, 1 for Monday to 7 for Sunday.</summary>
    public static int Iso(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    /// <summary>Every occurrence start date of the series, in order, within the limits.</summary>
    public static IEnumerable<DateOnly> Dates(DateOnly start, RecurrenceRule? rule)
    {
        if (rule is null)
        {
            yield return start;
            yield break;
        }
        var interval = Math.Clamp(rule.Interval, 1, MaxInterval);
        var limit = Math.Min(rule.Count ?? MaxOccurrences, MaxOccurrences);
        var last = rule.Until is { } until && until < start.AddYears(MaxYears) ? until : start.AddYears(MaxYears);
        var weekdays = rule.Weekdays is { Count: > 0 } list ? list.ToHashSet() : [Iso(start.DayOfWeek)];
        var weekStart = start.AddDays(-(Iso(start.DayOfWeek) - 1));
        var found = 0;
        for (var date = start; date <= last && found < limit; date = date.AddDays(1))
        {
            var match = rule.Frequency switch
            {
                "daily" => (date.DayNumber - start.DayNumber) % interval == 0,
                "weekly" => (date.DayNumber - weekStart.DayNumber) / 7 % interval == 0 && weekdays.Contains(Iso(date.DayOfWeek)),
                "monthly" => date.Day == start.Day && ((date.Year - start.Year) * 12 + date.Month - start.Month) % interval == 0,
                _ => date == start,
            };
            if (match)
            {
                found++;
                yield return date;
            }
        }
    }

    /// <summary>The last moment the series can touch, for range queries: the last occurrence date plus the event's length.</summary>
    public static DateOnly LastDate(DateOnly start, RecurrenceRule? rule) => Dates(start, rule).DefaultIfEmpty(start).Last();
}
