using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 018 (R1) Phase 2 and 3, spec 003 FR-016: feature options, "Connect Outlook" with delegated access as the
/// employee, the token store and every clean-up trigger. Directories and people are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OutlookConnectTests(TenantWorld world)
{
    private FakeMicrosoftGraph Graph => world.Factory.Graph;

    private sealed record Setup(TestTenant Tenant, Guid Connection, Guid Directory)
    {
        public Guid Id => Tenant.Id;
    }

    /// <summary>A tenant with a Microsoft 365 connection, the given delegated features on and consented, and an employee.</summary>
    private async Task<Setup> SetupAsync(string[] features, string package = "workplace_plus", bool consent = true)
    {
        var t = await world.CreateTenantAsync("olk-" + Guid.NewGuid().ToString("N")[..8], package);
        var directory = Guid.NewGuid();
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features,
        });
        var connection = package == "workplace" ? Guid.Empty : (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        if (consent)
        {
            foreach (var f in features)
            {
                await AdminConsentAsync(t, connection, directory, f);
            }
        }
        Graph.ConsentDelegated(directory, "User.Read", "Mail.ReadBasic", "Mail.Read", "Calendars.ReadBasic");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        return new Setup(t, connection, directory);
    }

    private async Task AdminConsentAsync(TestTenant t, Guid connection, Guid directory, string feature)
    {
        var start = await t["admin"].Client.GetAsync($"/api/v1/integrations/connections/{connection}/features/{feature}/consent");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var state = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query)["state"];
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(state)}");
        var client = ClientWithCookies(t.Key, t["admin"].Token, ("tb_consent", Cookie(start, "tb_consent")!));
        var done = await client.GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.EndsWith("consent=granted", done.Headers.Location!.ToString(), StringComparison.Ordinal);
    }

    private HttpClient ClientWithCookies(string tenantKey, string sessionToken, params (string Name, string Value)[] cookies)
    {
        var client = world.Factory.ClientFor($"{tenantKey}.{ApiFactory.BaseDomain}");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}").Prepend($"tb_session={sessionToken}")));
        return client;
    }

    private static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.Select(v => v.Split(';')[0]).Where(v => v.StartsWith(name + "=", StringComparison.Ordinal)).Select(v => v[(name.Length + 1)..]).FirstOrDefault()
            : null;

    private sealed record ConnectStart(HttpResponseMessage Response, Uri Authorize, string Binding);

    private static async Task<ConnectStart> StartAsync(TestTenant t, string user = "employee", string query = "")
    {
        var start = await t[user].Client.GetAsync("/api/v1/me/outlook/connect" + query);
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        return new ConnectStart(start, start.Headers.Location!, Cookie(start, "tb_outlook") ?? string.Empty);
    }

    /// <summary>Microsoft answers the authorize request, the platform host relays, and the tenant host finishes as <paramref name="finisher"/>.</summary>
    private async Task<string> FinishAsync(TestTenant t, ConnectStart start, TestUser? finisher = null, string? binding = null, string? onTenantKey = null)
    {
        var answer = Graph.Authorize(start.Authorize);
        var relay = await world.Factory.PlatformClient().GetAsync("/api/v1/integrations/outlook/callback" + answer);
        Assert.Equal(HttpStatusCode.Redirect, relay.StatusCode);
        Assert.Equal($"{t.Key}.{ApiFactory.BaseDomain}", relay.Headers.Location!.Host);
        var user = finisher ?? t["employee"];
        var client = ClientWithCookies(onTenantKey ?? t.Key, user.Token, ("tb_outlook", binding ?? start.Binding));
        var done = await client.GetAsync(relay.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return done.Headers.Location!.ToString();
    }

    private async Task<string> ConnectAsync(TestTenant t, string user = "employee") =>
        await FinishAsync(t, await StartAsync(t, user), t[user]);

    private static async Task<JsonElement> StatusAsync(TestTenant t, string user = "employee") =>
        await t[user].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/outlook");

    private Task<long> TokensAsync(Setup s, Guid? user = null) =>
        world.Database.CountInTenantAsync(s.Id, "select count(*) from integrations.delegated_token where tenant_id = @t and (@u::uuid is null or user_id = @u)",
            ("t", s.Id), ("u", (object?)user ?? DBNull.Value));

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    [Fact]
    public async Task Connect_asks_only_for_the_scopes_of_features_that_are_on_with_a_login_hint_and_pkce()
    {
        var s = await SetupAsync(["calendar"]);
        Assert.Equal("not_connected", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());

        var start = await StartAsync(s.Tenant);
        Assert.StartsWith($"https://login.microsoftonline.com/{s.Directory:D}/oauth2/v2.0/authorize?", start.Authorize.ToString(), StringComparison.Ordinal);
        var q = FakeIdentityProvider.ParseQuery(start.Authorize.Query);
        Assert.Equal("https://graph.microsoft.com/Calendars.ReadBasic", q["scope"]);
        Assert.Equal(s.Tenant["employee"].Email, q["login_hint"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal($"https://platform.{ApiFactory.BaseDomain}/api/v1/integrations/outlook/callback", q["redirect_uri"]);
        Assert.False(q.ContainsKey("prompt"));
        Assert.DoesNotContain("offline_access", q["scope"], StringComparison.Ordinal);

        Assert.Equal("/?outlook=connected", await FinishAsync(s.Tenant, start));
        var status = await StatusAsync(s.Tenant);
        Assert.Equal("ok", status.GetProperty("state").GetString());
        Assert.Equal(["calendar"], status.GetProperty("features").EnumerateArray().Select(e => e.GetString()!).ToArray());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.OutlookConnected, s.Tenant["employee"].Id.ToString());
        Assert.Equal(1, await world.Database.OutboxCountAsync("OutlookConnected", s.Id));
    }

    [Fact]
    public async Task Silent_connect_uses_prompt_none_and_returns_to_a_local_path_only()
    {
        var s = await SetupAsync(["mail"]);
        var silent = await StartAsync(s.Tenant, query: "?silent=true&returnTo=%2Fhome%3Ftab%3Dmail");
        Assert.Equal("none", FakeIdentityProvider.ParseQuery(silent.Authorize.Query)["prompt"]);
        Assert.Equal("https://graph.microsoft.com/Mail.ReadBasic https://graph.microsoft.com/User.Read", FakeIdentityProvider.ParseQuery(silent.Authorize.Query)["scope"]);
        Assert.Equal("/home?tab=mail&outlook=connected", await FinishAsync(s.Tenant, silent));

        foreach (var evil in new[] { "https%3A%2F%2Fevil.example%2F", "%2F%2Fevil.example", "%2F%5Cevil.example" })
        {
            var start = await StartAsync(s.Tenant, query: "?returnTo=" + evil);
            Assert.Equal("/?outlook=connected", await FinishAsync(s.Tenant, start));
        }
    }

    [Fact]
    public async Task The_token_never_leaves_the_server_and_is_stored_encrypted()
    {
        var s = await SetupAsync(["mail", "calendar"]);
        var before = Graph.IssuedDelegatedTokens.ToHashSet();
        var start = await StartAsync(s.Tenant);
        var answer = Graph.Authorize(start.Authorize);
        var relay = await world.Factory.PlatformClient().GetAsync("/api/v1/integrations/outlook/callback" + answer);
        var done = await ClientWithCookies(s.Tenant.Key, s.Tenant["employee"].Token, ("tb_outlook", start.Binding)).GetAsync(relay.Headers.Location!.PathAndQuery);
        var token = Graph.IssuedDelegatedTokens.Except(before).Single();

        var bodies = new List<string> { done.ToString(), await done.Content.ReadAsStringAsync(), await (await s.Tenant["employee"].Client.GetAsync("/api/v1/me/outlook")).Content.ReadAsStringAsync() };
        Assert.All(bodies, b => Assert.DoesNotContain(token, b, StringComparison.Ordinal));
        Assert.Equal(0, await world.Database.CountInTenantAsync(s.Id,
            "select count(*) from audit.audit_event where tenant_id = @t and changes::text like @needle", ("t", s.Id), ("needle", "%" + token[..20] + "%")));

        var stored = await world.Factory.InScopeAsync(s.Id, async sp => await sp.GetRequiredService<IDbSession>().QuerySingleOrDefaultAsync(
            "select ciphertext, nonce, session_id from integrations.delegated_token where user_id = @u", new { u = s.Tenant["employee"].Id },
            r => (Cipher: r.GetFieldValue<byte[]>(0), Nonce: r.GetFieldValue<byte[]>(1), Session: r.GetGuid(2))));
        Assert.DoesNotContain(token, Encoding.UTF8.GetString(stored.Cipher), StringComparison.Ordinal);

        // AES-GCM associated data binds the ciphertext to tenant, user and session: a copied row does not decrypt.
        var protector = world.Factory.Services.GetRequiredService<TokenProtector>();
        var user = s.Tenant["employee"].Id;
        Assert.Equal(token, protector.Unprotect(stored.Nonce, stored.Cipher, s.Id, user, stored.Session));
        Assert.Null(protector.Unprotect(stored.Nonce, stored.Cipher, Guid.NewGuid(), user, stored.Session));
        Assert.Null(protector.Unprotect(stored.Nonce, stored.Cipher, s.Id, Guid.NewGuid(), stored.Session));
        Assert.Null(protector.Unprotect(stored.Nonce, stored.Cipher, s.Id, user, Guid.NewGuid()));
    }

    [Fact]
    public async Task Missing_consent_shows_admin_setup_and_never_another_prompt()
    {
        // Feature on but the administrator has not consented: TeamBeats does not send the employee to Microsoft.
        var pending = await SetupAsync(["mail"], consent: false);
        Assert.Equal("admin_setup", (await StatusAsync(pending.Tenant)).GetProperty("state").GetString());
        var direct = await pending.Tenant["employee"].Client.GetAsync("/api/v1/me/outlook/connect");
        Assert.Equal("/?outlook=admin_setup", direct.Headers.Location!.ToString());

        // Microsoft says consent is missing for the directory: admin setup, remembered for this session.
        var s = await SetupAsync(["mail"]);
        Graph.ConsentDelegated(s.Directory, "User.Read");
        Assert.Equal("/?outlook=admin_setup", await ConnectAsync(s.Tenant));
        Assert.Equal("admin_setup", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());
        Assert.Equal(0, await TokensAsync(s));
    }

    [Fact]
    public async Task Conditional_access_is_explained_and_not_retried()
    {
        var s = await SetupAsync(["calendar"]);
        Graph.NextAuthorizeError = ("interaction_required", "AADSTS53003: Access has been blocked by Conditional Access policies.");
        Assert.Equal("/?outlook=conditional_access", await ConnectAsync(s.Tenant));
        Assert.Equal("conditional_access", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());

        Graph.NextTokenError = "invalid_grant";
        Assert.Equal("/?outlook=not_connected", await ConnectAsync(s.Tenant));
        Assert.Equal(0, await TokensAsync(s));
    }

    [Fact]
    public async Task Disconnect_deletes_the_token_and_is_audited()
    {
        var s = await SetupAsync(["mail"]);
        await ConnectAsync(s.Tenant);
        Assert.Equal(1, await TokensAsync(s));
        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant["employee"].Client.DeleteAsync("/api/v1/me/outlook")).StatusCode);
        Assert.Equal(0, await TokensAsync(s));
        Assert.Equal("not_connected", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.OutlookDisconnected, s.Tenant["employee"].Id.ToString());
    }

    [Fact]
    public async Task Sign_out_session_revocation_and_membership_disable_delete_tokens()
    {
        var s = await SetupAsync(["mail"]);
        var employee = s.Tenant["employee"];
        await ConnectAsync(s.Tenant);
        await world.SeedUserAsync(s.Tenant, "colleague", MembershipTypes.Employee);
        await ConnectAsync(s.Tenant, "colleague");
        var other = await world.SeedUserAsync(s.Tenant, "third", MembershipTypes.Employee);
        await ConnectAsync(s.Tenant, "third");
        Assert.Equal(3, await TokensAsync(s));

        Assert.True((await employee.Client.PostAsync("/api/v1/auth/signout", null)).IsSuccessStatusCode);
        await DispatchAsync();
        Assert.Equal(0, await TokensAsync(s, employee.Id));

        await world.Factory.InScopeAsync(s.Id, async sp =>
        {
            await sp.GetRequiredService<ISessionRevocation>().RevokeUserSessionsAsync(s.Tenant["colleague"].Id, "admin");
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserDeprovisioned(other.Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        await DispatchAsync();
        Assert.Equal(0, await TokensAsync(s));
    }

    [Fact]
    public async Task Turning_a_feature_off_drops_tokens_only_it_needed()
    {
        var s = await SetupAsync(["mail", "calendar", "free_busy"]);
        await ConnectAsync(s.Tenant);
        var admin = s.Tenant["admin"].Client;

        // Free and busy uses the same scope as Calendar, so the token stays.
        await admin.PostAsync($"/api/v1/integrations/connections/{s.Connection}/features/free_busy:disable", null);
        Assert.Equal(1, await TokensAsync(s));
        Assert.Equal("ok", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());

        // Calendar alone needed Calendars.ReadBasic now: the token goes, and the next connect asks for mail only.
        await admin.PostAsync($"/api/v1/integrations/connections/{s.Connection}/features/calendar:disable", null);
        Assert.Equal(0, await TokensAsync(s));
        var start = await StartAsync(s.Tenant);
        Assert.DoesNotContain("Calendars", FakeIdentityProvider.ParseQuery(start.Authorize.Query)["scope"], StringComparison.Ordinal);

        // A feature turned on later makes the existing token ask for a reconnect.
        await FinishAsync(s.Tenant, start);
        await admin.PostAsync($"/api/v1/integrations/connections/{s.Connection}/features/calendar:enable", null);
        Assert.Equal("reconnect", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());

        // Disabling the connection, or deleting it, removes what is left.
        await admin.PostAsync($"/api/v1/integrations/connections/{s.Connection}:disable", null);
        Assert.Equal(0, await TokensAsync(s));
        Assert.Equal("turned_off", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Deleting_the_connection_deletes_its_tokens()
    {
        var s = await SetupAsync(["calendar"]);
        await ConnectAsync(s.Tenant);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Tenant["admin"].Client.DeleteAsync($"/api/v1/integrations/connections/{s.Connection}?confirm=true")).StatusCode);
        Assert.Equal(0, await TokensAsync(s));
        Assert.Equal("turned_off", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Preview_line_swaps_the_mail_scope_and_needs_new_consent()
    {
        var s = await SetupAsync(["mail"]);
        await ConnectAsync(s.Tenant);
        var admin = s.Tenant["admin"].Client;
        var url = $"/api/v1/integrations/connections/{s.Connection}/features/mail/options";

        var invalid = await admin.PutAsJsonAsync(url, new { maxMessages = 11, maxDays = 3 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadAsStringAsync();
        Assert.Contains("validation.integrations.max_messages_range", problem, StringComparison.Ordinal);
        Assert.Contains("validation.integrations.option_not_for_feature", problem, StringComparison.Ordinal);

        var view = await (await admin.PutAsJsonAsync(url, new { previewLine = true, maxMessages = 8 })).ReadAsync<JsonElement>();
        var mail = view.GetProperty("features").EnumerateArray().First(f => f.GetProperty("key").GetString() == "mail");
        Assert.Equal(["User.Read", "Mail.Read"], mail.GetProperty("permissions").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToArray());
        Assert.True(mail.GetProperty("options").GetProperty("previewLine").GetBoolean());
        Assert.Equal(8, mail.GetProperty("options").GetProperty("maxMessages").GetInt32());
        Assert.Equal("pending", mail.GetProperty("consentStatus").GetString());
        Assert.Equal("needs_consent", view.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(s.Id, AuditActions.ConnectionFeatureOptionsChanged, s.Connection.ToString());
        Assert.Equal(0, await TokensAsync(s));
        Assert.Equal("admin_setup", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());

        var consent = await admin.GetAsync($"/api/v1/integrations/connections/{s.Connection}/features/mail/consent");
        Assert.Contains("Mail.Read", FakeIdentityProvider.ParseQuery(consent.Headers.Location!.Query)["scope"], StringComparison.Ordinal);
        await AdminConsentAsync(s.Tenant, s.Connection, s.Directory, "mail");
        var start = await StartAsync(s.Tenant);
        Assert.Equal("https://graph.microsoft.com/Mail.Read https://graph.microsoft.com/User.Read", FakeIdentityProvider.ParseQuery(start.Authorize.Query)["scope"]);

        var calendar = await admin.PutAsJsonAsync($"/api/v1/integrations/connections/{s.Connection}/features/calendar/options", new { maxDays = 15 });
        Assert.Contains("validation.integrations.max_days_range", await calendar.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var employee = await s.Tenant["employee"].Client.PutAsJsonAsync(url, new { previewLine = false });
        Assert.Equal(HttpStatusCode.Forbidden, employee.StatusCode);
    }

    [Fact]
    public async Task Connect_finishes_only_for_the_same_user_session_and_browser()
    {
        var s = await SetupAsync(["mail"]);
        var colleague = await world.SeedUserAsync(s.Tenant, "colleague", MembershipTypes.Employee);
        Assert.Equal("/?outlook=failed", await FinishAsync(s.Tenant, await StartAsync(s.Tenant), finisher: colleague));
        Assert.Equal("/?outlook=failed", await FinishAsync(s.Tenant, await StartAsync(s.Tenant), binding: "forged"));

        // A second session of the same employee cannot finish a connect started in the first.
        var second = await world.Factory.InScopeAsync(s.Id, async sp =>
        {
            var token = await sp.GetRequiredService<TeamBeats.Modules.Identity.SessionService>().CreateUserSessionAsync(s.Tenant["employee"].Id, "test", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return token;
        });
        var start = await StartAsync(s.Tenant);
        Assert.Equal("/?outlook=failed", await FinishAsync(s.Tenant, start, finisher: s.Tenant["employee"] with { Token = second }));

        // The state is single use.
        var once = await StartAsync(s.Tenant);
        var answer = Graph.Authorize(once.Authorize);
        var client = ClientWithCookies(s.Tenant.Key, s.Tenant["employee"].Token, ("tb_outlook", once.Binding));
        Assert.EndsWith("outlook=connected", (await client.GetAsync("/api/v1/integrations/outlook/callback" + answer)).Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.EndsWith("outlook=failed", (await client.GetAsync("/api/v1/integrations/outlook/callback" + answer)).Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await TokensAsync(s));
    }

    [Fact]
    public async Task Another_tenant_cannot_finish_or_see_a_connect()
    {
        var a = await SetupAsync(["mail"]);
        var b = await SetupAsync(["mail"]);
        var start = await StartAsync(a.Tenant);
        var answer = Graph.Authorize(start.Authorize);

        // Tenant A's state on tenant B's host, with B's employee: nothing matches in B.
        var onB = ClientWithCookies(b.Tenant.Key, b.Tenant["employee"].Token, ("tb_outlook", start.Binding));
        Assert.EndsWith("outlook=failed", (await onB.GetAsync("/api/v1/integrations/outlook/callback" + answer)).Headers.Location!.ToString(), StringComparison.Ordinal);

        // B's session on A's host is not a session of A.
        var crossHost = ClientWithCookies(a.Tenant.Key, b.Tenant["employee"].Token, ("tb_outlook", start.Binding));
        Assert.Equal(HttpStatusCode.Unauthorized, (await crossHost.GetAsync("/api/v1/integrations/outlook/callback" + answer)).StatusCode);
        Assert.Equal(0, await TokensAsync(a));
        Assert.Equal(0, await TokensAsync(b));

        await ConnectAsync(a.Tenant);
        Assert.Equal(0, await world.Database.CountInTenantAsync(b.Id, "select count(*) from integrations.delegated_token where tenant_id = @t", ("t", b.Id)));
        Assert.Equal("not_connected", (await StatusAsync(b.Tenant)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Every_outlook_endpoint_refuses_without_the_entitlement()
    {
        var s = await SetupAsync([], package: "workplace");
        var client = s.Tenant["employee"].Client;
        foreach (var response in new[]
                 {
                     await client.GetAsync("/api/v1/me/outlook"), await client.GetAsync("/api/v1/me/outlook/connect"), await client.DeleteAsync("/api/v1/me/outlook"),
                 })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("entitlement.not_in_package", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(s.Tenant.Key).GetAsync("/api/v1/me/outlook")).StatusCode);
    }

    [Fact]
    public async Task No_feature_on_means_turned_off()
    {
        var s = await SetupAsync(["directory_sync"], consent: false);
        Assert.Equal("turned_off", (await StatusAsync(s.Tenant)).GetProperty("state").GetString());
        Assert.Equal("/?outlook=turned_off", (await s.Tenant["employee"].Client.GetAsync("/api/v1/me/outlook/connect")).Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("consent_required", null, "admin_setup")]
    [InlineData("interaction_required", "AADSTS65001: consent needed", "admin_setup")]
    [InlineData("interaction_required", "AADSTS50076: MFA required", "conditional_access")]
    [InlineData("access_denied", "AADSTS53003: blocked", "conditional_access")]
    [InlineData("login_required", "AADSTS50058: no session", "not_connected")]
    [InlineData("access_denied", "AADSTS65004: user declined", "not_connected")]
    public void Refusals_map_to_states(string error, string? description, string state) =>
        Assert.Equal(state, DelegatedAccessService.MapRefusal(error, description));
}
