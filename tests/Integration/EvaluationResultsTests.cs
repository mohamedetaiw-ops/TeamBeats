using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 T013 (US6): the evaluation overview lists only agents in the viewer's scope; an agent's dashboard shows a
/// gate and headline metrics per version; comparing two runs shows metric differences by locale, models, cost and
/// latency, and the cases that changed from pass to fail and back; a case opens with its evidence and execution
/// record; nobody outside the agent's scope or tenant sees any of it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EvaluationResultsTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("evd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "fadi", "aip", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", null, "subsidiary");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "fadi", BuiltInRoles.AgentDesigner, "org_subtree", finance);
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

    private static async Task SaveAsync(TestTenant t, Guid id, string[] tools)
    {
        var detail = await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"));
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = tools;
        definition["autonomy"] = "L1";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = detail.GetProperty("latest").GetProperty("revision").GetInt32(), definition }));
    }

    private async Task<int> SubmitAndPublishAsync(TestTenant t, Guid id)
    {
        var latest = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{id}"))).GetProperty("latest");
        var number = latest.GetProperty("number").GetInt32();
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = latest.GetProperty("revision").GetInt32() }));
        await world.Database.PassGateAsync(t.Id, id, number);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = number }));
        return number;
    }

    private async Task<Guid> RunAsync(TestTenant t, Guid agent) =>
        await FinishAsync(t, (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/runs", new { agentId = agent }), HttpStatusCode.Created)).GetProperty("run").GetProperty("id").GetGuid());

    private async Task<Guid> FinishAsync(TestTenant t, Guid run)
    {
        for (var i = 0; i < 20; i++)
        {
            if (await world.Database.TenantScalarAsync<string>(t.Id, "select status from evaluation.run where id = @run", ("run", run)) == "completed")
            {
                return run;
            }
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<EvaluationRunner>().HandleAsync(new EvaluationRunRequested(run), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
        throw new InvalidOperationException("The run did not finish.");
    }

    [Fact]
    public async Task The_dashboard_and_a_comparison_show_versions_metrics_and_the_cases_that_changed_within_scope()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var agent = detail.GetProperty("agent").GetProperty("id").GetGuid();
        await SaveAsync(t, agent, ["knowledge.search"]);
        await SubmitAndPublishAsync(t, agent);
        // One probe: version 1 cannot call the tool at all, so it passes; version 2 holds it and the persona may not use it.
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "Probes" } }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new
        {
            version = 0,
            cases = new[] { new { input = "What do the security records say? " + Call("test.secret", """{"about":"leave"}"""), locale = "en", expect = new { keyFacts = new[] { "security" } }, tags = new[] { "probe" } } },
        }));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite", new { version = 0, metrics = new[] { new { key = "unauthorized_attempts", weight = 1 } }, datasets = new[] { dataset } }));
        var first = await RunAsync(t, agent);

        await SaveAsync(t, agent, ["knowledge.search", "test.secret"]);
        var latest = (await OkAsync(t["sara"].Client.GetAsync($"{Agents}/{agent}"))).GetProperty("latest");
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{agent}/submit", new { revision = latest.GetProperty("revision").GetInt32() }));
        // Submitting started the gate run of version 2, which is the run to compare.
        var second = await FinishAsync(t, await world.Database.TenantScalarAsync<Guid>(t.Id,
            "select id from evaluation.run where agent_id = @agent and agent_version = 2 and trigger = 'gate'", ("agent", agent)));

        // The dashboard: newest version first, each with its gate and headline metrics.
        var dashboard = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{agent}/dashboard"));
        Assert.True(dashboard.GetProperty("canDesign").GetBoolean());
        var versions = dashboard.GetProperty("versions").EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.True(versions[1].GetProperty("current").GetBoolean());
        Assert.Equal("failed", versions[0].GetProperty("verdict").GetString());
        Assert.Contains(versions[0].GetProperty("metrics").EnumerateArray(), m => m.GetProperty("metric").GetString() == "unauthorized_attempts" && m.GetProperty("locale").GetString() == "en");

        // The comparison: the probe went from pass to fail, with metric differences, models, cost and latency.
        var compare = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/runs/compare?a={first}&b={second}"));
        var failing = Assert.Single(compare.GetProperty("nowFailing").EnumerateArray());
        Assert.Equal("agent", failing.GetProperty("suite").GetString());
        Assert.Empty(compare.GetProperty("nowPassing").EnumerateArray());
        var attempts = compare.GetProperty("metrics").EnumerateArray().Single(m => m.GetProperty("metric").GetString() == "unauthorized_attempts" && m.GetProperty("locale").GetString() == "en");
        Assert.True(attempts.GetProperty("delta").GetDouble() > 0);
        Assert.Contains("openai/gpt-4o-mini", compare.GetProperty("b").GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        Assert.True(compare.GetProperty("b").GetProperty("latencyP50").TryGetProperty("en", out _));

        // A case opens with its evidence and the link to its execution record.
        var item = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/runs/{second}/cases/{failing.GetProperty("caseInB").GetInt32()}"));
        Assert.True(item.TryGetProperty("agentRunId", out var runLink) && runLink.ValueKind != JsonValueKind.Null, item.GetRawText());
        Assert.True(item.GetProperty("failed").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["sara"].Client.GetAsync($"{Eval}/runs/{second}/cases/9999")).StatusCode);

        // The overview lists agents in the viewer's scope only; others see nothing of this agent.
        Assert.Contains((await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents"))).EnumerateArray(), a => a.GetProperty("agentId").GetGuid() == agent);
        Assert.Contains((await OkAsync(t["aip"].Client.GetAsync($"{Eval}/agents"))).EnumerateArray(), a => a.GetProperty("agentId").GetGuid() == agent);
        Assert.DoesNotContain((await OkAsync(t["fadi"].Client.GetAsync($"{Eval}/agents"))).EnumerateArray(), a => a.GetProperty("agentId").GetGuid() == agent);
        Assert.Empty((await OkAsync(t["emp"].Client.GetAsync($"{Eval}/agents"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.GetAsync($"{Eval}/agents/{agent}/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["fadi"].Client.GetAsync($"{Eval}/runs/compare?a={first}&b={second}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["aip"].Client.GetAsync($"{Eval}/agents/{agent}/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["aip"].Client.GetAsync($"{Eval}/runs/{second}/cases/1")).StatusCode);
        Assert.Empty((await OkAsync(other["aip"].Client.GetAsync($"{Eval}/agents"))).EnumerateArray());
    }

    [Fact]
    public async Task The_evaluation_section_opens_for_designers_reviewers_and_readers_only()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "rev", MembershipTypes.Employee);
        await world.AssignAsync(t, "rev", BuiltInRoles.EvaluationReviewer, "org_subtree", t.Unit("Hr"));
        async Task<JsonElement?> Section(string user) =>
            (await OkAsync(t[user].Client.GetAsync("/api/v1/admin/navigation"))).EnumerateArray().Cast<JsonElement?>().FirstOrDefault(s => s!.Value.GetProperty("key").GetString() == "evaluation");

        var sara = await Section("sara");
        Assert.NotNull(sara);
        Assert.False(sara.Value.GetProperty("wholeTenant").GetBoolean());
        Assert.Equal("/admin/evaluation", sara.Value.GetProperty("path").GetString());
        Assert.NotNull(await Section("rev"));
        Assert.True((await Section("aip"))!.Value.GetProperty("wholeTenant").GetBoolean());
        Assert.Null(await Section("emp"));
    }
}
