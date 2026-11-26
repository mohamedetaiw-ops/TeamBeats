using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Finance;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 R3 (connector marketplace, section 3): every published pack is listed with the capability it needs, whether
/// the package has it, whether the platform offers it and how many connections the tenant has. The platform operator
/// turns a pack off for all tenants with a reason: it cannot be added and its connections make no calls until it is on
/// again. Only the platform operator turns packs off, and counts stay in the tenant. All names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ConnectorMarketplaceTests(TenantWorld world)
{
    private const string Packs = "/api/v1/integrations/packs";
    private const string Platform = "/api/v1/platform/connector-packs";
    private const string Base = "/api/v1/integrations/pack-connections";

    private static JsonElement Pack(JsonElement list, string kind) => list.EnumerateArray().Single(p => p.GetProperty("kind").GetString() == kind);

    private async Task<Guid> SapAsync(TestTenant t, string host)
    {
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "sap-password"), "fictional-erp-password");
        var created = await admin.PostAsJsonAsync(Base, new
        {
            kind = SapS4FinancePack.KindName,
            displayName = "SAP",
            settings = new
            {
                baseUrl = $"https://{host}",
                auth = new { type = "basic", username = "TB_COMM_USER", secretRef = SecretReferences.For(t.Id, "sap-password") },
                companyCode = "1010",
                defaultAccount = "69900000",
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Base}/{id}:enable", null)).StatusCode);
        return id;
    }

    private Task<T> InScope<T>(TestTenant t, Func<IServiceProvider, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await action(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    [Fact]
    public async Task The_marketplace_lists_each_pack_with_its_capability_package_state_and_the_tenants_connections()
    {
        var t = await world.CreateTenantAsync("mkt-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        var host = $"sap-{Guid.NewGuid():N}"[..20] + ".rest.test";
        await SapAsync(t, host);

        var list = await (await t["admin"].Client.GetAsync(Packs)).ReadAsync<JsonElement>();
        var sap = Pack(list, SapS4FinancePack.KindName);
        Assert.Equal(("integration.sap", true, true, 1), (sap.GetProperty("capabilityKey").GetString(), sap.GetProperty("inPackage").GetBoolean(),
            sap.GetProperty("available").GetBoolean(), sap.GetProperty("connections").GetInt32()));
        Assert.Contains(sap.GetProperty("actions").EnumerateArray(), a => a.GetProperty("name").GetString() == FinancePack.Submit && a.GetProperty("sideEffect").GetString() == "write");
        Assert.Equal(0, Pack(list, OracleFusionFinancePack.KindName).GetProperty("connections").GetInt32());

        // Another tenant sees the same packs, its own package state and none of this tenant's connections.
        var plus = await world.CreateTenantAsync("mkt-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var other = await (await plus["admin"].Client.GetAsync(Packs)).ReadAsync<JsonElement>();
        var otherSap = Pack(other, SapS4FinancePack.KindName);
        Assert.Equal((false, 0), (otherSap.GetProperty("inPackage").GetBoolean(), otherSap.GetProperty("connections").GetInt32()));
        Assert.True(Pack(other, "custom_rest").GetProperty("inPackage").GetBoolean());

        // People without integration.manage do not see the marketplace.
        await world.SeedUserAsync(t, "emp", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync(Packs)).StatusCode);
    }

    [Fact]
    public async Task A_pack_the_platform_turns_off_cannot_be_added_and_its_connections_make_no_calls()
    {
        var t = await world.CreateTenantAsync("mkt-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        var host = $"sap-{Guid.NewGuid():N}"[..20] + ".rest.test";
        var calls = 0;
        world.Factory.Rest.On(host, _ =>
        {
            Interlocked.Increment(ref calls);
            return FakeRestApi.Json(HttpStatusCode.OK, "{}");
        });
        await SapAsync(t, host);

        // Only the platform operator turns packs off, and a reason is needed.
        // A tenant session never reaches the platform routes.
        Assert.False((await t["admin"].Client.PutAsJsonAsync($"{Platform}/{SapS4FinancePack.KindName}", new { available = false, reason = "x" })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Platform.PutAsJsonAsync($"{Platform}/{SapS4FinancePack.KindName}", new { available = false, reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.Platform.PutAsJsonAsync($"{Platform}/no_such_pack", new { available = false, reason = "x" })).StatusCode);

        var off = await world.Platform.PutAsJsonAsync($"{Platform}/{SapS4FinancePack.KindName}", new { available = false, reason = "Supplier invoice API change under review" });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        try
        {
            var platformList = await (await world.Platform.GetAsync(Platform)).ReadAsync<JsonElement>();
            Assert.False(Pack(platformList.GetProperty("items"), SapS4FinancePack.KindName).GetProperty("available").GetBoolean());

            var sap = Pack(await (await t["admin"].Client.GetAsync(Packs)).ReadAsync<JsonElement>(), SapS4FinancePack.KindName);
            Assert.Equal((false, "Supplier invoice API change under review"), (sap.GetProperty("available").GetBoolean(), sap.GetProperty("unavailableReason").GetString()));

            // A second connection cannot be added.
            var again = await t["admin"].Client.PostAsJsonAsync(Base, new { kind = SapS4FinancePack.KindName, displayName = "SAP 2", settings = new { } });
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            Assert.Contains("conflict.integrations.pack_unavailable", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // The existing connection makes no calls: the hand-off job waits, and says why.
            var before = calls;
            var claim = new FinanceHandOffRequest("expense_claim", Guid.NewGuid(), "EMP-1001", "AED", 20m, new DateOnly(2026, 11, 12), null, null, null,
                [new FinanceLine("meals", new DateOnly(2026, 11, 11), 20m)]);
            var view = await InScope(t, async sp =>
            {
                var result = await sp.GetRequiredService<IFinanceHandOff>().SubmitAsync(claim);
                await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50);
                return result;
            });
            Assert.Equal(FinanceHandOffStates.Queued, view.State);
            Assert.Equal(before, calls);
            Assert.Equal(ConnectorErrorClasses.PackUnavailable, await world.Database.TenantScalarAsync<string>(t.Id,
                "select error_class from integrations.run where action = 'submit_expense' order by created_at desc limit 1"));
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, (await world.Platform.PutAsJsonAsync($"{Platform}/{SapS4FinancePack.KindName}", new { available = true })).StatusCode);
        }
        Assert.True(Pack(await (await t["admin"].Client.GetAsync(Packs)).ReadAsync<JsonElement>(), SapS4FinancePack.KindName).GetProperty("available").GetBoolean());
        // Turned on again, the waiting job runs with nothing needed from the tenant.
        var waiting = calls;
        await InScope(t, sp => sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddMinutes(5), 50));
        Assert.True(calls > waiting);
        Assert.True(await world.Database.OwnerScalarAsync<long>(
            "select count(*) from audit.platform_audit_event where action = @a and target_id = @id", ("a", AuditActions.ConnectorPackTurnedOff), ("id", SapS4FinancePack.KindName)) >= 1);
        Assert.True(await world.Database.OwnerScalarAsync<long>(
            "select count(*) from audit.platform_audit_event where action = @a and target_id = @id", ("a", AuditActions.ConnectorPackTurnedOn), ("id", SapS4FinancePack.KindName)) >= 1);
    }
}
