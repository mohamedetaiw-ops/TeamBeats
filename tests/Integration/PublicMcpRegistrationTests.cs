using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 FR-023 (slice 4e): dynamic client registration (RFC 7591) for MCP clients. It is off unless the tenant
/// turns it on; a client that registers itself is held as pending and is unknown to the authorization server until an
/// API administrator approves it, and the external AI client policy must still allow it. Only public clients with
/// exact redirect URIs and known scopes register, and registrations are limited per tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicMcpRegistrationTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync(bool on = true)
    {
        var t = await world.CreateTenantAsync("mcpr-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "gia", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        if (on)
        {
            await SwitchAsync(t, true);
        }
        return t;
    }

    private static async Task SwitchAsync(TestTenant t, bool on)
    {
        var current = await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/settings"));
        var saved = await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings",
            new { dynamicRegistration = on, version = current.GetProperty("version").GetInt32() }));
        Assert.Equal(on, saved.GetProperty("dynamicRegistration").GetBoolean());
    }

    private Task<HttpResponseMessage> RegisterAsync(TestTenant t, object body) =>
        world.Factory.TenantClient(t.Key).PostAsJsonAsync("/oauth/register", body);

    private static object Client(string name = "Desk Assistant") =>
        new { client_name = name, redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "none", grant_types = new[] { "authorization_code", "refresh_token" } };

    private async Task<JsonElement> MetadataAsync(TestTenant t) =>
        await JsonAsync(await world.Factory.TenantClient(t.Key).GetAsync("/.well-known/oauth-authorization-server/oauth"));

    private static async Task<string> ErrorAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.BadRequest)
    {
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
    }

    private static async Task<bool> CodeAsync(TestTenant t, string clientId)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var response = await AuthorizeAsync(t, "gia", clientId, ["content:read"], verifier);
        var query = response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.Query.Length > 0
            ? QueryHelpers.ParseQuery(response.Headers.Location.Query) : [];
        return query.ContainsKey("code");
    }

    [Fact]
    public async Task Registration_is_off_unless_the_tenant_turns_it_on()
    {
        var t = await FreshAsync(on: false);
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterAsync(t, Client())).StatusCode);
        Assert.False((await MetadataAsync(t)).TryGetProperty("registration_endpoint", out _));
        Assert.False((await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/settings"))).GetProperty("dynamicRegistration").GetBoolean());

        await SwitchAsync(t, true);
        Assert.EndsWith("/oauth/register", (await MetadataAsync(t)).GetProperty("registration_endpoint").GetString(), StringComparison.Ordinal);

        // Another tenant's switch stays off, and only an API administrator changes it.
        var other = await FreshAsync(on: false);
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterAsync(other, Client())).StatusCode);
        var denied = await other["gia"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { dynamicRegistration = true });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task A_registered_client_waits_for_approval_and_the_ai_policy()
    {
        var t = await FreshAsync();
        var response = await RegisterAsync(t, Client());
        var registered = await JsonAsync(response, HttpStatusCode.Created);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var clientId = registered.GetProperty("client_id").GetString()!;
        Assert.Equal("none", registered.GetProperty("token_endpoint_auth_method").GetString());
        var scopes = registered.GetProperty("scope").GetString()!.Split(' ');
        Assert.Contains("content:read", scopes);
        Assert.DoesNotContain("content:draft", scopes);

        var app = (await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/apps"))).EnumerateArray()
            .Single(a => a.GetProperty("clientId").GetString() == clientId);
        Assert.Equal("pending", app.GetProperty("status").GetString());
        Assert.True(app.GetProperty("selfRegistered").GetBoolean());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiAppRegistered), ("id", app.GetProperty("id").GetString()!)));

        // Pending: unknown to the authorization server, even with the AI policy allowing it.
        await AllowExternalAsync(t, "aip", (clientId, "internal"));
        await JsonAsync(await t["gia"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes = new[] { "content:read" } }),
            HttpStatusCode.NotFound);
        Assert.False(await CodeAsync(t, clientId));

        // Approved: the employee consents and connects it.
        var id = app.GetProperty("id").GetString();
        Assert.Equal("active", (await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/apps/{id}:enable", null))).GetProperty("status").GetString());
        await JsonAsync(await t["gia"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes = new[] { "content:read" } }));
        Assert.True(await CodeAsync(t, clientId));

        // Without the AI policy it is refused again.
        await AllowExternalAsync(t, "aip");
        Assert.False(await CodeAsync(t, clientId));
    }

    [Fact]
    public async Task An_administrator_can_refuse_a_pending_client()
    {
        var t = await FreshAsync();
        var clientId = (await JsonAsync(await RegisterAsync(t, Client()), HttpStatusCode.Created)).GetProperty("client_id").GetString()!;
        var app = (await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/apps"))).EnumerateArray()
            .Single(a => a.GetProperty("clientId").GetString() == clientId);
        var refused = await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/apps/{app.GetProperty("id").GetString()}:disable", null));
        Assert.Equal("disabled", refused.GetProperty("status").GetString());
        await AllowExternalAsync(t, "aip", (clientId, "internal"));
        Assert.False(await CodeAsync(t, clientId));
    }

    [Fact]
    public async Task Only_public_clients_with_exact_redirects_and_known_scopes_register()
    {
        var t = await FreshAsync();
        Assert.Equal("invalid_client_metadata", await ErrorAsync(await RegisterAsync(t,
            new { client_name = "X", redirect_uris = new[] { Redirect }, token_endpoint_auth_method = "client_secret_basic" })));
        Assert.Equal("invalid_client_metadata", await ErrorAsync(await RegisterAsync(t,
            new { client_name = "X", redirect_uris = new[] { Redirect }, grant_types = new[] { "client_credentials" } })));
        Assert.Equal("invalid_redirect_uri", await ErrorAsync(await RegisterAsync(t, new { client_name = "X", redirect_uris = new[] { "https://a.example.test/*" } })));
        Assert.Equal("invalid_redirect_uri", await ErrorAsync(await RegisterAsync(t, new { client_name = "X", redirect_uris = Array.Empty<string>() })));
        var unknown = await RegisterAsync(t, new { client_name = "X", redirect_uris = new[] { Redirect }, scope = "content:read admin:everything" });
        Assert.Equal("invalid_client_metadata", await ErrorAsync(unknown));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from publicapi.app"));

        // Five refusals used this minute's registrations; a body that is not JSON is checked on a fresh tenant.
        var fresh = await FreshAsync();
        var empty = await world.Factory.TenantClient(fresh.Key).PostAsync("/oauth/register", new StringContent("not json", Encoding.UTF8, "application/json"));
        Assert.Equal("invalid_client_metadata", await ErrorAsync(empty));
    }

    [Fact]
    public async Task Registrations_are_limited_per_tenant_and_by_the_app_limit()
    {
        var t = await FreshAsync();
        for (var i = 0; i < 5; i++)
        {
            await JsonAsync(await RegisterAsync(t, Client("Client " + i)), HttpStatusCode.Created);
        }
        var limited = await RegisterAsync(t, Client("One more"));
        Assert.Equal("temporarily_unavailable", await ErrorAsync(limited, HttpStatusCode.TooManyRequests));
        Assert.NotNull(limited.Headers.RetryAfter);

        var small = await FreshAsync();
        await world.SetCapabilityAsync(small, Capabilities.ApiMaxApps, new { enabled = true, limit = 1 });
        await JsonAsync(await RegisterAsync(small, Client()), HttpStatusCode.Created);
        Assert.Equal("invalid_client_metadata", await ErrorAsync(await RegisterAsync(small, Client("Second"))));
    }
}
