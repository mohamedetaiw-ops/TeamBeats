using TeamBeats.Modules.Analytics;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T020 (FR-002): area dashboard groups fold and hide behind the minimum group size.</summary>
public sealed class AnalyticsAreaMathTests
{
    [Fact]
    public void Small_groups_fold_into_Other_and_one_small_group_takes_the_next_smallest_with_it()
    {
        var rows = AreaDashboards.Fold([new GroupFigure("a", 40, 12), new GroupFigure("b", 9, 6), new GroupFigure("c", 3, 2)], 5);
        Assert.Equal(["a", ItemAnalytics.Other], rows.Select(r => r.Key));
        Assert.Equal(12, rows[^1].Count);
        Assert.Null(rows[^1].People);

        // Alone, the small group gives Other no figures at all.
        var alone = AreaDashboards.Fold([new GroupFigure("c", 3, 2)], 5);
        Assert.Equal(ItemAnalytics.Other, Assert.Single(alone).Key);
        Assert.Null(alone[0].Count);

        // Large groups keep their figures, the largest first; a share goes to the rate column when asked.
        var shares = AreaDashboards.Fold([new GroupFigure("x", 5, 5, Amount: 0.5), new GroupFigure("y", 8, 7, Amount: 0.25)], 5, rate: true);
        Assert.Equal(["y", "x"], shares.Select(r => r.Key));
        Assert.Equal(0.25, shares[0].Rate);
        Assert.Null(shares[0].Amount);
    }

    [Fact]
    public void Weeks_stay_in_order_and_small_weeks_show_no_figures()
    {
        var weeks = AreaDashboards.Weeks([new GroupFigure("2026-11-02", 30, 9, Amount: 4), new GroupFigure("2026-11-09", 2, 1)], 5);
        Assert.Equal(["2026-11-02", "2026-11-09"], weeks.Select(w => w.Key));
        Assert.Equal(30, weeks[0].Count);
        Assert.Equal(4, weeks[0].Amount);
        Assert.Null(weeks[1].Count);
        Assert.Null(weeks[1].People);
    }

    [Fact]
    public void A_zero_is_shown_and_a_small_group_is_not()
    {
        Assert.True(AreaDashboards.Shown(0, 5));
        Assert.False(AreaDashboards.Shown(4, 5));
        Assert.True(AreaDashboards.Shown(5, 5));
    }
}
