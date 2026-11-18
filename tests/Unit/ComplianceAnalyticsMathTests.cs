using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T013 (US5): the compliance trend and the folding of small groups.</summary>
public sealed class ComplianceAnalyticsMathTests
{
    private static AnalyticsComplianceMember M(string status, DateTimeOffset? at = null, string? location = null) => new(null, location, status, at);

    [Fact]
    public void The_trend_counts_attestations_by_day_to_the_deadline_and_samples_a_long_campaign()
    {
        var launched = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var members = new[] { M("attested", launched.AddHours(2)), M("attested", launched.AddDays(2)), M("open"), M("overdue") };
        var campaign = new AnalyticsComplianceCampaign(Guid.NewGuid(), Guid.NewGuid(), 1, new Dictionary<string, string>(), launched, launched.AddDays(3), members);
        var trend = ComplianceAnalytics.Trend(campaign, launched.AddDays(30));
        Assert.Equal([0.25, 0.25, 0.5, 0.5], trend.Select(p => p.Rate));
        Assert.Equal(new DateOnly(2026, 3, 4), trend[^1].Day);

        var year = campaign with { Deadline = launched.AddDays(365) };
        var sampled = ComplianceAnalytics.Trend(year, launched.AddDays(400));
        Assert.True(sampled.Count <= ComplianceAnalytics.MaxPoints + 1);
        Assert.Equal(DateOnly.FromDateTime(launched.AddDays(365).UtcDateTime), sampled[^1].Day);
        // Before the deadline the trend ends today.
        Assert.Equal(new DateOnly(2026, 3, 2), ComplianceAnalytics.Trend(campaign, launched.AddDays(1))[^1].Day);
    }

    [Fact]
    public void Small_groups_fold_into_other_until_it_reaches_the_minimum()
    {
        var members = Enumerable.Repeat(M("attested", location: "dubai"), 6).Concat(Enumerable.Repeat(M("open", location: "cairo"), 5))
            .Concat(Enumerable.Repeat(M("overdue", location: "muscat"), 2)).ToList();
        var groups = ComplianceAnalytics.Groups(members, m => m.Location, 5);
        Assert.Equal(["dubai", ItemAnalytics.Other], groups.Select(g => g.Group));
        Assert.Equal(new ComplianceCountsView(7, 0, 2, 0), groups[1].Counts);
        Assert.Equal(new ComplianceCountsView(3, null, null, null), ComplianceAnalytics.Counts(members.Take(3).ToList(), 5));
        // Everything folds when one group would otherwise be the total minus the rest, and "other" under the minimum is left out.
        Assert.Equal([ItemAnalytics.Other], ComplianceAnalytics.Groups(members.Take(8).ToList(), m => m.Location, 5).Select(g => g.Group));
        Assert.Empty(ComplianceAnalytics.Groups(members.Take(3).ToList(), m => m.Location, 5));
    }
}
