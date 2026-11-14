using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T026 (US4 R2): the CSV export is audited and scoped (named people only for tenant-wide viewers, small groups
/// left out for unit viewers); the forecast projects the month end at the current daily rate against each monthly
/// budget; a display currency changes only the figures shown; and call records older than 25 months are folded into
/// daily rows without the person, keeping every total.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiUsageR2Tests(TenantWorld world)
{
    private sealed record Org(TestTenant T, Guid Gulf, Guid Africa);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("aiu-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SeedUserAsync(t, "gulfadmin", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var gulf = await world.CreateUnitAsync(t, "Gulf", null, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", null, "subsidiary");
        await world.AssignAsync(t, "gulfadmin", BuiltInRoles.AiPolicyAdministrator, "org_subtree", gulf);
        return new Org(t, gulf, africa);
    }

    private static async Task<HttpResponseMessage> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return response;
    }

    private static async Task<JsonElement> JsonAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK) =>
        JsonDocument.Parse(await (await OkAsync(call, expected)).Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Adds served calls as if the gateway had recorded them.</summary>
    private Task<long> SpendAsync(TestTenant t, DateTimeOffset at, decimal cost, Guid? unit, Guid? user, string provider = "openrouter", string classification = "internal",
        int count = 1) =>
        world.Database.TenantScalarAsync<long>(t.Id,
            """
            with added as (insert into ai.usage_record (tenant_id, id, at, unit_id, user_id, use_case, model, provider, input_tokens, output_tokens, cost, decision, classification)
              select platform.current_tenant_id(), gen_random_uuid(), @at, @unit, @user, 'summarize', 'openai/gpt-4o-mini', @provider, 100, 20, @cost, 'served', @level
                from generate_series(1, @count)
              returning 1)
            select count(*) from added
            """, ("at", at), ("unit", (object?)unit ?? DBNull.Value), ("user", (object?)user ?? DBNull.Value), ("provider", provider), ("cost", cost),
            ("level", classification), ("count", count));

    private static async Task<string[]> CsvAsync(TestTenant t, string user, string query = "")
    {
        var response = await OkAsync(t[user].Client.GetAsync($"/api/v1/ai/usage/export{query}"));
        Assert.StartsWith("text/csv", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("ai-usage-", response.Content.Headers.ContentDisposition?.FileName ?? string.Empty);
        return (await response.Content.ReadAsStringAsync()).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task The_export_is_audited_and_names_people_only_for_tenant_wide_viewers()
    {
        var o = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        await SpendAsync(o.T, now, 0.25m, o.Gulf, o.T["emp"].Id);
        await SpendAsync(o.T, now, 0.50m, o.Africa, o.T["layla"].Id);

        var all = await CsvAsync(o.T, "layla");
        Assert.Equal("date,use_case,model,provider,classification,unit_id,agent_id,user_id,requests,input_tokens,output_tokens,cost_usd", all[0]);
        Assert.Equal(3, all.Length);
        Assert.Contains(all, line => line.Contains(o.T["emp"].Id.ToString()) && line.EndsWith(",1,100,20,0.250000", StringComparison.Ordinal));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.AiUsageExported);

        // A unit viewer sees no people column, only their subtree, and no line that covers fewer than five people.
        var gulf = await CsvAsync(o.T, "gulfadmin");
        Assert.DoesNotContain("user_id", gulf[0]);
        Assert.Single(gulf);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["emp"].Client.GetAsync("/api/v1/ai/usage/export")).StatusCode);

        // A period longer than the limit is refused.
        await OkAsync(o.T["layla"].Client.GetAsync("/api/v1/ai/usage/export?from=2020-01-01&to=2026-01-01"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_forecast_projects_the_month_end_against_each_monthly_budget()
    {
        var o = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        await SpendAsync(o.T, now, 3.00m, o.Gulf, o.T["emp"].Id, provider: "openrouter", classification: "internal");
        await SpendAsync(o.T, now, 1.00m, o.Gulf, o.T["emp"].Id, provider: "private", classification: "confidential");
        await OkAsync(o.T["layla"].Client.PostAsJsonAsync("/api/v1/ai/budgets", new { scope = "tenant", period = "month", amount = 1.00m, action = "alert" }),
            HttpStatusCode.Created);

        var forecast = await JsonAsync(o.T["layla"].Client.GetAsync("/api/v1/ai/usage/forecast"));
        var days = DateTime.DaysInMonth(now.Year, now.Month);
        Assert.Equal(4.00m, forecast.GetProperty("monthToDate").GetDecimal());
        Assert.Equal(Math.Round(4.00m / now.Day * days, 2), forecast.GetProperty("projected").GetDecimal());
        Assert.Equal(2, forecast.GetProperty("byProvider").GetArrayLength());
        Assert.Contains(forecast.GetProperty("byClassification").EnumerateArray(), r => r.GetProperty("key").GetString() == "confidential" && r.GetProperty("cost").GetDecimal() == 1.00m);
        var budget = forecast.GetProperty("budgets")[0];
        Assert.Equal(4.00m, budget.GetProperty("used").GetDecimal());
        Assert.True(budget.GetProperty("overBudget").GetBoolean());

        // A unit viewer gets their subtree only, small groups hidden and no budgets.
        var unit = await JsonAsync(o.T["gulfadmin"].Client.GetAsync("/api/v1/ai/usage/forecast"));
        Assert.Equal("units", unit.GetProperty("scope").GetString());
        Assert.Equal(0, unit.GetProperty("budgets").GetArrayLength());
        Assert.Equal(0, unit.GetProperty("byProvider").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["emp"].Client.GetAsync("/api/v1/ai/usage/forecast")).StatusCode);

        // Usage by classification is a group of its own.
        var view = await JsonAsync(o.T["layla"].Client.GetAsync("/api/v1/ai/usage?groupBy=classification"));
        Assert.Equal(2, view.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task A_display_currency_changes_only_the_figures_shown()
    {
        var o = await FreshAsync();
        await SpendAsync(o.T, DateTimeOffset.UtcNow, 2.00m, o.Gulf, o.T["emp"].Id);
        async Task<JsonElement> PolicyAsync(object body, HttpStatusCode expected = HttpStatusCode.OK)
        {
            var current = await o.T["layla"].Client.GetAsync("/api/v1/ai/policy");
            using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy") { Content = JsonContent.Create(body) };
            put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
            return await JsonAsync(o.T["layla"].Client.SendAsync(put), expected);
        }

        var refused = await PolicyAsync(new { displayCurrency = "riyal", displayRate = 3.75m }, HttpStatusCode.BadRequest);
        Assert.Equal("validation.ai.display_currency", refused.GetProperty("errors").GetProperty("displayCurrency")[0].GetProperty("key").GetString());
        await PolicyAsync(new { displayCurrency = "SAR", displayRate = 0m }, HttpStatusCode.BadRequest);
        await PolicyAsync(new { displayCurrency = "sar", displayRate = 3.75m });

        var view = await JsonAsync(o.T["layla"].Client.GetAsync("/api/v1/ai/usage"));
        Assert.Equal("SAR", view.GetProperty("currency").GetString());
        Assert.Equal(3.75m, view.GetProperty("rate").GetDecimal());
        Assert.Equal(2.00m, view.GetProperty("totals").GetProperty("cost").GetDecimal());

        var csv = await CsvAsync(o.T, "layla");
        Assert.EndsWith(",cost_usd,cost_sar", csv[0], StringComparison.Ordinal);
        Assert.EndsWith(",2.000000,7.500000", csv[1], StringComparison.Ordinal);

        // Back to US dollars.
        await PolicyAsync(new { displayCurrency = "" });
        Assert.Equal("USD", (await JsonAsync(o.T["layla"].Client.GetAsync("/api/v1/ai/usage"))).GetProperty("currency").GetString());
    }

    [Fact]
    public async Task Records_older_than_25_months_are_folded_into_daily_rows_without_the_person()
    {
        var o = await FreshAsync();
        var now = DateTimeOffset.UtcNow;
        var old = UsageRetention.Cutoff(now).AddDays(-3);
        await SpendAsync(o.T, old, 0.10m, o.Gulf, o.T["emp"].Id, count: 3);
        await SpendAsync(o.T, old.AddMinutes(5), 0.10m, o.Gulf, o.T["layla"].Id);
        await SpendAsync(o.T, now, 0.10m, o.Gulf, o.T["emp"].Id);
        var from = DateOnly.FromDateTime(old.UtcDateTime);
        var to = from.AddDays(1);
        var before = await JsonAsync(o.T["layla"].Client.GetAsync($"/api/v1/ai/usage?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"));

        var folded = await world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            var count = await sp.GetRequiredService<UsageRetention>().RunBatchAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
            return count;
        });
        Assert.Equal(4, folded);
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from ai.usage_record"));
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from ai.usage_daily where users = 2 and requests = 4"));

        // The view of that period is unchanged.
        var after = await JsonAsync(o.T["layla"].Client.GetAsync($"/api/v1/ai/usage?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}"));
        Assert.Equal(before.GetProperty("totals").GetProperty("requests").GetInt64(), after.GetProperty("totals").GetProperty("requests").GetInt64());
        Assert.Equal(before.GetProperty("totals").GetProperty("cost").GetDecimal(), after.GetProperty("totals").GetProperty("cost").GetDecimal());
        Assert.Equal(2, after.GetProperty("totals").GetProperty("users").GetInt64());

        // Running again finds nothing more, and another tenant's records are never touched.
        Assert.Equal(0, await world.Factory.InScopeAsync(o.T.Id, sp => sp.GetRequiredService<UsageRetention>().RunBatchAsync(now, CancellationToken.None)));
        var other = await FreshAsync();
        await SpendAsync(other.T, old, 0.10m, other.Gulf, other.T["emp"].Id);
        await world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            await sp.GetRequiredService<UsageRetention>().RunBatchAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
            return 0;
        });
        Assert.Equal(1, await world.Database.TenantScalarAsync<long>(other.T.Id, "select count(*) from ai.usage_record"));
    }
}
