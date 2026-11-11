using TeamBeats.Modules.Hr;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 T010: leave day counts, entitlement bands, accrual, carry-over and the balance rule.</summary>
public sealed class LeaveMathTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private static DateOnly D(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    private static WorkingCalendarSnapshot Calendar(int[] week, params HolidayDay[] holidays) =>
        new(Guid.NewGuid(), 1, week.Select(WorkingCalendar.ToDayOfWeek), holidays, Riyadh);

    [Fact]
    public void A_week_of_leave_counts_only_working_days_and_a_holiday_counts_as_off()
    {
        // Monday 12 October to Sunday 18 October 2026, Monday to Friday, Wednesday a holiday.
        var days = LeaveMath.Days(Calendar([1, 2, 3, 4, 5], new HolidayDay(D("2026-10-14"), false)), D("2026-10-12"), D("2026-10-18"), false, false, 1);
        Assert.Equal(4m, LeaveMath.Total(days));
        Assert.DoesNotContain(days, d => d.Date == D("2026-10-14") || d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    [Fact]
    public void Half_starts_and_ends_take_half_a_day_and_a_half_holiday_stays_half()
    {
        var calendar = Calendar([7, 1, 2, 3, 4], new HolidayDay(D("2026-11-11"), true));
        // Sunday 8 to Thursday 12 November: half start, half-day holiday on Wednesday, half end.
        Assert.Equal(3.5m, LeaveMath.Total(LeaveMath.Days(calendar, D("2026-11-08"), D("2026-11-12"), true, true, 1)));
        Assert.Equal(0.5m, LeaveMath.Total(LeaveMath.Days(calendar, D("2026-11-09"), D("2026-11-09"), true, false, 1)));
        Assert.Equal(0.5m, LeaveMath.Total(LeaveMath.Days(calendar, D("2026-11-11"), D("2026-11-11"), true, false, 1)));
    }

    [Fact]
    public void Days_across_a_year_end_count_against_the_year_they_fall_in()
    {
        var days = LeaveMath.Days(Calendar([1, 2, 3, 4, 5]), D("2026-12-28"), D("2027-01-08"), false, false, 1);
        Assert.Equal(4m, days.Where(d => d.LeaveYear == 2026).Sum(d => d.Fraction));
        Assert.Equal(6m, days.Where(d => d.LeaveYear == 2027).Sum(d => d.Fraction));
        // A leave year that starts in April keeps March in the year before.
        Assert.Equal(2026, LeaveMath.LeaveYear(D("2027-03-31"), 4));
        Assert.Equal(2027, LeaveMath.LeaveYear(D("2027-04-01"), 4));
        Assert.Equal((D("2027-04-01"), D("2028-03-31")), (LeaveMath.YearStart(2027, 4), LeaveMath.YearEnd(2027, 4)));
    }

    [Fact]
    public void The_first_matching_band_gives_the_entitlement()
    {
        LeaveBand[] bands = [new(30, Grades: ["G9", "G10"]), new(25, MinServiceYears: 5), new(21)];
        Assert.Equal(30m, LeaveMath.Entitlement(bands, 1, "g9"));
        Assert.Equal(25m, LeaveMath.Entitlement(bands, 5, "G7"));
        Assert.Equal(21m, LeaveMath.Entitlement(bands, 4, null));
        Assert.Equal(0m, LeaveMath.Entitlement([new LeaveBand(30, MinServiceYears: 10)], 3, null));
        Assert.Equal(3, LeaveMath.ServiceYears(D("2022-11-12"), D("2026-11-11")));
        Assert.Equal(5, LeaveMath.ServiceYears(D("2021-11-11"), D("2026-11-11")));
        Assert.Equal(0, LeaveMath.ServiceYears(null, D("2026-11-11")));
    }

    [Fact]
    public void Monthly_accrual_adds_up_to_the_entitlement_exactly()
    {
        Assert.Equal(21m, Enumerable.Range(0, 12).Sum(i => LeaveMath.MonthlyAccrual(21m, i)));
        Assert.Equal(1.75m, LeaveMath.MonthlyAccrual(21m, 0));
        Assert.Equal(25m, Enumerable.Range(0, 12).Sum(i => LeaveMath.MonthlyAccrual(25m, i)));
        Assert.Equal(2.08m, LeaveMath.MonthlyAccrual(25m, 0));
        Assert.Equal(2.12m, LeaveMath.MonthlyAccrual(25m, 11));
        Assert.Equal(0, LeaveMath.MonthIndex(D("2026-04-15"), 4));
        Assert.Equal(11, LeaveMath.MonthIndex(D("2027-03-01"), 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => LeaveMath.MonthlyAccrual(21m, 12));
    }

    [Theory]
    [InlineData(8, 5, 5, 3)]
    [InlineData(3, 5, 3, 0)]
    [InlineData(0, 5, 0, 0)]
    [InlineData(-2, 5, -2, 0)]
    public void Carry_over_keeps_up_to_the_limit_and_carries_a_negative_balance_in_full(decimal balance, decimal limit, decimal carry, decimal lapse) =>
        Assert.Equal((carry, lapse), LeaveMath.CarryOver(balance, limit));

    [Theory]
    [InlineData(5, 5, false, 0, true)]
    [InlineData(4, 5, false, 0, false)]
    [InlineData(4, 5, true, 1, true)]
    [InlineData(4, 6, true, 1, false)]
    public void A_balance_never_goes_below_zero_unless_the_type_allows_it_and_then_not_below_its_maximum(decimal available, decimal days, bool negative,
        decimal max, bool allowed) =>
        Assert.Equal(allowed, LeaveMath.Allowed(available, days, negative, max));

    [Fact]
    public void Reduced_hours_apply_to_working_days_inside_a_period_only_and_a_holiday_is_not_one()
    {
        var calendar = new WorkingCalendarSnapshot(null, 1, [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday],
            [new HolidayDay(new DateOnly(2027, 2, 15), false)], TimeZoneInfo.Utc, [new ReducedHoursPeriod(new DateOnly(2027, 2, 8), new DateOnly(2027, 3, 9), 6m)]);
        Assert.Equal(6m, calendar.ReducedHours(new DateOnly(2027, 2, 8)));
        Assert.Equal(6m, calendar.ReducedHours(new DateOnly(2027, 3, 9)));
        Assert.Null(calendar.ReducedHours(new DateOnly(2027, 2, 12)));
        Assert.Null(calendar.ReducedHours(new DateOnly(2027, 2, 15)));
        Assert.Null(calendar.ReducedHours(new DateOnly(2027, 3, 10)));
        Assert.Single(calendar.Periods);
    }
}
