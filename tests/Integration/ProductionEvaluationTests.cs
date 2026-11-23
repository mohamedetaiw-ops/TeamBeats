using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 034 R2 T018 (US7, FR-023): a person's finished agent run is sampled by tenant policy and scored on the workers
/// with reference-free metrics; no question or answer text is kept unless the tenant retains Public and Internal samples;
/// Restricted runs give metadata metrics only and Confidential ones are judged only by an eligible model; opted-out people,
/// a tenant that turned it off, the daily cap and a package without it stop sampling; results are read in the agent's
/// scope and never across tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ProductionEvaluationTests(TenantWorld world)
{
    private const string Eval = "/api/v1/evaluation";
    private const string Agents = "/api/v1/agents";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pev-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        foreach (var name in new[] { "sara", "emp", "aip", "pia" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AddMembershipAsync(t, "emp", "Hr");
        await world.AssignAsync(t, "sara", BuiltInRoles.AgentDesigner, "org_subtree", hr);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()! : body.GetProperty("code").GetString()!;
    }

    private static string Call(string tool, string? args = null) => $"[fake:tool:{tool.Replace(".", "__")}]" + (args is null ? string.Empty : $" [fake:args:{args}]");

    private async Task<Guid> AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "enterprise_knowledge", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        definition["autonomy"] = "L0";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    private async Task NewsAsync(TestTenant t)
    {
        var variants = new Dictionary<string, object>
        {
            ["en"] = new { title = "Forklift inspection checklist", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "Check the forklift brakes, horn and mast chains before every shift." } } } } } },
        };
        var id = (await OkAsync(t["pia"].Client.PostAsJsonAsync("/api/v1/content/items", new { type = "news", variants }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["pia"].Client.PostAsJsonAsync($"/api/v1/content/items/{id}/publish", new { }));
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static Task<JsonElement> RunAsync(TestTenant t, Guid agent, string user = "emp") =>
        OkAsync(t[user].Client.PostAsJsonAsync($"{Agents}/{agent}/runs", new { message = "What do I check on a forklift? " + Call("knowledge.search", """{"query":"forklift"}""") }));

    private Task<long> SamplesAsync(TestTenant t) => world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.production_sample");

    /// <summary>The worker scores every queued sample, as the message consumer would.</summary>
    private async Task ScoreAsync(TestTenant t)
    {
        var ids = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().QueryAsync(
            "select id from evaluation.production_sample where status = 'queued'", null, r => r.GetGuid(0), CancellationToken.None));
        foreach (var id in ids)
        {
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<ProductionScorer>().HandleAsync(new ProductionSampleCaptured(id), CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
    }

    [Fact]
    public async Task A_sampled_run_is_scored_after_it_ends_and_keeps_no_text()
    {
        var t = await FreshAsync();
        await NewsAsync(t);
        var agent = await AgentAsync(t);
        var policy = await OkAsync(t["aip"].Client.GetAsync($"{Eval}/production/policy"));
        Assert.True(policy.GetProperty("available").GetBoolean());
        Assert.Equal(2, policy.GetProperty("ratePercent").GetDouble());
        Assert.Equal(1000, policy.GetProperty("dailyCap").GetInt32());
        Assert.Equal(0, policy.GetProperty("retentionDays").GetInt32());
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 }));

        var run = await RunAsync(t, agent);
        Assert.Equal("completed", run.GetProperty("status").GetString());
        var runId = run.GetProperty("id").GetGuid();
        // Sampled as the run ended; scoring waits for the worker, so the person's answer did not wait for it.
        Assert.Equal(1, await SamplesAsync(t));
        Assert.Equal("queued", await world.Database.TenantScalarAsync<string>(t.Id, "select status from evaluation.production_sample where agent_run_id = @id", ("id", runId)));
        Assert.Equal(1, await world.Database.OutboxCountAsync("ProductionSampleCaptured", t.Id));
        // The sample names the agent and its unit, never the person.
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from information_schema.columns where table_schema = 'evaluation' and table_name = 'production_sample' and column_name = 'user_id'"));

        await ScoreAsync(t);
        Assert.Equal("scored", await world.Database.TenantScalarAsync<string>(t.Id, "select status from evaluation.production_sample where agent_run_id = @id", ("id", runId)));
        // With metadata-only logging and no retention, the question, answer and evidence are gone once scored.
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.production_sample where content is not null"));

        var results = await OkAsync(t["sara"].Client.GetAsync($"{Eval}/agents/{agent}/production"));
        Assert.Equal(1, results.GetProperty("samples").GetInt32());
        Assert.Equal(1, results.GetProperty("scored").GetInt32());
        var metrics = results.GetProperty("metrics").EnumerateArray().ToDictionary(m => m.GetProperty("key").GetString()!, m => m);
        Assert.Equal(1, metrics["citation_correctness"].GetProperty("value").GetDouble());
        Assert.Equal(1, metrics["citation_correctness"].GetProperty("samples").GetInt32());
        Assert.Equal(0, metrics["unauthorized_attempts"].GetProperty("value").GetDouble());
        Assert.True(metrics.ContainsKey("groundedness"));
        var sample = Assert.Single(results.GetProperty("recent").EnumerateArray());
        Assert.Equal("full", sample.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("content").ValueKind);
        Assert.Equal(1, sample.GetProperty("toolCalls").GetInt32());
        Assert.True(sample.GetProperty("metrics").TryGetProperty("latency_ms", out _));

        // Scoring the same message again changes nothing.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var id = await sp.GetRequiredService<IDbSession>().ScalarAsync<Guid>("select id from evaluation.production_sample", null, CancellationToken.None);
            await sp.GetRequiredService<ProductionScorer>().HandleAsync(new ProductionSampleCaptured(id), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        Assert.Equal(1, await SamplesAsync(t));

        // The results are the agent's evaluation: the person who ran it and another tenant see nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["emp"].Client.GetAsync($"{Eval}/agents/{agent}/production")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"{Eval}/agents/{agent}/production")).StatusCode);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(world.B.Id, "select count(*) from evaluation.production_sample where agent_id = @a", ("a", agent)));
    }

    [Fact]
    public async Task Opted_out_people_a_disabled_tenant_and_the_daily_cap_stop_sampling()
    {
        var t = await FreshAsync();
        await NewsAsync(t);
        var agent = await AgentAsync(t);
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100, dailyCap = 1 }));

        // A person who opted out is never sampled.
        Assert.False((await OkAsync(t["emp"].Client.GetAsync($"{Eval}/production/opt-out"))).GetProperty("optedOut").GetBoolean());
        Assert.True((await OkAsync(t["emp"].Client.PutAsJsonAsync($"{Eval}/production/opt-out", new { optedOut = true }))).GetProperty("optedOut").GetBoolean());
        await RunAsync(t, agent);
        Assert.Equal(0, await SamplesAsync(t));
        await OkAsync(t["emp"].Client.PutAsJsonAsync($"{Eval}/production/opt-out", new { optedOut = false }));
        await RunAsync(t, agent);
        Assert.Equal(1, await SamplesAsync(t));
        // The agent reached its daily cap.
        await RunAsync(t, agent);
        Assert.Equal(1, await SamplesAsync(t));

        // A tenant that turned production evaluation off samples nothing, whatever the cap.
        var off = await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { enabled = false, dailyCap = 100 }));
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Equal(100, off.GetProperty("dailyCap").GetInt32());
        await RunAsync(t, agent);
        Assert.Equal(1, await SamplesAsync(t));
        Assert.Equal(2, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = 'eval.policy_changed' and target_type = 'eval_production_policy'"));

        // The policy is the AI Policy Administrator's, within limits.
        Assert.Equal("validation.eval.production_rate", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 101 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.production_cap", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { dailyCap = 1001 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.eval.production_retention", await ProblemAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { retentionDays = 31 }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync($"{Eval}/production/policy")).StatusCode);
    }

    [Fact]
    public async Task A_package_without_production_evaluation_samples_nothing()
    {
        var t = await FreshAsync("workplace");
        var policy = await OkAsync(t["aip"].Client.GetAsync($"{Eval}/production/policy"));
        Assert.False(policy.GetProperty("available").GetBoolean());
        Assert.False(policy.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100 })).StatusCode);
        // The person's own choice is still kept, so an upgrade honors it.
        Assert.True((await OkAsync(t["emp"].Client.PutAsJsonAsync($"{Eval}/production/opt-out", new { optedOut = true }))).GetProperty("optedOut").GetBoolean());

        var offered = await OfferAsync(t, "internal");
        Assert.False(offered);
        Assert.Equal(0, await SamplesAsync(t));
    }

    /// <summary>Offers a run as the runtime would at its end; returns whether its content was asked for.</summary>
    private async Task<bool> OfferAsync(TestTenant t, string classification, Guid? agent = null)
    {
        var asked = false;
        var facts = new ProductionRunFacts(Guid.NewGuid(), agent ?? Guid.NewGuid(), 1, t["emp"].Id, t.Unit("Hr"), "completed", "answered", null, "en", classification,
            "openai/gpt-4o-mini", 120, 40, 900, 1, 0);
        var source = new EvalRunSource(1, "content", Guid.NewGuid());
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IProductionSampler>().OfferAsync(facts, _ =>
            {
                asked = true;
                return Task.FromResult(new ProductionRunContent("What is the leave policy?", "Annual leave is 25 days [1].", "Annual leave is 25 days.",
                    [new EvalRunStep(1, "tool", "knowledge.search", "executed", null, null)], [source], [source], null));
            }, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        return asked;
    }

    [Fact]
    public async Task Restricted_runs_give_metadata_only_Confidential_needs_an_eligible_judge_and_retained_samples_expire()
    {
        var t = await FreshAsync();
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { ratePercent = 100, retentionDays = 10 }));

        // Restricted: the content is never read, and only metadata metrics are scored.
        Assert.False(await OfferAsync(t, "restricted"));
        // Confidential: the platform policy has no model eligible for it, so only the deterministic metrics count, and it is not retained.
        Assert.True(await OfferAsync(t, "confidential"));
        // Internal with retention on: kept for the tenant's period after scoring.
        Assert.True(await OfferAsync(t, "internal"));
        await ScoreAsync(t);

        var rows = await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IDbSession>().QueryAsync(
            "select classification, mode, metrics::text, content is not null, content_expires_at - sampled_at from evaluation.production_sample", null,
            r => (Classification: r.GetString(0), Mode: r.GetString(1), Metrics: JsonDocument.Parse(r.GetString(2)).RootElement.Clone(), Kept: r.GetBoolean(3),
                For: r.IsDBNull(4) ? (TimeSpan?)null : r.GetFieldValue<TimeSpan>(4)), CancellationToken.None));
        var restricted = rows.Single(r => r.Classification == "restricted");
        Assert.Equal("metadata", restricted.Mode);
        Assert.False(restricted.Kept);
        Assert.True(restricted.Metrics.TryGetProperty("latency_ms", out _));
        Assert.False(restricted.Metrics.TryGetProperty("citation_correctness", out _));
        var confidential = rows.Single(r => r.Classification == "confidential");
        Assert.Equal("deterministic", confidential.Mode);
        Assert.False(confidential.Kept);
        Assert.Equal(1, confidential.Metrics.GetProperty("citation_correctness").GetProperty("value").GetDouble());
        Assert.Equal("not_evaluated", confidential.Metrics.GetProperty("groundedness").GetProperty("state").GetString());
        var internalRun = rows.Single(r => r.Classification == "internal");
        Assert.True(internalRun.Kept);
        Assert.Equal(TimeSpan.FromDays(10), internalRun.For);

        // Shortening retention to none drops what was kept; retention past its time is purged.
        await OkAsync(t["aip"].Client.PutAsJsonAsync($"{Eval}/production/policy", new { retentionDays = 5 }));
        Assert.Equal(TimeSpan.FromDays(5), await world.Database.TenantScalarAsync<TimeSpan>(t.Id,
            "select content_expires_at - sampled_at from evaluation.production_sample where content is not null"));
        var purged = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ProductionRetention>().PurgeAsync(DateTimeOffset.UtcNow.AddDays(6), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });
        Assert.Equal(1, purged);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from evaluation.production_sample where content is not null"));
    }
}
