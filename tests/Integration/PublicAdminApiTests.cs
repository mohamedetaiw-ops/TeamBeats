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
/// Spec 035 R3 (T023): admin APIs beside SCIM, read only (FR-012). A person acting through a delegated app reads roles
/// and role assignments with their own rights: the scope needs an administrator's consent, a person without the right
/// gets nothing, a unit administrator sees only their unit, apps' own roles are left out, nothing changes a role, the
/// package must have admin APIs, and another tenant refuses the token.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicAdminApiTests(TenantWorld world)
{
    private const string Assignments = "/api/public/v1/admin/role-assignments";

    private static string Origin(TestTenant t) => $"https://{t.Key}.{ApiFactory.BaseDomain}";

    private async Task<(TestTenant T, Guid Unit, Guid Other)> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("adm-" + Guid.NewGuid().ToString("N")[..6], package);
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "lead", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "new", MembershipTypes.Employee);
        await world.AssignAsync(t, "lead", BuiltInRoles.OrganizationAdministrator, "org_unit", gulf);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return (t, gulf, egypt);
    }

    private static string Proof(ECDsa key, string method, string url, string? accessToken = null)
    {
        var claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString("N"), ["htm"] = method, ["htu"] = url };
        if (accessToken is not null)
        {
            claims["ath"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
        }
        var p = key.ExportParameters(false);
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            TokenType = "dpop+jwt",
            IssuedAt = DateTime.UtcNow,
            Claims = claims,
            AdditionalHeaderClaims = new Dictionary<string, object>
            {
                ["jwk"] = new Dictionary<string, object> { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = Base64UrlEncoder.Encode(p.Q.X), ["y"] = Base64UrlEncoder.Encode(p.Q.Y) },
            },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>A delegated app the tenant administrator consented to for the scopes, and the person's token, bound to the key when one is given.</summary>
    private async Task<string> TokenAsync(TestTenant t, string user, string[] scopes, ECDsa? key)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Access desk " + Guid.NewGuid().ToString("N")[..4], kind = "delegated_public", scopes, redirectUris = new[] { Redirect }, publisher = "Acme IT",
        }), HttpStatusCode.Created);
        var clientId = app.GetProperty("clientId").GetString()!;
        var appId = app.GetProperty("id").GetString()!;
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{appId}/admin-consent", new { scopes }));
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var authorize = await AuthorizeAsync(t, user, clientId, scopes, verifier);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, $"{(int)authorize.StatusCode}: {await authorize.Content.ReadAsStringAsync()}");
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect,
                ["code_verifier"] = verifier,
            }),
        };
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("DPoP", Proof(key, "POST", Origin(t) + "/oauth/token"));
        }
        return (await JsonAsync(await world.Factory.TenantClient(t.Key).SendAsync(request))).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> CallAsync(TestTenant t, string token, ECDsa? key, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue(key is null ? "Bearer" : "DPoP", token);
        if (key is not null)
        {
            request.Headers.TryAddWithoutValidation("DPoP", Proof(key, method.Method, Origin(t) + path.Split('?')[0], token));
        }
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var body = await JsonAsync(response, status);
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task An_administrator_reads_roles_and_assignments_through_an_app_with_their_own_rights()
    {
        var (t, gulf, egypt) = await FreshAsync();
        await world.AssignAsync(t, "new", BuiltInRoles.OrganizationAdministrator, "org_unit", egypt);
        var token = await TokenAsync(t, "admin", [ScopeNames.RolesRead], null);

        var roles = await JsonAsync(await CallAsync(t, token, null, HttpMethod.Get, "/api/public/v1/admin/roles"));
        Assert.Contains(roles.EnumerateArray(), r => r.GetProperty("key").GetString() == BuiltInRoles.OrganizationAdministrator
            && r.GetProperty("builtIn").GetBoolean() && r.GetProperty("permissions").GetArrayLength() > 0);

        var mine = await JsonAsync(await CallAsync(t, token, null, HttpMethod.Get, $"{Assignments}?userId={t["new"].Id}"));
        var held = mine.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(BuiltInRoles.OrganizationAdministrator, held.GetProperty("roleKey").GetString());
        Assert.Equal(egypt, held.GetProperty("scopeId").GetGuid());

        // Pages follow the cursor; apps' own roles are not in the list.
        await PublicApiTokens.AppAsync(world, t, ["content:read"], "tenant", null);
        var all = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var page = await JsonAsync(await CallAsync(t, token, null, HttpMethod.Get, $"{Assignments}?limit=1" + (cursor is null ? "" : $"&cursor={cursor}")));
            all.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("nextCursor").ValueKind == JsonValueKind.String ? page.GetProperty("nextCursor").GetString() : null;
        }
        while (cursor is not null);
        Assert.True(all.Count >= 3);
        Assert.Equal(all.Count, all.Select(a => a.GetProperty("id").GetGuid()).Distinct().Count());
        Assert.DoesNotContain(all, a => a.GetProperty("subjectType").GetString() == "app");
        Assert.Equal("validation.failed", await CodeAsync(await CallAsync(t, token, null, HttpMethod.Get, $"{Assignments}?cursor=not-a-cursor"), HttpStatusCode.BadRequest));

        // A unit administrator sees the assignments of their unit only.
        var lead = await TokenAsync(t, "lead", [ScopeNames.RolesRead], null);
        var seen = (await JsonAsync(await CallAsync(t, lead, null, HttpMethod.Get, Assignments))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(seen, a => a.GetProperty("scopeId").ValueKind == JsonValueKind.String && a.GetProperty("scopeId").GetGuid() == gulf);
        Assert.DoesNotContain(seen, a => a.GetProperty("scopeId").ValueKind == JsonValueKind.String && a.GetProperty("scopeId").GetGuid() == egypt);

        // A token bound with DPoP works the same way.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bound = await TokenAsync(t, "admin", [ScopeNames.RolesRead], key);
        await JsonAsync(await CallAsync(t, bound, key, HttpMethod.Get, "/api/public/v1/admin/roles"));

        // Nothing in the admin API changes a role (FR-012).
        var change = await CallAsync(t, token, null, HttpMethod.Post, Assignments,
            new { subjectType = "user", subjectId = t["new"].Id, roleKey = BuiltInRoles.OrganizationAdministrator, scopeType = "tenant" });
        Assert.Contains(change.StatusCode, new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed });
        Assert.Single((await JsonAsync(await CallAsync(t, token, null, HttpMethod.Get, $"{Assignments}?userId={t["new"].Id}"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Without_the_right_the_consent_or_the_package_the_admin_api_refuses()
    {
        var (t, _, _) = await FreshAsync();

        // A person without the right to view roles gets nothing.
        var employee = await TokenAsync(t, "emp", [ScopeNames.RolesRead], null);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, employee, null, HttpMethod.Get, "/api/public/v1/admin/roles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, employee, null, HttpMethod.Get, Assignments)).StatusCode);

        // Without the scope the token reads nothing here, and an application app cannot hold the scope.
        var other = await PersonAsync(world, t, "admin", ["profile:read"]);
        Assert.Equal("api.insufficient_scope", await CodeAsync(await CallAsync(t, other, null, HttpMethod.Get, "/api/public/v1/admin/roles"), HttpStatusCode.Forbidden));
        var application = await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new { name = "Reviewer", kind = "application", scopes = new[] { ScopeNames.RolesRead } });
        Assert.Equal(HttpStatusCode.BadRequest, application.StatusCode);

        // An employee cannot consent to it alone.
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Reviewer", kind = "delegated_public", scopes = new[] { ScopeNames.RolesRead }, redirectUris = new[] { Redirect },
        }), HttpStatusCode.Created);
        var consent = await t["emp"].Client.PostAsJsonAsync("/api/v1/me/app-consent",
            new { clientId = app.GetProperty("clientId").GetString(), redirectUri = Redirect, scopes = new[] { ScopeNames.RolesRead } });
        Assert.False(consent.IsSuccessStatusCode);

        // Another tenant refuses the token.
        var (g, _, _) = await FreshAsync();
        var token = await TokenAsync(t, "admin", [ScopeNames.RolesRead], null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(g, token, null, HttpMethod.Get, "/api/public/v1/admin/roles")).StatusCode);

        // A package without admin APIs refuses the scope.
        var (plus, _, _) = await FreshAsync("workplace_plus");
        var refused = await plus["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Reviewer", kind = "delegated_public", scopes = new[] { ScopeNames.RolesRead }, redirectUris = new[] { Redirect },
        });
        Assert.False(refused.IsSuccessStatusCode, await refused.Content.ReadAsStringAsync());
    }

    private static class ScopeNames
    {
        public const string RolesRead = "roles:read";
    }
}
