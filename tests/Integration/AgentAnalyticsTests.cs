using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Basic agent analytics (spec 020 FR-018, R1 part): runs, active users, task success, tool calls, ratings from routed
/// assistant turns, tokens and cost, and the gate result per version, for the people who may see the agent only. Test
/// panel runs are left out and nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AgentAnalyticsTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("aa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "emp2", "out", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AddMembershipAsync(t, "emp2", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<Guid> PublishedAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    [Fact]
    public async Task The_owner_sees_runs_users_success_ratings_and_cost_per_version_without_test_runs()
    {
        var t = await FreshAsync();
        var id = await PublishedAsync(t);
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hello" }));
        await OkAsync(t["emp2"].Client.PostAsJsonAsync($"{Agents}/{id}/runs", new { message = "Hello again" }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/test", new { message = "A test run" }));

        // A routed assistant turn counts as a run, and its rating counts for the version that answered.
        var turn = await OkAsync(t["emp"].Client.PostAsJsonAsync("/api/v1/assistant/turns", new { question = "Anything new? [fake:route:1]" }));
        Assert.Equal(id, turn.GetProperty("answeredBy").GetProperty("id").GetGuid());
        await OkAsync(t["emp"].Client.PostAsJsonAsync($"/api/v1/assistant/turns/{turn.GetProperty("id").GetGuid()}/feedback", new { helpful = true }), HttpStatusCode.NoContent);

        var view = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}/analytics"));
        Assert.Equal(30, view.GetProperty("days").GetInt32());
        var total = view.GetProperty("total");
        Assert.Equal(3, total.GetProperty("runs").GetInt64());
        Assert.Equal(2, total.GetProperty("users").GetInt64());
        Assert.Equal(3, total.GetProperty("answered").GetInt64());
        Assert.Equal(1.0, total.GetProperty("successRate").GetDouble());
        Assert.Equal(1, total.GetProperty("ratings").GetInt64());
        Assert.Equal(1, total.GetProperty("helpful").GetInt64());
        Assert.True(total.GetProperty("tokens").GetInt64() > 0);
        var version = Assert.Single(view.GetProperty("versions").EnumerateArray());
        Assert.Equal(1, version.GetProperty("version").GetInt32());
        Assert.Equal("passed", version.GetProperty("quality").GetString());

        // The policy administrator oversees it; an employee, someone outside the unit or another tenant does not find it.
        await OkAsync(t["aip"].Client.GetAsync($"{Agents}/{id}/analytics"));
        await OkAsync(t["emp"].Client.GetAsync($"{Agents}/{id}/analytics"), HttpStatusCode.NotFound);
        await OkAsync(t["out"].Client.GetAsync($"{Agents}/{id}/analytics"), HttpStatusCode.NotFound);
        var other = await FreshAsync();
        await OkAsync(other["aip"].Client.GetAsync($"{Agents}/{id}/analytics"), HttpStatusCode.NotFound);

        // The period is checked.
        await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}/analytics?days=0"), HttpStatusCode.BadRequest);
        await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}/analytics?days=366"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_agent_with_no_runs_shows_zeros_and_no_success_rate()
    {
        var t = await FreshAsync();
        var id = await PublishedAsync(t);
        var total = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}/analytics?days=7"))).GetProperty("total");
        Assert.Equal(0, total.GetProperty("runs").GetInt64());
        Assert.Equal(JsonValueKind.Null, total.GetProperty("successRate").ValueKind);
    }
}
