using TeamBeats.Modules.Agents;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 020 FR-005 forced security updates of templates: an update only takes things away, applies on its date (or at
/// once when critical), and an update that could not be applied safely stops the product from starting.
/// </summary>
public sealed class TemplateSecurityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly Dictionary<string, string> Reason = new() { ["en"] = "Drafting tool misused.", ["ar"] = "أسيء استخدام أداة الصياغة." };

    private static AgentDefinition Desk() => AgentTemplates.Find("it_service_desk")!.Definition;

    [Fact]
    public void An_update_removes_tools_and_lowers_limits_but_never_raises_them()
    {
        var update = new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Critical: true, RemoveTools: ["requests.prepare"], MaxAutonomy: "L1",
            MaxClassification: "public", MaxToolCalls: 3);
        var applied = TemplateSecurity.Apply(Desk(), [update]);
        Assert.DoesNotContain("requests.prepare", applied.Tools!);
        Assert.Contains("knowledge.search", applied.Tools!);
        Assert.Equal("L1", applied.Autonomy);
        Assert.Equal("public", applied.MaxClassification);
        Assert.Equal(3, applied.MaxToolCalls);
        Assert.True(TemplateSecurity.Breaks(Desk(), [update]));
        Assert.False(TemplateSecurity.Breaks(applied, [update]));

        // A looser limit than the agent has leaves the agent as it is.
        var loose = new TemplateSecurityUpdate("it_service_desk", 2, Reason, Now, Critical: true, MaxAutonomy: "L3", MaxClassification: "restricted", MaxToolCalls: 50);
        var same = TemplateSecurity.Apply(applied, [loose]);
        Assert.Equal("L1", same.Autonomy);
        Assert.Equal("public", same.MaxClassification);
        Assert.Equal(3, same.MaxToolCalls);
        Assert.False(TemplateSecurity.Breaks(applied, [loose]));
    }

    [Fact]
    public void A_set_date_update_applies_on_its_date_and_a_critical_one_at_once()
    {
        var dated = new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Now.AddDays(7));
        var critical = new TemplateSecurityUpdate("it_service_desk", 2, Reason, Now, Critical: true);
        var updates = new FixedUpdates([dated, critical]);
        Assert.Equal([2], updates.InForce("it_service_desk", Now).Select(u => u.Version));
        Assert.Equal([1, 2], updates.InForce("it_service_desk", Now.AddDays(7)).Select(u => u.Version));
        Assert.Empty(updates.InForce("hr_assistant", Now.AddDays(30)));
        Assert.Empty(updates.InForce(null, Now));
    }

    [Fact]
    public void An_update_without_notice_reason_or_known_template_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => TemplateSecurity.Check([new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Now.AddDays(6))]));
        Assert.Throws<InvalidOperationException>(() => TemplateSecurity.Check([new TemplateSecurityUpdate("it_service_desk", 1, null, Now, Critical: true)]));
        Assert.Throws<InvalidOperationException>(() => TemplateSecurity.Check([new TemplateSecurityUpdate("no_such_template", 1, Reason, Now, Critical: true)]));
        Assert.Throws<InvalidOperationException>(() => TemplateSecurity.Check([new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Critical: true, MaxAutonomy: "L9")]));
        Assert.Throws<InvalidOperationException>(() => TemplateSecurity.Check([
            new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Critical: true), new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Critical: true)]));
        TemplateSecurity.Check([new TemplateSecurityUpdate("it_service_desk", 1, Reason, Now, Now.AddDays(7))]);
    }

    [Fact]
    public void The_shipped_updates_load_and_pass_the_checks() => Assert.NotNull(new ShippedTemplateSecurityUpdates().All);

    private sealed class FixedUpdates(IReadOnlyList<TemplateSecurityUpdate> all) : ITemplateSecurityUpdates
    {
        public IReadOnlyList<TemplateSecurityUpdate> All => all;
    }
}
