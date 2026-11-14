using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 012 T016 (US8, FR-020): judged query sets are scored with precision at 5 and NDCG at 10 over graded relevance,
/// and the retrieval gate blocks a change whose mean falls more than the margin below the index in force.
/// </summary>
public sealed class SearchRankingScoringTests
{
    private static readonly Guid Best = Guid.NewGuid();
    private static readonly Guid Good = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static EvalRunRecord Ranked(params Guid[] items) =>
        new(Guid.Empty, "completed", "ranked", null, null, "en", null, 0, 0, 5, [], items.Select((id, i) => new EvalRunSource(i + 1, "content", id)).ToList(), [], null);

    private static readonly CaseExpectations Judged = new(Sources: [new("content", Best, 3), new("content", Good, 1)]);

    private static Dictionary<string, CaseMetric> Score(EvalRunRecord run) =>
        CaseEvaluators.Evaluate(RunService.SearchMetrics, Judged, "en", run, new Dictionary<string, JudgeVerdict?>());

    [Fact]
    public void Ndcg_rewards_the_best_answer_first_and_counts_grades()
    {
        // The ideal order scores 1.
        Assert.Equal(1, Score(Ranked(Best, Good, Other)).GetValueOrDefault("ndcg_at_10")!.Value);
        // The best answer second: (1/1 + 7/log2 3) / (7/1 + 1/log2 3).
        var swapped = Score(Ranked(Good, Best, Other));
        Assert.Equal(Math.Round((1 + 7 / Math.Log2(3)) / (7 + 1 / Math.Log2(3)), 4), swapped["ndcg_at_10"].Value);
        Assert.Equal(0.6667, swapped["precision_at_5"].Value);
        // Nothing relevant found scores 0, and an item past the tenth place does not count.
        var deep = Ranked([.. Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()), Best]);
        Assert.Equal(0, Score(deep)["ndcg_at_10"].Value);
        // An agent case leaves the grade empty, which counts as 1.
        Assert.Equal(1, CaseEvaluators.Ndcg([new ExpectedSource("content", Good)], [new EvalRunSource(1, "content", Good)], 10));
    }

    [Fact]
    public void The_gate_blocks_a_metric_that_falls_more_than_the_margin_in_a_locale()
    {
        var failures = RetrievalGates.Regressions(
        [
            new RankingScore("ndcg_at_10", "en", 0.80, 0.74, 20),
            new RankingScore("ndcg_at_10", "ar", 0.80, 0.75, 20),
            new RankingScore("precision_at_5", "en", 0.60, 0.70, 20),
        ]);
        Assert.Equal(["search_ranking/ndcg_at_10:en"], failures);
    }
}
