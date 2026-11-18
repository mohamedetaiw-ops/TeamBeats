using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T015 (US9): p75 vitals, flags against the spec 032 targets, and task flows on the outcome contract.</summary>
public sealed class ExperienceAnalyticsMathTests
{
    [Fact]
    public void Only_a_published_variant_with_enough_samples_missing_a_target_is_flagged()
    {
        var page = Guid.NewGuid();
        var old = Guid.NewGuid();
        var pages = new[] { new PageInventoryItem(page, "T01", "1", "two_column", null, 2), new PageInventoryItem(old, "T02", "1", null, null, null) };
        var samples = Enumerable.Range(1, 8).Select(i => (page, (int?)2, "mobile", (string?)"beats", (double?)(i * 500), (double?)150, (double?)0.15))
            .Concat(Enumerable.Range(1, 8).Select(_ => (page, (int?)1, "mobile", (string?)"beats", (double?)9000, (double?)900, (double?)1.0))) // An old release.
            .Concat(Enumerable.Range(1, 3).Select(_ => (page, (int?)2, "desktop", (string?)null, (double?)9000, (double?)null, (double?)null))) // Too few.
            .Concat([(Guid.NewGuid(), (int?)1, "mobile", (string?)null, (double?)1, (double?)1, (double?)0)]) // Not in the inventory.
            .ToList();
        var report = ExperienceAnalytics.Performance(pages, samples);
        Assert.Equal(19, report.Samples);
        var flag = Assert.Single(report.Flags);
        Assert.Equal((2, "mobile", 3000.0), (flag.Release!.Value, flag.DeviceClass, flag.Lcp!.Value));
        Assert.Equal(["lcp", "cls"], flag.Missed);
        Assert.Contains(report.Rows, r => r.Dimension == "theme" && r.Group == "none" && r.Samples == 3);
        Assert.Null(report.Rows.Single(r => r.Dimension == "device" && r.Group == "desktop").Inp);
    }

    [Fact]
    public void A_flow_shows_its_rate_and_drop_off_step_only_with_the_minimum()
    {
        var rows = new List<(string, string, string?, int)> { ("find_policy", "start", null, 10), ("find_policy", "complete", null, 7), ("find_policy", "abandon", "search", 3),
            ("submit", "start", null, 6), ("submit", "abandon", "form", 5), ("submit", "complete", null, 1) };
        var flows = ExperienceAnalytics.Flows(rows, new Dictionary<string, int> { ["find_policy"] = 4, ["submit"] = 6 }, 5).ToDictionary(f => f.Flow);
        Assert.Null(flows["find_policy"].CompletionRate); // Four people started it.
        Assert.Null(flows["find_policy"].DropOffStep);
        Assert.Equal(0.1667, flows["submit"].CompletionRate);
        Assert.Equal("form", flows["submit"].DropOffStep);
    }

    [Fact]
    public void Page_vitals_are_essential_and_carry_no_person()
    {
        var schema = AnalyticsEvents.Schemas[AnalyticsEvents.PageVitals];
        Assert.True(schema.Essential);
        Assert.False(schema.UserKeyed);
        Assert.Equal(AnalyticsEvents.DeviceClasses, schema.Fields["deviceClass"].Allowed);
    }
}
