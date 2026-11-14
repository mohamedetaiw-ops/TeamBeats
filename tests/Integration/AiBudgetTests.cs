using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T018 (US5, FR-012): budgets per tenant, org unit, agent and person with block, downgrade and alert actions,
/// the per person daily request limit with its reset time, the 80% and 100% alerts sent once, the scoped budget key,
/// concurrent calls within the spec 005 tolerance, requests given back when a call is refused later, and tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiBudgetTests(TenantWorld world)
{
    private const string Small = "openai/gpt-4o-mini";
    private const string Large = "openai/gpt-4o";
    private const string Embedding = "openai/text-embedding-3-small";

    private FakeOpenRouter Provider => world.Factory.OpenRouter;

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("aib-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp2", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "owner", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> CreateAsync(TestTenant t, object budget, HttpStatusCode expected = HttpStatusCode.Created) =>
        OkAsync(t["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets", budget), expected);

    private Task<AiResult> ChatAsync(TestTenant t, string user = "emp", string useCase = AiUseCases.Assistant, Guid? unit = null, Guid? agent = null,
        DataClassification classification = DataClassification.Internal) =>
        world.Factory.AsUserAsync(t.Id, t[user].Id, sp => sp.GetRequiredService<IAiGateway>().ChatAsync(
            new AiRequest(useCase, [new AiMessage(AiRoles.User, "Hello")], classification, AgentId: agent, ActingUnitId: unit)));

    /// <summary>Records spend in the current period as if earlier calls had cost it.</summary>
    private Task<long> SpendAsync(TestTenant t, decimal cost, Guid? unit = null, Guid? user = null, Guid? agent = null) =>
        world.Database.TenantScalarAsync<long>(t.Id,
            """
            with added as (insert into ai.usage_record (tenant_id, id, at, unit_id, user_id, use_case, agent_id, model, provider, cost, decision)
              values (platform.current_tenant_id(), gen_random_uuid(), now(), @unit, @user, 'assistant', @agent, 'openai/gpt-4o-mini', 'openrouter', @cost, 'served')
              returning 1)
            select count(*) from added
            """, ("unit", (object?)unit ?? DBNull.Value), ("user", (object?)user ?? DBNull.Value), ("agent", (object?)agent ?? DBNull.Value), ("cost", cost));

    private Task<long> AlertsAsync(TestTenant t, string user, int threshold) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t[user].Id), ("k", $"ai.budget_reached_{threshold}"));

    private static DateTimeOffset NextMonth()
    {
        var now = DateTimeOffset.UtcNow;
        return new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1);
    }

    [Fact]
    public async Task A_used_up_tenant_budget_that_blocks_denies_calls_until_it_resets_and_sends_nothing()
    {
        var t = await FreshAsync();
        var budget = await CreateAsync(t, new { scope = "tenant", period = "month", amount = 1.00m, action = "block" });
        Assert.Equal(1, budget.GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiBudgetChanged);

        Assert.True((await ChatAsync(t)).IsOk);
        await SpendAsync(t, 1.00m);
        var before = Provider.Requests;
        var denied = await ChatAsync(t);
        Assert.Equal(AiDenials.BudgetExhausted, denied.Denial);
        Assert.Equal(NextMonth(), denied.ResetsAt);
        Assert.Equal(before, Provider.Requests);

        var list = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/budgets"));
        Assert.True(list.GetProperty("editable").GetBoolean());
        var row = list.GetProperty("budgets")[0];
        Assert.True(row.GetProperty("used").GetDecimal() >= 1.00m);
        Assert.Equal(NextMonth(), row.GetProperty("resetsAt").GetDateTimeOffset());

        // Raising the limit lets calls through again.
        await OkAsync(t["layla"].Client.PutAsJsonAsync($"/api/v1/ai/budgets/{budget.GetProperty("id").GetGuid()}",
            new { amount = 5.00m, action = "block", version = 1 }));
        Assert.True((await ChatAsync(t)).IsOk);
    }

    [Fact]
    public async Task A_used_up_budget_that_downgrades_switches_to_the_cheaper_model()
    {
        var t = await FreshAsync();
        // Summaries normally use the larger model.
        var routes = AiUseCases.All.Where(u => u is not (AiUseCases.Transcribe or AiUseCases.Speak)).ToDictionary(u => u, u => u == AiUseCases.Embed ? new[] { Embedding } : u == AiUseCases.Summarize ? new[] { Large, Small } : new[] { Small, Large });
        var current = await t["layla"].Client.GetAsync("/api/v1/ai/policy");
        using (var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy") { Content = JsonContent.Create(new { allowlist = new[] { Small, Large, Embedding }, routes, confidential = Array.Empty<string>() }) })
        {
            put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
            await OkAsync(t["layla"].Client.SendAsync(put));
        }
        Assert.Equal(Large, (await ChatAsync(t, useCase: AiUseCases.Summarize)).Completion!.Model);

        await CreateAsync(t, new { scope = "tenant", period = "month", amount = 1.00m, action = "downgrade", downgradeModel = Small });
        await SpendAsync(t, 1.00m);
        var cheaper = await ChatAsync(t, useCase: AiUseCases.Summarize);
        Assert.True(cheaper.IsOk, cheaper.Denial);
        Assert.Equal(Small, cheaper.Completion!.Model);
        Assert.Equal(Small, Provider.LastModel);

        // The cheaper model must be one the policy allows.
        await OkAsync(t["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets",
            new { scope = "tenant", period = "day", amount = 1.00m, action = "downgrade", downgradeModel = "unknown/model" }), HttpStatusCode.BadRequest);
        await OkAsync(t["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets",
            new { scope = "tenant", period = "day", amount = 1.00m, action = "alert", downgradeModel = Small }), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Alerts_at_80_and_100_percent_go_once_to_policy_administrators_and_the_owner_and_alert_only_lets_calls_through()
    {
        var t = await FreshAsync();
        var hr = await world.CreateUnitAsync(t, "HR" + Guid.NewGuid().ToString("N")[..4], null, "department");
        await CreateAsync(t, new { scope = "org_unit", scopeId = hr, period = "month", amount = 1.00m, action = "alert", ownerId = t["owner"].Id });

        await SpendAsync(t, 0.85m, unit: hr);
        Assert.True((await ChatAsync(t, unit: hr)).IsOk);
        Assert.Equal(1, await AlertsAsync(t, "layla", 80));
        Assert.Equal(1, await AlertsAsync(t, "owner", 80));
        Assert.Equal(0, await AlertsAsync(t, "layla", 100));
        Assert.Equal(0, await AlertsAsync(t, "emp", 80));

        await SpendAsync(t, 0.20m, unit: hr);
        Assert.True((await ChatAsync(t, unit: hr)).IsOk);
        Assert.True((await ChatAsync(t, unit: hr)).IsOk);
        Assert.Equal(1, await AlertsAsync(t, "layla", 80));
        Assert.Equal(1, await AlertsAsync(t, "layla", 100));
        Assert.Equal(1, await AlertsAsync(t, "owner", 100));
        Assert.Equal(2, await world.Database.OutboxCountAsync("AiBudgetThresholdReached", t.Id));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiBudgetReached);

        // Calls outside the unit are not counted against it.
        Assert.True((await ChatAsync(t)).IsOk);
    }

    [Fact]
    public async Task A_unit_budget_covers_its_child_units_and_an_agent_budget_covers_only_that_agent()
    {
        var t = await FreshAsync();
        var parent = await world.CreateUnitAsync(t, "Gulf" + Guid.NewGuid().ToString("N")[..4], null, "subsidiary");
        var child = await world.CreateUnitAsync(t, "GulfHr" + Guid.NewGuid().ToString("N")[..4], parent, "department");
        await CreateAsync(t, new { scope = "org_unit", scopeId = parent, period = "month", amount = 1.00m, action = "block" });
        await SpendAsync(t, 1.00m, unit: child);
        Assert.Equal(AiDenials.BudgetExhausted, (await ChatAsync(t, unit: child)).Denial);
        Assert.Equal(AiDenials.BudgetExhausted, (await ChatAsync(t, unit: parent)).Denial);
        Assert.True((await ChatAsync(t)).IsOk);

        var agent = Guid.NewGuid();
        await CreateAsync(t, new { scope = "agent", scopeId = agent, period = "day", requests = 1, action = "block" });
        Assert.True((await ChatAsync(t, useCase: AiUseCases.Agent, agent: agent)).IsOk);
        Assert.Equal(AiDenials.QuotaExhausted, (await ChatAsync(t, useCase: AiUseCases.Agent, agent: agent)).Denial);
        Assert.True((await ChatAsync(t, useCase: AiUseCases.Agent, agent: Guid.NewGuid())).IsOk);
    }

    [Fact]
    public async Task A_per_person_daily_limit_denies_with_the_reset_time_and_leaves_other_people_alone()
    {
        var t = await FreshAsync();
        await CreateAsync(t, new { scope = "each_user", period = "day", requests = 2, action = "block", ownerId = t["owner"].Id });
        Assert.True((await ChatAsync(t, "emp")).IsOk);
        Assert.True((await ChatAsync(t, "emp")).IsOk);
        var denied = await ChatAsync(t, "emp");
        Assert.Equal(AiDenials.QuotaExhausted, denied.Denial);
        var today = DateTimeOffset.UtcNow;
        Assert.Equal(new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, TimeSpan.Zero).AddDays(1), denied.ResetsAt);
        Assert.True((await ChatAsync(t, "emp2")).IsOk);

        // A per person limit alerts only its owner, never every administrator.
        Assert.Equal(1, await AlertsAsync(t, "owner", 100));
        Assert.Equal(0, await AlertsAsync(t, "layla", 100));

        // The figure on the list is per person, so none is shown.
        var list = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/budgets"));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("budgets")[0].GetProperty("used").ValueKind);
    }

    [Fact]
    public async Task Concurrent_calls_at_a_request_limit_stay_within_the_limit()
    {
        var t = await FreshAsync();
        await CreateAsync(t, new { scope = "user", scopeId = t["emp"].Id, period = "day", requests = 5, action = "block" });
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => ChatAsync(t, "emp")));
        Assert.Equal(5, results.Count(r => r.IsOk));
        Assert.All(results.Where(r => !r.IsOk), r => Assert.Equal(AiDenials.QuotaExhausted, r.Denial));
        Assert.Equal(5, await world.Database.TenantScalarAsync<int>(t.Id, "select requests from ai.budget_use"));
    }

    [Fact]
    public async Task A_call_refused_after_the_budget_step_gives_its_request_back()
    {
        var t = await FreshAsync();
        await CreateAsync(t, new { scope = "each_user", period = "day", requests = 1, action = "block" });
        // Confidential is blocked by the default policy, after the budget step counted the request.
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(t, classification: DataClassification.Confidential)).Denial);
        Assert.True((await ChatAsync(t)).IsOk);
        Assert.Equal(AiDenials.QuotaExhausted, (await ChatAsync(t)).Denial);
    }

    [Fact]
    public async Task Scoped_budgets_need_their_key_and_only_policy_administrators_change_budgets()
    {
        var t = await FreshAsync("workplace");
        var refused = await t["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets", new { scope = "each_user", period = "day", requests = 10, action = "block" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not_in_package", await refused.Content.ReadAsStringAsync());
        await CreateAsync(t, new { scope = "tenant", period = "month", amount = 100m, action = "alert" });
        var list = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/budgets"));
        Assert.False(list.GetProperty("scopedAllowed").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync("/api/v1/ai/budgets")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.PostAsJsonAsync("/api/v1/ai/budgets",
            new { scope = "tenant", period = "day", amount = 1m, action = "block" })).StatusCode);

        // One budget of a kind per scope and period, and a save over an older version is refused.
        await OkAsync(t["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets", new { scope = "tenant", period = "month", amount = 50m, action = "block" }),
            HttpStatusCode.Conflict);
        var id = list.GetProperty("budgets")[0].GetProperty("id").GetGuid();
        await OkAsync(t["layla"].Client.PutAsJsonAsync($"/api/v1/ai/budgets/{id}", new { amount = 50m, action = "block", version = 7 }), HttpStatusCode.Conflict);
        await OkAsync(t["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets", new { scope = "tenant", period = "month", amount = 5m, requests = 5, action = "block" }),
            HttpStatusCode.BadRequest);
        await OkAsync(t["layla"].Client.DeleteAsync($"/api/v1/ai/budgets/{id}"), HttpStatusCode.NoContent);
        Assert.Equal(0, (await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/budgets"))).GetProperty("budgets").GetArrayLength());
    }

    [Fact]
    public async Task Budgets_of_one_tenant_never_apply_to_or_show_in_another()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        var budget = await CreateAsync(a, new { scope = "tenant", period = "month", amount = 1.00m, action = "block" });
        await SpendAsync(a, 2.00m);
        Assert.Equal(AiDenials.BudgetExhausted, (await ChatAsync(a)).Denial);
        Assert.True((await ChatAsync(b)).IsOk);
        Assert.Equal(0, (await OkAsync(b["layla"].Client.GetAsync("/api/v1/ai/budgets"))).GetProperty("budgets").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await b["layla"].Client.PutAsJsonAsync($"/api/v1/ai/budgets/{budget.GetProperty("id").GetGuid()}",
            new { amount = 50m, action = "block", version = 1 })).StatusCode);
    }
}
