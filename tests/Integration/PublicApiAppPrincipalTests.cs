using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 phase 2 (T005, T006): application apps as principals and their credentials. An application app holds
/// only roles marked assignable to apps, and only at the scopes it was given, so an app with a role at Acme Gulf reads
/// nothing of Acme Egypt or of the group above. Credentials are a secret or a public key for private key JWT; a client
/// assertion is good once, for this tenant only, and API administrators are told before a credential expires.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiAppPrincipalTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ap-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        await world.CreateUnitAsync(t, "GulfSales", gulf, "department");
        return t;
    }

    private static string Origin(TestTenant t) => $"https://{t.Key}.{ApiFactory.BaseDomain}";

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<(string Id, string ClientId)> AppAsync(TestTenant t, string kind = "application", string[]? scopes = null)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Sync " + kind, kind, scopes = scopes ?? ["content:read"],
            redirectUris = kind == "application" ? Array.Empty<string>() : ["http://127.0.0.1:7777/callback"],
        }), HttpStatusCode.Created);
        return (app.GetProperty("id").GetString()!, app.GetProperty("clientId").GetString()!);
    }

    private static Dictionary<string, string> PublicJwk(ECDsa key)
    {
        var p = key.ExportParameters(false);
        return new() { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = Base64UrlEncoder.Encode(p.Q.X), ["y"] = Base64UrlEncoder.Encode(p.Q.Y) };
    }

    private static string Assertion(ECDsa key, string clientId, string audience, TimeSpan? lifetime = null, string? jti = null, string? subject = null)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(2)),
            Claims = new Dictionary<string, object> { ["sub"] = subject ?? clientId, ["jti"] = jti ?? Guid.NewGuid().ToString("N") },
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256),
        });
    }

    private Task<HttpResponseMessage> TokenAsync(TestTenant t, Dictionary<string, string> form) =>
        world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(form));

    private Task<HttpResponseMessage> AssertionTokenAsync(TestTenant t, string clientId, string assertion) => TokenAsync(t, new()
    {
        ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["scope"] = "content:read",
        ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", ["client_assertion"] = assertion,
    });

    private static async Task AssertInvalidClientAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("invalid_client", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private Task<HttpResponseMessage> AssignToAppAsync(TestTenant t, string appId, string role, string scopeType, Guid? scopeId, object? condition = null) =>
        t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments", new { subjectType = "app", subjectId = Guid.Parse(appId), roleKey = role, scopeType, scopeId, condition });

    /// <summary>A decision taken as the app itself, as a public API call with its token takes it.</summary>
    private async Task<bool> AppMayAsync(TestTenant t, string appId, string permission, AuthzTarget target)
    {
        await using var scope = world.Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        context.SetTenant(t.Id);
        context.SetActor(ActorType.App, Guid.Parse(appId));
        return (await scope.ServiceProvider.GetRequiredService<IAuthorizationService>().CheckAsync(permission, target)).Allowed;
    }

    [Fact]
    public async Task A_public_key_signs_client_assertions_that_are_good_once_and_for_this_tenant_only()
    {
        var t = await FreshAsync();
        var (id, clientId) = await AppAsync(t);
        var metadata = await JsonAsync(await world.Factory.TenantClient(t.Key).GetAsync("/.well-known/oauth-authorization-server/oauth"));
        Assert.Contains("private_key_jwt", metadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray().Select(e => e.GetString()));

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentials = $"/api/v1/api-access/apps/{id}/credentials";
        // Only the public part is registered: a key with its private part, a short RSA key or a symmetric key is refused.
        var withPrivate = new Dictionary<string, string>(PublicJwk(key)) { ["d"] = Base64UrlEncoder.Encode(key.ExportParameters(true).D) };
        Assert.Contains("validation.api.public_key_private", await (await t["admin"].Client.PostAsJsonAsync(credentials, new { kind = "public_key", publicKey = withPrivate }))
            .Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var shortRsa = RSA.Create(1024);
        var rsa = shortRsa.ExportParameters(false);
        Assert.Contains("validation.api.public_key_invalid", await (await t["admin"].Client.PostAsJsonAsync(credentials, new
        {
            kind = "public_key", publicKey = new { kty = "RSA", n = Base64UrlEncoder.Encode(rsa.Modulus), e = Base64UrlEncoder.Encode(rsa.Exponent) },
        })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("validation.api.public_key_invalid", await (await t["admin"].Client.PostAsJsonAsync(credentials, new { kind = "public_key", publicKey = new { kty = "oct" } }))
            .Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var issued = await JsonAsync(await t["admin"].Client.PostAsJsonAsync(credentials, new { kind = "public_key", publicKey = PublicJwk(key) }), HttpStatusCode.Created);
        Assert.Equal(JsonValueKind.Null, issued.GetProperty("secret").ValueKind);
        var credential = issued.GetProperty("credential").GetProperty("id").GetString()!;
        Assert.Equal("public_key", issued.GetProperty("credential").GetProperty("kind").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiCredentialAdded, id);
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync(credentials, new { kind = "public_key", publicKey = PublicJwk(key) })).StatusCode);

        var issuer = Origin(t) + "/oauth";
        var assertion = Assertion(key, clientId, issuer + "/token");
        var tokens = await JsonAsync(await AssertionTokenAsync(t, clientId, assertion));
        Assert.False(tokens.TryGetProperty("refresh_token", out _));
        var claims = new JsonWebToken(tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(credential, claims.GetClaim("tb_family").Value);
        // The issuer itself is an accepted audience too (RFC 7523 section 3).
        await JsonAsync(await AssertionTokenAsync(t, clientId, Assertion(key, clientId, issuer)));

        // Used once only, then refused: a replayed assertion, one for another audience or tenant, one that lives too
        // long, one about another client, one signed by another key, and a secret sent beside an assertion.
        await AssertInvalidClientAsync(await AssertionTokenAsync(t, clientId, assertion));
        await AssertInvalidClientAsync(await AssertionTokenAsync(t, clientId, Assertion(key, clientId, Origin(world.B) + "/oauth/token")));
        await AssertInvalidClientAsync(await AssertionTokenAsync(t, clientId, Assertion(key, clientId, issuer + "/token", TimeSpan.FromHours(1))));
        await AssertInvalidClientAsync(await AssertionTokenAsync(t, clientId, Assertion(key, clientId, issuer + "/token", subject: "someone-else")));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await AssertInvalidClientAsync(await AssertionTokenAsync(t, clientId, Assertion(other, clientId, issuer + "/token")));
        // Two ways of authenticating in one request is a malformed request (RFC 6749 section 2.3).
        var both = await TokenAsync(t, new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = "tbs_x",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", ["client_assertion"] = Assertion(key, clientId, issuer + "/token"),
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Contains("invalid_request", await both.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // The same app and assertion on another tenant's host: the client is not known there.
        await AssertInvalidClientAsync(await AssertionTokenAsync(world.B, clientId, Assertion(key, clientId, Origin(world.B) + "/oauth/token")));

        // A second credential overlaps for rotation; a third is refused until one is revoked.
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync(credentials, new { }), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync(credentials, new { kind = "public_key", publicKey = PublicJwk(other) })).StatusCode);

        // A revoked key signs nothing any more, and its tokens are refused at once.
        await JsonAsync(await t["admin"].Client.PostAsync($"{credentials}/{credential}:revoke", null));
        await AssertInvalidClientAsync(await AssertionTokenAsync(t, clientId, Assertion(key, clientId, issuer + "/token")));
        var client = world.Factory.TenantClient(t.Key);
        using var call = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/me");
        call.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(call)).StatusCode);

        // A public client has no credential at all.
        var (publicId, publicClient) = await AppAsync(t, "delegated_public", ["profile:read"]);
        Assert.Contains("validation.api.credential_not_for_kind", await (await t["admin"].Client.PostAsJsonAsync(
            $"/api/v1/api-access/apps/{publicId}/credentials", new { kind = "public_key", publicKey = PublicJwk(key) })).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotEmpty(publicClient);
    }

    [Fact]
    public async Task An_app_reads_only_within_the_roles_and_scopes_it_was_given()
    {
        var t = await FreshAsync();
        var (id, _) = await AppAsync(t);
        var gulf = t.Unit("Gulf");

        // Before any role, the app may read nothing, not even what every employee may.
        Assert.False(await AppMayAsync(t, id, Permissions.OrgUnitView, AuthzTarget.OrgUnit(gulf)));
        Assert.False(await AppMayAsync(t, id, Permissions.PageView, AuthzTarget.Tenant));

        // Administration roles are not for apps (FR-011), and neither is a condition on people's attributes.
        Assert.Contains("validation.role.not_assignable_to_apps",
            await (await AssignToAppAsync(t, id, BuiltInRoles.TenantAdministrator, "tenant", null)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("validation.role.not_assignable_to_apps",
            await (await AssignToAppAsync(t, id, BuiltInRoles.ContentEditor, "org_unit", gulf)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("validation.role.app_condition", await (await AssignToAppAsync(t, id, BuiltInRoles.AppReader, "org_unit", gulf,
            new { attribute = "job_grade", @operator = "gte", value = 7 })).Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A delegated app acts with the person's own access, and an app of another tenant is not known here.
        var (delegatedId, _) = await AppAsync(t, "delegated_public", ["profile:read"]);
        Assert.Contains("validation.role.app_not_found",
            await (await AssignToAppAsync(t, delegatedId, BuiltInRoles.AppReader, "org_unit", gulf)).Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var foreign = await JsonAsync(await world.B["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Foreign", kind = "application", scopes = new[] { "content:read" } }), HttpStatusCode.Created);
        Assert.Contains("validation.role.app_not_found", await (await AssignToAppAsync(t, foreign.GetProperty("id").GetString()!, BuiltInRoles.AppReader, "org_unit", gulf))
            .Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The App reader role at Acme Gulf: Gulf only, not Egypt, not the group above, not the department below.
        var assigned = await JsonAsync(await AssignToAppAsync(t, id, BuiltInRoles.AppReader, "org_unit", gulf), HttpStatusCode.Created);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiRoleAssignedToApp, assigned.GetProperty("id").GetString()!);
        Assert.True(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(gulf)));
        Assert.False(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(t.Unit("Egypt"))));
        Assert.False(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(t.Unit("Group"))));
        Assert.False(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(t.Unit("GulfSales"))));
        Assert.False(await AppMayAsync(t, id, Permissions.ContentEdit, AuthzTarget.OrgUnit(gulf)));
        Assert.False(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.Tenant));
        // The same app ID in another tenant holds nothing there.
        Assert.False(await AppMayAsync(world.B, id, Permissions.ContentView, AuthzTarget.OrgUnit(world.B.Unit("Gulf"))));
        var listed = await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/authz/assignments?appId={id}"));
        Assert.Equal([BuiltInRoles.AppReader], listed.EnumerateArray().Select(a => a.GetProperty("roleKey").GetString()));
        Assert.All(listed.EnumerateArray(), a => Assert.Equal("app", a.GetProperty("subjectType").GetString()));

        // At subtree scope the department below is covered too.
        var subtree = await JsonAsync(await AssignToAppAsync(t, id, BuiltInRoles.AppReader, "org_subtree", gulf), HttpStatusCode.Created);
        Assert.True(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(t.Unit("GulfSales"))));

        // A removed role is gone on the next decision, well within 60 seconds (FR-007).
        (await t["admin"].Client.DeleteAsync($"/api/v1/authz/assignments/{subtree.GetProperty("id").GetString()}")).EnsureSuccessStatusCode();
        Assert.False(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(t.Unit("GulfSales"))));
        Assert.True(await AppMayAsync(t, id, Permissions.ContentView, AuthzTarget.OrgUnit(gulf)));
    }

    [Fact]
    public async Task A_role_apps_may_hold_never_holds_an_administration_permission()
    {
        var t = await FreshAsync();
        var roles = await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/authz/roles"));
        var forApps = roles.EnumerateArray().Where(r => r.GetProperty("assignableToApps").GetBoolean()).ToList();
        Assert.Equal([BuiltInRoles.AppReader], forApps.Select(r => r.GetProperty("key").GetString()));

        var refused = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "sync_admin", permissions = new[] { Permissions.ContentView, Permissions.ApiManage }, delegable = false, assignableToApps = true,
        });
        Assert.Contains("validation.role.app_excluded_permission", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var made = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "news_reader", permissions = new[] { Permissions.ContentView }, delegable = false, assignableToApps = true,
        }), HttpStatusCode.Created);
        Assert.True(made.GetProperty("assignableToApps").GetBoolean());

        // The database refuses it too, whichever way it is tried.
        var error = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IDbSession>().ExecAsync(
                "insert into authz.role_permission (tenant_id, role_id, permission_key) select tenant_id, id, 'authz.roles_assign' from authz.role where key = 'news_reader'",
                null, CancellationToken.None);
            return 0;
        }));
        Assert.Equal("23514", error.SqlState);
        error = await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(() => world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IDbSession>().ExecAsync("update authz.role set assignable_to_apps = true where key = 'security_officer'", null, CancellationToken.None);
            return 0;
        }));
        Assert.Equal("23514", error.SqlState);
    }

    [Fact]
    public async Task Api_administrators_are_told_once_before_a_credential_expires()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "integrator", MembershipTypes.Employee);
        await world.AssignAsync(t, "integrator", BuiltInRoles.IntegrationAdministrator, "tenant", null);
        var (id, _) = await AppAsync(t);
        var soon = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("credential").GetProperty("id").GetString()!;
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created);
        var now = DateTimeOffset.UtcNow;
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await session.ExecAsync("update publicapi.app_credential set expires_at = @at where id = @id", new { at = now.AddDays(10), id = Guid.Parse(soon) }, CancellationToken.None);
            await session.CommitAsync();
            return 0;
        });

        async Task<int> RunAsync() => (await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var run = await sp.GetRequiredService<PublicApiMaintenance>().RunDueAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        })).Alerted;

        Assert.Equal(1, await RunAsync());
        Assert.Equal(0, await RunAsync());
        long Notified(string user) => world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k and link = @l",
            ("u", t[user].Id), ("k", PublicApiMaintenance.ExpiringTemplate), ("l", $"/admin/api/apps/{id}")).GetAwaiter().GetResult();
        Assert.Equal(1, Notified("integrator"));
        Assert.Equal(1, Notified("admin"));
        // Other tenants hear nothing of it.
        Assert.Equal(0, await world.Database.CountInTenantAsync(world.B.Id, "select count(*) from notifications.notification where template_key = @k",
            ("k", PublicApiMaintenance.ExpiringTemplate)));
    }
}
