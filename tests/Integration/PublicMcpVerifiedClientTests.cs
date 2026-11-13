using System.Net;
using System.Net.Http.Headers;
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
/// Spec 035 FR-023, US3 scenario 1 (slice 4d): MCP clients from the platform's verified list. The platform operator
/// records a reviewed client ID metadata document; a tenant's AI policy administrator allows the client; the first
/// employee who connects it registers it for the tenant, with the name, publisher, scopes and redirect URIs of the list.
/// A client the policy does not allow, or a URL not on the list, gets nothing; blocking it on the platform disables it
/// for every tenant at once, tokens included.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicMcpVerifiedClientTests(TenantWorld world)
{
    private static string NewClientId() => $"https://ai.example.test/{Guid.NewGuid():N}/client.json";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("mcpv-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "gia", "aip" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private Task<HttpResponseMessage> ListAsync(string clientId, string status = "active") =>
        world.Platform.PutAsJsonAsync("/api/v1/platform/api/verified-mcp-clients", new
        {
            clientId, name = "Desk Assistant", publisher = "Assistant Labs", redirectUris = new[] { Redirect }, scopes = new[] { "profile:read", "content:read" },
            status,
        });

    private static string Verifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Task<HttpResponseMessage> AuthorizeAtAsync(TestTenant t, string user, string clientId, string redirect, string verifier)
    {
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return t[user].Client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = "profile:read content:read", ["state"] = "s",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        }));
    }

    private static async Task<Dictionary<string, Microsoft.Extensions.Primitives.StringValues>> AnswerAsync(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.Query.Length > 0
            ? QueryHelpers.ParseQuery(response.Headers.Location!.Query)
            : QueryHelpers.ParseQuery("?" + await response.Content.ReadAsStringAsync());

    private async Task<string> TokenAsync(TestTenant t, string clientId, string redirect, string verifier, string code) =>
        (await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        })))).GetProperty("access_token").GetString()!;

    private Task<HttpResponseMessage> ToolsAsync(TestTenant t, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } }) };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private async Task<JsonElement?> TenantAppAsync(TestTenant t, string clientId) =>
        (await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/apps"))).EnumerateArray()
            .Cast<JsonElement?>().FirstOrDefault(a => a!.Value.GetProperty("clientId").GetString() == clientId);

    [Fact]
    public async Task A_verified_client_connects_without_a_registration_once_the_policy_allows_it()
    {
        var t = await FreshAsync();
        var clientId = NewClientId();
        await JsonAsync(await ListAsync(clientId));

        // The AI policy administrator sees the list; nothing is registered until an employee connects an allowed client.
        var offered = await JsonAsync(await t["aip"].Client.GetAsync("/api/v1/api-access/verified-mcp-clients"));
        var entry = offered.GetProperty("items").EnumerateArray().Single(c => c.GetProperty("clientId").GetString() == clientId);
        Assert.Equal("Assistant Labs", entry.GetProperty("publisher").GetString());
        Assert.False(entry.TryGetProperty("reviewedBy", out _));

        var refused = await AnswerAsync(await AuthorizeAtAsync(t, "gia", clientId, Redirect, Verifier()));
        Assert.Equal("access_denied", refused["error"].ToString());
        Assert.Null(await TenantAppAsync(t, clientId));

        await AllowExternalAsync(t, "aip", (clientId, "internal"));
        var first = await AuthorizeAtAsync(t, "gia", clientId, Redirect, Verifier());
        Assert.True(first.StatusCode == HttpStatusCode.Redirect, $"{(int)first.StatusCode}: {await first.Content.ReadAsStringAsync()}");
        Assert.StartsWith("/app-consent", first.Headers.Location!.OriginalString, StringComparison.Ordinal);

        var app = (await TenantAppAsync(t, clientId))!.Value;
        Assert.True(app.GetProperty("verified").GetBoolean());
        Assert.Equal("Desk Assistant", app.GetProperty("name").GetString());
        Assert.Equal("mcp_client", app.GetProperty("kind").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiAppRegistered), ("id", app.GetProperty("id").GetString()!)));

        // The consent screen says the client is verified, then the code flow and the MCP server work as for any client.
        var prompt = await JsonAsync(await t["gia"].Client.GetAsync(QueryHelpers.AddQueryString("/api/v1/me/app-consent", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["redirect_uri"] = Redirect, ["scope"] = "profile:read content:read",
        })));
        Assert.True(prompt.GetProperty("verified").GetBoolean());
        Assert.Equal("Assistant Labs", prompt.GetProperty("publisher").GetString());
        await JsonAsync(await t["gia"].Client.PostAsJsonAsync("/api/v1/me/app-consent",
            new { clientId, redirectUri = Redirect, scopes = new[] { "profile:read", "content:read" } }));

        // A native client listens on any loopback port (RFC 8252 section 7.3); the path must still match.
        const string otherPort = "http://127.0.0.1:43123/callback";
        var verifier = Verifier();
        var answer = await AnswerAsync(await AuthorizeAtAsync(t, "gia", clientId, otherPort, verifier));
        Assert.False(string.IsNullOrEmpty(answer["code"].ToString()), string.Join(", ", answer.Select(kv => $"{kv.Key}={kv.Value}")));
        var token = await TokenAsync(t, clientId, otherPort, verifier, answer["code"].ToString());
        Assert.Equal(HttpStatusCode.OK, (await ToolsAsync(t, token)).StatusCode);
        var wrongPath = await AnswerAsync(await AuthorizeAtAsync(t, "gia", clientId, "http://127.0.0.1:43123/other", Verifier()));
        Assert.False(wrongPath.ContainsKey("code"));

        // The list owns what the client is; the tenant keeps its switches and limits.
        var changed = await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.GetProperty("id").GetString()}",
            new { scopes = new[] { "profile:read" }, version = app.GetProperty("version").GetInt32() });
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        Assert.Contains("validation.api.verified_app_fixed", await changed.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Blocking it on the platform disables it at once: the token is refused and no new sign-in starts.
        await JsonAsync(await ListAsync(clientId, "blocked"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await ToolsAsync(t, token)).StatusCode);
        Assert.False((await AnswerAsync(await AuthorizeAtAsync(t, "gia", clientId, Redirect, Verifier()))).ContainsKey("code"));
        Assert.DoesNotContain((await JsonAsync(await t["aip"].Client.GetAsync("/api/v1/api-access/verified-mcp-clients"))).GetProperty("items").EnumerateArray(),
            c => c.GetProperty("clientId").GetString() == clientId);
    }

    [Fact]
    public async Task Only_listed_clients_and_only_their_redirect_uris_are_accepted()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var unlisted = NewClientId();
        await AllowExternalAsync(t, "aip", (unlisted, "internal"));
        var unknown = await AuthorizeAtAsync(t, "gia", unlisted, Redirect, Verifier());
        Assert.False((await AnswerAsync(unknown)).ContainsKey("code"));
        Assert.Null(await TenantAppAsync(t, unlisted));

        var clientId = NewClientId();
        await JsonAsync(await ListAsync(clientId));
        await AllowExternalAsync(t, "aip", (clientId, "internal"));
        Assert.False((await AnswerAsync(await AuthorizeAtAsync(t, "gia", clientId, "https://evil.example.test/callback", Verifier()))).ContainsKey("code"));
        Assert.Null(await TenantAppAsync(t, clientId));

        // Registering it in one tenant registers nothing in another.
        Assert.Equal(HttpStatusCode.Redirect, (await AuthorizeAtAsync(t, "gia", clientId, Redirect, Verifier())).StatusCode);
        Assert.NotNull(await TenantAppAsync(t, clientId));
        Assert.Null(await TenantAppAsync(other, clientId));
    }

    [Fact]
    public async Task Only_the_platform_operator_keeps_the_list_and_entries_are_checked()
    {
        var t = await FreshAsync();
        var clientId = NewClientId();
        var tenantTry = await t["admin"].Client.PutAsJsonAsync("/api/v1/platform/api/verified-mcp-clients", new
        {
            clientId, name = "X", publisher = "Y", redirectUris = new[] { Redirect }, scopes = new[] { "content:read" },
        });
        Assert.NotEqual(HttpStatusCode.OK, tenantTry.StatusCode);

        foreach (var bad in new object[]
        {
            new { clientId = "http://ai.example.test/client.json", name = "X", publisher = "Y", redirectUris = new[] { Redirect }, scopes = new[] { "content:read" } },
            new { clientId = "https://ai.example.test/", name = "X", publisher = "Y", redirectUris = new[] { Redirect }, scopes = new[] { "content:read" } },
            new { clientId = clientId + "?x=1", name = "X", publisher = "Y", redirectUris = new[] { Redirect }, scopes = new[] { "content:read" } },
            new { clientId, name = "X", publisher = "", redirectUris = new[] { Redirect }, scopes = new[] { "content:read" } },
            new { clientId, name = "X", publisher = "Y", redirectUris = new[] { "https://a.example.test/*" }, scopes = new[] { "content:read" } },
            new { clientId, name = "X", publisher = "Y", redirectUris = new[] { Redirect }, scopes = new[] { "events:subscribe" } },
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await world.Platform.PutAsJsonAsync("/api/v1/platform/api/verified-mcp-clients", bad)).StatusCode);
        }

        await JsonAsync(await ListAsync(clientId));
        var listed = await JsonAsync(await world.Platform.GetAsync("/api/v1/platform/api/verified-mcp-clients"));
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), c => c.GetProperty("clientId").GetString() == clientId);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.platform_audit_event where action = @a and target_id = @id", ("a", AuditActions.ApiVerifiedClientChanged), ("id", clientId)));
    }
}
