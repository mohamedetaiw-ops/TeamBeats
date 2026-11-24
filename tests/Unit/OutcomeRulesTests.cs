using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 034 R2 T022 (FR-027): business outcomes of a group of runs, with the minimum group size.</summary>
public sealed class OutcomeRulesTests
{
    [Fact]
    public void Outcomes_are_withheld_below_the_minimum_group_size_and_time_saved_needs_a_baseline()
    {
        var agent = Guid.NewGuid();
        Assert.True(OutcomeRules.Score(new AgentOutcomeFigure(agent, 40, 4, 30, 5, 20), 12, 5).Withheld);
        Assert.True(OutcomeRules.Score(null, 12, 5).Withheld);
        Assert.True(OutcomeRules.Score(new AgentOutcomeFigure(agent, 0, 0, 0, 0, 0), 12, 0).Withheld);

        var (withheld, runs, completion, escalation, resolved, hours) = OutcomeRules.Score(new AgentOutcomeFigure(agent, 40, 5, 30, 5, 20), 12, 5);
        Assert.False(withheld);
        Assert.Equal(40, runs);
        Assert.Equal(0.75, completion);
        Assert.Equal(0.125, escalation);
        Assert.Equal(0.5, resolved);
        Assert.Equal(4, hours);
        Assert.Null(OutcomeRules.Score(new AgentOutcomeFigure(agent, 40, 5, 30, 5, 20), null, 5).Hours);
    }
}
