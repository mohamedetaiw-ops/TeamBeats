using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 034 slice 1: threshold levels, case rules, the CSV reader and the platform suites shipped with the product.</summary>
public sealed class EvaluationRulesTests
{
    private static readonly IReadOnlyList<(string, IReadOnlyDictionary<string, LevelRule>)> PlatformOnly = [(EvalLevels.Platform, ThresholdRules.PlatformRules)];

    private static List<string> Keys(ValidationErrors errors) => errors.Entries.Select(e => e.Key).ToList();

    [Fact]
    public void A_lower_level_may_only_make_thresholds_stricter_and_severities_higher()
    {
        // US10 scenario 1: platform 0.90 groundedness, tenant 0.95, so 0.92 at agent level is refused naming the tenant.
        var tenant = new Dictionary<string, LevelRule> { ["groundedness"] = new(0.95, null) };
        IReadOnlyList<(string, IReadOnlyDictionary<string, LevelRule>)> levels = [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, tenant)];
        Assert.Equal(["validation.eval.threshold_below_tenant"], Keys(ThresholdRules.Check([new SuiteMetric("groundedness", Threshold: 0.92)], EvalLevels.Agent, levels, true)));
        Assert.Equal(["validation.eval.threshold_below_platform", "validation.eval.threshold_below_tenant"],
            Keys(ThresholdRules.Check([new SuiteMetric("groundedness", Threshold: 0.85)], EvalLevels.Agent, levels, true)));
        Assert.False(ThresholdRules.Check([new SuiteMetric("groundedness", Threshold: 0.97)], EvalLevels.Agent, levels, true).Any);

        // Lower is better for failure rates: anything above zero is less strict than the platform.
        Assert.Equal(["validation.eval.threshold_below_platform"], Keys(ThresholdRules.Check([new SuiteMetric("forbidden_source_rate", Threshold: 0.1)], EvalLevels.Agent, PlatformOnly, true)));
        Assert.Equal(["validation.eval.severity_below_platform"],
            Keys(ThresholdRules.Check([new SuiteMetric("forbidden_source_rate", Severity: EvalSeverities.Warning)], EvalLevels.Agent, PlatformOnly, true)));

        var effective = ThresholdRules.Effective(["groundedness", "forbidden_source_rate"],
            [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, tenant), (EvalLevels.Agent, ThresholdRules.Rules([new SuiteMetric("groundedness", Threshold: 0.97)]))]);
        Assert.Equal(new EffectiveRule("groundedness", 0.97, EvalSeverities.Blocking, EvalLevels.Agent, EvalLevels.Platform), effective[0]);
        Assert.Equal(new EffectiveRule("forbidden_source_rate", 0, EvalSeverities.Critical, EvalLevels.Platform, EvalLevels.Platform), effective[1]);
    }

    [Fact]
    public void Metric_lists_refuse_unknown_and_duplicate_metrics_judged_criticals_and_custom_weights_without_the_capability()
    {
        Assert.Equal(["validation.eval.metric_unknown"], Keys(ThresholdRules.Check([new SuiteMetric("vibes")], EvalLevels.Agent, PlatformOnly, true)));
        Assert.Equal(["validation.eval.metric_duplicate"], Keys(ThresholdRules.Check([new SuiteMetric("relevance"), new SuiteMetric("relevance")], EvalLevels.Agent, PlatformOnly, true)));
        Assert.Equal(["validation.eval.judged_critical"], Keys(ThresholdRules.Check([new SuiteMetric("relevance", Severity: EvalSeverities.Critical)], EvalLevels.Agent, PlatformOnly, true)));
        Assert.Equal(["validation.eval.weights_advanced"], Keys(ThresholdRules.Check([new SuiteMetric("relevance", Weight: 2)], EvalLevels.Agent, PlatformOnly, false)));
        Assert.False(ThresholdRules.Check([new SuiteMetric("relevance", Weight: 2)], EvalLevels.Agent, PlatformOnly, true).Any);
        Assert.Equal(["validation.eval.threshold"], Keys(ThresholdRules.Check([new SuiteMetric("relevance", Threshold: 1.5)], EvalLevels.Agent, PlatformOnly, true)));
        Assert.Equal(["validation.eval.metrics"], Keys(ThresholdRules.Check(null, EvalLevels.Agent, PlatformOnly, true)));
    }

    [Fact]
    public void A_case_needs_input_a_launch_locale_and_an_expectation()
    {
        var errors = new ValidationErrors();
        Assert.Null(CaseRules.Check(new CaseInput("  ", "fr", null, new CaseExpectations(), ["Bad Tag"], "fatal", "secret"), "c", errors));
        Assert.Equal(
            ["validation.eval.case_input", "validation.eval.case_locale", "validation.eval.severity", "validation.eval.classification", "validation.eval.case_tags", "validation.eval.case_expectation"],
            Keys(errors));

        var ok = CaseRules.Check(new CaseInput(" How many leave days? ", "en", null, new CaseExpectations(Refusal: true), null, null, null), "c", new ValidationErrors())!;
        Assert.Equal("How many leave days?", ok.Input);
        Assert.Equal(EvalSeverities.Blocking, ok.Severity);
        Assert.Equal("internal", ok.Classification);
        Assert.Empty(ok.Tags!);

        var bad = new ValidationErrors();
        CaseRules.Check(new CaseInput("x", "ar", null, new CaseExpectations(Tools: [new ExpectedTool("Not A Tool", null)], Sources: [new ExpectedSource("content", null)]), null, null, null), "c", bad);
        Assert.Equal(["validation.eval.case_sources", "validation.eval.case_tools"], Keys(bad));
    }

    [Fact]
    public void Csv_import_reads_quoted_cells_lists_and_flags_and_refuses_unknown_columns()
    {
        var item = Guid.NewGuid();
        var csv = "input,locale,key_facts,sources,refusal,tags\r\n" +
                  $"\"Leave, annual\",en,25 days|carry over,content:{item},no,leave|eligibility\r\n" +
                  "\"He said \"\"no\"\"\nthen left\",ar,,,yes,\r\n\r\n";
        var parsed = CaseImport.FromCsv(csv);
        Assert.Empty(parsed.Errors);
        Assert.Equal(2, parsed.Rows.Count);
        var first = parsed.Rows[0].Case!;
        Assert.Equal("Leave, annual", first.Input);
        Assert.Equal(["25 days", "carry over"], first.Expect!.KeyFacts);
        Assert.Equal(new ExpectedSource("content", item), first.Expect.Sources![0]);
        Assert.False(first.Expect.Refusal);
        Assert.Equal(["leave", "eligibility"], first.Tags);
        var second = parsed.Rows[1];
        Assert.Equal(2, second.Row);
        Assert.Equal("He said \"no\"\nthen left", second.Case!.Input);
        Assert.True(second.Case.Expect!.Refusal);

        Assert.Equal("validation.eval.import_column", CaseImport.FromCsv("input,locale,colour\nx,en,red").Errors.Single().Key);
        Assert.Equal("validation.eval.import_header", CaseImport.FromCsv("answer\nx").Errors.Single().Key);
        Assert.Equal("validation.eval.import_json", CaseImport.FromJson("{not json").Errors.Single().Key);
    }

    [Fact]
    public void Platform_suites_cover_the_mandatory_safety_suites_and_every_launch_template_in_both_languages()
    {
        var mandatory = PlatformSuites.All.Where(s => s.Mandatory).Select(s => s.Key).Order().ToList();
        Assert.Equal(["safety.data_classification", "safety.permission_isolation", "safety.prompt_injection", "safety.tool_parameters", "safety.unsafe_actions"], mandatory);
        foreach (var template in AgentTemplates.All)
        {
            Assert.Single(PlatformSuites.All, s => s.Template == template.Id);
        }
        foreach (var suite in PlatformSuites.All)
        {
            Assert.False(ThresholdRules.Check(suite.Metrics, EvalLevels.Platform, PlatformOnly, true).Any, suite.Key);
            Assert.Equal(["ar", "en"], suite.Cases.Select(c => c.Locale).Distinct().Order());
            foreach (var c in suite.Cases)
            {
                var errors = new ValidationErrors();
                Assert.NotNull(CaseRules.Check(new CaseInput(c.Input, c.Locale, null, c.Expect, c.Tags, c.Severity, null), suite.Key, errors));
            }
        }
        Assert.Equal(6, PlatformSuites.For("hr_assistant").Count);
        Assert.Equal(5, PlatformSuites.For(null).Count);
    }
}
