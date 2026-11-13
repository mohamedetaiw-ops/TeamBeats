using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T018, US6 and FR-028 (slice 5a): stopping an app now. An API administrator revokes every token of an app
/// without disabling it; the platform operator blocks a client ID in every tenant; turning the public API off needs a
/// reason, which the audit keeps; a refresh token used twice tells the person and the API administrators and raises a
/// security alert.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiEmergencyTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("apie-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "gia", "officer" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private Task<HttpResponseMessage> MeAsync(TestTenant t, string token) => SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/me");

    private static async Task<JsonElement> AppAsync(TestTenant t, Guid id) =>
        (await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/apps/{id}"))).GetProperty("app");

    [Fact]
    public async Task Revoking_all_tokens_refuses_them_and_leaves_the_app_enabled()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var (token, appId, _) = await DelegatedAsync(world, t, "gia", ["profile:read"], "delegated_public", null);
        var (otherToken, _, _) = await DelegatedAsync(world, other, "gia", ["profile:read"], "delegated_public", null);
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(t, token)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await t["gia"].Client.PostAsync($"/api/v1/api-access/apps/{appId}:revoke-tokens", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"/api/v1/api-access/apps/{appId}:revoke-tokens", null)).StatusCode);
        var revoked = await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/apps/{appId}:revoke-tokens", null));
        Assert.Equal("active", revoked.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(t, token)).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiTokensRevoked), ("id", appId.ToString())));

        // Another tenant's app is untouched.
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(other, otherToken)).StatusCode);
    }

    [Fact]
    public async Task A_platform_block_stops_a_client_in_every_tenant_until_it_is_lifted()
    {
        var t = await FreshAsync();
        var (token, appId, clientId) = await DelegatedAsync(world, t, "gia", ["profile:read"], "delegated_public", null);

        Assert.NotEqual(HttpStatusCode.OK, (await t["admin"].Client.PostAsJsonAsync("/api/v1/platform/api/blocked-clients", new { clientId, reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await world.Platform.PostAsJsonAsync("/api/v1/platform/api/blocked-clients", new { clientId, reason = "" })).StatusCode);
        await JsonAsync(await world.Platform.PostAsJsonAsync("/api/v1/platform/api/blocked-clients", new { clientId, reason = "Vulnerability in the client" }));

        Assert.Equal(HttpStatusCode.Unauthorized, (await MeAsync(t, token)).StatusCode);
        Assert.Equal("blocked", (await AppAsync(t, appId)).GetProperty("status").GetString());
        // The tenant cannot enable a blocked app.
        await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/apps/{appId}:enable", null));
        Assert.Equal("blocked", (await AppAsync(t, appId)).GetProperty("status").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.platform_audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiClientBlocked), ("id", clientId)));
        var listed = await JsonAsync(await world.Platform.GetAsync("/api/v1/platform/api/blocked-clients"));
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), b => b.GetProperty("clientId").GetString() == clientId);

        var lifted = await world.Platform.DeleteAsync("/api/v1/platform/api/blocked-clients?clientId=" + Uri.EscapeDataString(clientId));
        Assert.Equal(HttpStatusCode.NoContent, lifted.StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.platform_audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiClientUnblocked), ("id", clientId)));
        Assert.Equal("active", (await AppAsync(t, appId)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await MeAsync(t, token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await world.Platform.DeleteAsync("/api/v1/platform/api/blocked-clients?clientId=" + Uri.EscapeDataString(clientId))).StatusCode);
    }

    [Fact]
    public async Task Turning_the_public_api_off_needs_a_reason_that_the_audit_keeps()
    {
        var t = await FreshAsync();
        var missing = await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { apiEnabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("validation.api.switch_reason", await missing.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { apiEnabled = false, reason = "Suspected leaked secret" }));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and changes->>'reason' = 'Suspected leaked secret'", ("a", AuditActions.ApiSettingsChanged)));
        // Turning it back on needs no reason.
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { apiEnabled = true }));
    }

    [Fact]
    public async Task A_token_used_twice_tells_the_person_and_the_administrators_and_raises_an_alert()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var (_, appId, _) = await DelegatedAsync(world, t, "gia", ["profile:read"], "delegated_public", null);
        var family = Guid.NewGuid();
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new TokenFamilyRevoked(family, appId, t["gia"].Id, TokenFamilyReasons.ReuseDetected), CancellationToken.None);
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new TokenFamilyRevoked(Guid.NewGuid(), appId, t["gia"].Id, "admin"), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        while (await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0)
        {
        }

        var alerts = (await JsonAsync(await t["officer"].Client.GetAsync("/api/v1/security/alerts"))).EnumerateArray().ToList();
        var alert = Assert.Single(alerts, a => a.GetProperty("kind").GetString() == "api_token_reuse");
        Assert.Equal(t["gia"].Id, alert.GetProperty("subjectUserId").GetGuid());
        Assert.Equal(appId, alert.GetProperty("facts").GetProperty("appId").GetGuid());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'api.token_reuse.user'", ("u", t["gia"].Id)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'api.token_reuse.admin'", ("u", t["admin"].Id)));
        // Other reasons tell no one, and another tenant sees nothing.
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where template_key = 'api.token_reuse.user'"));
        Assert.DoesNotContain((await JsonAsync(await other["officer"].Client.GetAsync("/api/v1/security/alerts"))).EnumerateArray(),
            a => a.GetProperty("kind").GetString() == "api_token_reuse");
    }
}
