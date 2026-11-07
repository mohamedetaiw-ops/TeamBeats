using TeamBeats.Primitives;

namespace TeamBeats.Modules.Hr.Contracts;

/// <summary>
/// The working-day contract of spec 022 FR-007: specs 016 (due times) and 024 (working-day timers) ask it for the
/// calendar that applies to an org unit or a person, never the calendar tables. All calls are in the current tenant.
/// </summary>
public interface IWorkingCalendar
{
    /// <summary>The calendar of the nearest assigned ancestor of the unit, or the tenant default.</summary>
    Task<WorkingCalendarSnapshot> ForUnitAsync(Guid orgUnitId, CancellationToken ct = default);

    /// <summary>The calendar of the person's primary membership unit, or the tenant default.</summary>
    Task<WorkingCalendarSnapshot> ForUserAsync(Guid userId, CancellationToken ct = default);

    Task<WorkingCalendarSnapshot> TenantDefaultAsync(CancellationToken ct = default);
}

/// <summary>A holiday on a calendar. A half day still counts as a working day for adding, and as half a day for counting.</summary>
public sealed record HolidayDay(DateOnly Date, bool HalfDay);

/// <summary>
/// A resolved calendar at one version. Pure functions over working days; dates are calendar dates in the tenant time
/// zone (<see cref="Today"/> converts an instant).
/// </summary>
public sealed class WorkingCalendarSnapshot
{
    private const int SearchLimitDays = 3660;
    private readonly HashSet<DayOfWeek> _weekDays;
    private readonly Dictionary<DateOnly, bool> _holidays;

    public WorkingCalendarSnapshot(Guid? calendarId, int version, IEnumerable<DayOfWeek> weekDays, IEnumerable<HolidayDay> holidays, TimeZoneInfo zone)
    {
        CalendarId = calendarId;
        Version = version;
        _weekDays = [.. weekDays];
        if (_weekDays.Count == 0)
        {
            throw new ArgumentException("A calendar needs at least one working day in the week.", nameof(weekDays));
        }
        _holidays = holidays.ToDictionary(h => h.Date, h => h.HalfDay);
        Zone = zone;
    }

    /// <summary>Null for the built-in tenant week (Monday to Friday, no holidays) before anyone saves a calendar.</summary>
    public Guid? CalendarId { get; }

    public int Version { get; }

    public TimeZoneInfo Zone { get; }

    public IReadOnlySet<DayOfWeek> WeekDays => _weekDays;

    public DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Zone).DateTime);

    /// <summary>1 for a full working day, 0.5 for a half-day holiday on a working weekday, otherwise 0.</summary>
    public decimal WorkingFraction(DateOnly date)
    {
        if (!_weekDays.Contains(date.DayOfWeek))
        {
            return 0m;
        }
        return _holidays.TryGetValue(date, out var half) ? (half ? 0.5m : 0m) : 1m;
    }

    public bool IsWorkingDay(DateOnly date) => WorkingFraction(date) > 0;

    /// <summary>Working days from <paramref name="from"/> to <paramref name="to"/>, both included; zero when the range is empty.</summary>
    public decimal CountWorkingDays(DateOnly from, DateOnly to)
    {
        var total = 0m;
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            total += WorkingFraction(d);
        }
        return total;
    }

    /// <summary>
    /// The date <paramref name="days"/> working days after <paramref name="start"/> (before it when negative). The start
    /// itself is not counted; zero gives the start.
    /// </summary>
    public DateOnly AddWorkingDays(DateOnly start, int days)
    {
        var step = Math.Sign(days);
        var remaining = Math.Abs(days);
        var date = start;
        var searched = 0;
        while (remaining > 0)
        {
            date = date.AddDays(step);
            if (++searched > SearchLimitDays + Math.Abs(days) * 7)
            {
                throw new InvalidOperationException("No working day was found within ten years.");
            }
            if (IsWorkingDay(date))
            {
                remaining--;
            }
        }
        return date;
    }

    /// <summary>The first working day on or after the date.</summary>
    public DateOnly NextWorkingDayOnOrAfter(DateOnly date)
    {
        for (var i = 0; i <= SearchLimitDays; i++, date = date.AddDays(1))
        {
            if (IsWorkingDay(date))
            {
                return date;
            }
        }
        throw new InvalidOperationException("No working day was found within ten years.");
    }
}

/// <summary>Published on any change to a calendar, its holidays or the units it applies to (spec 022 section 12).</summary>
public sealed record WorkingCalendarChanged(Guid? CalendarId, IReadOnlyList<DateOnly> AffectedDates, IReadOnlyList<Guid> AffectedUnits) : IIntegrationEvent;
