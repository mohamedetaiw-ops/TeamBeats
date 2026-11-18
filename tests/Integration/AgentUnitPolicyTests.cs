using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 027 R2 T016 (US8 scenarios 3 and 4, FR-018): an org unit's agent policy can only be stricter than the policy
/// above it, a looser rule is refused with the parent named, the agents of the unit and the units below it follow the
/// narrowed policy, and published agents that break it are suspended. Units outside the administrator's scope and other
/// tenants are not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentUnitPolicyTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("aup-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "aip", "gulfaip", "africaaip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "GulfHr", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", gulf);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "gulfaip", BuiltInRoles.AiPolicyAdministrator, "org_subtree", gulf);
        await world.AssignAsync(t, "africaaip", BuiltInRoles.AiPolicyAdministrator, "org_subtree", africa);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Dictionary<string, string[]>> ErrorsAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!).ToArray());
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, Guid unit, int version, object policy) =>
        client.PutAsJsonAsync($"{Agents}/policy/units/{unit}", new { version, policy });

    private async Task SetTenantAsync(TestTenant t, AgentPolicy policy)
    {
        var current = await OkAsync(t["aip"].Client.GetAsync($"{Agents}/policy"));
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Agents}/policy", new { version = current.GetProperty("version").GetInt32(), policy }));
    }

    private async Task<Guid> PublishedAgentAsync(TestTenant t, string autonomy)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("GulfHr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["autonomy"] = autonomy;
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    [Fact]
    public async Task A_unit_rule_looser_than_the_policy_above_is_refused_with_the_parent_named()
    {
        var t = await FreshAsync();
        await SetTenantAsync(t, AgentPolicy.Default with { MaxAutonomy = "L3", AllowedTools = ["knowledge.search", "test.order"] });
        var holding = t.Unit("Holding");
        var gulf = t.Unit("Gulf");

        // The tenant sets the limit: autonomy above L3, a tool the tenant does not allow, and a weaker rule than the tool's own.
        var errors = await ErrorsAsync(PutAsync(t["aip"].Client, holding, 0, new { maxAutonomy = "L4", allowedTools = new[] { "test.note" }, toolRules = new Dictionary<string, string> { ["test.order"] = "confirm" } }));
        Assert.Equal(["validation.agent_policy.tenant_limit"], errors["maxAutonomy"]);
        Assert.Equal(["validation.agent_policy.tenant_limit"], errors["allowedTools.test.note"]);
        Assert.Equal(["validation.agent_policy.tenant_limit"], errors["toolRules.test.order"]);

        var saved = await OkAsync(PutAsync(t["aip"].Client, holding, 0, new { maxAutonomy = "L2", maxToolCalls = 10 }));
        Assert.Equal(1, saved.GetProperty("version").GetInt32());
        Assert.Equal("L2", saved.GetProperty("effective").GetProperty("maxAutonomy").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentPolicyChanged, holding.ToString());

        // A parent unit sets the limit now: Gulf cannot go back above Holding's L2 or its ten tool calls.
        errors = await ErrorsAsync(PutAsync(t["gulfaip"].Client, gulf, 0, new { maxAutonomy = "L3", maxToolCalls = 15 }));
        Assert.Equal(["validation.agent_policy.unit_limit"], errors["maxAutonomy"]);
        Assert.Equal(["validation.agent_policy.unit_limit"], errors["maxToolCalls"]);
        var gulfView = await OkAsync(PutAsync(t["gulfaip"].Client, gulf, 0, new { maxAutonomy = "L1" }));
        Assert.Equal("L2", gulfView.GetProperty("inherited").GetProperty("maxAutonomy").GetString());
        Assert.Equal(10, gulfView.GetProperty("effective").GetProperty("maxToolCalls").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(t["gulfaip"].Client, gulf, 0, new { maxAutonomy = "L0" })).StatusCode);

        // Gulf's administrator cannot see Holding or Africa; another tenant sees nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["gulfaip"].Client.GetAsync($"{Agents}/policy/units/{holding}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["africaaip"].Client.GetAsync($"{Agents}/policy/units/{gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutAsync(t["africaaip"].Client, gulf, 1, new { maxAutonomy = "L0" })).StatusCode);
        var other = await world.CreateTenantAsync("aux-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Agents}/policy/units/{gulf}")).StatusCode);
        // A designer without ai.policy_manage cannot read it either.
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"{Agents}/policy/units/{gulf}")).StatusCode);
    }

    [Fact]
    public async Task A_stricter_unit_policy_suspends_its_agents_that_break_it_and_leaves_other_units_alone()
    {
        var t = await FreshAsync();
        var agent = await PublishedAgentAsync(t, "L2");
        Assert.Equal(AgentStatuses.Published, (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{agent}"))).GetProperty("agent").GetProperty("status").GetString());

        // Africa's policy does not reach a Gulf agent.
        await OkAsync(PutAsync(t["africaaip"].Client, t.Unit("Africa"), 0, new { maxAutonomy = "L0" }));
        Assert.Equal(AgentStatuses.Published, (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{agent}"))).GetProperty("agent").GetProperty("status").GetString());

        // Gulf's policy caps autonomy at L1, below the agent's L2, so the agent is suspended in the same save.
        await OkAsync(PutAsync(t["gulfaip"].Client, t.Unit("Gulf"), 0, new { maxAutonomy = "L1" }));
        Assert.Equal(AgentStatuses.Suspended, (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{agent}"))).GetProperty("agent").GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AgentSuspended, agent.ToString());

        // Resuming checks the unit's policy too, until the unit's rules are dropped.
        Assert.Equal(HttpStatusCode.BadRequest, (await t["aip"].Client.PostAsJsonAsync($"{Agents}/{agent}/resume", new { })).StatusCode);
        await OkAsync(t["gulfaip"].Client.DeleteAsync($"{Agents}/policy/units/{t.Unit("Gulf")}"), HttpStatusCode.NoContent);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{agent}/resume", new { }));
        Assert.Equal(AgentStatuses.Published, (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{agent}"))).GetProperty("agent").GetProperty("status").GetString());
    }
}
