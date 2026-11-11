using TeamBeats.Modules.Hr;
using TeamBeats.Modules.Hr.Attendance;
using TeamBeats.Modules.Hr.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 T020: one day's punches against the working hours rule and the calendar (US3 scenarios 2 and 4).</summary>
public sealed class AttendanceMathTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    // 2027-02-08 is a Monday; the week runs Sunday to Thursday, so 2027-02-12 (Friday) is a day off.
    private static readonly DateOnly Monday = new(2027, 2, 8);
    private static readonly DateOnly Friday = new(2027, 2, 12);
    private static readonly DateOnly Holiday = new(2027, 2, 15);
    private static readonly DateOnly HalfHoliday = new(2027, 2, 16);
    private static readonly DateOnly Later = new(2027, 3, 1);

    private static WorkingCalendarSnapshot Calendar(params ReducedHoursPeriod[] periods) =>
        new(null, 1, [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday],
            [new HolidayDay(Holiday, false), new HolidayDay(HalfHoliday, true)], Riyadh, periods);

    private static Punch P(DateOnly date, int hour, int minute, string direction, bool remote = false)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute));
        return new Punch(new DateTimeOffset(local, Riyadh.GetUtcOffset(local)).ToUniversalTime(), direction, remote, PunchSources.Web);
    }

    private static AttendanceDay Day(DateOnly date, IReadOnlyList<Punch> punches, decimal leave = 0m, WorkingCalendarSnapshot? calendar = null, DateOnly? today = null) =>
        AttendanceMath.Evaluate(date, punches, WorkRule.Default, calendar ?? Calendar(), leave, today ?? Later);

    [Fact]
    public void An_on_time_day_inside_the_grace_minutes_has_no_exceptions_and_shows_local_times()
    {
        var day = Day(Monday, [P(Monday, 9, 10, "in"), P(Monday, 16, 50, "out")]);
        Assert.Equal(AttendanceDayKinds.Working, day.Kind);
        Assert.Equal(new TimeOnly(9, 10), day.FirstIn);
        Assert.Equal(new TimeOnly(16, 50), day.LastOut);
        Assert.Equal(7.67m, day.HoursWorked);
        Assert.Equal(8m, day.ExpectedHours);
        Assert.Empty(day.Flags);
        Assert.Equal(2, day.Punches);
    }

    [Fact]
    public void Arriving_after_the_grace_and_leaving_before_it_flags_late_and_early_leave()
    {
        var day = Day(Monday, [P(Monday, 9, 16, "in"), P(Monday, 16, 44, "out")]);
        Assert.Equal([AttendanceFlags.Late, AttendanceFlags.EarlyLeave], day.Flags);
    }

    [Fact]
    public void A_break_counts_only_the_time_signed_in_and_punches_out_of_order_are_sorted()
    {
        var day = Day(Monday, [P(Monday, 13, 0, "in"), P(Monday, 9, 0, "in"), P(Monday, 17, 0, "out"), P(Monday, 12, 0, "out")]);
        Assert.Equal(7m, day.HoursWorked);
        Assert.Empty(day.Flags);
    }

    [Fact]
    public void An_open_sign_in_is_a_missing_punch_only_once_the_day_is_over()
    {
        Assert.Contains(AttendanceFlags.MissingPunch, Day(Monday, [P(Monday, 9, 0, "in")]).Flags);
        var today = Day(Monday, [P(Monday, 9, 0, "in")], today: Monday);
        Assert.Empty(today.Flags);
        Assert.Equal(0m, today.HoursWorked);
        Assert.Contains(AttendanceFlags.MissingPunch, Day(Monday, [P(Monday, 9, 0, "out")], today: Monday).Flags);
        Assert.Contains(AttendanceFlags.MissingPunch, Day(Monday, [P(Monday, 9, 0, "in"), P(Monday, 10, 0, "in"), P(Monday, 17, 0, "out")]).Flags);
    }

    [Fact]
    public void A_past_working_day_with_no_punches_is_absent_but_today_and_a_day_off_are_not()
    {
        Assert.Equal([AttendanceFlags.Absent], Day(Monday, []).Flags);
        Assert.Empty(Day(Monday, [], today: Monday).Flags);
        var off = Day(Friday, []);
        Assert.Equal(AttendanceDayKinds.DayOff, off.Kind);
        Assert.Equal(0m, off.ExpectedHours);
        Assert.Empty(off.Flags);
    }

    [Fact]
    public void Work_on_a_holiday_or_a_day_off_is_flagged_so_a_compensation_day_can_follow()
    {
        var holiday = Day(Holiday, [P(Holiday, 10, 0, "in"), P(Holiday, 14, 0, "out")]);
        Assert.Equal(AttendanceDayKinds.Holiday, holiday.Kind);
        Assert.Equal(4m, holiday.HoursWorked);
        Assert.Equal(0m, holiday.ExpectedHours);
        Assert.Equal([AttendanceFlags.WorkedDayOff], holiday.Flags);
        Assert.Equal([AttendanceFlags.WorkedDayOff], Day(Friday, [P(Friday, 10, 0, "in"), P(Friday, 11, 0, "out")]).Flags);
    }

    [Fact]
    public void Approved_leave_removes_the_exceptions_and_a_half_day_halves_the_expected_hours()
    {
        var leave = Day(Monday, [], leave: 1m);
        Assert.Equal(AttendanceDayKinds.Leave, leave.Kind);
        Assert.Equal(0m, leave.ExpectedHours);
        Assert.Empty(leave.Flags);
        var half = Day(Monday, [P(Monday, 13, 0, "in"), P(Monday, 17, 0, "out")], leave: 0.5m);
        Assert.Equal(AttendanceDayKinds.Working, half.Kind);
        Assert.Equal(4m, half.ExpectedHours);
        Assert.Empty(half.Flags);
        Assert.Equal(4m, Day(HalfHoliday, []).ExpectedHours);
        Assert.Empty(Day(HalfHoliday, []).Flags);
    }

    [Fact]
    public void Inside_a_reduced_hours_period_the_day_ends_that_many_hours_after_the_start()
    {
        var calendar = Calendar(new ReducedHoursPeriod(Monday, Monday, 6m));
        var day = Day(Monday, [P(Monday, 9, 0, "in"), P(Monday, 15, 0, "out")], calendar: calendar);
        Assert.Equal(6m, day.ExpectedHours);
        Assert.Empty(day.Flags);
        Assert.Contains(AttendanceFlags.EarlyLeave, Day(Monday, [P(Monday, 9, 0, "in"), P(Monday, 14, 0, "out")], calendar: calendar).Flags);
    }

    [Fact]
    public void Totals_count_worked_days_hours_and_each_exception_for_the_monthly_summary()
    {
        var days = new[]
        {
            Day(Monday, [P(Monday, 9, 30, "in", remote: true), P(Monday, 17, 0, "out")]),
            Day(Monday.AddDays(1), []),
            Day(Holiday, [P(Holiday, 10, 0, "in"), P(Holiday, 12, 0, "out")]),
        };
        var totals = AttendanceMath.Totals(days);
        Assert.Equal(2, totals.WorkedDays);
        Assert.Equal(9.5m, totals.HoursWorked);
        Assert.Equal(16m, totals.ExpectedHours);
        Assert.Equal(1, totals.Late);
        Assert.Equal(1, totals.Absent);
        Assert.Equal(1, totals.Remote);
        Assert.Equal(1, totals.WorkedDaysOff);
        Assert.Equal(0, totals.MissingPunch);
    }

    [Fact]
    public void The_distance_check_measures_great_circle_metres()
    {
        Assert.Equal(0, AttendanceMath.DistanceMeters(25.2, 55.27, 25.2, 55.27), 3);
        var oneDegree = AttendanceMath.DistanceMeters(0, 0, 1, 0);
        Assert.InRange(oneDegree, 111_000, 111_400);
    }

    [Fact]
    public void A_compensation_day_is_a_full_day_for_a_full_working_day_and_a_half_for_at_least_half()
    {
        Assert.Equal(1m, AttendanceMath.CompensationDays(8m, WorkRule.Default));
        Assert.Equal(1m, AttendanceMath.CompensationDays(9.5m, WorkRule.Default));
        Assert.Equal(0.5m, AttendanceMath.CompensationDays(4m, WorkRule.Default));
        Assert.Equal(0m, AttendanceMath.CompensationDays(3.99m, WorkRule.Default));
        Assert.Equal(1m, AttendanceMath.CompensationDays(6m, new WorkRule(new TimeOnly(8, 0), new TimeOnly(14, 0), 0, 0)));
    }
}
