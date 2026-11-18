using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T012 (US4): percentiles, slow steps and the suppression of structured reasons.</summary>
public sealed class ServiceAnalyticsMathTests
{
    [Fact]
    public void Percentile_uses_the_nearest_rank()
    {
        double[] values = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        Assert.Equal(50, ServiceAnalytics.Percentile(values, 0.5));
        Assert.Equal(90, ServiceAnalytics.Percentile(values, 0.9));
        Assert.Equal(7, ServiceAnalytics.Percentile([7], 0.9));
    }

    [Fact]
    public void A_step_twice_as_slow_as_the_typical_step_is_marked()
    {
        var decisions = Enumerable.Range(0, 5).SelectMany(_ => new[]
        {
            new AnalyticsStepFact("check", "approved", 60, false),
            new AnalyticsStepFact("sign", "approved", 70, false),
            new AnalyticsStepFact("finance", "approved", 600, false),
        }).Append(new AnalyticsStepFact("rare", "rejected", 9000, false)).ToList();
        var steps = ServiceAnalytics.Steps(decisions, 5).ToDictionary(s => s.Step);
        Assert.True(steps["finance"].Slow);
        Assert.False(steps["check"].Slow);
        Assert.Null(steps["rare"].MedianSeconds); // One decision: under the minimum, not shown and never marked.
        Assert.False(steps["rare"].Slow);
        Assert.Equal(1, steps["rare"].Rejected);
    }

    [Fact]
    public void Reasons_hide_small_cells_and_one_more_when_a_single_one_would_be_hidden()
    {
        var answers = Enumerable.Repeat(("u1", "pay"), 6).Concat(Enumerable.Repeat(("u1", "growth"), 2)).Concat(Enumerable.Repeat(("u1", "family"), 5)).ToList();
        var (rows, all) = ServiceAnalytics.ReasonRows(answers, ["pay", "growth", "family"], 5);
        var row = Assert.Single(rows);
        Assert.Equal(13, row.Total);
        Assert.Null(row.Counts["growth"]);
        Assert.Null(row.Counts["family"]); // The smallest other count, so growth is not thirteen minus the rest.
        Assert.Equal(6, row.Counts["pay"]);
        Assert.Equal(13, all.Total);
    }
}
