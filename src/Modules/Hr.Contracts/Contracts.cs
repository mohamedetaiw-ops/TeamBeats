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
/// A reduced hours period on a calendar (spec 022 US2 scenario 3, for example Ramadan): working days stay working days,
/// with <paramref name="Hours"/> working hours each. Attendance rules read it.
/// </summary>
public sealed record ReducedHoursPeriod(DateOnly Start, DateOnly End, decimal Hours);

/// <summary>
/// A resolved calendar at one version. Pure functions over working days; dates are calendar dates in the tenant time
/// zone (<see cref="Today"/> converts an instant).
/// </summary>
public sealed class WorkingCalendarSnapshot
{
    private const int SearchLimitDays = 3660;
    private readonly HashSet<DayOfWeek> _weekDays;
    private readonly Dictionary<DateOnly, bool> _holidays;

    private readonly List<ReducedHoursPeriod> _periods;

    public WorkingCalendarSnapshot(Guid? calendarId, int version, IEnumerable<DayOfWeek> weekDays, IEnumerable<HolidayDay> holidays, TimeZoneInfo zone,
        IEnumerable<ReducedHoursPeriod>? periods = null)
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
        _periods = [.. (periods ?? []).OrderBy(p => p.Start)];
    }

    public IReadOnlyList<ReducedHoursPeriod> Periods => _periods;

    /// <summary>The reduced working hours of a working day inside a period, or null for a normal day or a day off.</summary>
    public decimal? ReducedHours(DateOnly date) =>
        IsWorkingDay(date) ? _periods.FirstOrDefault(p => p.Start <= date && date <= p.End)?.Hours : null;

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

    /// <summary>True for a holiday of the calendar, whole or half day, on any day of the week.</summary>
    public bool IsHoliday(DateOnly date) => _holidays.ContainsKey(date);

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

/// <summary>A balance for the current leave year. <see cref="Source"/> is <c>teambeats</c> or <c>hr</c>; HR balances carry the time they were read.</summary>
public sealed record LeaveBalance(string TypeKey, IReadOnlyDictionary<string, string> TypeNames, int LeaveYear, decimal Balance, decimal Pending, decimal Available, string Unit,
    string Source = "teambeats", DateTimeOffset? AsOf = null, bool Stale = false);

/// <summary>
/// One person away on one date (spec 022 FR-009). <see cref="TypeKey"/> is set only when the viewer may see the leave
/// type; otherwise the day shows as "out of office".
/// </summary>
public sealed record LeaveAbsence(Guid UserId, DateOnly Date, decimal Fraction, string Status, string? TypeKey);

/// <summary>
/// Leave reads for other modules (spec 022 slice 1): the assistant (spec 014) answers "how many days do I have" with the
/// person's own balances, and profiles (spec 010) show "out of office". All calls are in the current tenant and for the
/// signed-in viewer.
/// </summary>
public interface ILeave
{
    /// <summary>The signed-in person's balances for the current leave year of each active type.</summary>
    Task<IReadOnlyList<LeaveBalance>> MyBalancesAsync(CancellationToken ct = default);

    /// <summary>Approved leave of the given people between two dates, with the type only where the viewer may see it.</summary>
    Task<IReadOnlyList<LeaveAbsence>> AvailabilityAsync(IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Leave was approved (spec 022 section 12). IDs and dates only.</summary>
public sealed record LeaveApproved(Guid RequestId, Guid UserId, DateOnly From, DateOnly To) : IIntegrationEvent;

/// <summary>An approved correction changed a person's attendance day (spec 022 section 12). IDs and the date only.</summary>
public sealed record AttendanceCorrected(Guid RequestId, Guid UserId, DateOnly Date) : IIntegrationEvent;

/// <summary>An HR letter was issued as a PDF (spec 022 section 12). IDs and the reference only, never the letter's content.</summary>
public sealed record LetterIssued(Guid LetterId, Guid UserId, string Reference) : IIntegrationEvent;

/// <summary>An expense claim was approved by finance (spec 022 section 12). The total in the policy currency, no lines or receipts.</summary>
public sealed record ExpenseApproved(Guid ClaimId, Guid UserId, string Reference, decimal Total, string Currency) : IIntegrationEvent;

/// <summary>Approved leave was cancelled, or shortened (then <see cref="From"/> and <see cref="To"/> are the days given back).</summary>
public sealed record LeaveCancelled(Guid RequestId, Guid UserId, DateOnly From, DateOnly To) : IIntegrationEvent;

/// <summary>A journey started for a person (spec 022 section 12). IDs and the kind only.</summary>
public sealed record JourneyStarted(Guid JourneyId, Guid UserId, string Kind, DateOnly AnchorDate) : IIntegrationEvent;

/// <summary>An open journey step passed its due time.</summary>
public sealed record JourneyStepOverdue(Guid JourneyId, Guid UserId, string StepKey) : IIntegrationEvent;

/// <summary>Every step of a journey is done.</summary>
public sealed record JourneyCompleted(Guid JourneyId, Guid UserId, string Kind) : IIntegrationEvent;
