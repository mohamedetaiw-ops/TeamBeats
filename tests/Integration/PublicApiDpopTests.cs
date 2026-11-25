using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 R3 (T021): sender-constrained tokens with DPoP (RFC 9449). A token request with a proof gets tokens bound to
/// the client's key, of type DPoP; a bound token is accepted only with the DPoP scheme and a fresh proof of that key for
/// this method, URL and token, once. Refresh tokens stay on their key. An app can require DPoP, and then gets no token
/// without a proof and no call with an unbound token. Proofs of another tenant, another key or another request are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiDpopTests(TenantWorld world)
{
    private const string Me = "/api/public/v1/content";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("dp-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "aip", MembershipTypes.Employee);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private static string Origin(TestTenant t) => $"https://{t.Key}.{ApiFactory.BaseDomain}";

    private static Dictionary<string, object> Jwk(ECDsa key, bool withPrivate = false)
    {
        var p = key.ExportParameters(withPrivate);
        var jwk = new Dictionary<string, object> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = Base64UrlEncoder.Encode(p.Q.X), ["y"] = Base64UrlEncoder.Encode(p.Q.Y) };
        if (withPrivate)
        {
            jwk["d"] = Base64UrlEncoder.Encode(p.D);
        }
        return jwk;
    }

    private static string Thumbprint(ECDsa key) =>
        Base64UrlEncoder.Encode(new JsonWebKey(JsonSerializer.Serialize(Jwk(key))).ComputeJwkThumbprint());

    /// <summary>A DPoP proof signed with <paramref name="key"/>, by default for now and a fresh ID.</summary>
    private static string Proof(ECDsa key, string method, string url, string? accessToken = null, DateTime? at = null, string? jti = null,
        bool privateJwk = false, string type = "dpop+jwt")
    {
        var when = at ?? DateTime.UtcNow;
        var claims = new Dictionary<string, object> { ["jti"] = jti ?? Guid.NewGuid().ToString("N"), ["htm"] = method, ["htu"] = url };
        if (accessToken is not null)
        {
            claims["ath"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
        }
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = type,
            IssuedAt = when,
            Claims = claims,
            AdditionalHeaderClaims = new Dictionary<string, object> { ["jwk"] = Jwk(key, privateJwk) },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256),
        });
    }

    private async Task<(string Id, string ClientId, string Secret)> ApplicationAsync(TestTenant t, bool requireDpop = false)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Sync " + Guid.NewGuid().ToString("N")[..4], kind = "application", scopes = new[] { "content:read" }, requireDpop }), HttpStatusCode.Created);
        Assert.Equal(requireDpop, app.GetProperty("requireDpop").GetBoolean());
        var id = app.GetProperty("id").GetString()!;
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "app", subjectId = Guid.Parse(id), roleKey = BuiltInRoles.AppReader, scopeType = "tenant", scopeId = (Guid?)null }), HttpStatusCode.Created);
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        return (id, app.GetProperty("clientId").GetString()!, secret);
    }

    private Task<HttpResponseMessage> TokenAsync(TestTenant t, Dictionary<string, string> form, string? proof)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = new FormUrlEncodedContent(form) };
        if (proof is not null)
        {
            request.Headers.TryAddWithoutValidation("DPoP", proof);
        }
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private Task<HttpResponseMessage> ClientTokenAsync(TestTenant t, (string Id, string ClientId, string Secret) app, string? proof) =>
        TokenAsync(t, new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.ClientId, ["client_secret"] = app.Secret, ["scope"] = "content:read",
        }, proof);

    private Task<HttpResponseMessage> CallAsync(TestTenant t, string scheme, string token, string? proof, HttpMethod? method = null, string path = Me,
        object? body = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(scheme, token);
        if (proof is not null)
        {
            request.Headers.TryAddWithoutValidation("DPoP", proof);
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
        }
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private static async Task AssertDpopRefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.StartsWith("DPoP ", challenge, StringComparison.Ordinal);
        Assert.Contains("error=\"invalid_dpop_proof\"", challenge, StringComparison.Ordinal);
        Assert.Equal("api.token_invalid", (await JsonAsync(response, HttpStatusCode.Unauthorized)).GetProperty("code").GetString());
    }

    private static async Task AssertInvalidProofAsync(HttpResponseMessage response)
    {
        var body = await JsonAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("invalid_dpop_proof", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_token_bound_to_a_key_works_only_with_a_fresh_proof_of_that_key()
    {
        var t = await FreshAsync();
        var url = Origin(t) + Me;
        var metadata = await JsonAsync(await world.Factory.TenantClient(t.Key).GetAsync("/.well-known/oauth-authorization-server/oauth"));
        Assert.Contains("ES256", metadata.GetProperty("dpop_signing_alg_values_supported").EnumerateArray().Select(x => x.GetString()));

        var app = await ApplicationAsync(t);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tokenUrl = Origin(t) + "/oauth/token";

        // Proofs that do not hold are refused at the token endpoint.
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, Proof(key, "POST", tokenUrl, privateJwk: true)));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, Proof(key, "GET", tokenUrl)));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, Proof(key, "POST", Origin(t) + "/oauth/other")));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, Proof(key, "POST", tokenUrl, at: DateTime.UtcNow.AddMinutes(-10))));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, Proof(key, "POST", tokenUrl, type: "JWT")));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, Proof(key, "POST", tokenUrl, accessToken: "not-a-token")));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, "not.a.proof"));

        var tokenProof = Proof(key, "POST", tokenUrl);
        var issued = await JsonAsync(await ClientTokenAsync(t, app, tokenProof));
        Assert.Equal("DPoP", issued.GetProperty("token_type").GetString());
        var token = issued.GetProperty("access_token").GetString()!;
        var cnf = new JsonWebToken(token).GetPayloadValue<JsonElement>("cnf");
        Assert.Equal(Thumbprint(key), cnf.GetProperty("jkt").GetString());
        // A token proof is good once.
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, tokenProof));

        // The bound token is refused as a Bearer token, without a proof, and with proofs that do not match the call.
        await AssertDpopRefusedAsync(await CallAsync(t, "Bearer", token, null));
        await AssertDpopRefusedAsync(await CallAsync(t, "Bearer", token, Proof(key, "GET", url, token)));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, null));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, Proof(key, "GET", url)));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, Proof(key, "POST", url, token)));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, Proof(key, "GET", Origin(t) + "/api/public/v1/news", token)));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, Proof(key, "GET", url, token, at: DateTime.UtcNow.AddMinutes(-6))));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, Proof(key, "GET", url, token, at: DateTime.UtcNow.AddMinutes(3))));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, Proof(other, "GET", url, token)));

        // With a fresh proof of its key it works, and that proof is spent.
        var proof = Proof(key, "GET", url, token);
        await JsonAsync(await CallAsync(t, "DPoP", token, proof));
        await AssertDpopRefusedAsync(await CallAsync(t, "DPoP", token, proof));
        await JsonAsync(await CallAsync(t, "DPoP", token, Proof(key, "GET", url + "?ignored=query", token)));
        Assert.True(await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @action and changes->>'reason' = 'dpop'", ("action", AuditActions.ApiTokenRefused)) >= 10);

        // An unbound token keeps working as a Bearer token, and is refused with the DPoP scheme.
        var plain = (await JsonAsync(await ClientTokenAsync(t, app, null))).GetProperty("access_token").GetString()!;
        Assert.False(new JsonWebToken(plain).TryGetPayloadValue<JsonElement>("cnf", out _));
        await JsonAsync(await CallAsync(t, "Bearer", plain, null));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(t, "DPoP", plain, Proof(key, "GET", url, plain))).StatusCode);

        // Another tenant does not take the token, even with a proof for its own address.
        var g = await FreshAsync();
        var foreign = new HttpRequestMessage(HttpMethod.Get, Me);
        foreign.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        foreign.Headers.TryAddWithoutValidation("DPoP", Proof(key, "GET", Origin(g) + Me, token));
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(g.Key).SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task An_app_that_requires_dpop_gets_no_token_and_no_call_without_a_proof()
    {
        var t = await FreshAsync();
        var app = await ApplicationAsync(t);
        var url = Origin(t) + Me;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var plain = (await JsonAsync(await ClientTokenAsync(t, app, null))).GetProperty("access_token").GetString()!;

        // Turning the setting on refuses tokens issued without a proof and token requests without one.
        var detail = await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/apps/{app.Id}"));
        var changed = await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.Id}",
            new { requireDpop = true, version = detail.GetProperty("app").GetProperty("version").GetInt32() }));
        Assert.True(changed.GetProperty("requireDpop").GetBoolean());
        Assert.True(await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @action and coalesce(changes->'after'->>'requireDpop', changes->'after'->>'RequireDpop') = 'true'", ("action", AuditActions.ApiAppChanged)) == 1);
        await AssertDpopRefusedAsync(await CallAsync(t, "Bearer", plain, null));
        await AssertInvalidProofAsync(await ClientTokenAsync(t, app, null));

        var token = (await JsonAsync(await ClientTokenAsync(t, app, Proof(key, "POST", Origin(t) + "/oauth/token")))).GetProperty("access_token").GetString()!;
        await JsonAsync(await CallAsync(t, "DPoP", token, Proof(key, "GET", url, token)));

        // A new app that requires it from the start.
        var strict = await ApplicationAsync(t, requireDpop: true);
        await AssertInvalidProofAsync(await ClientTokenAsync(t, strict, null));

        // Only API administrators change it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.Id}", new { requireDpop = false })).StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_and_mcp_calls_stay_on_the_key_they_were_bound_to()
    {
        var t = await FreshAsync();
        string[] scopes = ["profile:read"];
        var created = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Assistant", kind = "mcp_client", scopes, redirectUris = new[] { Redirect }, publisher = "Acme IT",
        }), HttpStatusCode.Created);
        var clientId = created.GetProperty("clientId").GetString()!;
        await AllowExternalAsync(t, "aip", (clientId, "internal"));
        await JsonAsync(await t["emp"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes }));
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var authorize = await AuthorizeAsync(t, "emp", clientId, scopes, verifier);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tokenUrl = Origin(t) + "/oauth/token";
        var issued = await JsonAsync(await TokenAsync(t, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }, Proof(key, "POST", tokenUrl)));
        Assert.Equal("DPoP", issued.GetProperty("token_type").GetString());
        var token = issued.GetProperty("access_token").GetString()!;
        var refresh = issued.GetProperty("refresh_token").GetString()!;

        // The MCP server takes the token only with a proof of the key.
        var mcp = Origin(t) + "/mcp";
        var rpc = new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } };
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(t, "DPoP", token, Proof(key, "POST", mcp, token), HttpMethod.Post, "/mcp", rpc)).StatusCode);
        await AssertDpopRefusedAsync(await CallAsync(t, "Bearer", token, null, HttpMethod.Post, "/mcp", rpc));

        // A refresh needs a proof of the same key; without one or with another key the grant is refused.
        Dictionary<string, string> Refresh(string value) => new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = value };
        Assert.Equal("invalid_grant", (await JsonAsync(await TokenAsync(t, Refresh(refresh), null), HttpStatusCode.BadRequest)).GetProperty("error").GetString());
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal("invalid_grant", (await JsonAsync(await TokenAsync(t, Refresh(refresh), Proof(other, "POST", tokenUrl)), HttpStatusCode.BadRequest))
            .GetProperty("error").GetString());
        var renewed = await JsonAsync(await TokenAsync(t, Refresh(refresh), Proof(key, "POST", tokenUrl)));
        Assert.Equal("DPoP", renewed.GetProperty("token_type").GetString());
        var next = renewed.GetProperty("access_token").GetString()!;
        Assert.Equal(Thumbprint(key), new JsonWebToken(next).GetPayloadValue<JsonElement>("cnf").GetProperty("jkt").GetString());
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(t, "DPoP", next, Proof(key, "POST", mcp, next), HttpMethod.Post, "/mcp", rpc)).StatusCode);
    }
}
