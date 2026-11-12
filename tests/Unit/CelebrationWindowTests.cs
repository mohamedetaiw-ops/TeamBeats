using TeamBeats.Modules.Engagement;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 023 FR-015: the celebrations window. A 29 February birthday counts on the 28th outside leap years.</summary>
public sealed class CelebrationWindowTests
{
    [Fact]
    public void The_window_covers_seven_days_from_today()
    {
        var window = Celebrations.Window(new DateOnly(2026, 12, 29), 7);
        Assert.Equal(new DateOnly(2026, 12, 29), window[0].Date);
        Assert.Equal(new DateOnly(2027, 1, 4), window[^1].Date);
        Assert.Equal(7, window.Count);
    }

    [Fact]
    public void Leap_day_birthdays_fall_on_the_28th_in_other_years()
    {
        var common = Celebrations.Window(new DateOnly(2027, 2, 27), 3);
        Assert.Contains(common, w => w.Month == 2 && w.Day == 29 && w.Date == new DateOnly(2027, 2, 28));
        var leap = Celebrations.Window(new DateOnly(2028, 2, 27), 3);
        Assert.Single(leap, w => w.Month == 2 && w.Day == 29);
        Assert.Contains(leap, w => w.Month == 2 && w.Day == 29 && w.Date == new DateOnly(2028, 2, 29));
    }
}
