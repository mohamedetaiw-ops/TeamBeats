using TeamBeats.Modules.Evaluation;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 034 R2 T020 (US8, FR-025): why a production sample is flagged for review, and how its text is redacted.</summary>
public sealed class ReviewRulesTests
{
    private static Dictionary<string, CaseMetric> Metrics(double groundedness = 1, double attempts = 0) => new()
    {
        ["groundedness"] = new(CaseMetricStates.Scored, groundedness),
        ["relevance"] = new(CaseMetricStates.Scored, 1),
        ["unauthorized_attempts"] = new(CaseMetricStates.Scored, attempts),
    };

    private static readonly Dictionary<string, StoredVerdict> Sure = new() { ["groundedness"] = new("pass", 1, null) };

    [Fact]
    public void A_sample_is_flagged_for_a_low_score_feedback_an_unsure_judge_or_a_denial_and_otherwise_not()
    {
        Assert.Empty(ReviewRules.Reasons(Metrics(), Sure, 0, "answered", false));
        Assert.Equal([ReviewReasons.LowScore], ReviewRules.Reasons(Metrics(groundedness: 0), Sure, 0, "answered", false));
        Assert.Equal([ReviewReasons.NegativeFeedback], ReviewRules.Reasons(Metrics(), Sure, 0, "answered", true));
        Assert.Equal([ReviewReasons.JudgeUncertain], ReviewRules.Reasons(Metrics(), new Dictionary<string, StoredVerdict> { ["groundedness"] = new("pass", 0.6, null) }, 0, "answered", false));
        // A verdict that contradicts its own score is not sure either.
        Assert.Equal([ReviewReasons.JudgeUncertain], ReviewRules.Reasons(Metrics(), new Dictionary<string, StoredVerdict> { ["groundedness"] = new("pass", 0.2, null) }, 0, "answered", false));
        Assert.Equal([ReviewReasons.PolicyDenial], ReviewRules.Reasons(Metrics(), Sure, 1, "answered", false));
        Assert.Equal([ReviewReasons.PolicyDenial], ReviewRules.Reasons(Metrics(attempts: 1), Sure, 0, "answered", false));
        Assert.Equal([ReviewReasons.PolicyDenial], ReviewRules.Reasons(Metrics(), Sure, 0, "policy_blocked", false));
        // A metric the judge could not score is no reason by itself.
        var unjudged = new Dictionary<string, CaseMetric> { ["groundedness"] = new(CaseMetricStates.NotEvaluated, null) };
        Assert.Empty(ReviewRules.Reasons(unjudged, new Dictionary<string, StoredVerdict>(), 0, "answered", false));
    }

    [Fact]
    public void Emails_phone_and_id_numbers_and_ibans_are_redacted_and_ordinary_text_is_kept()
    {
        Assert.Equal("Mail [email] today", ReviewRules.Redact("Mail sam.lee@example.com today"));
        Assert.Equal("Call [number] now", ReviewRules.Redact("Call +971 50 123 4567 now"));
        Assert.Equal("ID [number]", ReviewRules.Redact("ID 784-1990-1234567-1"));
        Assert.Equal("Pay to [iban]", ReviewRules.Redact("Pay to AE07 0331 2345 6789 0123 456"));
        Assert.Equal("Annual leave is 25 days from 2026.", ReviewRules.Redact("Annual leave is 25 days from 2026."));
        Assert.Equal("رصيدك 25 يوما", ReviewRules.Redact("رصيدك 25 يوما"));
    }
}
