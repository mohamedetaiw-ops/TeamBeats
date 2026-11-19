using System.Text.Json;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Analytics;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 021 T017 (US8 scenario 3, FR-016): summary figures and their check, and small AI use cases folding away.</summary>
public sealed class AnalyticsAiMathTests
{
    private static SummaryFigure F(string label, double value) => new(label, JsonSerializer.SerializeToElement(value));

    private static readonly IReadOnlyList<SummaryFigure> Figures = [F("requests", 120), F("assistant.answerRate", 0.8333), F("weeks[2026-11-09].requests", 41)];

    [Fact]
    public void Figures_are_labelled_by_table_row_and_column_and_hidden_ones_are_left_out()
    {
        var tables = new[]
        {
            new ReportTable(ReportTables.Summary, ["requests", "currency"], [[JsonSerializer.SerializeToElement(120), JsonSerializer.SerializeToElement("USD")]]),
            new ReportTable("weeks", ["group", "requests"], [[JsonSerializer.SerializeToElement("2026-11-09"), JsonSerializer.SerializeToElement(41)],
                [JsonSerializer.SerializeToElement("2026-11-16"), null]]),
        };
        var figures = DashboardSummaries.Figures(tables);
        Assert.Equal(["requests", "weeks[2026-11-09].requests"], figures.Select(f => f.Label));
        Assert.Contains("[f2] weeks(2026-11-09).requests = 41", DashboardSummaries.Prompt(figures, "en"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_is_kept_only_when_every_number_is_a_named_figure()
    {
        var ok = DashboardSummaries.Verify("""{"summary":"There were 120 requests; 83.33% of questions were answered.","figures":["f1","f2"]}""", Figures);
        Assert.NotNull(ok);
        Assert.Equal(2, ok.Value.Used.Count);
        // A week named in a label may be quoted; Arabic digits count as the same number.
        Assert.NotNull(DashboardSummaries.Verify("""{"summary":"In the week of 2026-11-09 there were 41 requests.","figures":["f3"]}""", Figures));
        Assert.NotNull(DashboardSummaries.Verify("""{"summary":"بلغت الطلبات ١٢٠ طلبا.","figures":["f1"]}""", Figures));
        // An invented number, a figure that was not named, an unknown id or a broken answer are refused.
        Assert.Null(DashboardSummaries.Verify("""{"summary":"There were 150 requests.","figures":["f1"]}""", Figures));
        Assert.Null(DashboardSummaries.Verify("""{"summary":"There were 41 requests.","figures":["f1"]}""", Figures));
        Assert.Null(DashboardSummaries.Verify("""{"summary":"There were 120 requests.","figures":["f9"]}""", Figures));
        Assert.Null(DashboardSummaries.Verify("""{"summary":"Fine.","figures":[]}""", Figures));
        Assert.Null(DashboardSummaries.Verify("not json", Figures));
    }

    [Fact]
    public void Small_use_cases_fold_into_other_with_complementary_suppression()
    {
        AiUsageCount C(string key, long requests, long users) => new(key, requests, users);
        var one = AiUsageAnalytics.Fold([C("assistant", 50, 9), C("summarize", 20, 6), C("translate", 3, 1)], 5);
        // translate alone would show one person's calls: summarize, the next smallest, joins it.
        Assert.Equal(["assistant", "other"], one.Select(g => g.Group));
        Assert.Equal(23, one[1].Requests);
        Assert.Null(one[1].Users);
        var lone = AiUsageAnalytics.Fold([C("translate", 3, 1)], 5);
        Assert.Null(Assert.Single(lone).Requests);
        Assert.Equal(["assistant"], AiUsageAnalytics.Fold([C("assistant", 50, 9)], 5).Select(g => g.Group));
    }
}
