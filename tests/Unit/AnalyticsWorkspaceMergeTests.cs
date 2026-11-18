using TeamBeats.Modules.Analytics;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T010 (FR-004): the workspace breakdown folds small groups so no group can be worked out by subtracting.</summary>
public sealed class AnalyticsWorkspaceMergeTests
{
    [Fact]
    public void Merge_keeps_other_at_or_above_the_minimum_by_folding_the_smallest_shown_group()
    {
        static IEnumerable<(string, string, int)> People(string group, int count) => Enumerable.Range(1, count).Select(i => (group, group + i, 1));
        // Three groups of six and one of two: the two fold, "other" is still under five, so the smallest shown group folds too.
        var merged = Workspace.MergeGroups([.. People("a", 7), .. People("b", 6), .. People("c", 2)], 5, true);
        Assert.Equal(new[] { ("a", 7), (ItemAnalytics.Other, 8) }, merged.ToArray());
        // Overlapping people in "other" are counted once: two people plus five, not eight.
        var overlap = Workspace.MergeGroups([("x", "p1", 1), ("y", "p1", 1), ("z", "p2", 1), .. People("big", 5)], 5, true);
        Assert.Equal(new[] { (ItemAnalytics.Other, 7) }, overlap.ToArray());
        // Nothing small: every group is shown and there is no "other".
        Assert.Equal(new[] { ("a", 7), ("b", 6) }, Workspace.MergeGroups([.. People("a", 7), .. People("b", 6)], 5, true).ToArray());
    }
}
