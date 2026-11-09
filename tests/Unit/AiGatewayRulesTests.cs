using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 013 US3 scenario 4 and FR-005 to FR-008: the provider circuit breaker, policy validation and the structured output check.</summary>
public sealed class AiGatewayRulesTests
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);
    }

    private static CatalogueEntry Model(string name, string capability = "chat", bool confidential = false) =>
        new(name, "openrouter", [], [capability], 32000, ["eu"], confidential, confidential, confidential ? "confidential" : "internal", 1, 1, "active", null);

    private static readonly CatalogueSnapshot Catalogue = new(
        new Dictionary<string, CatalogueEntry>
        {
            ["a/chat"] = Model("a/chat"),
            ["b/private"] = Model("b/private", confidential: true),
            ["c/embed"] = Model("c/embed", "embeddings"),
        },
        new Dictionary<string, IReadOnlyList<string>> { [AiUseCases.Assistant] = ["a/chat"], [AiUseCases.Embed] = ["c/embed"] });

    [Fact]
    public void Circuit_opens_at_half_failures_over_twenty_calls_and_closes_after_a_good_trial()
    {
        var clock = new Clock();
        var circuits = new ProviderCircuits(clock, NullLogger<ProviderCircuits>.Instance);
        for (var i = 0; i < 19; i++)
        {
            circuits.Record("openrouter", failed: i % 2 == 0);
        }
        Assert.Equal("closed", circuits.State("openrouter"));
        circuits.Record("openrouter", failed: true);
        Assert.Equal("open", circuits.State("openrouter"));
        Assert.False(circuits.Allow("openrouter"));
        Assert.True(circuits.Allow("other"));

        clock.UtcNow += ProviderCircuits.OpenFor;
        Assert.Equal("half_open", circuits.State("openrouter"));
        Assert.True(circuits.Allow("openrouter"));
        Assert.False(circuits.Allow("openrouter")); // one trial at a time
        circuits.Record("openrouter", failed: true);
        Assert.Equal("open", circuits.State("openrouter"));

        clock.UtcNow += ProviderCircuits.OpenFor;
        Assert.True(circuits.Allow("openrouter"));
        circuits.Record("openrouter", failed: false);
        Assert.Equal("closed", circuits.State("openrouter"));
    }

    [Fact]
    public void Old_calls_leave_the_window()
    {
        var clock = new Clock();
        var circuits = new ProviderCircuits(clock, NullLogger<ProviderCircuits>.Instance);
        for (var i = 0; i < 15; i++)
        {
            circuits.Record("openrouter", failed: true);
        }
        clock.UtcNow += TimeSpan.FromMinutes(2);
        for (var i = 0; i < 10; i++)
        {
            circuits.Record("openrouter", failed: false);
        }
        Assert.Equal("closed", circuits.State("openrouter"));
    }

    [Fact]
    public void Policy_validation_keeps_routes_to_allowed_capable_models_and_Confidential_to_eligible_ones()
    {
        var current = PolicyDocument.Platform(Catalogue);
        Assert.Equal(["a/chat"], current.RouteOf(AiUseCases.Assistant, Catalogue));
        Assert.Empty(current.RouteOf(AiUseCases.Summarize, Catalogue));

        var ok = ModelPolicies.Validate(new ModelPolicyRequest(["a/chat", "b/private", "c/embed"],
            new Dictionary<string, List<string>> { [AiUseCases.Assistant] = ["b/private", "a/chat"], [AiUseCases.Embed] = ["c/embed"] },
            ["b/private"], 2000, 0, 1, ["eu"], null), current, Catalogue);
        Assert.Equal(["b/private", "a/chat"], ok.RouteOf(AiUseCases.Assistant, Catalogue));
        Assert.Equal(PolicyDocument.MetadataOnly, ok.Logging);

        AssertInvalid(new ModelPolicyRequest(["a/chat"], new Dictionary<string, List<string>> { [AiUseCases.Embed] = ["a/chat"] }, null, null, null, null, null, null), current);
        AssertInvalid(new ModelPolicyRequest(["a/chat"], null, ["a/chat"], null, null, null, null, null), current);
        AssertInvalid(new ModelPolicyRequest(["x/unknown"], null, null, null, null, null, null, null), current);
        AssertInvalid(new ModelPolicyRequest(null, null, null, null, 1.5, 0.5, null, null), current);
        AssertInvalid(new ModelPolicyRequest(null, new Dictionary<string, List<string>> { ["chit_chat"] = [] }, null, null, null, null, null, null), current);
    }

    private static void AssertInvalid(ModelPolicyRequest request, PolicyDocument current)
    {
        var ex = Assert.Throws<AppException>(() => ModelPolicies.Validate(request, current, Catalogue));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Structured_output_must_be_json_of_the_schema_type_with_required_properties()
    {
        var schema = JsonDocument.Parse("""{"type":"object","required":["answer"]}""").RootElement;
        Assert.True(StructuredOutput.Matches("""{"answer":"yes"}""", schema));
        Assert.False(StructuredOutput.Matches("""{"other":"yes"}""", schema));
        Assert.False(StructuredOutput.Matches("yes", schema));
        Assert.False(StructuredOutput.Matches("[1]", schema));
    }

    [Fact]
    public void Token_estimate_does_not_undercount_Arabic()
    {
        Assert.Equal(3, AiGatewayService.Estimate(["مرحبا بكم"]));
        Assert.Equal(0, AiGatewayService.Estimate([]));
    }
}
