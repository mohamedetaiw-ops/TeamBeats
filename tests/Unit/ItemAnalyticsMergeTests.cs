using TeamBeats.Modules.Analytics;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 US1 scenario 2 and the differencing edge case: no group under the minimum is ever shown, "other" included.</summary>
public sealed class ItemAnalyticsMergeTests
{
    private static Dictionary<string, List<int>> Merge(int minimum, params (string Key, int Count)[] groups) =>
        ItemAnalytics.Merge(groups.SelectMany(g => Enumerable.Repeat(g.Key, g.Count)).Select((k, i) => (k, i)).GroupBy(p => p.k, p => p.i), minimum);

    [Fact]
    public void Groups_at_the_minimum_are_shown_and_smaller_ones_are_merged()
    {
        var merged = Merge(5, ("a", 5), ("b", 3), ("c", 4));
        Assert.Equal(["a", ItemAnalytics.Other], merged.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(7, merged[ItemAnalytics.Other].Count);
    }

    [Fact]
    public void One_small_group_is_never_left_alone_in_other()
    {
        var merged = Merge(5, ("a", 6), ("b", 9), ("c", 2));
        Assert.Equal(["b", ItemAnalytics.Other], merged.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(8, merged[ItemAnalytics.Other].Count);
    }

    [Fact]
    public void A_tenant_minimum_above_five_applies_and_nothing_is_lost()
    {
        var merged = Merge(10, ("a", 12), ("b", 9), ("c", 30));
        Assert.Equal(["c", ItemAnalytics.Other], merged.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(51, merged.Values.Sum(v => v.Count));
        Assert.All(merged.Values, v => Assert.True(v.Count >= 10));
    }

    [Fact]
    public void Without_small_groups_there_is_no_other()
    {
        Assert.DoesNotContain(ItemAnalytics.Other, Merge(5, ("a", 5), ("b", 5)).Keys);
        Assert.Empty(Merge(5));
    }
}

/// <summary>Spec 021 T007: unit counts under the minimum are merged the same way, and weeks start on Monday (ISO).</summary>
public sealed class AdoptionCountsTests
{
    [Fact]
    public void Unit_counts_under_the_minimum_are_never_shown_alone()
    {
        var merged = Adoption.MergeCounts([("a", 6), ("b", 9), ("c", 2), ("d", 0)], 5);
        Assert.Equal([("b", 9), (ItemAnalytics.Other, 8)], merged);
        Assert.Empty(Adoption.MergeCounts([], 5));
        Assert.Equal([(ItemAnalytics.Other, 4)], Adoption.MergeCounts([("a", 4)], 5));
    }

    [Theory]
    [InlineData("2026-11-09", "2026-11-09")]
    [InlineData("2026-11-15", "2026-11-09")]
    [InlineData("2026-11-11", "2026-11-09")]
    public void A_week_starts_on_monday(string day, string monday) =>
        Assert.Equal(DateOnly.Parse(monday), Adoption.WeekOf(DateOnly.Parse(day)));
}
