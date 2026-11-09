using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 012 T007 (US3 scenario 3, FR-008) with spec 034 US5 scenario 5: a retrieval configuration change builds a
/// shadow index, runs the published agents' retrieval cases against it as their personas, and replaces the live index
/// only when the gate passed or a search administrator applied it with a reason. A forbidden source retrieved rejects
/// it. The shadow index follows owner changes while the change is open, and nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchRetrievalGateTests(TenantWorld world)
{
    private const string Config = "/api/v1/search/retrieval-config";
    private const string Items = "/api/v1/content/items";
    private const string Agents = "/api/v1/agents";
    private const string Eval = "/api/v1/evaluation";

    private const string Checklist =
        "Check the forklift brakes, horn and mast chains before every shift. Walk around the truck and look for leaks under it. Test the lights and the reversing alarm. "
        + "Make sure the load backrest is in place and the forks are not cracked or bent. Check the tyre pressure and look for cuts in the tyres. "
        + "Read the hour meter and write it in the log book. Report any fault to the shift supervisor before you drive the truck, and tag it out of service if it is unsafe. "
        + "Charge electric trucks only in the marked charging area and never smoke near the batteries. Wear the seat belt at all times while driving.";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("srg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "pia", "sara", "aip", "emp" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var hr = await world.CreateUnitAsync(t, "Hr", null, "subsidiary");
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
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

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 40 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static async Task<Guid> NewsAsync(HttpClient client, string title, string body)
    {
        var id = (await OkAsync(client.PostAsJsonAsync(Items, new
        {
            type = "news", variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(body) } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    /// <summary>The HR template in Hr with knowledge search, published as version 1 (its own gate is marked passed).</summary>
    private async Task<Guid> AgentAsync(TestTenant t)
    {
        var detail = await OkAsync(t["sara"].Client.PostAsJsonAsync(Agents, new { templateId = "hr_assistant", orgUnitId = t.Unit("Hr") }), HttpStatusCode.Created);
        var id = detail.GetProperty("agent").GetProperty("id").GetGuid();
        var definition = JsonSerializer.Deserialize<Dictionary<string, object?>>(detail.GetProperty("definition").GetRawText())!;
        definition["tools"] = new[] { "knowledge.search" };
        definition["autonomy"] = "L1";
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Agents}/{id}/draft", new { revision = 1, definition }));
        await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Agents}/{id}/submit", new { revision = 2 }));
        await world.Database.PassGateAsync(t.Id, id, 1);
        await OkAsync(t["aip"].Client.PostAsJsonAsync($"{Agents}/{id}/publish", new { version = 1 }));
        return id;
    }

    /// <summary>Gives the agent its own suite with one dataset of the given cases.</summary>
    private static async Task SuiteAsync(TestTenant t, Guid agent, object[] cases)
    {
        var dataset = (await OkAsync(t["sara"].Client.PostAsJsonAsync($"{Eval}/datasets", new { agentId = agent, names = new { en = "Retrieval cases" } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/datasets/{dataset}/cases", new { version = 0, cases }));
        await OkAsync(t["sara"].Client.PutAsJsonAsync($"{Eval}/agents/{agent}/suite",
            new { version = 0, metrics = new[] { new { key = "recall_at_5", weight = 1 } }, datasets = new[] { dataset } }));
    }

    private static object Case(string input, object expect) => new { input, locale = "en", expect, tags = new[] { "retrieval" } };

    private Task<long> UnitsAsync(TestTenant t, string table, Guid item) =>
        world.Database.TenantScalarAsync<long>(t.Id, $"select count(*) from search.{table} where item_id = @item", ("item", item));

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    [Fact]
    public async Task A_passing_gate_applies_the_change_and_replaces_the_live_index()
    {
        var t = await FreshAsync();
        var item = await NewsAsync(t["pia"].Client, "Forklift inspection checklist", Checklist);
        await DispatchAsync();
        Assert.Equal(1, await UnitsAsync(t, "unit", item));
        var agent = await AgentAsync(t);
        await SuiteAsync(t, agent, Enumerable.Range(1, 20)
            .Select(i => Case($"What do I check on a forklift before shift {i}?", new { sources = new[] { new { source = "content", itemId = item } } })).ToArray());

        var before = await OkAsync(t["admin"].Client.GetAsync(Config));
        Assert.Equal(0, before.GetProperty("active").GetProperty("version").GetInt32());
        Assert.Equal(400, before.GetProperty("active").GetProperty("passageTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("open").ValueKind);

        var proposed = await OkAsync(t["admin"].Client.PostAsJsonAsync(Config, new { passageTokens = 100, overlapPercent = 10, topK = 5 }), HttpStatusCode.Accepted);
        var open = proposed.GetProperty("open");
        Assert.Equal("building", open.GetProperty("status").GetString());
        Assert.Equal(1, open.GetProperty("settings").GetProperty("version").GetInt32());
        // Search keeps the configuration in force until the gate decides.
        Assert.Equal(0, proposed.GetProperty("active").GetProperty("version").GetInt32());

        await DispatchAsync();

        var after = await OkAsync(t["admin"].Client.GetAsync(Config));
        Assert.Equal(JsonValueKind.Null, after.GetProperty("open").ValueKind);
        var active = after.GetProperty("active");
        Assert.Equal(1, active.GetProperty("version").GetInt32());
        Assert.Equal(100, active.GetProperty("passageTokens").GetInt32());
        Assert.Equal(5, active.GetProperty("topK").GetInt32());
        var change = after.GetProperty("history")[0];
        Assert.Equal("active", change.GetProperty("status").GetString());
        Assert.Equal("passed", change.GetProperty("gateResult").GetString());
        Assert.True(change.GetProperty("shadowUnits").GetInt32() > 1);

        // The 20 cases ran as the agent's unit persona against the shadow index, and found the checklist each time.
        var run = await world.Database.TenantScalarAsync<string>(t.Id,
            "select json_build_object('verdict', verdict, 'total', total, 'errors', errors)::text from evaluation.run where capability = 'retrieval'");
        using (var doc = JsonDocument.Parse(run!))
        {
            Assert.Equal("passed", doc.RootElement.GetProperty("verdict").GetString());
            Assert.Equal(20, doc.RootElement.GetProperty("total").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("errors").GetInt32());
        }

        // The live index now holds the shorter passages and the shadow index is empty.
        Assert.True(await UnitsAsync(t, "unit", item) > 1);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.shadow_unit"));
        var passages = await world.Factory.AsUserAsync(t.Id, t["emp"].Id,
            sp => sp.GetRequiredService<ISearchRetrieval>().RetrieveAsync(new RetrievalRequest("forklift brakes horn", "assistant")));
        Assert.Contains(passages.Passages, p => p.ItemId == item);
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.SearchRetrievalChangeProposed));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.SearchRetrievalChangeDecided));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.EvalGateDecided));
        Assert.Equal(0, await AuditCountAsync(t, AuditActions.SearchRetrievalChangeOverridden));
    }

    [Fact]
    public async Task An_incomplete_gate_holds_the_change_for_a_reason_and_a_forbidden_source_rejects_it()
    {
        var t = await FreshAsync();
        var item = await NewsAsync(t["pia"].Client, "Forklift inspection checklist", Checklist);
        await DispatchAsync();

        // No published agent has a retrieval case, so the gate cannot measure anything and holds the change.
        await OkAsync(t["admin"].Client.PostAsJsonAsync(Config, new { topK = 6 }), HttpStatusCode.Accepted);
        await DispatchAsync();
        var held = (await OkAsync(t["admin"].Client.GetAsync(Config))).GetProperty("open");
        Assert.Equal("held", held.GetProperty("status").GetString());
        Assert.Equal("incomplete", held.GetProperty("gateResult").GetString());
        Assert.Equal("retrieval/no_cases", held.GetProperty("failures")[0].GetString());
        Assert.True(held.GetProperty("canApply").GetBoolean());
        var id = held.GetProperty("id").GetGuid();

        // Only one change at a time.
        Assert.Equal("conflict.search.retrieval_change_open",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync(Config, new { topK = 7 }), HttpStatusCode.Conflict));
        Assert.Equal("validation.search.override_reason",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Config}/{id}/apply", new { reason = "ok" }), HttpStatusCode.BadRequest));

        // While the change is open, owner changes reach the shadow index too.
        var later = await NewsAsync(t["pia"].Client, "Pallet truck charging", "Charge pallet trucks in the marked area at the end of every shift.");
        await DispatchAsync();
        Assert.Equal(1, await UnitsAsync(t, "shadow_unit", later));

        var applied = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Config}/{id}/apply", new { reason = "No retrieval cases yet; reviewed by hand." }));
        Assert.Equal(1, applied.GetProperty("active").GetProperty("version").GetInt32());
        Assert.Equal(6, applied.GetProperty("active").GetProperty("topK").GetInt32());
        Assert.Equal("No retrieval cases yet; reviewed by hand.", applied.GetProperty("history")[0].GetProperty("overrideReason").GetString());
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.SearchRetrievalChangeOverridden));
        Assert.Equal(1, await UnitsAsync(t, "unit", later));

        // A case that must not see the checklist does see it: a critical failure, so the change is rejected.
        var agent = await AgentAsync(t);
        await SuiteAsync(t, agent, [Case("forklift checklist", new { forbiddenSources = new[] { new { source = "content", itemId = item } } })]);
        await OkAsync(t["admin"].Client.PostAsJsonAsync(Config, new { passageTokens = 200 }), HttpStatusCode.Accepted);
        await DispatchAsync();
        var view = await OkAsync(t["admin"].Client.GetAsync(Config));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("open").ValueKind);
        var rejected = view.GetProperty("history")[0];
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("failed_critical", rejected.GetProperty("gateResult").GetString());
        Assert.Contains(rejected.GetProperty("failures").EnumerateArray(), f => f.GetString()!.StartsWith("retrieval/forbidden_source_rate", StringComparison.Ordinal));
        Assert.Equal("conflict.search.retrieval_change_decided",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Config}/{rejected.GetProperty("id").GetGuid()}/apply",
                new { reason = "Apply it anyway, please." }), HttpStatusCode.Conflict));
        Assert.Equal(1, view.GetProperty("active").GetProperty("version").GetInt32());
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.shadow_unit"));
    }

    [Fact]
    public async Task Only_search_administrators_of_the_tenant_manage_changes_and_cancel_empties_the_shadow()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var item = await NewsAsync(t["pia"].Client, "Forklift inspection checklist", Checklist);
        await DispatchAsync();

        await OkAsync(t["emp"].Client.GetAsync(Config), HttpStatusCode.Forbidden);
        await OkAsync(t["emp"].Client.PostAsJsonAsync(Config, new { topK = 4 }), HttpStatusCode.Forbidden);
        Assert.Equal("validation.search.retrieval_unchanged",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync(Config, new { topK = 8, passageTokens = 400 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.search.passage_tokens",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync(Config, new { passageTokens = 5000 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.search.embedding_model",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync(Config, new { embeddingModel = "bad model!" }), HttpStatusCode.BadRequest));

        var id = (await OkAsync(t["admin"].Client.PostAsJsonAsync(Config, new { passageTokens = 100 }), HttpStatusCode.Accepted))
            .GetProperty("open").GetProperty("id").GetGuid();
        // Build the shadow index only, so the change waits for its gate.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<TeamBeats.Modules.Search.RetrievalShadowBuilder>().HandleAsync(new RetrievalShadowBuildRequested(id), CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return true;
        });
        Assert.True(await UnitsAsync(t, "shadow_unit", item) > 1);
        Assert.Equal(1, await UnitsAsync(t, "unit", item));

        // The gate retrieval reads the shadow index; another tenant has no such change, and a change not waiting is refused.
        var shadow = await world.Factory.AsUserAsync(t.Id, t["emp"].Id,
            sp => sp.GetRequiredService<IRetrievalEvaluation>().RetrieveProposedAsync(id, "forklift brakes", "agent"));
        Assert.Contains(shadow.Passages, p => p.ItemId == item);
        var refused = await Assert.ThrowsAsync<AppException>(() => world.Factory.AsUserAsync(other.Id, other["emp"].Id,
            sp => sp.GetRequiredService<IRetrievalEvaluation>().RetrieveProposedAsync(id, "forklift brakes", "agent")));
        Assert.Equal("conflict.search.retrieval_change_not_gating", refused.DetailKey);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(other.Id, "select count(*) from search.shadow_unit"));
        await OkAsync(other["admin"].Client.PostAsJsonAsync($"{Config}/{id}/cancel", new { }), HttpStatusCode.NotFound);
        Assert.Equal(JsonValueKind.Null, (await OkAsync(other["admin"].Client.GetAsync(Config))).GetProperty("open").ValueKind);

        Assert.Equal("conflict.search.retrieval_gate_running",
            await ProblemKeyAsync(t["admin"].Client.PostAsJsonAsync($"{Config}/{id}/apply", new { reason = "Apply before the gate ends." }), HttpStatusCode.Conflict));
        var cancelled = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Config}/{id}/cancel", new { }));
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("open").ValueKind);
        Assert.Equal("cancelled", cancelled.GetProperty("history")[0].GetProperty("status").GetString());
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.shadow_unit"));
        Assert.Equal(1, await AuditCountAsync(t, AuditActions.SearchRetrievalChangeCancelled));

        // A late gate decision for the cancelled change changes nothing.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<TeamBeats.Modules.Search.RetrievalGateDecisions>()
                .HandleAsync(new TeamBeats.Modules.Evaluation.Contracts.EvaluationRetrievalGateDecided(id, "passed", []), CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return true;
        });
        Assert.Equal(0, (await OkAsync(t["admin"].Client.GetAsync(Config))).GetProperty("active").GetProperty("version").GetInt32());

        // The queued build and gate messages of the cancelled change find nothing to do when they arrive.
        await DispatchAsync();
        var final = await OkAsync(t["admin"].Client.GetAsync(Config));
        Assert.Equal(0, final.GetProperty("active").GetProperty("version").GetInt32());
        Assert.Equal("cancelled", final.GetProperty("history")[0].GetProperty("status").GetString());
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from search.shadow_unit"));
    }
}
