using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 034 R2 T018 (US7, FR-023): the rules production sampling decides by, each on its own.</summary>
public sealed class ProductionSamplingTests
{
    private static readonly Guid Policy = Guid.NewGuid();

    private static EvalRunRecord Run(string? answer, IReadOnlyList<EvalRunSource>? read = null, IReadOnlyList<EvalRunSource>? cited = null,
        IReadOnlyList<EvalRunStep>? steps = null) =>
        new(Guid.NewGuid(), "completed", "answered", null, answer, "en", "openai/gpt-4o-mini", 100, 20, 900, steps ?? [], read ?? [], cited ?? [], null);

    [Fact]
    public void A_run_is_chosen_by_its_id_at_about_the_rate_and_always_the_same_way()
    {
        var ids = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid()).ToList();
        var share = ids.Count(id => ProductionSampling.Selected(id, 2)) / (double)ids.Count * 100;
        Assert.InRange(share, 1.5, 2.5);
        Assert.All(ids.Take(200), id => Assert.Equal(ProductionSampling.Selected(id, 2), ProductionSampling.Selected(id, 2)));
        Assert.All(ids.Take(200), id => Assert.True(ProductionSampling.Selected(id, 100)));
        Assert.All(ids.Take(200), id => Assert.False(ProductionSampling.Selected(id, 0)));
    }

    [Fact]
    public void Restricted_runs_give_metadata_only_and_only_public_or_internal_content_is_retained()
    {
        Assert.Equal(ProductionSampling.Metadata, ProductionSampling.ModeFor("restricted"));
        Assert.Equal(ProductionSampling.Full, ProductionSampling.ModeFor("confidential"));
        Assert.Equal(ProductionSampling.Full, ProductionSampling.ModeFor("internal"));
        Assert.True(ProductionSampling.Retains("internal", 7));
        Assert.True(ProductionSampling.Retains("public", 30));
        Assert.False(ProductionSampling.Retains("internal", 0));
        Assert.False(ProductionSampling.Retains("confidential", 30));
        Assert.False(ProductionSampling.Retains("restricted", 30));
    }

    [Fact]
    public void Reference_free_scores_check_citations_refusals_and_the_trajectory()
    {
        var source = new EvalRunSource(1, "content", Policy);
        var judged = new Dictionary<string, JudgeVerdict?>
        {
            ["groundedness"] = new("groundedness", "pass", 1, "Supported.", "judge", "groundedness.v1"),
            ["relevance"] = new("relevance", "fail", 0, "Off topic.", "judge", "relevance.v1"),
        };

        var cited = ProductionSampling.Score(Run("Annual leave is 25 days [1].", [source], [source]), judged);
        Assert.Equal(1, cited["citation_correctness"].Value);
        Assert.Equal(1, cited["groundedness"].Value);
        Assert.Equal(0, cited["relevance"].Value);
        Assert.Equal(1, cited["refusal_correctness"].Value);
        Assert.Equal(0, cited["unauthorized_attempts"].Value);

        // An answer built on sources that cites none, and a refusal although a source was found, both fail.
        Assert.Equal(0, ProductionSampling.Score(Run("Annual leave is 25 days.", [source]), judged)["citation_correctness"].Value);
        Assert.Equal(0, ProductionSampling.Score(Run("I cannot help with that.", [source]), judged)["refusal_correctness"].Value);
        // A refusal with nothing to answer from is right, and an answer without sources has no citation to check.
        var empty = ProductionSampling.Score(Run("I could not find anything about that."), judged);
        Assert.Equal(1, empty["refusal_correctness"].Value);
        Assert.False(empty.ContainsKey("citation_correctness"));

        // A call the gateway refused as unauthorized fails the trajectory check; without a judge the judged metrics are not evaluated.
        var denied = ProductionSampling.Score(Run("Done.", steps: [new EvalRunStep(1, "tool", "test.secret", "refused", "tool.denied", null)]), new Dictionary<string, JudgeVerdict?>());
        Assert.Equal(1, denied["unauthorized_attempts"].Value);
        Assert.Equal(CaseMetricStates.NotEvaluated, denied["groundedness"].State);
    }
}

/// <summary>Spec 034 R2 T019 (FR-024): when a production score drifts.</summary>
public sealed class DriftRulesTests
{
    [Fact]
    public void A_score_drifts_below_its_threshold_or_ten_points_under_the_gate_result()
    {
        var groundedness = MetricCatalog.Find("groundedness")!;
        var relevance = MetricCatalog.Find("relevance")!;
        var attempts = MetricCatalog.Find("unauthorized_attempts")!;
        Assert.True(DriftRules.Drifts(groundedness, 0.85, null, 0.90));
        Assert.False(DriftRules.Drifts(groundedness, 0.92, 0.95, 0.90));
        Assert.True(DriftRules.Drifts(groundedness, 0.90, 1.0, 0.85));
        Assert.False(DriftRules.Drifts(relevance, 0.5, null, null));
        Assert.True(DriftRules.Drifts(relevance, 0.8, 0.9, null));
        Assert.False(DriftRules.Drifts(relevance, 0.81, 0.9, null));
        // Lower is better: any unauthorized attempt in real use passes its zero threshold.
        Assert.True(DriftRules.Drifts(attempts, 0.05, 0, 0));
        Assert.False(DriftRules.Drifts(attempts, 0, 0, 0));
    }
}
