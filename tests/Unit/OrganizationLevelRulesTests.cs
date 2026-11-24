using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Evaluation;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 034 R2 T023 (US10, FR-020): the organization level only tightens the levels above it, and the strictest rule applies.</summary>
public sealed class OrganizationLevelRulesTests
{
    private static readonly IReadOnlyDictionary<string, LevelRule> Tenant = ThresholdRules.Rules([new SuiteMetric("groundedness", Threshold: 0.92)]);
    private static readonly IReadOnlyDictionary<string, LevelRule> Group =
        ThresholdRules.Rules([new SuiteMetric("groundedness", Threshold: 0.94), new SuiteMetric("relevance", Severity: EvalSeverities.Blocking)]);

    [Fact]
    public void A_looser_value_than_a_unit_above_names_the_organization_level()
    {
        var above = new List<(string, IReadOnlyDictionary<string, LevelRule>)>
        {
            (EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, Tenant), (EvalLevels.Organization, Group),
        };
        var errors = ThresholdRules.Check([new SuiteMetric("groundedness", Threshold: 0.93), new SuiteMetric("relevance", Severity: EvalSeverities.Warning)],
            EvalLevels.Agent, above, weightsAllowed: false);
        Assert.Contains(("metrics[0].threshold", "validation.eval.threshold_below_org"), errors.Entries);
        Assert.Contains(("metrics[1].severity", "validation.eval.severity_below_org"), errors.Entries);
        Assert.False(ThresholdRules.Check([new SuiteMetric("groundedness", Threshold: 0.95)], EvalLevels.Agent, above, weightsAllowed: false).Any);
    }

    [Fact]
    public void The_effective_rule_is_the_strictest_level_including_the_units()
    {
        var rule = ThresholdRules.Effective(["groundedness"],
            [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, Tenant), (EvalLevels.Organization, Group)]).Single();
        Assert.Equal(0.94, rule.Threshold);
        Assert.Equal(EvalLevels.Organization, rule.ThresholdLevel);
        Assert.Equal("org:" + Guid.Empty.ToString("N"), UnitEvalSettings.SuiteKey(Guid.Empty));
    }
}
