using TeamBeats.Modules.Evaluation;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 034 R2 T021 (US9, FR-026): how people are split between the arms of a comparison.</summary>
public sealed class ComparisonRulesTests
{
    [Fact]
    public void People_are_split_at_about_the_share_each_always_on_the_same_arm_and_a_new_comparison_draws_again()
    {
        var comparison = Guid.NewGuid();
        var people = Enumerable.Range(0, 20_000).Select(_ => Guid.NewGuid()).ToList();
        var share = people.Count(p => ComparisonRules.InChallenger(comparison, p, 10)) / (double)people.Count * 100;
        Assert.InRange(share, 9, 11);
        Assert.All(people.Take(200), p => Assert.Equal(ComparisonRules.InChallenger(comparison, p, 10), ComparisonRules.InChallenger(comparison, p, 10)));
        // A larger share keeps everyone who already had the challenger.
        Assert.All(people.Where(p => ComparisonRules.InChallenger(comparison, p, 10)).Take(200), p => Assert.True(ComparisonRules.InChallenger(comparison, p, 50)));
        var other = Guid.NewGuid();
        Assert.Contains(people.Take(500), p => ComparisonRules.InChallenger(comparison, p, 50) != ComparisonRules.InChallenger(other, p, 50));
    }

    [Theory]
    [InlineData("openai/gpt-4o", true)]
    [InlineData("anthropic/claude-3.5-sonnet:beta", true)]
    [InlineData("GPT 4", false)]
    [InlineData("o", false)]
    public void Model_names_follow_the_routing_names(string model, bool valid) => Assert.Equal(valid, ComparisonRules.ValidModel(model));
}
