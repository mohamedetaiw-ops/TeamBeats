using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 phase 1 (T003, T004): the per-tenant OAuth 2.1 authorization server. Each tenant host has its own issuer and
/// signing keys; codes need PKCE with S256; tokens are bound to one resource; refresh tokens rotate and a reused one
/// revokes its family; the implicit and password grants do not exist; tokens, app IDs and codes of one tenant are refused
/// on another tenant's host.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiAuthorizationServerTests(TenantWorld world)
{
    private const string Redirect = "http://127.0.0.1:7777/callback";
    private const string WebRedirect = "https://tools.example/oauth/callback";

    private Task<TestTenant> FreshAsync(string package = "workplace_plus") => world.CreateTenantAsync("oa-" + Guid.NewGuid().ToString("N")[..6], package);

    private static string Origin(TestTenant t) => $"https://{t.Key}.{ApiFactory.BaseDomain}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> CreateAppAsync(TestTenant t, string kind, string[] scopes, string[]? redirects = null, bool allowGuests = false)
    {
        var response = await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Field app " + kind, kind, scopes, redirectUris = redirects ?? (kind == "application" ? [] : [Redirect]), allowGuests, publisher = "Acme IT",
        });
        return await JsonAsync(response, HttpStatusCode.Created);
    }

    private static string Show(HttpResponseMessage response) => $"{(int)response.StatusCode} {response.Content.ReadAsStringAsync().Result}";

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static string AuthorizeUrl(string clientId, string challenge, string scope, string? resource = null, string method = "S256",
        string responseType = "code", string redirect = Redirect)
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["response_type"] = responseType, ["redirect_uri"] = redirect, ["scope"] = scope, ["state"] = "s-1",
            ["code_challenge"] = challenge, ["code_challenge_method"] = method,
        };
        if (resource is not null)
        {
            query["resource"] = resource;
        }
        return QueryHelpers.AddQueryString("/oauth/authorize", query);
    }

    private static Dictionary<string, string> Query(Uri location) =>
        QueryHelpers.ParseQuery(location.Query).ToDictionary(p => p.Key, p => p.Value.ToString());

    /// <summary>Signs a person in to an app that already has consent and returns the authorization code.</summary>
    private static async Task<string> CodeAsync(HttpClient person, string clientId, string challenge, string scope, string? resource = null,
        string redirect = Redirect)
    {
        var response = await person.GetAsync(AuthorizeUrl(clientId, challenge, scope, resource, redirect: redirect));
        Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var location = response.Headers.Location!;
        Assert.StartsWith(redirect, location.ToString(), StringComparison.Ordinal);
        var query = Query(location);
        Assert.Equal("s-1", query["state"]);
        return query["code"];
    }

    private static Task<HttpResponseMessage> TokenAsync(HttpClient client, Dictionary<string, string> form) =>
        client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    private static async Task ConsentAsync(TestTenant t, string user, string clientId, string[] scopes)
    {
        var response = await t[user].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes });
        await JsonAsync(response);
    }

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '='))).RootElement;
    }

    private static string KeyId(string jwt)
    {
        var part = jwt.Split('.')[0].Replace('-', '+').Replace('_', '/');
        return JsonDocument.Parse(Convert.FromBase64String(part.PadRight(part.Length + (4 - part.Length % 4) % 4, '='))).RootElement.GetProperty("kid").GetString()!;
    }

    private async Task<HttpResponseMessage> CallAsync(TestTenant t, string token, string path = "/api/public/v1/me")
    {
        var client = world.Factory.TenantClient(t.Key);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Each_tenant_host_has_its_own_issuer_metadata_and_keys()
    {
        var t = await FreshAsync();
        var anonymous = world.Factory.TenantClient(t.Key);
        var metadata = await JsonAsync(await anonymous.GetAsync("/.well-known/oauth-authorization-server/oauth"));
        Assert.Equal(Origin(t) + "/oauth", metadata.GetProperty("issuer").GetString());
        Assert.Equal(Origin(t) + "/oauth/authorize", metadata.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(Origin(t) + "/oauth/token", metadata.GetProperty("token_endpoint").GetString());
        // PKCE with S256 only; the code flow only; no implicit and no password grant (FR-004).
        Assert.Equal(["S256"], metadata.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["code"], metadata.GetProperty("response_types_supported").EnumerateArray().Select(e => e.GetString()));
        var grants = metadata.GetProperty("grant_types_supported").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain("password", grants);
        Assert.DoesNotContain("implicit", grants);
        Assert.Contains("content:read", metadata.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()));

        var keys = await JsonAsync(await anonymous.GetAsync("/oauth/jwks"));
        var mine = keys.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToList();
        var others = (await JsonAsync(await world.Factory.TenantClient(world.B.Key).GetAsync("/oauth/jwks"))).GetProperty("keys").EnumerateArray()
            .Select(k => k.GetProperty("kid").GetString()).ToList();
        Assert.NotEmpty(mine);
        Assert.Empty(mine.Intersect(others));
        Assert.All(keys.GetProperty("keys").EnumerateArray(), k => Assert.False(k.TryGetProperty("d", out _)));

        // The platform host has no authorization server.
        Assert.Equal(HttpStatusCode.NotFound, (await world.Platform.GetAsync("/.well-known/oauth-authorization-server/oauth")).StatusCode);
    }

    [Fact]
    public async Task An_employee_signs_in_consents_and_refresh_tokens_rotate_until_one_is_reused()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "dana", MembershipTypes.Employee);
        var app = await CreateAppAsync(t, "delegated_public", ["profile:read", "content:read"]);
        var clientId = app.GetProperty("clientId").GetString()!;
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiAppRegistered, app.GetProperty("id").GetString());
        var (verifier, challenge) = Pkce();

        // Without a session the authorization server sends the browser to the tenant's own sign-in and back.
        var signedOut = await world.Factory.TenantClient(t.Key).GetAsync(AuthorizeUrl(clientId, challenge, "profile:read content:read"));
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        var signIn = signedOut.Headers.Location!.ToString();
        Assert.StartsWith("/signin?returnTo=", signIn, StringComparison.Ordinal);
        Assert.StartsWith("/oauth/authorize?", Uri.UnescapeDataString(signIn["/signin?returnTo=".Length..]), StringComparison.Ordinal);

        // Signed in but not yet consented: the consent page, which shows the app as text and the scopes.
        var dana = t["dana"].Client;
        var unconsented = await dana.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read content:read"));
        Assert.Equal(HttpStatusCode.Redirect, unconsented.StatusCode);
        Assert.StartsWith("/app-consent?", unconsented.Headers.Location!.ToString(), StringComparison.Ordinal);
        var prompt = await JsonAsync(await dana.GetAsync($"/api/v1/me/app-consent?client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&scope=profile:read%20content:read"));
        Assert.Equal("Acme IT", prompt.GetProperty("publisher").GetString());
        Assert.False(prompt.GetProperty("needsAdmin").GetBoolean());
        Assert.All(prompt.GetProperty("scopes").EnumerateArray(), s => Assert.False(s.GetProperty("granted").GetBoolean()));
        await ConsentAsync(t, "dana", clientId, ["profile:read", "content:read"]);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiConsentGranted, app.GetProperty("id").GetString());

        var code = await CodeAsync(dana, clientId, challenge, "profile:read content:read");
        var anonymous = world.Factory.TenantClient(t.Key);
        var tokens = await JsonAsync(await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        var access = tokens.GetProperty("access_token").GetString()!;
        var refresh = tokens.GetProperty("refresh_token").GetString()!;
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 500, 600);
        var claims = Payload(access);
        Assert.Equal(Origin(t) + "/oauth", claims.GetProperty("iss").GetString());
        Assert.Equal(Origin(t) + "/api/public", claims.GetProperty("aud").GetString());
        Assert.Equal(t["dana"].Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal(t.Id.ToString(), claims.GetProperty("tb_tenant").GetString());
        Assert.False(claims.TryGetProperty("tb_gen", out _));
        var jwks = await JsonAsync(await anonymous.GetAsync("/oauth/jwks"));
        Assert.Contains(KeyId(access), jwks.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()));

        var me = await JsonAsync(await CallAsync(t, access));
        Assert.Equal(t["dana"].Id, me.GetProperty("id").GetGuid());
        Assert.Equal("employee", me.GetProperty("membershipType").GetString());
        // A cookie session never authenticates the public API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await dana.GetAsync("/api/public/v1/me")).StatusCode);
        // The same code a second time is refused.
        var again = await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("invalid_grant", await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, access)).StatusCode);

        // A new sign-in: each refresh gives a new refresh token, and presenting an old one revokes the family.
        (verifier, challenge) = Pkce();
        code = await CodeAsync(dana, clientId, challenge, "profile:read");
        tokens = await JsonAsync(await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        var first = tokens.GetProperty("refresh_token").GetString()!;
        var rotated = await JsonAsync(await TokenAsync(anonymous, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = first }));
        var second = rotated.GetProperty("refresh_token").GetString()!;
        var secondAccess = rotated.GetProperty("access_token").GetString()!;
        Assert.NotEqual(first, second);
        Assert.Equal("profile:read", Payload(secondAccess).GetProperty("scope").GetString());
        await JsonAsync(await CallAsync(t, secondAccess));
        // A refresh cannot add a scope the sign-in did not have.
        var wider = await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = second, ["scope"] = "profile:read content:read",
        });
        Assert.Equal(HttpStatusCode.BadRequest, wider.StatusCode);

        var reused = await TokenAsync(anonymous, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = first });
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        Assert.Contains("invalid_grant", await reused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, secondAccess)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await TokenAsync(anonymous, new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = second })).StatusCode);
        // Two reuses: the code presented twice, then the old refresh token.
        Assert.Equal(2, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = 'api.token_family_revoked' and changes->>'reason' = 'reuse_detected'"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiTokenFamilyRevoked);
        Assert.NotEmpty(refresh);
    }

    [Fact]
    public async Task Pkce_is_required_and_implicit_password_and_foreign_redirects_are_refused()
    {
        var t = await FreshAsync();
        var app = await CreateAppAsync(t, "delegated_public", ["profile:read"]);
        var clientId = app.GetProperty("clientId").GetString()!;
        await ConsentAsync(t, "admin", clientId, ["profile:read"]);
        var admin = t["admin"].Client;
        var (verifier, challenge) = Pkce();

        // No code challenge, or the plain method: refused.
        var noPkce = await admin.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read").Replace("&code_challenge=" + challenge, "", StringComparison.Ordinal));
        Assert.Contains("invalid_request", Uri.UnescapeDataString(noPkce.Headers.Location?.ToString() ?? await noPkce.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        var plain = await admin.GetAsync(AuthorizeUrl(clientId, verifier, "profile:read", method: "plain"));
        Assert.Contains("invalid_request", Uri.UnescapeDataString(plain.Headers.Location?.ToString() ?? await plain.Content.ReadAsStringAsync()), StringComparison.Ordinal);
        // The implicit flow does not exist.
        var implicitFlow = await admin.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read", responseType: "token"));
        Assert.Contains("unsupported_response_type", Uri.UnescapeDataString(implicitFlow.Headers.Location?.ToString() ?? await implicitFlow.Content.ReadAsStringAsync()),
            StringComparison.Ordinal);
        // A redirect URI the app did not register gets an error page, never a redirect.
        var foreign = await admin.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read", redirect: "https://evil.example/cb"));
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Null(foreign.Headers.Location);
        // A scope the app did not register.
        var scope = await admin.GetAsync(AuthorizeUrl(clientId, challenge, "documents:read"));
        Assert.Contains("invalid_scope", Uri.UnescapeDataString(scope.Headers.Location?.ToString() ?? await scope.Content.ReadAsStringAsync()), StringComparison.Ordinal);

        var anonymous = world.Factory.TenantClient(t.Key);
        var password = await TokenAsync(anonymous, new() { ["grant_type"] = "password", ["client_id"] = clientId, ["username"] = "admin", ["password"] = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, password.StatusCode);
        Assert.Contains("unsupported_grant_type", await password.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A code redeemed with the wrong verifier is refused.
        var code = await CodeAsync(admin, clientId, challenge, "profile:read");
        var wrong = await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = Pkce().Verifier,
        });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("invalid_grant", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tokens_app_ids_and_codes_of_one_tenant_are_refused_on_another()
    {
        var t = await FreshAsync();
        var app = await CreateAppAsync(t, "delegated_public", ["profile:read"]);
        var clientId = app.GetProperty("clientId").GetString()!;
        await ConsentAsync(t, "admin", clientId, ["profile:read"]);
        var (verifier, challenge) = Pkce();
        var code = await CodeAsync(t["admin"].Client, clientId, challenge, "profile:read");

        // Tenant A's app ID in tenant B's authorization flow and token endpoint is unknown there.
        var b = world.Factory.TenantClient(world.B.Key, world.B["admin"].Token);
        var crossAuthorize = await b.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read"));
        Assert.Equal(HttpStatusCode.Unauthorized, crossAuthorize.StatusCode);
        Assert.Null(crossAuthorize.Headers.Location);
        Assert.Contains("invalid_client", await crossAuthorize.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var crossCode = await TokenAsync(world.Factory.TenantClient(world.B.Key), new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, crossCode.StatusCode);
        Assert.Contains("invalid_client", await crossCode.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var tokens = await JsonAsync(await TokenAsync(world.Factory.TenantClient(t.Key), new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        var access = tokens.GetProperty("access_token").GetString()!;
        await JsonAsync(await CallAsync(t, access));
        // Tenant A's access token on tenant B's host: refused and audited there as a cross-tenant attempt.
        var before = await world.Database.AuditCountAsync(world.B.Id, AuditActions.ApiTokenRefused);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(world.B, access)).StatusCode);
        Assert.Equal(before + 1, await world.Database.AuditCountAsync(world.B.Id, AuditActions.ApiTokenRefused));
        // A token signed with another key, or with its payload changed, is refused.
        var parts = access.Split('.');
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, $"{parts[0]}.{parts[1]}x.{parts[2]}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, "not-a-token")).StatusCode);
        var missing = await world.Factory.TenantClient(t.Key).GetAsync("/api/public/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("Bearer", missing.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Tokens_are_bound_to_one_resource()
    {
        var t = await FreshAsync();
        var mcp = await CreateAppAsync(t, "mcp_client", ["profile:read", "search:query"]);
        var clientId = mcp.GetProperty("clientId").GetString()!;
        await ConsentAsync(t, "admin", clientId, ["profile:read", "search:query"]);
        var (verifier, challenge) = Pkce();
        // An MCP client asks for the MCP server; it cannot name the REST API.
        var wrongResource = await t["admin"].Client.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read", Origin(t) + "/api/public"));
        Assert.Contains("invalid_target", Uri.UnescapeDataString(wrongResource.Headers.Location?.ToString() ?? await wrongResource.Content.ReadAsStringAsync()),
            StringComparison.Ordinal);
        var code = await CodeAsync(t["admin"].Client, clientId, challenge, "profile:read", Origin(t) + "/mcp");
        var tokens = await JsonAsync(await TokenAsync(world.Factory.TenantClient(t.Key), new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal(Origin(t) + "/mcp", Payload(access).GetProperty("aud").GetString());
        // A token for the MCP server is not a REST API token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, access)).StatusCode);
    }

    [Fact]
    public async Task An_application_app_uses_its_secret_and_loses_access_when_the_credential_or_app_is_revoked()
    {
        var t = await FreshAsync();
        // Application apps hold only application scopes; profile:read is for apps acting for a person.
        var refused = await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new { name = "Sync", kind = "application", scopes = new[] { "profile:read" } });
        Assert.Contains("validation.api.scope_not_for_kind", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var app = await CreateAppAsync(t, "application", ["content:read", "events:subscribe"]);
        var id = app.GetProperty("id").GetString()!;
        var clientId = app.GetProperty("clientId").GetString()!;
        var issued = await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created);
        var secret = issued.GetProperty("secret").GetString()!;
        var credential = issued.GetProperty("credential").GetProperty("id").GetString()!;
        Assert.Equal(secret[^4..], issued.GetProperty("credential").GetProperty("hint").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiCredentialAdded, id);
        // The secret is kept only as a salted hash and is never in the audit record.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from publicapi.app_credential where secret_hash like '%{secret}%'"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from audit.audit_event where changes::text like '%{secret}%'"));
        // The secret is shown once: the app page lists the credential without it.
        var detail = await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/apps/{id}"));
        Assert.DoesNotContain(secret, detail.GetRawText(), StringComparison.Ordinal);

        var anonymous = world.Factory.TenantClient(t.Key);
        var wrong = await TokenAsync(anonymous, new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = "tbs_wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Contains("invalid_client", await wrong.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var basic = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["scope"] = "content:read" }) };
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        var tokens = await JsonAsync(await anonymous.SendAsync(basic));
        Assert.False(tokens.TryGetProperty("refresh_token", out _));
        var access = tokens.GetProperty("access_token").GetString()!;
        var claims = Payload(access);
        Assert.Equal(id, claims.GetProperty("sub").GetString());
        Assert.Equal("application", claims.GetProperty("tb_kind").GetString());
        Assert.Equal(credential, claims.GetProperty("tb_family").GetString());
        // The token is accepted, but /me needs profile:read, which only an app acting for a person holds.
        var me = await CallAsync(t, access);
        Assert.Equal(HttpStatusCode.Forbidden, me.StatusCode);
        Assert.Contains("api.insufficient_scope", await me.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // An application app cannot run the code flow, and a delegated app cannot use client credentials.
        Assert.Contains("unauthorized_client", Show(await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["client_secret"] = secret, ["code"] = "x", ["redirect_uri"] = Redirect, ["code_verifier"] = Pkce().Verifier,
        })), StringComparison.Ordinal);

        // Revoking the credential refuses the tokens it got at once.
        await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/apps/{id}/credentials/{credential}:revoke", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiCredentialRevoked, id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, access)).StatusCode);
        Assert.Contains("invalid_client", await (await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret,
        })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A new secret works until the app is disabled.
        secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { expiresInMonths = 12 }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        Assert.Contains("validation.api.credential_expiry", await (await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { expiresInMonths = 13 }))
            .Content.ReadAsStringAsync(), StringComparison.Ordinal);
        access = (await JsonAsync(await TokenAsync(anonymous, new() { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret })))
            .GetProperty("access_token").GetString()!;
        await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/api-access/apps/{id}:disable", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiAppDisabled, id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, access)).StatusCode);
        Assert.Contains("invalid_client", await (await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret,
        })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Write_scopes_need_an_administrator_and_guests_and_leavers_are_refused()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "omar", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "gwen", MembershipTypes.Guest);
        var app = await CreateAppAsync(t, "delegated_web", ["profile:read", "content:draft"], ["https://tools.example/cb", WebRedirect]);
        // A web app never gets a plain HTTP redirect, even to a loopback address.
        Assert.Contains("validation.api.redirect_invalid", await (await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Web", kind = "delegated_web", scopes = new[] { "profile:read" }, redirectUris = new[] { Redirect } })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var id = app.GetProperty("id").GetString()!;
        var clientId = app.GetProperty("clientId").GetString()!;

        // content:draft needs an API administrator's consent (FR-013): the employee cannot give it.
        var prompt = await JsonAsync(await t["omar"].Client.GetAsync(
            $"/api/v1/me/app-consent?client_id={clientId}&redirect_uri={Uri.EscapeDataString(WebRedirect)}&scope=profile:read%20content:draft"));
        Assert.True(prompt.GetProperty("needsAdmin").GetBoolean());
        var denied = await t["omar"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = WebRedirect, scopes = new[] { "profile:read", "content:draft" } });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("authz.denied.api_admin_consent", await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{id}/admin-consent", new { scopes = new[] { "profile:read", "content:draft" } }));
        var (verifier, challenge) = Pkce();
        var code = await CodeAsync(t["omar"].Client, clientId, challenge, "profile:read content:draft", redirect: WebRedirect);

        // A confidential web app needs its secret at the token endpoint.
        var anonymous = world.Factory.TenantClient(t.Key);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = WebRedirect, ["code_verifier"] = verifier,
        };
        Assert.Contains("invalid_client", await (await TokenAsync(anonymous, form)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        form["client_secret"] = secret;
        var access = (await JsonAsync(await TokenAsync(anonymous, form))).GetProperty("access_token").GetString()!;
        await JsonAsync(await CallAsync(t, access));

        // Guests cannot authorize the app unless it allows guests.
        var guest = await t["gwen"].Client.GetAsync(AuthorizeUrl(clientId, challenge, "profile:read", redirect: WebRedirect));
        Assert.Equal(HttpStatusCode.Redirect, guest.StatusCode);
        Assert.Equal("access_denied", Query(guest.Headers.Location!)["error"]);

        // Disabling the membership refuses the delegated token at once (FR-007).
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.PostAsync($"/api/v1/admin/users/{t["omar"].Id}:disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, access)).StatusCode);

        // Removing a scope from the app ends the sign-ins that had it.
        var renamed = await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{id}", new { name = "Drafting tool", scopes = new[] { "profile:read" } }));
        Assert.Equal("Drafting tool", renamed.GetProperty("name").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiAppChanged, id);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from publicapi.token_family where app_id = '{id}' and revoked_at is null"));
    }

    [Fact]
    public async Task Revoking_a_consent_or_a_token_ends_the_sign_in_and_the_tenant_may_shorten_tokens()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "lina", MembershipTypes.Employee);
        var settings = await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { accessTokenMinutes = 5, refreshTokenDays = 7 }));
        Assert.Equal(5, settings.GetProperty("accessTokenMinutes").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiSettingsChanged);
        // The tenant may only lower the platform defaults.
        Assert.Contains("validation.api.access_token_minutes",
            await (await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { accessTokenMinutes = 61 })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var app = await CreateAppAsync(t, "delegated_public", ["profile:read"]);
        var clientId = app.GetProperty("clientId").GetString()!;
        await ConsentAsync(t, "lina", clientId, ["profile:read"]);
        var anonymous = world.Factory.TenantClient(t.Key);
        async Task<JsonElement> SignInAsync()
        {
            var (verifier, challenge) = Pkce();
            var code = await CodeAsync(t["lina"].Client, clientId, challenge, "profile:read");
            return await JsonAsync(await TokenAsync(anonymous, new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
            }));
        }

        var tokens = await SignInAsync();
        Assert.InRange(tokens.GetProperty("expires_in").GetInt32(), 250, 300);
        // RFC 7009: the app revokes its own refresh token, and the family ends.
        var revoke = await anonymous.PostAsync("/oauth/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["token"] = tokens.GetProperty("refresh_token").GetString()!, ["token_type_hint"] = "refresh_token",
        }));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, tokens.GetProperty("access_token").GetString()!)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await TokenAsync(anonymous, new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
        })).StatusCode);

        // The employee revokes the app on the connected apps page.
        tokens = await SignInAsync();
        var connected = await JsonAsync(await t["lina"].Client.GetAsync("/api/v1/me/connected-apps"));
        var consent = connected.EnumerateArray().Single().GetProperty("consentId").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await t["lina"].Client.DeleteAsync($"/api/v1/me/connected-apps/{consent}")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiConsentRevoked, app.GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, tokens.GetProperty("access_token").GetString()!)).StatusCode);
        Assert.Empty((await JsonAsync(await t["lina"].Client.GetAsync("/api/v1/me/connected-apps"))).EnumerateArray());
        // Another person's consent is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.DeleteAsync($"/api/v1/me/connected-apps/{consent}")).StatusCode);
    }

    [Fact]
    public async Task The_package_decides_and_only_api_administrators_manage_apps()
    {
        // Workplace: read scopes and two apps; no write scopes.
        var t = await FreshAsync("workplace");
        await world.SeedUserAsync(t, "sam", MembershipTypes.Employee);
        Assert.Contains("validation.api.scope_not_in_package", await (await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Writer", kind = "delegated_public", scopes = new[] { "content:draft" }, redirectUris = new[] { Redirect } })).Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        await CreateAppAsync(t, "delegated_public", ["profile:read"]);
        await CreateAppAsync(t, "application", ["content:read"]);
        var third = await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new { name = "Third", kind = "application", scopes = new[] { "content:read" } });
        Assert.Equal(HttpStatusCode.Forbidden, third.StatusCode);
        Assert.Contains("entitlement.quota_exhausted", await third.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Employees without api.manage cannot see or register apps.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sam"].Client.GetAsync("/api/v1/api-access/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sam"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Mine", kind = "application", scopes = new[] { "content:read" } })).StatusCode);

        // A tenant whose package lacks api.public has no authorization server and no app administration.
        var closed = await world.CreateTenantAsync("oa-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        await world.PinPackageVersionAsync(closed, "workplace", new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
            [Capabilities.OrgHierarchy] = new { enabled = true },
        });
        var list = await closed["admin"].Client.GetAsync("/api/v1/api-access/apps");
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Contains("entitlement.not_in_package", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var authorize = await closed["admin"].Client.GetAsync(AuthorizeUrl("tb_unknown", Pkce().Challenge, "profile:read"));
        Assert.Contains("access_denied", await authorize.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var call = await world.Factory.TenantClient(closed.Key).GetAsync("/api/public/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, call.StatusCode);
    }
    [Fact]
    public async Task Api_administrators_see_every_consent_and_can_end_a_persons_and_viewers_only_read()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "dana", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "sec", MembershipTypes.Employee);
        await world.AssignAsync(t, "sec", "security_officer", "tenant", null);
        var app = await CreateAppAsync(t, "delegated_public", ["profile:read", "content:read"]);
        var id = app.GetProperty("id").GetString()!;
        var clientId = app.GetProperty("clientId").GetString()!;
        await ConsentAsync(t, "dana", clientId, ["profile:read"]);
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{id}/admin-consent", new { scopes = new[] { "content:read" } }));
        var (verifier, challenge) = Pkce();
        var code = await CodeAsync(t["dana"].Client, clientId, challenge, "profile:read");
        var access = (await JsonAsync(await TokenAsync(world.Factory.TenantClient(t.Key), new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }))).GetProperty("access_token").GetString()!;
        await JsonAsync(await CallAsync(t, access));

        // The admin area lists the section for API administrators and viewers, not for employees.
        static async Task<bool> HasSection(HttpClient client) =>
            (await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/navigation")).EnumerateArray().Any(s => s.GetProperty("key").GetString() == "api");
        Assert.True(await HasSection(t["admin"].Client));
        Assert.True(await HasSection(t["sec"].Client));
        Assert.False(await HasSection(t["dana"].Client));

        // Every consent in the tenant (FR-029): the person's own and the one for everyone.
        var consents = await JsonAsync(await t["sec"].Client.GetAsync("/api/v1/api-access/consents"));
        Assert.Equal(2, consents.GetArrayLength());
        var danas = consents.EnumerateArray().Single(c => c.GetProperty("userId").ValueKind == JsonValueKind.String);
        Assert.Equal(t["dana"].Id.ToString(), danas.GetProperty("userId").GetString());
        Assert.Equal(["profile:read"], danas.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.Contains(consents.EnumerateArray(), c => c.GetProperty("userId").ValueKind == JsonValueKind.Null);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dana"].Client.GetAsync("/api/v1/api-access/consents")).StatusCode);
        var consentId = danas.GetProperty("consentId").GetString()!;
        // A viewer cannot end it, another tenant's administrator cannot find it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sec"].Client.PostAsync($"/api/v1/api-access/consents/{consentId}:revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.PostAsync($"/api/v1/api-access/consents/{consentId}:revoke", null)).StatusCode);
        Assert.DoesNotContain((await JsonAsync(await world.B["admin"].Client.GetAsync("/api/v1/api-access/consents"))).EnumerateArray(),
            c => c.GetProperty("consentId").GetString() == consentId);

        // An administrator ends Dana's consent: the app's token for her stops at once.
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.PostAsync($"/api/v1/api-access/consents/{consentId}:revoke", null)).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiConsentRevoked, id);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, access)).StatusCode);
        Assert.Equal(0, (await JsonAsync(await t["dana"].Client.GetAsync("/api/v1/me/connected-apps"))).GetArrayLength());
        Assert.Single((await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/consents?appId={id}"))).EnumerateArray());
    }
}
