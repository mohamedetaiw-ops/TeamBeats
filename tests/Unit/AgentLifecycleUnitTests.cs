using TeamBeats.Modules.Agents;
using Xunit;
using TeamBeats.Modules.Agents.Contracts;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 020 US2 scenario 1: the review page lists the fields that changed against the published version.</summary>
public sealed class AgentLifecycleUnitTests
{
    private static AgentDefinition Definition(string autonomy, params string[] tools) => new(
        new() { ["en"] = "Helper", ["ar"] = "مساعد" }, null, "book-open", "knowledge",
        new() { ["en"] = "Answer from the sources.", ["ar"] = "أجب من المصادر." }, "neutral", "text", ["policy"], [.. tools],
        new AgentAudience(AgentAudience.Units, [Guid.Parse("8b7c1f2e-0000-4000-8000-000000000001")]), ["ar", "en"], null, autonomy, "internal", 5);

    [Fact]
    public void Only_changed_fields_are_listed_against_the_published_version()
    {
        var changes = AgentLifecycle.Diff(Definition("L0", "knowledge.search"), Definition("L2", "knowledge.search", "test.note"));
        Assert.Equal(["tools", "autonomy"], changes.Select(c => c.Field).ToArray());
        Assert.Equal("L0", changes[1].From!.Value.GetString());
        Assert.Equal("L2", changes[1].To!.Value.GetString());
        Assert.Empty(AgentLifecycle.Diff(Definition("L0", "knowledge.search"), Definition("L0", "knowledge.search")));
    }

    [Fact]
    public void A_first_version_lists_every_field_as_new()
    {
        var changes = AgentLifecycle.Diff(null, Definition("L0"));
        Assert.All(changes, c => Assert.Null(c.From));
        Assert.Contains(changes, c => c.Field == "instructions");
    }

    [Theory]
    [InlineData(AgentRiskClasses.Low, false)]
    [InlineData(AgentRiskClasses.Medium, true)]
    [InlineData(AgentRiskClasses.High, true)]
    public void Medium_and_high_risk_need_a_second_person(string risk, bool required) =>
        Assert.Equal(required, AgentLifecycle.RequiresOtherApprover(risk));
}
