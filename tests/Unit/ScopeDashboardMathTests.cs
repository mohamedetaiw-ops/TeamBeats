using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T014 (US6): tiles from a scope's and the organization's figures with the minimum group size.</summary>
public sealed class ScopeDashboardMathTests
{
    [Fact]
    public void A_share_needs_the_minimum_behind_it_and_a_count_is_per_person()
    {
        var share = ScopeDashboards.Tile("compliance", new AnalyticsScopeFigure("compliance", 3, 6), 10, new AnalyticsScopeFigure("compliance", 10, 40), 100, 5);
        Assert.Equal(new ScopeTile("compliance", 3, 6, 0.5, 0.25, true), share);
        var few = ScopeDashboards.Tile("compliance", new AnalyticsScopeFigure("compliance", 3, 4), 10, null, 100, 5);
        Assert.False(few.Enough);
        Assert.Null(few.Value);
        var count = ScopeDashboards.Tile("open_requests", new AnalyticsScopeFigure("open_requests", 4, null), 8, new AnalyticsScopeFigure("open_requests", 10, null), 100, 5);
        Assert.Equal(new ScopeTile("open_requests", 4, null, 0.5, 0.1, true), count);
        // Without an organization figure the group rate is null.
        Assert.Null(ScopeDashboards.Tile("adoption", new AnalyticsScopeFigure("adoption", 5, 8), 8, null, 100, 5).GroupRate);
    }
}
