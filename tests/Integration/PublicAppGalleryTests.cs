using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 R3 (T025): the public app gallery. The platform operator publishes a reviewed app; a tenant's API
/// administrator adds it, which registers it with the gallery's name, publisher, scopes and redirect URIs and gives the
/// admin consent for its scopes, so an employee connects it at once. Withdrawing it disables it in every tenant. Adding
/// needs the API manage right and a package that covers its scopes; other tenants are untouched; only platform operators
/// publish.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicAppGalleryTests(TenantWorld world)
{
    private static string NewClientId() => $"https://apps.example.test/{Guid.NewGuid():N}/client.json";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("gal-" + Guid.NewGuid().ToString("N")[..6], package, "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "gia", MembershipTypes.Employee);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private Task<HttpResponseMessage> PublishAsync(string clientId, string[] scopes, string status = "published", HttpClient? as_ = null) =>
        (as_ ?? world.Platform).PutAsJsonAsync("/api/v1/platform/api/gallery", new
        {
            clientId, name = "Board Notes", publisher = "Notes Labs", description = "Meeting notes linked to your intranet news.",
            homepage = "https://notes.example.test", redirectUris = new[] { Redirect }, scopes, status,
        });

    private static async Task<JsonElement> EntryAsync(TestTenant t, string clientId) =>
        (await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/gallery"))).GetProperty("items").EnumerateArray()
            .Single(e => e.GetProperty("clientId").GetString() == clientId);

    private static Task<HttpResponseMessage> AddAsync(TestTenant t, string clientId, string user = "admin") =>
        t[user].Client.PostAsJsonAsync("/api/v1/api-access/gallery:add", new { clientId });

    private static string Verifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<HttpResponseMessage> ConnectAsync(TestTenant t, string clientId, string[] scopes, string verifier) =>
        await AuthorizeAsync(t, "gia", clientId, scopes, verifier);

    [Fact]
    public async Task An_administrator_adds_a_published_app_and_employees_connect_it_without_asking_again()
    {
        var clientId = NewClientId();
        string[] scopes = ["profile:read", "content:read", "content:draft"];
        await JsonAsync(await PublishAsync(clientId, scopes));
        Assert.True(await world.Database.OwnerScalarAsync<long>(
            "select count(*) from audit.platform_audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiGalleryAppChanged), ("id", clientId)) >= 1);
        var t = await FreshAsync();

        var entry = await EntryAsync(t, clientId);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("appId").ValueKind);
        Assert.True(entry.GetProperty("inPackage").GetBoolean());
        Assert.Equal("Notes Labs", entry.GetProperty("publisher").GetString());
        // Until it is added the tenant does not know the client.
        var before = await ConnectAsync(t, clientId, scopes, Verifier());
        Assert.False(before.StatusCode == HttpStatusCode.Redirect && before.Headers.Location!.Query.Contains("code=", StringComparison.Ordinal));
        // An employee can neither see nor add gallery apps.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gia"].Client.GetAsync("/api/v1/api-access/gallery")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AddAsync(t, clientId, "gia")).StatusCode);

        var detail = await JsonAsync(await AddAsync(t, clientId));
        var app = detail.GetProperty("app");
        Assert.True(app.GetProperty("gallery").GetBoolean());
        Assert.Equal("delegated_public", app.GetProperty("kind").GetString());
        Assert.Equal("Board Notes", app.GetProperty("name").GetString());
        Assert.Equal(scopes.Order(StringComparer.Ordinal), detail.GetProperty("adminConsentScopes").EnumerateArray().Select(s => s.GetString()!).Order(StringComparer.Ordinal));
        var appId = app.GetProperty("id").GetGuid();
        Assert.Equal(appId, (await EntryAsync(t, clientId)).GetProperty("appId").GetGuid());
        // Adding again returns the same registration.
        Assert.Equal(appId, (await JsonAsync(await AddAsync(t, clientId))).GetProperty("app").GetProperty("id").GetGuid());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.ApiGalleryAppAdded)));

        // The gallery owns what the app is; the tenant cannot rename it.
        var renamed = await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{appId}", new { name = "Mine now" });
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.Contains("validation.api.verified_app_fixed", await renamed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The consent screen shows a verified publisher, and the employee connects without an administrator.
        var prompt = await JsonAsync(await t["gia"].Client.GetAsync(QueryHelpers.AddQueryString("/api/v1/me/app-consent", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = Redirect, ["scope"] = string.Join(' ', scopes),
        })));
        Assert.True(prompt.GetProperty("verified").GetBoolean());
        var verifier = Verifier();
        var authorize = await ConnectAsync(t, clientId, scopes, verifier);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, await authorize.Content.ReadAsStringAsync());
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), authorize.Headers.Location!.ToString());
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        })));
        var token = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/me")).StatusCode);

        // Another tenant sees the entry but has not added it, and its server does not take this tenant's token.
        var other = await FreshAsync();
        Assert.Equal(JsonValueKind.Null, (await EntryAsync(other, clientId)).GetProperty("appId").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(world, other, token, HttpMethod.Get, "/api/public/v1/me")).StatusCode);

        // Withdrawing the app disables it at once: it leaves the gallery, its registration shows as blocked and its tokens stop.
        await JsonAsync(await PublishAsync(clientId, scopes, "withdrawn"));
        Assert.DoesNotContain((await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/gallery"))).GetProperty("items").EnumerateArray(),
            e => e.GetProperty("clientId").GetString() == clientId);
        Assert.Equal("blocked", (await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/apps/{appId}"))).GetProperty("app").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(other, clientId)).StatusCode);
    }

    [Fact]
    public async Task Only_platform_operators_publish_and_an_app_needs_scopes_the_package_covers()
    {
        var t = await FreshAsync();
        // A tenant administrator cannot publish.
        var byTenant = await PublishAsync(NewClientId(), ["profile:read"], as_: t["admin"].Client);
        Assert.Contains(byTenant.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.Unauthorized });

        // Entries are checked: a verified AI client cannot also be a gallery app, nor a plain address or an unknown status.
        var listed = NewClientId();
        await JsonAsync(await world.Platform.PutAsJsonAsync("/api/v1/platform/api/verified-mcp-clients", new
        {
            clientId = listed, name = "Desk Assistant", publisher = "Assistant Labs", redirectUris = new[] { Redirect }, scopes = new[] { "profile:read" },
        }));
        var clash = await PublishAsync(listed, ["profile:read"]);
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);
        Assert.Contains("validation.api.gallery_client_listed", await clash.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync("notes-app", ["profile:read"])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PublishAsync(NewClientId(), ["profile:read"], "hidden")).StatusCode);

        // An app whose scope the package does not include is shown as such and cannot be added.
        var admin = NewClientId();
        await JsonAsync(await PublishAsync(admin, ["profile:read", "roles:read"]));
        Assert.False((await EntryAsync(t, admin)).GetProperty("inPackage").GetBoolean());
        var refused = await AddAsync(t, admin);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.api.scope_not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // An address not in the gallery is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await AddAsync(t, NewClientId())).StatusCode);
    }
}
