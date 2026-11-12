using TeamBeats.Modules.Engagement;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 023 FR-013: leaderboard periods. Weeks run Sunday to Saturday; months and quarters are calendar ones.</summary>
public sealed class PointsPeriodTests
{
    [Theory]
    [InlineData("week", "2026-11-12", "2026-11-08", "2026-11-14")]
    [InlineData("week", "2026-11-08", "2026-11-08", "2026-11-14")]
    [InlineData("month", "2026-02-10", "2026-02-01", "2026-02-28")]
    [InlineData("quarter", "2026-11-12", "2026-10-01", "2026-12-31")]
    [InlineData("quarter", "2026-01-01", "2026-01-01", "2026-03-31")]
    public void Periods_cover_the_right_days(string period, string today, string from, string to)
    {
        var (f, t) = Points.PeriodOf(period, DateOnly.Parse(today));
        Assert.Equal(DateOnly.Parse(from), f);
        Assert.Equal(DateOnly.Parse(to), t);
    }
}
