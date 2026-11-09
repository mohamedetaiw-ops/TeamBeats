using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 T011 and T012 for model routes (US5 scenario 4) with spec 013 FR-019: a change to the agent route runs every
/// published agent against the proposed routes before it applies; a gate with nothing failing applies it on its own;
/// a critical failure in any agent rejects it; blocking failures or an incomplete gate list what failed and leave it to
/// the AI Policy Administrator to apply with a reason; gate runs never count as the agent's own release gate; and
/// nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationRouteGateTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";
    private const string Small = "openai/gpt-4o-mini";
    private const string Large = "openai/gpt-4o";
    private const string Embedding = "openai/text-embedding-3-small";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("evr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
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

    private static string Call(string tool, string? args = null) => $"[fake:tool:{tool.Replace(".", "__")}]" + (args is null ? string.Empty : $" [fake:args:{args}]");

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    /// <summary>Publishes the HR template in Hr; its release gate is marked passed (EvaluationGateTests covers that gate).</summary>
    private async Task<Guid> PublishedAsync(TestTenant t, string[] tools)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = "L1";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    /// <summary>Moves the agent route to the larger model only, which the gate holds; returns the proposed change.</summary>
    private static async Task<Guid> ProposeAsync(TestTenant t)
    {
        var client = t["aip"].Client;
        var current = await client.GetAsync("/api/v1/ai/policy");
        var routes = AiUseCases.All.ToDictionary(u => u, u => u == AiUseCases.Embed ? new[] { Embedding } : u == AiUseCases.Agent ? new[] { Large } : new[] { Small, Large });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy")
        {
            Content = JsonContent.Create(new { allowlist = new[] { Small, Large, Embedding }, routes, confidential = Array.Empty<string>() }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        var held = await OkAsync(client.SendAsync(request), HttpStatusCode.Accepted);
        Assert.Equal(new[] { AiUseCases.Agent }, held.GetProperty("routeChange").GetProperty("useCases").EnumerateArray().Select(u => u.GetString()).ToArray());
        return held.GetProperty("routeChange").GetProperty("id").GetGuid();
    }

    private static Task<JsonElement> PendingAsync(TestTenant t) => OkAsync(t["aip"].Client.GetAsync("/api/v1/ai/policy"));

    [Fact]
    public async Task With_no_published_agent_the_gate_passes_and_the_change_applies_on_its_own()
    {
        var t = await FreshAsync();
        var change = await ProposeAsync(t);
        await RouteGateFlow.StartAsync(world, t.Id, change);
        await RouteGateFlow.DecideAsync(world, t.Id, change);

        var view = await PendingAsync(t);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("pending").ValueKind);
        Assert.Equal(Large, view.GetProperty("effectiveRoutes").GetProperty(AiUseCases.Agent)[0].GetString());
        var list = await OkAsync(t["aip"].Client.GetAsync("/api/v1/ai/route-changes"));
        Assert.Equal("applied", list[0].GetProperty("status").GetString());
        Assert.Equal("passed", list[0].GetProperty("gateStatus").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AiRouteChangeApplied));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalGateDecided));
        // A second delivery of either message changes nothing.
        await RouteGateFlow.StartAsync(world, t.Id, change);
        await RouteGateFlow.DecideAsync(world, t.Id, change);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AiRouteChangeApplied));
    }

    [Fact]
    public async Task Published_agents_run_on_the_proposed_model_and_an_incomplete_gate_waits_for_a_reason()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var agent = await PublishedAsync(t, ["knowledge.search"]);
        var change = await ProposeAsync(t);
        await RouteGateFlow.StartAsync(world, t.Id, change);

        var runs = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/runs?agentId={agent}"));
        var gateRun = runs.EnumerateArray().Single(r => r.GetProperty("trigger").GetString() == RunTriggers.Route);
        Assert.Equal(1, gateRun.GetProperty("agentVersion").GetInt32());
        await RouteGateFlow.RunAsync(world, t.Id, change);
        // Every case ran on the proposed route, not the one in force.
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from evaluation.case_result where run_id = @run and status = 'done' and model <> @model", ("run", gateRun.GetProperty("id").GetGuid()), ("model", Large)));
        Assert.True(await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.case_result where run_id = @run and status = 'done'",
            ("run", gateRun.GetProperty("id").GetGuid())) > 0);

        await RouteGateFlow.DecideAsync(world, t.Id, change);
        var pending = (await PendingAsync(t)).GetProperty("pending");
        Assert.Equal("incomplete", pending.GetProperty("gateStatus").GetString());
        Assert.True(pending.GetProperty("canOverride").GetBoolean());
        Assert.Contains(pending.GetProperty("failures").EnumerateArray(), f => f.GetString()!.StartsWith($"{agent:N}/", StringComparison.Ordinal));
        // The route run is not the agent's own release gate.
        var review = await OkAsync(t["aip"].Client.GetAsync($"{Eval}/gates?agentId={agent}&version=1"));
        Assert.Equal("passed", review.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await other["aip"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{change}/override", new { reason = "Another tenant applying it." })).StatusCode);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from evaluation.route_gate"));
        var applied = await OkAsync(t["aip"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{change}/override", new { reason = "Reviewed the agent's answers by hand." }));
        Assert.True(applied.GetProperty("applied").GetBoolean());
    }

    [Fact]
    public async Task A_critical_failure_in_any_agent_rejects_the_change()
    {
        var t = await FreshAsync();
        var agent = await PublishedAsync(t, ["knowledge.search", "test.secret"]);
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "Security probes" } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new
        {
            version = 0,
            cases = new[] { new { input = "What do the security records say? " + Call("test.secret", """{"about":"leave"}"""), locale = "en", expect = new { keyFacts = new[] { "security" } }, tags = new[] { "probe" } } },
        }));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite", new { version = 0, metrics = new[] { new { key = "unauthorized_attempts", weight = 1 } }, datasets = new[] { dataset } }));

        var change = await ProposeAsync(t);
        await RouteGateFlow.StartAsync(world, t.Id, change);
        await RouteGateFlow.RunAsync(world, t.Id, change);
        await RouteGateFlow.DecideAsync(world, t.Id, change);

        var view = await PendingAsync(t);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("pending").ValueKind);
        Assert.Equal(Small, view.GetProperty("effectiveRoutes").GetProperty(AiUseCases.Agent)[0].GetString());
        var list = await OkAsync(t["aip"].Client.GetAsync("/api/v1/ai/route-changes"));
        Assert.Equal("rejected", list[0].GetProperty("status").GetString());
        Assert.Contains($"{agent:N}/unauthorized_attempts:en", list[0].GetProperty("failures").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.AiRouteChangeRejected));
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["aip"].Client.PostAsJsonAsync($"/api/v1/ai/route-changes/{change}/override", new { reason = "Applying despite the critical failure." })).StatusCode);
    }
}
