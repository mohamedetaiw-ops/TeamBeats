using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 026 slice 6 (T030): hosting region at tenant creation (FR-008) and break-glass in the tenant's audit view (FR-020).</summary>
[Collection(WorldCollection.Name)]
public sealed class ResidencyBreakGlassTests(TenantWorld world)
{
    private static object Body(string key, string package, string? region) => new
    {
        key,
        displayName = $"{key} holding",
        packageKey = package,
        defaultLocale = "en",
        enabledLocales = new[] { "en", "ar" },
        timeZone = "Asia/Dubai",
        admins = new[] { $"admin@{key}.example" },
        region,
    };

    private static async Task<string?> ErrorKeyAsync(HttpResponseMessage response, string field)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors").GetProperty(field)[0].GetProperty("key").GetString();
    }

    private static async Task<JsonElement> PostureAsync(HttpClient client) =>
        await (await client.GetAsync("/api/v1/security/posture")).ReadAsync<JsonElement>();

    private static string CheckState(JsonElement posture, string key) =>
        posture.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key).GetProperty("state").GetString()!;

    [Fact]
    public async Task Region_is_chosen_at_creation_within_the_offered_regions_and_the_package()
    {
        var regions = await (await world.Platform.GetAsync("/api/v1/platform/regions")).ReadAsync<JsonElement>();
        Assert.Equal(["uae", "ksa"], regions.GetProperty("offered").EnumerateArray().Select(r => r.GetString()!).ToArray());
        Assert.Equal("uae", regions.GetProperty("default").GetString());

        // Unknown region: refused before anything is written.
        var unknown = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", Body("region-mars", "workplace_plus", "mars"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("validation.tenant.region_unknown", await ErrorKeyAsync(unknown, "region"));

        // A non-default region on a package without deployment.region_choice: the whole creation rolls back.
        var refused = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", Body("region-basic", "workplace", "ksa"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("validation.tenant.region_not_in_package", await ErrorKeyAsync(refused, "region"));
        var list = await world.Platform.GetFromJsonAsync<JsonElement>("/api/v1/platform/tenants?q=region-basic");
        Assert.Equal(0, list.GetArrayLength());

        // The default region needs no capability; the region shows on the posture page.
        var basic = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", Body("region-default", "workplace", null));
        Assert.Equal(HttpStatusCode.Created, basic.StatusCode);
        var basicId = (await basic.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var basicTenant = new TestTenant("region-default", basicId);
        var basicAdmin = await world.SeedUserAsync(basicTenant, "admin", "employee");
        Assert.Equal("uae", (await PostureAsync(basicAdmin.Client)).GetProperty("region").GetString());

        var chosen = await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", Body("region-ksa", "workplace_plus", "KSA"));
        Assert.Equal(HttpStatusCode.Created, chosen.StatusCode);
        var id = (await chosen.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal("ksa", await world.Database.ScalarInTenantAsync<string>(id, "select region from tenancy.tenant_settings"));
        var admin = await world.SeedUserAsync(new TestTenant("region-ksa", id), "admin", "employee");
        Assert.Equal("ksa", (await PostureAsync(admin.Client)).GetProperty("region").GetString());

        // Regions are platform endpoints: a tenant host does not have them.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.Client.GetAsync("/api/v1/platform/regions")).StatusCode);
    }

    [Fact]
    public async Task Break_glass_is_audited_to_the_tenant_alerts_it_and_recovers_a_network_lockout()
    {
        var tenant = await world.CreateTenantAsync("glass-co", "workplace_plus");
        var admin = tenant.Users["admin"].Client;
        var officer = await world.SeedUserAsync(tenant, "officer", "employee");
        await world.AssignAsync(tenant, "officer", "security_officer", "tenant", null);
        var path = $"/api/v1/platform/tenants/{tenant.Id}/break-glass";
        Assert.Equal("passed", CheckState(await PostureAsync(admin), "no_platform_access"));

        // Validation: a reason the tenant can read, and a bounded length.
        var vague = await world.Platform.PostAsJsonAsync(path, new { reason = "fix", minutes = 60 });
        Assert.Equal(HttpStatusCode.BadRequest, vague.StatusCode);
        Assert.Equal("validation.break_glass.reason", await ErrorKeyAsync(vague, "reason"));
        var tooLong = await world.Platform.PostAsJsonAsync(path, new { reason = "Customer ticket 4411: admins locked out", minutes = 600 });
        Assert.Equal("validation.break_glass.minutes", await ErrorKeyAsync(tooLong, "minutes"));
        Assert.Equal(HttpStatusCode.NotFound, (await world.Platform.PostAsJsonAsync($"/api/v1/platform/tenants/{Guid.NewGuid()}/break-glass",
            new { reason = "Customer ticket 4411: admins locked out", minutes = 30 })).StatusCode);

        // The tenant locks every administrator out with an allow list that excludes them.
        await world.Database.OwnerExecInTenantAsync(tenant.Id,
            "insert into security.policy (tenant_id, ip_allow, admin_ip_allow) values (@t, '{203.0.113.0/24}', '{203.0.113.0/24}') " +
            "on conflict (tenant_id) do update set ip_allow = excluded.ip_allow, admin_ip_allow = excluded.admin_ip_allow", ("t", tenant.Id));
        await world.Factory.InScopeAsync(tenant.Id, sp =>
        {
            sp.GetRequiredService<TeamBeats.Modules.Security.PolicyStore>().Forget(tenant.Id); // The rules are cached for 30 seconds.
            return Task.FromResult(true);
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/v1/security/posture")).StatusCode);

        var opened = await world.Platform.PostAsJsonAsync(path, new { reason = "Customer ticket 4411: admins locked out", minutes = 30 });
        Assert.Equal(HttpStatusCode.Created, opened.StatusCode);
        var session = (await opened.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var again = await world.Platform.PostAsJsonAsync(path, new { reason = "Customer ticket 4411: admins locked out", minutes = 30 });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // Opening is in the tenant's stream and the platform stream, and raises an alert for the tenant.
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.BreakGlassOpened, session.ToString());
        Assert.Equal(1, await world.Database.PlatformAuditCountAsync(AuditActions.BreakGlassOpened, tenant.Id));
        Assert.Equal(1, await world.Database.CountInTenantAsync(tenant.Id, "select count(*) from security.alert where kind = 'platform_access'"));

        // Reset clears the allow lists, keeps the before values in the audit entry, and lets the administrators back in.
        var reset = await world.Platform.PostAsync($"{path}/{session}/network-rules:reset", null);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(tenant.Id, "select count(*) from security.policy where cardinality(ip_allow) > 0 or cardinality(admin_ip_allow) > 0"));
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.BreakGlassNetworkReset, session.ToString());
        Assert.Equal(1, await world.Database.PlatformAuditCountAsync(AuditActions.BreakGlassNetworkReset, tenant.Id));
        var posture = await PostureAsync(admin);
        Assert.Equal("failed", CheckState(posture, "no_platform_access"));

        // The security officer sees the entries in the audit view.
        var events = await (await officer.Client.GetAsync($"/api/v1/audit/events?action={AuditActions.BreakGlassOpened}")).ReadAsync<JsonElement>();
        Assert.Equal(1, events.GetProperty("items").GetArrayLength());

        // Another tenant: the session does not exist there (row-level security), and a tenant host has no platform routes.
        var other = await world.CreateTenantAsync("glass-other", "workplace_plus");
        var cross = await world.Platform.PostAsync($"/api/v1/platform/tenants/{other.Id}/break-glass/{session}/network-rules:reset", null);
        Assert.Equal(HttpStatusCode.Conflict, cross.StatusCode);
        Assert.Equal(0, await world.Database.AuditCountAsync(other.Id, AuditActions.BreakGlassNetworkReset));
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync($"{path}/{session}:end", null)).StatusCode);

        // Ending closes the session; it cannot be used or ended twice.
        Assert.Equal(HttpStatusCode.NoContent, (await world.Platform.PostAsync($"{path}/{session}:end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await world.Platform.PostAsync($"{path}/{session}:end", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await world.Platform.PostAsync($"{path}/{session}/network-rules:reset", null)).StatusCode);
        await world.Database.AssertAuditedAsync(tenant.Id, AuditActions.BreakGlassEnded, session.ToString());
        Assert.Equal("passed", CheckState(await PostureAsync(admin), "no_platform_access"));
        var sessions = await (await world.Platform.GetAsync(path)).ReadAsync<JsonElement>();
        Assert.False(sessions[0].GetProperty("open").GetBoolean());
        Assert.Equal(1, sessions[0].GetProperty("actions").GetInt32());
    }
}
