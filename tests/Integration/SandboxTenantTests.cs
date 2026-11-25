using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 R3 (T024): developer sandbox tenants. A platform operator creates one for 1 to 30 days; it has lower API
/// limits until it ends, its developer page says when, it is suspended when the date passes and closed 14 days later,
/// and other tenants are never touched. Only platform operators create one.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SandboxTenantTests(TenantWorld world)
{
    private async Task<(TestTenant T, JsonElement View)> SandboxAsync(int days)
    {
        var key = "sbx-" + Guid.NewGuid().ToString("N")[..8];
        var response = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            key, displayName = "Partner sandbox", packageKey = "workplace_plus", defaultLocale = "en", enabledLocales = new[] { "en", "ar" },
            timeZone = "Asia/Dubai", admins = new[] { $"admin@{key}.example" }, sandboxDays = days,
        });
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        var view = JsonDocument.Parse(text).RootElement.Clone();
        var t = new TestTenant(key, view.GetProperty("id").GetGuid());
        await world.SeedUserAsync(t, "admin", MembershipTypes.Employee);
        return (t, view);
    }

    private Task<string?> StatusAsync(TestTenant t) => world.Database.OwnerScalarAsync<string>("select status from tenancy.tenant where id = @id", ("id", t.Id));

    private Task<int> RunAsync() => SandboxWorker.RunOnceAsync(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance, CancellationToken.None);

    [Fact]
    public async Task A_sandbox_has_lower_limits_says_when_it_ends_and_ends_on_its_own()
    {
        var (t, view) = await SandboxAsync(7);
        var until = view.GetProperty("sandboxUntil").GetDateTimeOffset();
        Assert.InRange(until, DateTimeOffset.UtcNow.AddDays(6.9), DateTimeOffset.UtcNow.AddDays(7.1));
        var listed = await (await world.Platform.GetAsync($"/api/v1/platform/tenants?q={t.Key}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(until, listed.EnumerateArray().Single().GetProperty("sandboxUntil").GetDateTimeOffset());

        var limits = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var entitlements = sp.GetRequiredService<IEntitlementService>();
            return (Calls: await entitlements.GetLimitAsync(Capabilities.ApiMonthlyCalls), Apps: await entitlements.GetLimitAsync(Capabilities.ApiMaxApps));
        });
        Assert.Equal(SandboxLimits.MonthlyCalls, limits.Calls);
        Assert.Equal(SandboxLimits.MaxApps, limits.Apps);

        var page = await (await t["admin"].Client.GetAsync("/api/v1/api-access/developer")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(until, page.GetProperty("sandboxUntil").GetDateTimeOffset());

        // A live tenant has no end date and the expiry pass leaves it alone.
        var live = await world.CreateTenantAsync("live-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var livePage = await (await live["admin"].Client.GetAsync("/api/v1/api-access/developer")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, livePage.GetProperty("sandboxUntil").ValueKind);

        // Before its date nothing happens; after it the sandbox is suspended, and 14 days later closed.
        await RunAsync();
        Assert.Equal("active", await StatusAsync(t));
        await world.Database.OwnerExecAsync("update tenancy.tenant set sandbox_until = now() - interval '1 hour' where id = @id", ("id", t.Id));
        await world.Database.OwnerExecAsync("update tenancy.tenant set sandbox_until = null where id = @id", ("id", live.Id));
        Assert.True(await RunAsync() >= 1);
        Assert.Equal("suspended", await StatusAsync(t));
        Assert.Equal("active", await StatusAsync(live));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = 'tenant.status_changed' and changes->>'reason' = 'sandbox_expired'"));
        Assert.NotEqual(HttpStatusCode.OK, (await t["admin"].Client.GetAsync("/api/v1/api-access/developer")).StatusCode);

        await world.Database.OwnerExecAsync("update tenancy.tenant set sandbox_until = now() - interval '15 days' where id = @id", ("id", t.Id));
        await RunAsync();
        Assert.Equal("closed", await StatusAsync(t));
        Assert.NotNull(await world.Database.OwnerScalarAsync<DateTime?>("select deletion_due_at from tenancy.tenant where id = @id", ("id", t.Id)));
        await RunAsync();
        Assert.Equal("closed", await StatusAsync(t));
    }

    [Fact]
    public async Task A_sandbox_lasts_1_to_30_days_and_only_platform_operators_make_one()
    {
        foreach (var days in new[] { 0, 31 })
        {
            var key = "sbx-" + Guid.NewGuid().ToString("N")[..8];
            var refused = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", new
            {
                key, displayName = "Partner sandbox", packageKey = "workplace_plus", defaultLocale = "en", admins = new[] { $"admin@{key}.example" }, sandboxDays = days,
            });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("validation.tenant.sandbox_days", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        var (t, _) = await SandboxAsync(30);
        var byTenantAdmin = await t["admin"].Client.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            key = "sbx-" + Guid.NewGuid().ToString("N")[..8], displayName = "Mine", packageKey = "workplace_plus", defaultLocale = "en", admins = new[] { "x@y.example" }, sandboxDays = 7,
        });
        Assert.Contains(byTenantAdmin.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });
    }
}
