using TeamBeats.Modules.Analytics;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T011 (FR-004): campaign breakdowns fold small units and locations so none can be worked out from the others.</summary>
public sealed class CampaignAnalyticsMergeTests
{
    [Fact]
    public void Merge_folds_small_groups_per_dimension()
    {
        var merged = CampaignAnalytics.Merge(
        [
            new("unit", "u1", 6, 2, 1), new("unit", "u2", 5, 1, 1), new("unit", "u3", 2, 0, 0),
            new("location", "Dubai", 9, 3, 2), new("location", "Cairo", 1, 0, 0),
        ], 5);
        Assert.Equal(new[] { ("location", ItemAnalytics.Other, 10, 3, 2), ("unit", "u1", 6, 2, 1), ("unit", ItemAnalytics.Other, 7, 1, 1) }, merged.ToArray());
    }

    [Fact]
    public void Counts_hide_what_people_did_under_the_minimum()
    {
        var small = CampaignAnalytics.Counts(4, 4, 2, 1, 1, 5);
        Assert.Null(small.Viewed);
        Assert.Null(small.CompletedShare);
        var big = CampaignAnalytics.Counts(8, 8, 2, 1, 1, 5);
        Assert.Equal(0.25, big.ViewedShare);
    }
}
