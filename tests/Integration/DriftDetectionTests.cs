using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R2 T019 (US7 scenario 6, FR-024): rolling production scores are compared with the last gate result of the
/// same version and the metric's threshold; a drift opens one alert per metric and language that tells the agent owner
/// and the AI Policy Administrators once, stays up to date while it lasts and resolves when the window recovers. The
/// alerts are read in the agent's scope and never across tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DriftDetectionTests(TenantWorld world)
{
    private const string Agents = "/api/v1/agents";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(TestTenant T, Guid Agent)> AgentAsync()
    {
        var t = await world.CreateTenantAsync("dft-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = hr }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        // The gate measured relevance at 0.95 in English, with no threshold of its own.
        await world.Database.TenantScalarAsync<long>(t.Id,
            """
            with r as (update evaluation.run set result = jsonb_set(result, '{metrics}', @metrics::jsonb) where agent_id = @agent and trigger = 'gate' returning 1)
            select count(*) from r
            """, ("agent", id),
            ("metrics", """[{"key":"relevance","weight":1,"threshold":null,"severity":null,"thresholdLevel":null,"locales":[{"locale":"en","cases":30,"value":0.95,"failures":1,"notEvaluated":0,"status":"no_threshold"}],"overall":0.95}]"""));
        return (t, id);
    }

    /// <summary>Production samples as the scorer leaves them, with the given scores.</summary>
    private Task SamplesAsync(TestTenant t, Guid agent, int count, double groundedness, double relevance, string model = "openai/gpt-4o-mini") =>
        world.Database.TenantScalarAsync<long>(t.Id,
            """
            with s as (
              insert into evaluation.production_sample (tenant_id, id, agent_run_id, agent_id, agent_version, org_unit_id, language, classification, mode, status,
                run_status, outcome, model, input_tokens, output_tokens, latency_ms, tool_calls, denials, metrics, sampled_at, scored_at)
              select platform.current_tenant_id(), gen_random_uuid(), gen_random_uuid(), @agent, 1, @unit, 'en', 'internal', 'full', 'scored', 'completed', 'answered',
                @model, 100, 20, 900, 1, 0,
                jsonb_build_object('groundedness', jsonb_build_object('state', 'scored', 'value', @g::float8, 'note', null),
                                   'relevance', jsonb_build_object('state', 'scored', 'value', @r::float8, 'note', null)),
                now() - interval '1 hour', now() - interval '1 hour'
              from generate_series(1, @n) returning 1)
            select count(*) from s
            """, ("agent", agent), ("unit", t.Unit("Hr")), ("model", model), ("g", groundedness), ("r", relevance), ("n", count));

    private Task<int> ScanAsync(TestTenant t) => world.Factory.InScopeAsync(t.Id, async sp =>
    {
        var n = await sp.GetRequiredService<DriftDetector>().ScanAsync(CancellationToken.None);
        await sp.GetRequiredService<IDbSession>().CommitAsync();
        return n;
    });

    private Task<long> NotifiedAsync(TestTenant t, string user) => world.Database.TenantScalarAsync<long>(t.Id,
        "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", DriftDetector.Template));

    [Fact]
    public async Task A_drop_below_the_threshold_or_the_gate_opens_one_alert_that_resolves_when_scores_recover()
    {
        var (t, agent) = await AgentAsync();

        // Too few samples decide nothing.
        await SamplesAsync(t, agent, DriftRules.MinSamples - 1, 0.5, 0.5);
        Assert.Equal(0, await ScanAsync(t));

        // Groundedness falls below its 0.90 threshold and relevance 15 points under the gate; a second model has appeared.
        await SamplesAsync(t, agent, 5, 0.6, 0.8, "openai/gpt-4o");
        Assert.Equal(2, await ScanAsync(t));
        var view = await OkAsync(t["sara"].Client.GetAsync($"/api/v1/evaluation/agents/{agent}/drift"));
        Assert.Equal(DriftRules.MinSamples, view.GetProperty("minSamples").GetInt32());
        Assert.Equal("/admin/ai/routing", view.GetProperty("routingLink").GetString());
        var alerts = view.GetProperty("alerts").EnumerateArray().ToDictionary(a => a.GetProperty("metric").GetString()!, a => a);
        Assert.Equal(0.9, alerts["groundedness"].GetProperty("threshold").GetDouble());
        Assert.Equal(0.95, alerts["relevance"].GetProperty("baseline").GetDouble());
        Assert.InRange(alerts["relevance"].GetProperty("value").GetDouble(), 0.5, 0.8);
        Assert.Equal("open", alerts["groundedness"].GetProperty("status").GetString());
        Assert.Equal(24, alerts["groundedness"].GetProperty("samples").GetInt32());
        Assert.Equal(DriftRules.Examples, alerts["groundedness"].GetProperty("examples").GetArrayLength());
        Assert.Equal(["openai/gpt-4o", "openai/gpt-4o-mini"], alerts["groundedness"].GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        // The owner and the AI Policy Administrator are told once.
        Assert.Equal(1, await NotifiedAsync(t, "sara"));
        Assert.Equal(1, await NotifiedAsync(t, "aip"));
        Assert.Equal(0, await NotifiedAsync(t, "emp"));

        // Still drifting: the same alerts, no new message.
        Assert.Equal(0, await ScanAsync(t));
        Assert.Equal(2, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.drift_alert where status = 'open'"));
        Assert.Equal(1, await NotifiedAsync(t, "sara"));

        // The window recovers: the old samples age out and good ones come in.
        await world.Database.TenantScalarAsync<long>(t.Id,
            "with s as (update evaluation.production_sample set sampled_at = now() - interval '8 days' returning 1) select count(*) from s");
        await SamplesAsync(t, agent, DriftRules.MinSamples, 1, 1);
        Assert.Equal(0, await ScanAsync(t));
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.drift_alert where status = 'open'"));
        var resolved = await OkAsync(t["sara"].Client.GetAsync($"/api/v1/evaluation/agents/{agent}/drift"));
        Assert.All(resolved.GetProperty("alerts").EnumerateArray(), a => Assert.Equal("resolved", a.GetProperty("status").GetString()));

        // Read in the agent's scope only, and never from another tenant.
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"/api/v1/evaluation/agents/{agent}/drift")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"/api/v1/evaluation/agents/{agent}/drift")).StatusCode);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(world.B.Id, "select count(*) from evaluation.drift_alert where agent_id = @a", ("a", agent)));
    }
}
