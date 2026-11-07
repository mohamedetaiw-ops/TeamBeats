using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 003 User Story 2 (R1): Entra ID sign-in, JIT, group-to-role mapping, and the T012 to T017 protections.</summary>
[Collection(WorldCollection.Name)]
public sealed class EntraSignInTests(TenantWorld world)
{
    private const string ClientId = "7d0b4bb8-6d1e-4c0e-9a0f-2a8f5f0b1c01";
    private const string ClientSecret = "fictional-test-client-secret";
    private static readonly string EditorsGroup = "1f6c2b8e-0d1a-4e8b-9c7a-5b3d2e1f0a11";

    private FakeIdentityProvider Idp => world.Factory.Idp;

    /// <summary>A fresh tenant with Entra ID configured through the admin API, so tests never share sign-in state.</summary>
    private async Task<SsoTenant> TenantAsync(bool jit = true, object[]? groupRoleMap = null, string package = "workplace_plus")
    {
        var key = "sso-" + Guid.NewGuid().ToString("N")[..8];
        var tenant = await world.CreateTenantAsync(key, package);
        var directory = Guid.NewGuid();
        var secretRef = SecretReferences.For(tenant.Id, "entra-client");
        world.Factory.Secrets.Set(secretRef, ClientSecret);
        var response = await tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/identity-provider", new
        {
            kind = "entra",
            issuer = FakeIdentityProvider.IssuerFor(directory),
            clientId = ClientId,
            secretRef,
            displayName = "Contoso",
            jitEnabled = jit,
            jitDomains = jit ? new[] { $"{key}.example" } : [],
            groupRoleMap = groupRoleMap ?? [],
        });
        await response.ReadAsync<JsonElement>();
        return new SsoTenant(tenant, directory);
    }

    private sealed record SsoTenant(TestTenant Tenant, Guid Directory)
    {
        public string Key => Tenant.Key;
        public Guid Id => Tenant.Id;
        public FakeUser User(string name, string? domain = null) => new(Guid.NewGuid().ToString("D"), $"{name.ToLowerInvariant()}@{domain ?? Key + ".example"}", name);
    }

    private sealed record Started(string AuthorizeUrl, string State, string? Binding);

    private sealed record Landed(HttpResponseMessage Response, string Location, string? SessionToken);

    private async Task<Started> StartAsync(string tenantKey, string? returnTo = null)
    {
        var client = world.Factory.TenantClient(tenantKey);
        var path = "/api/v1/auth/signin/sso" + (returnTo is null ? string.Empty : "?returnTo=" + Uri.EscapeDataString(returnTo));
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        var binding = Cookie(response, "tb_sso");
        var state = location.Contains('?') ? FakeIdentityProvider.ParseQuery(new Uri(location, UriKind.RelativeOrAbsolute).IsAbsoluteUri ? new Uri(location).Query : location[location.IndexOf('?')..]).GetValueOrDefault("state") : null;
        return new Started(location, state ?? string.Empty, binding);
    }

    private async Task<Landed> CallbackAsync(string tenantKey, string query, string? binding, string? sessionToken = null)
    {
        var client = world.Factory.TenantClient(tenantKey);
        var cookies = new List<string>();
        if (binding is not null)
        {
            cookies.Add($"tb_sso={binding}");
        }
        if (sessionToken is not null)
        {
            cookies.Add($"tb_session={sessionToken}");
        }
        if (cookies.Count > 0)
        {
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies));
        }
        var response = await client.GetAsync("/api/v1/auth/signin/sso/callback?" + query);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return new Landed(response, response.Headers.Location!.ToString(), Cookie(response, "tb_session"));
    }

    private async Task<Landed> SignInAsync(SsoTenant t, FakeUser user, string? returnTo = null)
    {
        var started = await StartAsync(t.Key, returnTo);
        var code = Idp.Authenticate(started.AuthorizeUrl, user, ClientSecret);
        return await CallbackAsync(t.Key, $"code={code}&state={started.State}", started.Binding);
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(name + "=", StringComparison.Ordinal))
                .Select(v => v[(name.Length + 1)..]).FirstOrDefault(v => v.Length > 0)
            : null;

    private Task<long> FailuresAsync(Guid tenantId, string reason) =>
        world.Database.CountInTenantAsync(tenantId,
            $"select count(*) from audit.audit_event where action = '{AuditActions.SignInFailed}' and changes->>'reason' = '{reason}'");

    private Task<long> RefusalsAsync(Guid tenantId, string reason) =>
        world.Database.CountInTenantAsync(tenantId,
            $"select count(*) from audit.audit_event where action = '{AuditActions.SignInRefused}' and changes->>'reason' = '{reason}'");

    [Fact]
    public async Task Start_redirects_to_the_configured_directory_with_pkce_state_and_nonce()
    {
        var t = await TenantAsync();
        var started = await StartAsync(t.Key);
        Assert.StartsWith($"{FakeIdentityProvider.Authority}/{t.Directory:D}/oauth2/v2.0/authorize?", started.AuthorizeUrl, StringComparison.Ordinal);
        var query = FakeIdentityProvider.ParseQuery(new Uri(started.AuthorizeUrl).Query);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal($"https://{t.Key}.{ApiFactory.BaseDomain}/api/v1/auth/signin/sso/callback", query["redirect_uri"]);
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.NotNull(started.Binding);
        // Only hashes of the state and browser binding are stored.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from identity.oidc_login_state where state_hash = '{started.State}' or browser_binding_hash = '{started.Binding}'"));
    }

    [Fact]
    public async Task Jit_user_from_an_allowed_domain_signs_in_lands_on_return_path_and_is_linked_on_next_sign_in()
    {
        var t = await TenantAsync();
        var user = t.User("Layla");

        var first = await SignInAsync(t, user, "/news?tab=latest");
        Assert.Equal("/news?tab=latest", first.Location);
        Assert.NotNull(first.SessionToken);
        var me = await world.Factory.TenantClient(t.Key, first.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(user.Email, me.GetProperty("email").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SignInSucceeded);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from identity.user_account where created_via = 'jit' and email = '{user.Email}'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from tenancy.tenant_membership m join identity.user_account u on u.id = m.user_id where u.email = '{user.Email}' and m.membership_type = 'employee'"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from tenancy.org_membership m join identity.user_account u on u.id = m.user_id where u.email = '{user.Email}'"));

        // The email changes at the provider; the object ID does not, so the same user signs in again (spec 003 edge case).
        var second = await SignInAsync(t, user with { Email = "layla.renamed@" + t.Key + ".example" });
        Assert.Equal("/", second.Location);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from identity.external_identity"
            + $" where subject = '{user.ObjectId}'"));
        Assert.Equal(me.GetProperty("id").GetGuid(),
            (await world.Factory.TenantClient(t.Key, second.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Jit_disabled_refuses_unknown_users_and_audits()
    {
        var t = await TenantAsync(jit: false);
        var landed = await SignInAsync(t, t.User("Omar"));
        Assert.Equal("/signin/problem?reason=no_access", landed.Location);
        Assert.Null(landed.SessionToken);
        Assert.Equal(1, await RefusalsAsync(t.Id, "unknown_identity"));
    }

    [Fact]
    public async Task Jit_refuses_a_domain_that_is_not_allowed()
    {
        var t = await TenantAsync();
        var landed = await SignInAsync(t, t.User("Guest", "elsewhere.example"));
        Assert.Equal("/signin/problem?reason=no_access", landed.Location);
        Assert.Equal(1, await RefusalsAsync(t.Id, "jit_domain_not_allowed"));
    }

    [Fact]
    public async Task Appointed_admin_signs_in_through_entra_without_jit()
    {
        var key = "sso-adm-" + Guid.NewGuid().ToString("N")[..6];
        var created = await (await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            key, displayName = key, packageKey = "workplace", defaultLocale = "en", enabledLocales = new[] { "en" },
            timeZone = "Asia/Dubai", admins = new[] { $"owner@{key}.example" },
        })).ReadAsync<JsonElement>();
        var tenantId = created.GetProperty("id").GetGuid();
        // The appointed owner configures Entra ID first through a seeded helper session, then signs in through it.
        var helper = await world.SeedUserAsync(new TestTenant(key, tenantId), "helper", MembershipTypes.Employee);
        await world.Factory.InScopeAsync<int>(tenantId, async sp =>
        {
            await sp.GetRequiredService<TeamBeats.Authorization.Contracts.IAuthorizationProvisioning>()
                .AssignBuiltInRoleAsync(helper.Id, TeamBeats.Authorization.Contracts.BuiltInRoles.TenantAdministrator,
                    TeamBeats.Authorization.Contracts.ScopeType.Tenant, null, null);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return 0;
        });
        var directory = Guid.NewGuid();
        var secretRef = SecretReferences.For(tenantId, "entra");
        world.Factory.Secrets.Set(secretRef, ClientSecret);
        (await helper.Client.PutAsJsonAsync("/api/v1/tenant/identity-provider", new
        {
            kind = "entra", issuer = FakeIdentityProvider.IssuerFor(directory), clientId = ClientId, secretRef,
        })).EnsureSuccessStatusCode();

        var landed = await SignInAsync(new SsoTenant(new TestTenant(key, tenantId), directory),
            new FakeUser(Guid.NewGuid().ToString("D"), $"Owner@{key}.example", "Owner"));
        Assert.NotNull(landed.SessionToken);
        var settings = await world.Factory.TenantClient(key, landed.SessionToken).GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    [Fact]
    public async Task Disabled_membership_sees_no_access_and_is_audited()
    {
        var t = await TenantAsync();
        var user = t.User("Sara");
        var first = await SignInAsync(t, user);
        var userId = (await world.Factory.TenantClient(t.Key, first.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid();
        (await t.Tenant["admin"].Client.PostAsync($"/api/v1/admin/users/{userId}:disable", null)).EnsureSuccessStatusCode();

        var again = await SignInAsync(t, user);
        Assert.Equal("/signin/problem?reason=no_access", again.Location);
        Assert.Null(again.SessionToken);
        Assert.Equal(1, await RefusalsAsync(t.Id, "no_access"));
    }

    [Fact]
    public async Task Suspended_tenant_shows_the_suspended_page()
    {
        var t = await TenantAsync();
        var started = await StartAsync(t.Key);
        var code = Idp.Authenticate(started.AuthorizeUrl, t.User("Late"), ClientSecret);
        (await world.Platform.PostAsJsonAsync($"/api/v1/platform/tenants/{t.Id}:suspend", new { reason = "test" })).EnsureSuccessStatusCode();

        var landed = await CallbackAsync(t.Key, $"code={code}&state={started.State}", started.Binding);
        Assert.Equal("/signin/problem?reason=tenant_suspended", landed.Location);
        Assert.Equal("/signin/problem?reason=tenant_suspended", (await StartAsync(t.Key)).AuthorizeUrl);
    }

    public static TheoryData<string, Func<FakeUser, FakeUser>> BadTokens() => new()
    {
        { "wrong_issuer", u => u with { IssuerOverride = FakeIdentityProvider.IssuerFor(Guid.NewGuid()) } },
        { "wrong_audience", u => u with { AudienceOverride = Guid.NewGuid().ToString("D") } },
        { "expired", u => u with { IssuedAgo = TimeSpan.FromHours(2), Lifetime = TimeSpan.FromHours(1) } },
        { "bad_signature", u => u with { SignWithForeignKey = true } },
        { "nonce_missing", u => u with { OmitNonce = true } },
        { "nonce_mismatch", u => u with { NonceOverride = "another-sign-in" } },
        { "subject_missing", u => u with { OmitObjectId = true } },
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task Invalid_id_tokens_are_refused_and_audited(string reason, Func<FakeUser, FakeUser> spoil)
    {
        var t = await TenantAsync();
        var landed = await SignInAsync(t, spoil(t.User("Mallory")));
        Assert.Equal("/signin/problem?reason=failed", landed.Location);
        Assert.Null(landed.SessionToken);
        Assert.Equal(1, await FailuresAsync(t.Id, reason));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from identity.user_account where email like 'mallory@%'"));
        // No token content is stored in audit.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where changes::text like '%eyJ%'"));
    }

    [Fact]
    public async Task Token_within_five_minutes_of_clock_skew_is_accepted()
    {
        var t = await TenantAsync();
        var landed = await SignInAsync(t, t.User("Skew") with { IssuedAgo = TimeSpan.FromMinutes(63), Lifetime = TimeSpan.FromHours(1) });
        Assert.NotNull(landed.SessionToken);
    }

    [Fact]
    public async Task Replayed_callback_is_refused()
    {
        var t = await TenantAsync();
        var started = await StartAsync(t.Key);
        var code = Idp.Authenticate(started.AuthorizeUrl, t.User("Replay"), ClientSecret);
        var first = await CallbackAsync(t.Key, $"code={code}&state={started.State}", started.Binding);
        Assert.NotNull(first.SessionToken);

        var replay = await CallbackAsync(t.Key, $"code={code}&state={started.State}", started.Binding);
        Assert.Equal("/signin/problem?reason=failed", replay.Location);
        Assert.Null(replay.SessionToken);
        Assert.Equal(1, await FailuresAsync(t.Id, "state_invalid"));
    }

    [Fact]
    public async Task Callback_from_another_browser_is_refused_login_csrf()
    {
        var t = await TenantAsync();
        // The attacker starts a sign-in and sends the victim the callback link; the victim's browser lacks the binding cookie.
        var attacker = await StartAsync(t.Key);
        var code = Idp.Authenticate(attacker.AuthorizeUrl, t.User("Attacker"), ClientSecret);
        var victim = await StartAsync(t.Key);

        var landed = await CallbackAsync(t.Key, $"code={code}&state={attacker.State}", victim.Binding);
        Assert.Equal("/signin/problem?reason=failed", landed.Location);
        Assert.Null(landed.SessionToken);
        var noCookie = await CallbackAsync(t.Key, $"code=x&state={victim.State}", null);
        Assert.Null(noCookie.SessionToken);
        Assert.Equal(2, await FailuresAsync(t.Id, "browser_binding_mismatch"));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/path")]
    [InlineData("/\\evil.example")]
    [InlineData("/api/v1/tenant/settings")]
    [InlineData("javascript:alert(1)")]
    public async Task Return_path_outside_the_app_lands_on_home(string returnTo)
    {
        var t = await TenantAsync();
        var landed = await SignInAsync(t, t.User("Redirect" + Math.Abs(returnTo.GetHashCode()) % 1000), returnTo);
        Assert.Equal("/", landed.Location);
    }

    [Fact]
    public async Task State_from_tenant_a_is_unknown_on_tenant_b()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var started = await StartAsync(a.Key);
        var code = Idp.Authenticate(started.AuthorizeUrl, a.User("Cross"), ClientSecret);

        var landed = await CallbackAsync(b.Key, $"code={code}&state={started.State}", started.Binding);
        Assert.Equal("/signin/problem?reason=failed", landed.Location);
        Assert.Null(landed.SessionToken);
        Assert.Equal(1, await FailuresAsync(b.Id, "state_invalid"));
    }

    [Fact]
    public async Task Token_from_tenant_a_directory_is_refused_for_tenant_b()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var landed = await SignInAsync(b, b.User("Borrowed") with { IssuerOverride = FakeIdentityProvider.IssuerFor(a.Directory) });
        Assert.Null(landed.SessionToken);
        Assert.Equal(1, await FailuresAsync(b.Id, "wrong_issuer"));
    }

    [Fact]
    public async Task Same_object_id_in_two_tenants_creates_two_users_and_sessions_do_not_cross()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var oid = Guid.NewGuid().ToString("D");
        var inA = await SignInAsync(a, new FakeUser(oid, $"twin@{a.Key}.example", "Twin"));
        var inB = await SignInAsync(b, new FakeUser(oid, $"twin@{b.Key}.example", "Twin"));
        var idA = (await world.Factory.TenantClient(a.Key, inA.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid();
        var idB = (await world.Factory.TenantClient(b.Key, inB.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid();
        Assert.NotEqual(idA, idB);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(b.Key, inA.SessionToken).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Group_mapping_grants_roles_and_removes_them_when_the_group_is_left()
    {
        var t = await TenantAsync(groupRoleMap:
        [
            new { groupId = EditorsGroup, roleKey = "content_editor", scopeType = "tenant", scopeId = (Guid?)null },
        ]);
        var user = t.User("Editor") with { Groups = [EditorsGroup, Guid.NewGuid().ToString("D")] };

        var first = await SignInAsync(t, user);
        var access = await world.Factory.TenantClient(t.Key, first.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        Assert.Contains("content_editor", access.ToString(), StringComparison.Ordinal);

        var second = await SignInAsync(t, user with { Groups = [] });
        var after = await world.Factory.TenantClient(t.Key, second.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        Assert.DoesNotContain("content_editor", after.ToString(), StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RoleRevoked);
    }

    [Fact]
    public async Task Group_mapping_never_removes_a_role_an_admin_assigned()
    {
        var t = await TenantAsync(groupRoleMap:
        [
            new { groupId = EditorsGroup, roleKey = "content_editor", scopeType = "tenant", scopeId = (Guid?)null },
        ]);
        var user = t.User("Kept");
        var first = await SignInAsync(t, user);
        var userId = (await world.Factory.TenantClient(t.Key, first.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("id").GetGuid();
        (await t.Tenant["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "user", subjectId = userId, roleKey = "content_editor", scopeType = "tenant" })).EnsureSuccessStatusCode();

        await SignInAsync(t, user with { Groups = [EditorsGroup] });
        var last = await SignInAsync(t, user with { Groups = [] });
        var access = await world.Factory.TenantClient(t.Key, last.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        Assert.Contains("content_editor", access.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Group_overage_leaves_mapped_roles_unchanged()
    {
        var t = await TenantAsync(groupRoleMap:
        [
            new { groupId = EditorsGroup, roleKey = "content_editor", scopeType = "tenant", scopeId = (Guid?)null },
        ]);
        var user = t.User("Overage") with { Groups = [EditorsGroup] };
        await SignInAsync(t, user);
        var landed = await SignInAsync(t, user with { Groups = [], GroupOverage = true });
        var access = await world.Factory.TenantClient(t.Key, landed.SessionToken).GetFromJsonAsync<JsonElement>("/api/v1/me/access");
        Assert.Contains("content_editor", access.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_provider_shows_the_unavailable_page_and_never_falls_back()
    {
        var t = await TenantAsync();
        var started = await StartAsync(t.Key);
        var code = Idp.Authenticate(started.AuthorizeUrl, t.User("Down"), ClientSecret);
        Idp.Unavailable = true;
        try
        {
            var landed = await CallbackAsync(t.Key, $"code={code}&state={started.State}", started.Binding);
            Assert.Equal("/signin/problem?reason=idp_unavailable", landed.Location);
            Assert.Null(landed.SessionToken);
            var other = await TenantAsync();
            Assert.Equal("/signin/problem?reason=idp_unavailable", (await StartAsync(other.Key)).AuthorizeUrl);
        }
        finally
        {
            Idp.Unavailable = false;
        }
    }

    [Fact]
    public async Task Provider_error_at_the_callback_is_refused()
    {
        var t = await TenantAsync();
        var started = await StartAsync(t.Key);
        var landed = await CallbackAsync(t.Key, $"error=access_denied&state={started.State}", started.Binding);
        Assert.Equal("/signin/problem?reason=failed", landed.Location);
        Assert.Equal(1, await FailuresAsync(t.Id, "idp_error"));
    }

    [Fact]
    public async Task Tenant_without_a_provider_shows_not_configured_and_options_say_so()
    {
        var tenant = await world.CreateTenantAsync("sso-none-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var client = world.Factory.TenantClient(tenant.Key);
        var options = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/signin/options");
        Assert.False(options.GetProperty("sso").GetBoolean());
        var start = await client.GetAsync("/api/v1/auth/signin/sso");
        Assert.Equal("/signin/problem?reason=not_configured", start.Headers.Location!.ToString());

        var configured = await TenantAsync();
        var shown = await world.Factory.TenantClient(configured.Key).GetFromJsonAsync<JsonElement>("/api/v1/auth/signin/options");
        Assert.True(shown.GetProperty("sso").GetBoolean());
        Assert.Equal("Contoso", shown.GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Admin_api_needs_identity_config_permission_and_never_returns_a_secret_value()
    {
        var t = await TenantAsync();
        var employee = await world.SeedUserAsync(t.Tenant, "plain", MembershipTypes.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.GetAsync("/api/v1/tenant/identity-provider")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.Client.PutAsJsonAsync("/api/v1/tenant/identity-provider", new { kind = "entra" })).StatusCode);

        var body = await t.Tenant["admin"].Client.GetStringAsync("/api/v1/tenant/identity-provider");
        Assert.DoesNotContain(ClientSecret, body, StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.IdpConfigChanged);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from audit.audit_event where changes::text like '%{ClientSecret}%'"));
    }

    public static TheoryData<string, object> InvalidConfigs() => new()
    {
        { "issuer", new { kind = "entra", issuer = "https://login.microsoftonline.com/common/v2.0", clientId = ClientId } },
        { "issuer", new { kind = "entra", issuer = "https://evil.example/" + Guid.NewGuid() + "/v2.0", clientId = ClientId } },
        { "kind", new { kind = "saml", issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()), clientId = ClientId } },
        { "clientId", new { kind = "entra", issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()), clientId = "not-a-guid" } },
        { "jitDomains", new { kind = "entra", issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()), clientId = ClientId, jitEnabled = true } },
        { "groupRoleMap", new { kind = "entra", issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()), clientId = ClientId,
            groupRoleMap = new[] { new { groupId = EditorsGroup, roleKey = "platform_operator", scopeType = "tenant" } } } },
    };

    [Theory]
    [MemberData(nameof(InvalidConfigs))]
    public async Task Invalid_provider_configuration_is_rejected(string field, object request)
    {
        var tenant = await world.CreateTenantAsync("sso-bad-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var json = JsonSerializer.SerializeToElement(request);
        var payload = json.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        payload["secretRef"] = SecretReferences.For(tenant.Id, "entra");
        var response = await tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/identity-provider", payload);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
    }

    [Fact]
    public async Task Secret_reference_of_another_tenant_is_rejected()
    {
        var a = await TenantAsync();
        var tenant = await world.CreateTenantAsync("sso-ref-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var response = await tenant["admin"].Client.PutAsJsonAsync("/api/v1/tenant/identity-provider", new
        {
            kind = "entra", issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()), clientId = ClientId,
            secretRef = SecretReferences.For(a.Id, "entra-client"),
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("secretRef", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
    /// <summary>Spec 026 T008 and T009 (US2 scenario 1): MFA can be required only once a sign-in has shown it, then a sign-in without it is refused.</summary>
    [Fact]
    public async Task Required_MFA_refuses_a_sign_in_without_the_claim_after_one_has_shown_it()
    {
        var t = await TenantAsync();
        var admin = t.Tenant["admin"].Client;
        var before = await admin.PutAsJsonAsync("/api/v1/security/policy", new { requireMfa = true });
        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);
        Assert.Contains("validation.security.mfa_not_seen", await before.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A password-only sign-in works while MFA is not required, and does not count as seeing the claim.
        var passwordOnly = await SignInAsync(t, t.User("Pwd"));
        Assert.NotNull(passwordOnly.SessionToken);
        Assert.False((await (await admin.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>()).GetProperty("mfaClaimSeen").GetBoolean());

        var withMfa = await SignInAsync(t, t.User("Strong") with { Amr = ["pwd", "mfa"] });
        Assert.NotNull(withMfa.SessionToken);
        Assert.True((await (await admin.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>()).GetProperty("mfaClaimSeen").GetBoolean());
        (await admin.PutAsJsonAsync("/api/v1/security/policy", new { requireMfa = true })).EnsureSuccessStatusCode();

        var refused = await SignInAsync(t, t.User("Weak"));
        Assert.Null(refused.SessionToken);
        Assert.Equal("/signin/problem?reason=mfa_required", refused.Location);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from identity.user_account where email like 'weak@%'"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityMfaMissing);

        var allowed = await SignInAsync(t, t.User("Second") with { Amr = ["pwd", "mfa"] });
        Assert.NotNull(allowed.SessionToken);

        // Another tenant's requirement and claim history are its own.
        var other = await TenantAsync();
        Assert.NotNull((await SignInAsync(other, other.User("Elsewhere"))).SessionToken);
        Assert.False((await (await other.Tenant["admin"].Client.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>()).GetProperty("mfaClaimSeen").GetBoolean());
    }
    /// <summary>Spec 026 T008 and T010 (US2 scenario 2, SC-004): every sensitive action needs a sign-in inside the step-up window.</summary>
    [Fact]
    public async Task Sensitive_actions_need_a_recent_sign_in_and_step_up_asks_the_provider_for_a_fresh_one()
    {
        var t = await TenantAsync();
        var user = t.User("Stepper") with { Groups = [] };
        var first = await SignInAsync(t, user);
        var userId = (await (await world.Factory.TenantClient(t.Key, first.SessionToken).GetAsync("/api/v1/me")).ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        t.Tenant.Users["stepper"] = new TestUser("stepper", userId, user.Email, first.SessionToken!, world.Factory.TenantClient(t.Key, first.SessionToken));
        await world.AssignAsync(t.Tenant, "stepper", "tenant_administrator", "tenant", null);

        var client = world.Factory.TenantClient(t.Key, first.SessionToken);
        var sensitive = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Put, "/api/v1/security/policy", new { idleMinutes = 40 }),
            (HttpMethod.Post, "/api/v1/authz/assignments", new { subjectType = "user", subjectId = t.Tenant["admin"].Id, roleKey = "content_publisher", scopeType = "tenant" }),
            (HttpMethod.Delete, $"/api/v1/authz/assignments/{Guid.NewGuid()}", null),
            (HttpMethod.Put, "/api/v1/tenant/identity-provider", new { kind = "entra" }),
            (HttpMethod.Put, "/api/v1/tenant/directory", new { }),
            (HttpMethod.Post, "/api/v1/audit/exports", new { format = "csv" }),
            (HttpMethod.Post, "/api/v1/integrations/connections", new { kind = "microsoft365" }),
            (HttpMethod.Post, "/api/v1/tenant/admins", new { email = "someone@" + t.Key + ".example" }),
        };
        Task<HttpResponseMessage> SendAsync(HttpClient c, (HttpMethod Method, string Path, object? Body) a) =>
            c.SendAsync(new HttpRequestMessage(a.Method, a.Path) { Content = a.Body is null ? null : JsonContent.Create(a.Body) });

        // Fresh from sign-in: none of them asks for step-up (they may fail validation, which is fine here).
        foreach (var action in sensitive)
        {
            var response = await SendAsync(client, action);
            Assert.NotEqual("security.step_up_required", await CodeAsync(response));
        }

        // Eleven minutes later, past the default 10 minute window: every one of them asks for step-up and changes nothing.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update identity.user_session set authenticated_at = now() - interval '11 minutes' where user_id = @u",
            ("u", userId));
        var policyBefore = await (await client.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>();
        foreach (var action in sensitive)
        {
            var response = await SendAsync(client, action);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("security.step_up_required", body.GetProperty("code").GetString());
            Assert.Equal("/api/v1/auth/signin/sso?stepUp=true", body.GetProperty("stepUpUrl").GetString());
        }
        Assert.Equal(policyBefore.GetProperty("idleMinutes").GetInt32(),
            (await (await client.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>()).GetProperty("idleMinutes").GetInt32());
        // Reading is not sensitive.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/authz/roles")).StatusCode);

        // Step-up: the provider is asked for a fresh sign-in, and an old provider session is not accepted.
        var started = await StartStepUpAsync(t, first.SessionToken!, "/admin/security");
        var query = FakeIdentityProvider.ParseQuery(new Uri(started.AuthorizeUrl).Query);
        Assert.Equal("login", query["prompt"]);
        Assert.Equal("0", query["max_age"]);
        var stale = await CallbackAsync(t.Key, $"code={Idp.Authenticate(started.AuthorizeUrl, user with { AuthTimeAgo = TimeSpan.FromMinutes(30) }, ClientSecret)}&state={started.State}",
            started.Binding, first.SessionToken);
        Assert.Null(stale.SessionToken);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityStepUpFailed);

        var again = await StartStepUpAsync(t, first.SessionToken!, "/admin/security");
        var fresh = await CallbackAsync(t.Key, $"code={Idp.Authenticate(again.AuthorizeUrl, user, ClientSecret)}&state={again.State}", again.Binding, first.SessionToken);
        Assert.Equal("/admin/security", fresh.Location);
        Assert.NotNull(fresh.SessionToken);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityStepUpCompleted, userId.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        var stepped = world.Factory.TenantClient(t.Key, fresh.SessionToken);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(stepped, sensitive[0])).StatusCode);

        // A tenant can shorten the window.
        (await stepped.PutAsJsonAsync("/api/v1/security/policy", new { stepUpMinutes = 1 })).EnsureSuccessStatusCode();
        await world.Database.OwnerExecInTenantAsync(t.Id, "update identity.user_session set authenticated_at = now() - interval '2 minutes' where user_id = @u",
            ("u", userId));
        Assert.Equal("security.step_up_required", await CodeAsync(await SendAsync(stepped, sensitive[0])));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return text.StartsWith('{') && JsonDocument.Parse(text).RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    private async Task<Started> StartStepUpAsync(SsoTenant t, string sessionToken, string returnTo)
    {
        var client = world.Factory.TenantClient(t.Key, sessionToken);
        var response = await client.GetAsync("/api/v1/auth/signin/sso?stepUp=true&returnTo=" + Uri.EscapeDataString(returnTo));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        return new Started(location, FakeIdentityProvider.ParseQuery(new Uri(location).Query)["state"], Cookie(response, "tb_sso"));
    }
}
