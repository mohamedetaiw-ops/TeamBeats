using TeamBeats.Modules.Hr;
using TeamBeats.Modules.Hr.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 T003: working-day arithmetic over a calendar snapshot.</summary>
public sealed class WorkingDayTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");

    private static WorkingCalendarSnapshot Calendar(int[] week, params HolidayDay[] holidays) =>
        new(Guid.NewGuid(), 1, week.Select(WorkingCalendar.ToDayOfWeek), holidays, Dubai);

    private static readonly int[] MondayToFriday = [1, 2, 3, 4, 5];
    private static readonly int[] SundayToThursday = [7, 1, 2, 3, 4];

    private static DateOnly D(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("2026-11-06", false)] // Friday
    [InlineData("2026-11-07", false)] // Saturday
    [InlineData("2026-11-08", true)] // Sunday
    [InlineData("2026-11-12", true)] // Thursday
    public void A_Sunday_to_Thursday_week_works_on_Sunday_and_rests_on_Friday(string date, bool working) =>
        Assert.Equal(working, Calendar(SundayToThursday).IsWorkingDay(D(date)));

    [Fact]
    public void Counting_skips_weekends_and_holidays_and_counts_half_days_as_half()
    {
        var calendar = Calendar(MondayToFriday, new HolidayDay(D("2026-12-02"), false), new HolidayDay(D("2026-12-03"), true));
        // Monday 30 November to Friday 4 December: five weekdays, one holiday, one half day.
        Assert.Equal(3.5m, calendar.CountWorkingDays(D("2026-11-30"), D("2026-12-04")));
        Assert.Equal(0m, calendar.CountWorkingDays(D("2026-12-05"), D("2026-12-06")));
        Assert.Equal(0m, calendar.CountWorkingDays(D("2026-12-04"), D("2026-12-01")));
    }

    [Fact]
    public void A_holiday_on_a_weekend_changes_nothing() =>
        Assert.Equal(5m, Calendar(MondayToFriday, new HolidayDay(D("2026-12-05"), false)).CountWorkingDays(D("2026-11-30"), D("2026-12-06")));

    [Fact]
    public void Adding_working_days_crosses_weekends_holidays_and_the_year_end()
    {
        var calendar = Calendar(MondayToFriday, new HolidayDay(D("2027-01-01"), false));
        // Wednesday 30 December plus two: Thursday 31, skip the Friday holiday and the weekend, Monday 4 January.
        Assert.Equal(D("2027-01-04"), calendar.AddWorkingDays(D("2026-12-30"), 2));
        Assert.Equal(D("2026-12-30"), calendar.AddWorkingDays(D("2026-12-30"), 0));
        Assert.Equal(D("2026-12-30"), calendar.AddWorkingDays(D("2027-01-04"), -2));
    }

    [Fact]
    public void A_half_day_counts_as_a_working_day_when_adding()
    {
        var calendar = Calendar(MondayToFriday, new HolidayDay(D("2026-12-01"), true));
        Assert.Equal(D("2026-12-01"), calendar.AddWorkingDays(D("2026-11-30"), 1));
        Assert.Equal(D("2026-12-01"), calendar.NextWorkingDayOnOrAfter(D("2026-11-28")) .AddDays(1));
    }

    [Fact]
    public void Today_is_the_date_in_the_tenant_time_zone()
    {
        var calendar = Calendar(MondayToFriday);
        // 21:00 UTC on 7 November is 01:00 on 8 November in Dubai.
        Assert.Equal(D("2026-11-08"), calendar.Today(new DateTimeOffset(2026, 11, 7, 21, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void A_week_without_working_days_is_refused() =>
        Assert.Throws<ArgumentException>(() => Calendar([]));

    [Fact]
    public void Iso_day_numbers_map_to_days_of_the_week()
    {
        Assert.Equal(DayOfWeek.Monday, WorkingCalendar.ToDayOfWeek(1));
        Assert.Equal(DayOfWeek.Sunday, WorkingCalendar.ToDayOfWeek(7));
    }
}
