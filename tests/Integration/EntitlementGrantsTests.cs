using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 005 R2 US4 (FR-004, FR-008): add-ons and contract overrides set by the platform, overrides over add-ons over the
/// package, each only inside its validity window, audited to the tenant and the platform, and never reachable by a tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EntitlementGrantsTests(TenantWorld world)
{
    private static string Grants(TestTenant t, string kind) => $"/api/v1/platform/tenants/{t.Id}/{kind}";

    private async Task<JsonElement> PlanAsync(TestTenant t) => await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements");

    private static JsonElement Capability(JsonElement plan, string key) => plan.GetProperty("capabilities").GetProperty(key);

    private async Task<TestTenant> TenantAsync() => await world.CreateTenantAsync("eg-" + Guid.NewGuid().ToString("N")[..8], "workplace");

    [Fact]
    public async Task An_addon_enables_a_capability_for_its_tenant_only_and_is_audited()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        Assert.False(Capability(await PlanAsync(t), Capabilities.IntegrationSap).GetProperty("enabled").GetBoolean());

        var put = await world.Platform.PutAsJsonAsync($"{Grants(t, "addons")}/{Capabilities.IntegrationSap}", new { enabled = true, reason = "SAP add-on order 42" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var plan = await PlanAsync(t);
        Assert.True(Capability(plan, Capabilities.IntegrationSap).GetProperty("enabled").GetBoolean());
        Assert.Equal("addon", plan.GetProperty("sources").GetProperty(Capabilities.IntegrationSap).GetString());
        Assert.Equal("package", plan.GetProperty("sources").GetProperty(Capabilities.AiAssistant).GetString());
        Assert.Contains(plan.GetProperty("addons").EnumerateArray(), a => a.GetProperty("capabilityKey").GetString() == Capabilities.IntegrationSap);
        Assert.False(Capability(await PlanAsync(other), Capabilities.IntegrationSap).GetProperty("enabled").GetBoolean());

        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.AddonChanged)));
        Assert.True(await world.Database.OwnerScalarAsync<long>("select count(*) from audit.platform_audit_event where action = @a and subject_tenant_id = @t",
            ("a", AuditActions.AddonChanged), ("t", t.Id)) == 1);

        Assert.Equal(HttpStatusCode.NoContent, (await world.Platform.DeleteAsync($"{Grants(t, "addons")}/{Capabilities.IntegrationSap}")).StatusCode);
        Assert.False(Capability(await PlanAsync(t), Capabilities.IntegrationSap).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await world.Platform.DeleteAsync($"{Grants(t, "addons")}/{Capabilities.IntegrationSap}")).StatusCode);
    }

    [Fact]
    public async Task An_override_wins_over_an_addon_until_its_end_date_and_then_reverts()
    {
        var t = await TenantAsync();
        var key = Capabilities.AiAssistantMonthlyRequests;
        (await world.Platform.PutAsJsonAsync($"{Grants(t, "addons")}/{key}", new { enabled = true, limit = 20000 })).EnsureSuccessStatusCode();
        Assert.Equal(20000, Capability(await PlanAsync(t), key).GetProperty("limit").GetInt64());

        var until = DateTimeOffset.UtcNow.AddMonths(6);
        (await world.Platform.PutAsJsonAsync($"{Grants(t, "overrides")}/{key}", new { enabled = true, limit = 50000, validTo = until })).EnsureSuccessStatusCode();
        var plan = await PlanAsync(t);
        Assert.Equal(50000, Capability(plan, key).GetProperty("limit").GetInt64());
        Assert.Equal("override", plan.GetProperty("sources").GetProperty(key).GetString());

        // The contract ended: the add-on applies again with no job.
        (await world.Platform.PutAsJsonAsync($"{Grants(t, "overrides")}/{key}", new
        {
            enabled = true, limit = 50000, validFrom = DateTimeOffset.UtcNow.AddMonths(-6), validTo = DateTimeOffset.UtcNow.AddMinutes(-1),
        })).EnsureSuccessStatusCode();
        plan = await PlanAsync(t);
        Assert.Equal(20000, Capability(plan, key).GetProperty("limit").GetInt64());
        Assert.False(plan.GetProperty("overrides").EnumerateArray().Single().GetProperty("active").GetBoolean());
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.OverrideChanged)));
    }

    [Fact]
    public async Task Grants_are_validated()
    {
        var t = await TenantAsync();
        async Task Invalid(string kind, string key, object body, string field, string error)
        {
            var response = await world.Platform.PutAsJsonAsync($"{Grants(t, kind)}/{key}", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            Assert.Contains(error, errors.GetProperty(field).EnumerateArray().Select(e => e.GetProperty("key").GetString()));
        }
        await Invalid("overrides", Capabilities.IntegrationSap, new { enabled = true }, "validTo", "validation.grant.end_required");
        await Invalid("addons", Capabilities.IntegrationSap, new { enabled = true, limit = 5 }, "limit", "validation.grant.limit_invalid");
        await Invalid("addons", Capabilities.AiAssistantMonthlyRequests, new { enabled = true, limit = -1 }, "limit", "validation.grant.limit_invalid");
        await Invalid("addons", Capabilities.IntegrationSap, new { enabled = true, validFrom = DateTimeOffset.UtcNow, validTo = DateTimeOffset.UtcNow.AddDays(-1) },
            "validTo", "validation.grant.end_before_start");
        await Invalid("addons", "no.such.capability", new { enabled = true }, "capabilityKey", "validation.package.unknown_capability");
        Assert.Equal(HttpStatusCode.NotFound, (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/addons/{Capabilities.IntegrationSap}",
            new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task A_tenant_administrator_cannot_reach_any_grant_route()
    {
        var t = await TenantAsync();
        var admin = t["admin"].Client;
        foreach (var kind in new[] { "addons", "overrides" })
        {
            Assert.NotEqual(HttpStatusCode.OK, (await admin.GetAsync(Grants(t, kind))).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Grants(t, kind)}/{Capabilities.IntegrationSap}",
                new { enabled = true, validTo = DateTimeOffset.UtcNow.AddDays(30) })).StatusCode);
        }
        Assert.False(Capability(await PlanAsync(t), Capabilities.IntegrationSap).GetProperty("enabled").GetBoolean());
    }
}
