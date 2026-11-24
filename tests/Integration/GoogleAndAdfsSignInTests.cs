using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 003 R2: Google Workspace sign-in (US4) and AD FS sign-in (US5) over OIDC on the Entra ID flow. Google lets in
/// only accounts of the tenant's hosted domains; AD FS links on objectGUID to the user that the tenant's own AD DS
/// connection synced, and a token without it is refused with the reason kept for the admin. Tenant boundary included.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class GoogleAndAdfsSignInTests(TenantWorld world)
{
    private const string GoogleClient = "123456789012-abcdef0123456789.apps.googleusercontent.com";
    private const string AdfsClient = "0d8c4b1e-1d2f-4a3b-8c9d-0e1f2a3b4c5d";
    private const string Secret = "fictional-test-client-secret";
    private const string Provider = "/api/v1/tenant/identity-provider";

    private FakeIdentityProvider Idp => world.Factory.Idp;

    private async Task<HttpResponseMessage> SaveAsync(TestTenant t, object body) => await t["admin"].Client.PutAsJsonAsync(Provider, body);

    private string SecretRef(TestTenant t)
    {
        var secretRef = SecretReferences.For(t.Id, "sso-client");
        world.Factory.Secrets.Set(secretRef, Secret);
        return secretRef;
    }

    private async Task<TestTenant> GoogleTenantAsync()
    {
        var t = await world.CreateTenantAsync("gg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var saved = await SaveAsync(t, new
        {
            kind = "google", issuer = FakeIdentityProvider.GoogleIssuer, clientId = GoogleClient, secretRef = SecretRef(t),
            jitEnabled = true, jitDomains = new[] { $"{t.Key}.example" }, hostedDomains = new[] { $"{t.Key}.example" },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return t;
    }

    private async Task<string> SignInAsync(TestTenant t, FakeUser user, string clientSecret = Secret)
    {
        var client = world.Factory.TenantClient(t.Key);
        var start = await client.GetAsync("/api/v1/auth/signin/sso");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!.ToString();
        var binding = start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_sso=", StringComparison.Ordinal))["tb_sso=".Length..];
        var state = FakeIdentityProvider.ParseQuery(new Uri(authorize).Query)["state"];
        var code = Idp.Authenticate(authorize, user, clientSecret);
        var callback = world.Factory.TenantClient(t.Key);
        callback.DefaultRequestHeaders.Add("Cookie", $"tb_sso={binding}");
        var landed = await callback.GetAsync($"/api/v1/auth/signin/sso/callback?code={code}&state={Uri.EscapeDataString(state)}");
        Assert.Equal(HttpStatusCode.Redirect, landed.StatusCode);
        return landed.Headers.Location!.ToString();
    }

    private async Task<JsonElement> ProviderAsync(TestTenant t) => await t["admin"].Client.GetFromJsonAsync<JsonElement>(Provider);

    [Fact]
    public async Task A_google_workspace_account_of_a_hosted_domain_signs_in_and_links_on_sub()
    {
        var t = await GoogleTenantAsync();
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", (await world.Factory.TenantClient(t.Key).GetAsync("/api/v1/auth/signin/sso")).Headers.Location!.ToString(),
            StringComparison.Ordinal);
        var sub = "1098" + RandomNumberGenerator.GetInt32(100000000, 999999999).ToString(CultureInfo.InvariantCulture);
        var user = new FakeUser(sub, $"mona@{t.Key}.example", "Mona Adel");

        Assert.Equal("/", await SignInAsync(t, user));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from identity.external_identity where issuer = @i and subject = @s", ("i", FakeIdentityProvider.GoogleIssuer), ("s", sub)));
        // The email changes; sub does not, so the same user signs in again.
        Assert.Equal("/", await SignInAsync(t, user with { Email = $"mona.adel@{t.Key}.example" }));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from identity.user_account where display_name = 'Mona Adel'"));
        Assert.Equal("google", await world.Database.TenantScalarAsync<string>(t.Id,
            $"select changes->>'method' from audit.audit_event where action = '{AuditActions.SignInSucceeded}' order by occurred_at desc limit 1"));
    }

    [Fact]
    public async Task Personal_and_other_workspace_google_accounts_are_refused_and_the_admin_sees_why()
    {
        var t = await GoogleTenantAsync();
        Assert.Equal("/signin/problem?reason=failed", await SignInAsync(t, new FakeUser("2001", $"me@{t.Key}.example", "Personal") { HostedDomain = "" }));
        Assert.Equal("/signin/problem?reason=failed", await SignInAsync(t, new FakeUser("2002", "someone@elsewhere.example", "Other")));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from identity.external_identity where issuer = @i", ("i", FakeIdentityProvider.GoogleIssuer)));
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id,
            $"select count(*) from audit.audit_event where action = '{AuditActions.SignInFailed}' and changes->>'reason' = 'hosted_domain_mismatch'"));
        Assert.Equal("hosted_domain_mismatch", (await ProviderAsync(t)).GetProperty("lastProblem").GetString());

        // The next good sign-in clears it.
        Assert.Equal("/", await SignInAsync(t, new FakeUser("2003", $"hana@{t.Key}.example", "Hana")));
        Assert.Equal(JsonValueKind.Null, (await ProviderAsync(t)).GetProperty("lastProblem").ValueKind);
    }

    [Fact]
    public async Task A_google_provider_needs_its_issuer_a_google_client_and_hosted_domains()
    {
        var t = await world.CreateTenantAsync("gv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var secretRef = SecretRef(t);
        await AssertInvalidAsync(await SaveAsync(t, new { kind = "google", issuer = FakeIdentityProvider.GoogleIssuer, clientId = GoogleClient, secretRef }),
            "hostedDomains", "validation.idp.hosted_domains_required");
        await AssertInvalidAsync(await SaveAsync(t, new { kind = "google", issuer = "https://accounts.example.com", clientId = GoogleClient, secretRef, hostedDomains = new[] { "corp.example" } }),
            "issuer", "validation.idp.issuer_invalid");
        await AssertInvalidAsync(await SaveAsync(t, new { kind = "google", issuer = FakeIdentityProvider.GoogleIssuer, clientId = "not-a-google-client", secretRef, hostedDomains = new[] { "corp.example" } }),
            "clientId", "validation.idp.client_id_invalid");
        await AssertInvalidAsync(await SaveAsync(t, new { kind = "okta", issuer = "https://x.example", clientId = "x", secretRef }), "kind", "validation.idp.kind_unsupported");
    }

    /// <summary>Spec 003 T036, spec 026 FR-004: Google sends no MFA claim, so the security page says so and MFA cannot be required.</summary>
    [Fact]
    public async Task Security_page_says_google_sends_no_mfa_claim_and_mfa_cannot_be_required()
    {
        var t = await GoogleTenantAsync();
        var admin = t["admin"].Client;
        var policy = await (await admin.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>();
        Assert.Equal("google", policy.GetProperty("mfaProvider").GetString());
        Assert.True(policy.GetProperty("mfaClaimsUnavailable").GetBoolean());
        Assert.False(policy.GetProperty("mfaClaimSeen").GetBoolean());
        var refused = await admin.PutAsJsonAsync("/api/v1/security/policy", new { requireMfa = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.security.mfa_not_seen", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A tenant without single sign-on keeps the default mapping and no note.
        var plain = await (await world.A["admin"].Client.GetAsync("/api/v1/security/policy")).ReadAsync<JsonElement>();
        Assert.Equal("entra", plain.GetProperty("mfaProvider").GetString());
        Assert.False(plain.GetProperty("mfaClaimsUnavailable").GetBoolean());
    }

    private static async Task AssertInvalidAsync(HttpResponseMessage response, string field, string key)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Contains(key, errors.GetProperty(field).EnumerateArray().Select(e => e.GetProperty("key").GetString()));
    }

    // AD FS: the tenant's AD DS connection read by its sync agent, then AD FS on top.

    private static string AdfsHost(OnPremAd s) => $"adfs.{s.Tenant.Key}.example";

    private Task<OnPremAd> AdTenantAsync() => OnPremAd.SetupAsync(world, "workplace_plus");

    private Task SyncAsync(OnPremAd s, params (string Guid, string Name)[] users) => s.SyncAsync(world, users);

    private Task<HttpResponseMessage> SaveAdfsAsync(OnPremAd s, Guid? connection = null) => SaveAsync(s.Tenant, new
    {
        kind = "adfs", issuer = FakeIdentityProvider.AdfsIssuerFor(AdfsHost(s)), clientId = AdfsClient, secretRef = SecretRef(s.Tenant),
        directoryConnectionId = connection ?? s.Connection,
    });

    [Fact]
    public async Task An_adfs_token_links_on_object_guid_to_the_user_directory_sync_created()
    {
        var s = await AdTenantAsync();
        var (alice, bob) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        await SyncAsync(s, (alice, "Alice"), (bob, "Bob"));
        Assert.Equal(HttpStatusCode.OK, (await SaveAdfsAsync(s)).StatusCode);
        var users = await world.Database.TenantScalarAsync<long>(s.Tenant.Id, "select count(*) from identity.user_account");

        Assert.StartsWith($"https://{AdfsHost(s)}/adfs/oauth2/authorize?", (await world.Factory.TenantClient(s.Tenant.Key).GetAsync("/api/v1/auth/signin/sso")).Headers.Location!.ToString(),
            StringComparison.Ordinal);
        // Base64 of the bytes (the AD FS default) and the text form a claim rule may make both link to the synced user.
        Assert.Equal("/", await SignInAsync(s.Tenant, new FakeUser(alice, $"alice@{s.Tenant.Key}.example", "Alice")));
        Assert.Equal("/", await SignInAsync(s.Tenant, new FakeUser(bob, $"bob@{s.Tenant.Key}.example", "Bob") { ObjectGuidAsText = true }));
        Assert.Equal(users, await world.Database.TenantScalarAsync<long>(s.Tenant.Id, "select count(*) from identity.user_account"));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            "select count(*) from identity.external_identity where subject = @s and issuer = @i", ("s", alice), ("i", DirectorySyncService.OnPremIssuer(s.Connection))));

        // Someone AD FS knows but the directory did not sync has no account: sync decides who gets in, not JIT.
        Assert.Equal("/signin/problem?reason=no_access", await SignInAsync(s.Tenant, new FakeUser(Guid.NewGuid().ToString("D"), $"eve@{s.Tenant.Key}.example", "Eve")));
    }

    [Fact]
    public async Task An_adfs_token_without_object_guid_is_refused_and_the_admin_sees_the_missing_claim()
    {
        var s = await AdTenantAsync();
        var alice = Guid.NewGuid().ToString("D");
        await SyncAsync(s, (alice, "Alice"));
        Assert.Equal(HttpStatusCode.OK, (await SaveAdfsAsync(s)).StatusCode);

        Assert.Equal("/signin/problem?reason=failed", await SignInAsync(s.Tenant, new FakeUser(alice, $"alice@{s.Tenant.Key}.example", "Alice") { OmitObjectId = true }));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(s.Tenant.Id,
            $"select count(*) from audit.audit_event where action = '{AuditActions.SignInFailed}' and changes->>'reason' = 'object_guid_missing' and changes->>'method' = 'adfs'"));
        var provider = await ProviderAsync(s.Tenant);
        Assert.Equal("object_guid_missing", provider.GetProperty("lastProblem").GetString());
        Assert.Equal(JsonValueKind.String, provider.GetProperty("lastProblemAt").ValueKind);

        Assert.Equal("/", await SignInAsync(s.Tenant, new FakeUser(alice, $"alice@{s.Tenant.Key}.example", "Alice")));
        Assert.Equal(JsonValueKind.Null, (await ProviderAsync(s.Tenant)).GetProperty("lastProblem").ValueKind);
    }

    [Fact]
    public async Task An_adfs_provider_needs_an_ad_ds_connection_of_this_tenant_and_its_own_https_issuer()
    {
        var a = await AdTenantAsync();
        var b = await AdTenantAsync();
        // Tenant B's connection is not visible to tenant A, so it cannot tie A's AD FS to B's synced users.
        await AssertInvalidAsync(await SaveAdfsAsync(a, b.Connection), "directoryConnectionId", "validation.idp.directory_connection_invalid");
        await AssertInvalidAsync(await SaveAsync(a.Tenant, new { kind = "adfs", issuer = FakeIdentityProvider.AdfsIssuerFor(AdfsHost(a)), clientId = AdfsClient, secretRef = SecretRef(a.Tenant) }),
            "directoryConnectionId", "validation.idp.directory_connection_required");
        await AssertInvalidAsync(await SaveAsync(a.Tenant, new { kind = "adfs", issuer = $"http://{AdfsHost(a)}/adfs", clientId = AdfsClient, secretRef = SecretRef(a.Tenant), directoryConnectionId = a.Connection }),
            "issuer", "validation.idp.issuer_invalid");
        Assert.Equal(HttpStatusCode.OK, (await SaveAdfsAsync(a)).StatusCode);
        Assert.Equal(a.Connection, (await ProviderAsync(a.Tenant)).GetProperty("directoryConnectionId").GetGuid());
    }

    [Fact]
    public async Task Sign_in_options_name_the_provider_kind()
    {
        var t = await GoogleTenantAsync();
        var options = await world.Factory.TenantClient(t.Key).GetFromJsonAsync<JsonElement>("/api/v1/auth/signin/options");
        Assert.True(options.GetProperty("sso").GetBoolean());
        Assert.Equal("google", options.GetProperty("kind").GetString());
    }
}
