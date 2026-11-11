using TeamBeats.Modules.Hr.Contracts;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>One day a leave request takes: the fraction of a working day and the leave year it counts against.</summary>
public sealed record LeaveDay(DateOnly Date, decimal Fraction, int LeaveYear);

/// <summary>
/// One entitlement band of a policy: the days a year for people with at least <see cref="MinServiceYears"/> of service
/// and, when <see cref="Grades"/> is given, one of those grades. The first matching band wins.
/// </summary>
public sealed record LeaveBand(decimal Days, int? MinServiceYears = null, IReadOnlyList<string>? Grades = null);

/// <summary>Pure leave arithmetic (spec 022 FR-001, FR-002 and the R2 plan defaults), unit tested.</summary>
public static class LeaveMath
{
    public const decimal MaxDaysPerYear = 366m;

    /// <summary>The leave year a date falls in: the year in which the leave year containing the date starts.</summary>
    public static int LeaveYear(DateOnly date, int startMonth) => date.Month >= startMonth ? date.Year : date.Year - 1;

    public static DateOnly YearStart(int leaveYear, int startMonth) => new(leaveYear, startMonth, 1);

    public static DateOnly YearEnd(int leaveYear, int startMonth) => YearStart(leaveYear, startMonth).AddYears(1).AddDays(-1);

    /// <summary>
    /// The working days from <paramref name="start"/> to <paramref name="end"/> on the person's calendar. A half start
    /// or end takes half of that day; a one-day request with either half takes half a day. Weekends and holidays are left
    /// out; a half-day holiday counts as half.
    /// </summary>
    public static IReadOnlyList<LeaveDay> Days(WorkingCalendarSnapshot calendar, DateOnly start, DateOnly end, bool startHalf, bool endHalf, int startMonth)
    {
        var days = new List<LeaveDay>();
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var fraction = calendar.WorkingFraction(d);
            if ((d == start && startHalf) || (d == end && endHalf))
            {
                fraction = Math.Min(fraction, 0.5m);
            }
            if (fraction > 0)
            {
                days.Add(new LeaveDay(d, fraction, LeaveYear(d, startMonth)));
            }
        }
        return days;
    }

    public static decimal Total(IEnumerable<LeaveDay> days) => days.Sum(d => d.Fraction);

    /// <summary>Whole years of service on a date; zero before the hire date or without one.</summary>
    public static int ServiceYears(DateOnly? hired, DateOnly on)
    {
        if (hired is not { } h || on < h)
        {
            return 0;
        }
        var years = on.Year - h.Year;
        return h.AddYears(years) > on ? years - 1 : years;
    }

    /// <summary>The yearly entitlement of the first band that matches, or zero when none does.</summary>
    public static decimal Entitlement(IReadOnlyList<LeaveBand> bands, int serviceYears, string? grade)
    {
        foreach (var band in bands)
        {
            if (band.MinServiceYears is { } min && serviceYears < min)
            {
                continue;
            }
            if (band.Grades is { Count: > 0 } grades && (grade is null || !grades.Contains(grade, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }
            return band.Days;
        }
        return 0m;
    }

    /// <summary>
    /// The accrual of month <paramref name="monthIndex"/> (0 for the first month of the leave year): a twelfth of the
    /// entitlement rounded to two places, with the last month taking what is left so the year adds up exactly.
    /// </summary>
    public static decimal MonthlyAccrual(decimal entitlement, int monthIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(monthIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(monthIndex, 11);
        var month = Math.Round(entitlement / 12m, 2, MidpointRounding.AwayFromZero);
        return monthIndex < 11 ? month : entitlement - month * 11;
    }

    /// <summary>The month of the leave year a date falls in, 0 to 11.</summary>
    public static int MonthIndex(DateOnly date, int startMonth) => ((date.Month - startMonth) % 12 + 12) % 12;

    /// <summary>
    /// What moves to the next leave year and what lapses: a positive balance carries up to the limit and the rest lapses;
    /// a negative balance carries in full, so days taken in advance are not forgiven.
    /// </summary>
    public static (decimal Carry, decimal Lapse) CarryOver(decimal balance, decimal limit) =>
        balance <= 0 ? (balance, 0m) : (Math.Min(balance, limit), balance - Math.Min(balance, limit));

    /// <summary>
    /// Whether taking <paramref name="days"/> leaves an allowed balance: never below zero unless the type allows a
    /// negative balance, and then never below its maximum.
    /// </summary>
    public static bool Allowed(decimal available, decimal days, bool negativeAllowed, decimal negativeMax) =>
        available - days >= (negativeAllowed ? -negativeMax : 0m);
}
