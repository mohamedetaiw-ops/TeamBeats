using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Access tokens for public API tests: an application app with the App reader role, or a person through the code flow with PKCE.</summary>
internal static class PublicApiTokens
{
    public const string Redirect = "http://127.0.0.1:7777/callback";

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>An application app with the scopes and a role (by default App reader, none when null) at the given scope, and its access token.</summary>
    public static async Task<string> AppAsync(TenantWorld world, TestTenant t, string[] scopes, string scopeType, Guid? scopeId,
        string? role = BuiltInRoles.AppReader)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Reader " + Guid.NewGuid().ToString("N")[..4], kind = "application", scopes }), HttpStatusCode.Created);
        var id = app.GetProperty("id").GetString()!;
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        if (role is not null)
        {
            await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
                new { subjectType = "app", subjectId = Guid.Parse(id), roleKey = role, scopeType, scopeId }), HttpStatusCode.Created);
        }
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.GetProperty("clientId").GetString()!, ["client_secret"] = secret, ["scope"] = string.Join(' ', scopes),
        })));
        return tokens.GetProperty("access_token").GetString()!;
    }

    /// <summary>A delegated app the person consented to, and the person's access token. Write scopes need an administrator's consent first.</summary>
    public static async Task<string> PersonAsync(TenantWorld world, TestTenant t, string user, string[] scopes) =>
        (await DelegatedAsync(world, t, user, scopes, "delegated_public", null)).Token;

    /// <summary>
    /// A delegated app of the kind, registered and consented to by the person, and the person's access token. <paramref name="registered"/>
    /// runs with the client ID before the authorization request, as an MCP client must be on the external AI client policy by then.
    /// </summary>
    public static async Task<(string Token, Guid AppId, string ClientId)> DelegatedAsync(TenantWorld world, TestTenant t, string user, string[] scopes,
        string kind, Func<string, Task>? registered)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Notes " + Guid.NewGuid().ToString("N")[..4], kind, scopes, redirectUris = new[] { Redirect }, publisher = "Acme IT",
        }), HttpStatusCode.Created);
        var clientId = app.GetProperty("clientId").GetString()!;
        var adminScopes = scopes.Where(s => s is "content:draft" or "documents:write" or "requests:submit").ToArray();
        if (adminScopes.Length > 0)
        {
            await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.GetProperty("id").GetString()}/admin-consent", new { scopes = adminScopes }));
        }
        if (registered is not null)
        {
            await registered(clientId);
        }
        await JsonAsync(await t[user].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes }));
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = await AuthorizeAsync(t, user, clientId, scopes, verifier);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, $"{(int)authorize.StatusCode}: {await authorize.Content.ReadAsStringAsync()}");
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), authorize.Headers.Location!.ToString());
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        })));
        return (tokens.GetProperty("access_token").GetString()!, app.GetProperty("id").GetGuid(), clientId);
    }

    /// <summary>The authorization request of the code flow with PKCE, as the person's browser sends it.</summary>
    public static Task<HttpResponseMessage> AuthorizeAsync(TestTenant t, string user, string clientId, string[] scopes, string verifier)
    {
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return t[user].Client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = Redirect, ["scope"] = string.Join(' ', scopes), ["state"] = "s",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        }));
    }

    /// <summary>Lists the MCP clients on the external AI client policy, each with its classification cap, as an AI policy administrator.</summary>
    public static async Task AllowExternalAsync(TestTenant t, string by, params (string ClientId, string Cap)[] clients)
    {
        var current = await JsonAsync(await t[by].Client.GetAsync("/api/v1/agents/policy"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.GetProperty("policy").GetRawText())!;
        body["external"] = new { enabled = true, clients = clients.Select(c => new { clientId = c.ClientId, maxClassification = c.Cap }).ToArray() };
        await JsonAsync(await t[by].Client.PutAsJsonAsync("/api/v1/agents/policy", new { version = current.GetProperty("version").GetInt32(), policy = body }));
    }

    public static Task<HttpResponseMessage> SendAsync(TenantWorld world, TestTenant t, string token, HttpMethod method, string path, object? body = null,
        Action<HttpRequestMessage>? change = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        change?.Invoke(request);
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }
}
