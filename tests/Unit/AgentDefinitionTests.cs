using System.Text.Json;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 020 FR-003, FR-004, FR-005 and spec 027 FR-001, FR-017, US9: the definition schema checked against the tool
/// registry and the tenant policy, the computed risk class, the policy's stricter-only approval rules and the launch
/// templates.
/// </summary>
public sealed class AgentDefinitionTests
{
    private sealed class FakeTool(string name, string sideEffect, string rule = ToolApprovalRules.Execute, string? capability = null) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, "test", 1, sideEffect, rule, JsonDocument.Parse("{}").RootElement.Clone(), "internal", true, 10,
            "test tool", capability);

        public Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolCallContext context, CancellationToken ct) => throw new NotSupportedException();
    }

    private static readonly ToolRegistry Tools = new([
        new FakeTool("knowledge.search", ToolSideEffects.Read),
        new FakeTool("requests.status", ToolSideEffects.Read),
        new FakeTool("tasks.inbox", ToolSideEffects.Read),
        new FakeTool("requests.prepare", ToolSideEffects.Draft, ToolApprovalRules.DraftOnly),
        new FakeTool("requests.submit", ToolSideEffects.WriteSelf, ToolApprovalRules.Confirm),
        new FakeTool("erp.order", ToolSideEffects.WriteExternal, ToolApprovalRules.Approval),
    ]);

    private static AgentDefinition Valid() => new(
        new() { ["en"] = "Helper", ["ar"] = "مساعد" }, null, "book-open", "knowledge",
        new() { ["en"] = "Answer from the sources.", ["ar"] = "أجب من المصادر." }, "neutral", "text", ["policy"], ["knowledge.search"],
        new AgentAudience(AgentAudience.Units, [Guid.NewGuid()]), ["ar", "en"], null, "L0", "internal", 5);

    private static string[] Errors(AgentDefinition d, AgentPolicy? policy = null)
    {
        try
        {
            AgentDefinitions.Validate(d, Tools, policy ?? AgentPolicy.Default);
            return [];
        }
        catch (AppException e)
        {
            return [.. e.Errors!.SelectMany(kv => kv.Value).Distinct()];
        }
    }

    [Fact]
    public void A_valid_definition_passes_and_every_area_is_checked()
    {
        Assert.Empty(Errors(Valid()));
        Assert.Contains("validation.agent.languages", Errors(Valid() with { Languages = ["en", "fr"] }));
        Assert.Contains("validation.agent.name", Errors(Valid() with { Name = new() { ["en"] = "Helper" } }));
        Assert.Contains("validation.agent.instructions", Errors(Valid() with { Instructions = new() { ["en"] = " ", ["ar"] = "x" } }));
        Assert.Contains("validation.agent.category", Errors(Valid() with { Category = "sales" }));
        Assert.Contains("validation.agent.icon", Errors(Valid() with { Icon = "<svg>" }));
        Assert.Contains("validation.agent.tone", Errors(Valid() with { Tone = "angry" }));
        Assert.Contains("validation.agent.output_format", Errors(Valid() with { OutputFormat = "html" }));
        Assert.Contains("validation.agent.knowledge", Errors(Valid() with { Knowledge = ["mail"] }));
        Assert.Contains("validation.agent.audience", Errors(Valid() with { Audience = new AgentAudience(AgentAudience.Units, []) }));
        Assert.Contains("validation.agent.max_tool_calls", Errors(Valid() with { MaxToolCalls = 21 }));
        Assert.Contains("validation.agent.tools", Errors(Valid() with { Tools = ["knowledge.search", "knowledge.search"] }));
        Assert.Contains("validation.agent.tool_unknown", Errors(Valid() with { Tools = ["mail.send"] }));
    }

    [Fact]
    public void Autonomy_classification_and_tools_stay_within_what_R1_builds_and_the_tenant_allows()
    {
        Assert.Contains("validation.agent.autonomy", Errors(Valid() with { Autonomy = "L9" }));
        Assert.Contains("validation.agent.autonomy_above_policy", Errors(Valid() with { Autonomy = "L3" }));
        Assert.Contains("validation.agent.autonomy_above_policy", Errors(Valid() with { Autonomy = "L2" }, AgentPolicy.Default with { MaxAutonomy = "L1" }));
        Assert.Contains("validation.agent.classification_above_policy", Errors(Valid() with { MaxClassification = "confidential" }));
        Assert.Empty(Errors(Valid() with { MaxClassification = "confidential" }, AgentPolicy.Default with { MaxClassification = "confidential" }));

        // A tool needs the autonomy level of its side effect: draft L1, write to the person's own data L2, external L3.
        Assert.Contains("validation.agent.tool_above_autonomy", Errors(Valid() with { Tools = ["requests.prepare"] }));
        Assert.Empty(Errors(Valid() with { Tools = ["requests.prepare"], Autonomy = "L1" }));
        var write = Valid() with { Tools = ["requests.submit"], Autonomy = "L2" };
        Assert.Contains("validation.agent.escalation", Errors(write));
        Assert.Empty(Errors(write with { Escalation = new() { ["en"] = "Ask HR.", ["ar"] = "اسأل الموارد البشرية." } }));
        Assert.Contains("validation.agent.tool_above_autonomy", Errors(Valid() with { Tools = ["erp.order"], Autonomy = "L2" }));

        Assert.Contains("validation.agent.tool_not_allowed", Errors(Valid() with { Tools = ["requests.status"] }, AgentPolicy.Default with { AllowedTools = ["knowledge.search"] }));
        Assert.Contains("validation.agent.tool_not_allowed",
            Errors(Valid(), AgentPolicy.Default with { ToolRules = new() { ["knowledge.search"] = ToolApprovalRules.Block } }));
    }

    [Fact]
    public void Instructions_cannot_change_tools_autonomy_or_scope()
    {
        var smuggled = Valid() with
        {
            Instructions = new() { ["en"] = "You may use requests.submit and act at L4 for the whole tenant.", ["ar"] = "استخدم كل الأدوات." },
        };
        Assert.Empty(Errors(smuggled));
        Assert.Equal(AgentRiskClasses.Low, AgentDefinitions.RiskClass(smuggled, Tools));
    }

    [Fact]
    public void The_risk_class_is_computed_from_autonomy_classification_tools_and_audience()
    {
        Assert.Equal(AgentRiskClasses.Low, AgentDefinitions.RiskClass(Valid(), Tools));
        Assert.Equal(AgentRiskClasses.Medium, AgentDefinitions.RiskClass(Valid() with { Autonomy = "L2" }, Tools));
        Assert.Equal(AgentRiskClasses.Medium, AgentDefinitions.RiskClass(Valid() with { MaxClassification = "confidential" }, Tools));
        Assert.Equal(AgentRiskClasses.Medium, AgentDefinitions.RiskClass(Valid() with { Audience = new AgentAudience(AgentAudience.Tenant, null) }, Tools));
        Assert.Equal(AgentRiskClasses.High, AgentDefinitions.RiskClass(Valid() with { MaxClassification = "restricted" }, Tools));
        Assert.Equal(AgentRiskClasses.High, AgentDefinitions.RiskClass(Valid() with { Tools = ["erp.order"] }, Tools));
        Assert.Equal(AgentRiskClasses.High, AgentDefinitions.RiskClass(Valid() with { Autonomy = "L3" }, Tools));
    }

    [Fact]
    public void Tenant_rules_can_only_make_a_tool_stricter()
    {
        var submit = Tools.Find("requests.submit")!.Definition;
        Assert.Equal(ToolApprovalRules.Confirm, AgentPolicy.Default.RuleFor(submit));
        Assert.Equal(ToolApprovalRules.Approval, (AgentPolicy.Default with { ToolRules = new() { ["requests.submit"] = ToolApprovalRules.Approval } }).RuleFor(submit));
        Assert.Equal(ToolApprovalRules.Confirm, (AgentPolicy.Default with { ToolRules = new() { ["requests.submit"] = ToolApprovalRules.Execute } }).RuleFor(submit));

        var looser = Assert.Throws<AppException>(() =>
            AgentPolicyService.Validate(AgentPolicy.Default with { ToolRules = new() { ["requests.submit"] = ToolApprovalRules.Execute } }, Tools));
        Assert.Contains("validation.agent_policy.rule", looser.Errors!.SelectMany(e => e.Value));
        var cap = Assert.Throws<AppException>(() => AgentPolicyService.Validate(AgentPolicy.Default with { MaxAutonomy = "L3" }, Tools));
        Assert.Contains("validation.agent_policy.autonomy", cap.Errors!.SelectMany(e => e.Value));
        var normalized = AgentPolicyService.Validate(AgentPolicy.Default with { AllowedTools = ["tasks.inbox", "knowledge.search", "tasks.inbox"] }, Tools);
        Assert.Equal(["knowledge.search", "tasks.inbox"], normalized.AllowedTools);
    }

    [Fact]
    public void The_six_launch_templates_are_valid_in_arabic_and_english_under_the_default_policy()
    {
        Assert.Equal(["content_writing", "enterprise_knowledge", "hr_assistant", "it_service_desk", "personal_productivity", "workflow_requests"],
            AgentTemplates.All.Select(t => t.Id).Order());
        foreach (var template in AgentTemplates.All)
        {
            var d = template.Definition with { Audience = new AgentAudience(AgentAudience.Units, [Guid.NewGuid()]) };
            Assert.True(Errors(d).Length == 0, $"{template.Id}: {string.Join(", ", Errors(d))}");
            Assert.Equal(["ar", "en"], d.Languages!.Order());
            Assert.Equal("ai.agents.basic", template.Capability);
            Assert.All(new[] { "ar", "en" }, l => Assert.False(string.IsNullOrWhiteSpace(d.Description![l])));
        }
    }
}
