using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Entitlements;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 005 R2 US3 (FR-006, FR-007): atomic quota metering per tenant, capability and month, deny, grace and warn
/// enforcement, exhaustion audited and announced once per period, concurrency within the tolerance, and the tenant boundary.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EntitlementQuotaTests(TenantWorld world)
{
    private const string Key = Capabilities.AiAssistantMonthlyRequests;

    private async Task<TestTenant> TenantAsync(long limit, string? mode = null, int? gracePct = null)
    {
        var t = await world.CreateTenantAsync("eq-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/overrides/{Key}",
            new { enabled = true, limit, validTo = DateTimeOffset.UtcNow.AddYears(1) })).EnsureSuccessStatusCode();
        if (mode is not null)
        {
            (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/enforcement/{Key}", new { mode, gracePct })).EnsureSuccessStatusCode();
        }
        return t;
    }

    private Task<T> InTenantAsync<T>(TestTenant t, Func<IEntitlementService, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await action(sp.GetRequiredService<IEntitlementService>());
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<QuotaDecision> ConsumeAsync(TestTenant t, long amount = 1) => InTenantAsync(t, e => e.ConsumeAsync(Key, amount));

    private Task<long> OutboxAsync(TestTenant t) => world.Database.OwnerScalarAsync<long>(
        "select count(*) from messaging.outbox where tenant_id = @t and message_type = 'QuotaExhausted'", ("t", t.Id))!;

    [Fact]
    public async Task Deny_allows_up_to_the_limit_refuses_the_next_and_announces_it_once()
    {
        var t = await TenantAsync(3);
        for (var i = 1; i <= 3; i++)
        {
            var allowed = await ConsumeAsync(t);
            Assert.True(allowed.Allowed);
            Assert.Equal(i, allowed.Used);
        }
        var refused = await ConsumeAsync(t);
        Assert.False(refused.Allowed);
        Assert.Equal(QuotaDecision.QuotaExhausted, refused.Reason);
        Assert.Equal(3, refused.Used);
        Assert.False((await ConsumeAsync(t)).Allowed);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.QuotaExhausted)));
        Assert.Equal(1L, await OutboxAsync(t));

        // A reservation a later step refused is given back.
        await InTenantAsync(t, async e => { await e.ReleaseAsync(Key); return true; });
        Assert.True((await ConsumeAsync(t)).Allowed);
        Assert.False((await ConsumeAsync(t, 5)).Allowed);
    }

    [Fact]
    public async Task Grace_allows_its_percentage_over_the_limit_with_a_warning_and_warn_never_refuses()
    {
        var grace = await TenantAsync(10, "grace", 10);
        Assert.True((await ConsumeAsync(grace, 10)).Allowed);
        var over = await ConsumeAsync(grace);
        Assert.True(over.Allowed);
        Assert.True(over.OverLimit);
        Assert.Equal(11, over.Used);
        Assert.False((await ConsumeAsync(grace)).Allowed);

        var warn = await TenantAsync(2, "warn");
        Assert.True((await ConsumeAsync(warn, 5)).Allowed);
        Assert.True((await ConsumeAsync(warn)).OverLimit);
        Assert.Equal(1L, await OutboxAsync(warn));
    }

    [Fact]
    public async Task Concurrent_consumers_never_pass_the_limit()
    {
        var t = await TenantAsync(50);
        var results = await Task.WhenAll(Enumerable.Range(0, 80).Select(_ => ConsumeAsync(t)));
        Assert.Equal(50, results.Count(r => r.Allowed));
        Assert.Equal(50L, await world.Database.TenantScalarAsync<long>(t.Id, "select used from entitlements.usage_counter where capability_key = @k", ("k", Key)));
    }

    [Fact]
    public async Task A_module_with_its_own_meter_is_decided_by_its_count_and_the_plan_shows_the_usage()
    {
        var t = await TenantAsync(2);
        Assert.True((await InTenantAsync(t, e => e.CheckUsageAsync(Key, 1))).Allowed);
        var refused = await InTenantAsync(t, e => e.CheckUsageAsync(Key, 2));
        Assert.False(refused.Allowed);
        Assert.Equal(QuotaDecision.QuotaExhausted, refused.Reason);

        var plan = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");
        var usage = plan.GetProperty("usage").EnumerateArray().Single(u => u.GetProperty("capabilityKey").GetString() == Key);
        Assert.Equal(2, usage.GetProperty("used").GetInt64());
        Assert.Equal(2, usage.GetProperty("limit").GetInt64());
        Assert.Equal("deny", usage.GetProperty("mode").GetString());
        Assert.Equal(JsonValueKind.String, usage.GetProperty("exhaustedAt").ValueKind);
    }

    [Fact]
    public async Task The_plan_viewers_are_told_once_per_period()
    {
        var t = await TenantAsync(1);
        await ConsumeAsync(t);
        var message = new QuotaExhausted(t.Id, Key, DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), 1);
        for (var i = 0; i < 2; i++)
        {
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                await sp.GetRequiredService<QuotaExhaustedNotifier>().HandleAsync(message, CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return true;
            });
        }
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t["admin"].Id), ("k", QuotaExhaustedNotifier.Template)));
    }

    [Fact]
    public async Task Usage_and_enforcement_stay_in_their_tenant_and_only_the_platform_sets_enforcement()
    {
        var a = await TenantAsync(1);
        var b = await TenantAsync(1);
        Assert.True((await ConsumeAsync(a)).Allowed);
        Assert.False((await ConsumeAsync(a)).Allowed);
        Assert.True((await ConsumeAsync(b)).Allowed);

        Assert.NotEqual(HttpStatusCode.OK, (await a["admin"].Client.PutAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/enforcement/{Key}", new { mode = "warn" })).StatusCode);
        var invalid = await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/enforcement/{Capabilities.IntegrationSap}", new { mode = "sometimes" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var errors = (await invalid.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("validation.quota.not_a_limit", errors.GetProperty("capabilityKey")[0].GetProperty("key").GetString());
        Assert.Equal("validation.quota.mode_invalid", errors.GetProperty("mode")[0].GetProperty("key").GetString());

        // A change of enforcement is audited to the tenant and the platform (T015); a refused one leaves no entry.
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{a.Id}/enforcement/{Key}", new { mode = "grace", gracePct = 10 })).EnsureSuccessStatusCode();
        const string enforcementAudits = "select count(*) from audit.audit_event where action = @a and changes ? 'enforcement'";
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(a.Id, enforcementAudits, ("a", AuditActions.OverrideChanged)));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(b.Id, enforcementAudits, ("a", AuditActions.OverrideChanged)));
        Assert.Equal(1L, await world.Database.OwnerScalarAsync<long>(
            "select count(*) from audit.platform_audit_event where action = @a and subject_tenant_id = @t and changes ? 'enforcement'",
            ("a", AuditActions.OverrideChanged), ("t", a.Id)));
    }
}
