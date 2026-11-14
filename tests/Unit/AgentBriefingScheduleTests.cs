using TeamBeats.Modules.Agents;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 020 US8: when the next daily briefing is due, in the subscriber's time zone and on their chosen weekdays.</summary>
public sealed class AgentBriefingScheduleTests
{
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Fact]
    public void Later_the_same_working_day_is_next()
    {
        // Monday 2026-11-16 04:00 UTC is 07:00 in Riyadh, before an 08:00 briefing.
        var next = AgentBriefings.NextRun(new DateTimeOffset(2026, 11, 16, 4, 0, 0, TimeSpan.Zero), new TimeOnly(8, 0), [1, 2, 3, 4, 5], Riyadh);
        Assert.Equal(new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void After_Friday_the_next_is_Monday()
    {
        // Friday 2026-11-20 06:00 UTC is 09:00 in Riyadh, after the 08:00 briefing; the weekend is skipped.
        var next = AgentBriefings.NextRun(new DateTimeOffset(2026, 11, 20, 6, 0, 0, TimeSpan.Zero), new TimeOnly(8, 0), [1, 2, 3, 4, 5], Riyadh);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 5, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Summer_time_moves_the_hour_in_utc_but_not_on_the_wall_clock()
    {
        var winter = AgentBriefings.NextRun(new DateTimeOffset(2026, 1, 5, 0, 0, 0, TimeSpan.Zero), new TimeOnly(8, 0), [1], London);
        var summer = AgentBriefings.NextRun(new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero), new TimeOnly(8, 0), [1], London);
        Assert.Equal(8, winter.Hour);
        Assert.Equal(7, summer.Hour);
    }

    [Fact]
    public void The_exact_moment_is_not_due_again()
    {
        var at = new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero);
        Assert.Equal(at.AddDays(1), AgentBriefings.NextRun(at, new TimeOnly(8, 0), [1, 2, 3, 4, 5], Riyadh));
    }
}
