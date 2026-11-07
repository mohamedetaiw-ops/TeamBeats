using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 T011 (US2 scenario 4, FR-003): idle and absolute timeouts, use recorded at most once a minute, the
/// concurrent session limit, sign out everywhere and an administrator ending a user's sessions, with tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecuritySessionTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync() =>
        await world.CreateTenantAsync("ses-" + Guid.NewGuid().ToString("N")[..8], "workplace");

    private async Task<string> NewSessionAsync(TestTenant t, string user) =>
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var token = await sp.GetRequiredService<SessionService>().CreateUserSessionAsync(t[user].Id, "test", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return token;
        });

    private Task<HttpStatusCode> MeAsync(TestTenant t, string token) =>
        world.Factory.TenantClient(t.Key, token).GetAsync("/api/v1/me").ContinueWith(r => r.Result.StatusCode);

    private Task<int> AgeAsync(TestTenant t, string token, string column, TimeSpan age) =>
        world.Database.OwnerExecInTenantAsync(t.Id, $"update identity.user_session set {column} = now() - @age where token_hash = @h",
            ("age", age), ("h", SessionTokens.Hash(token)));

    private Task<long> RevokedAsync(TestTenant t, string token, string reason) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from identity.user_session where token_hash = @h and revoked_reason = @r",
            ("h", SessionTokens.Hash(token)), ("r", reason));

    private static Task<HttpResponseMessage> PolicyAsync(TestTenant t, object body) =>
        t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", body);

    [Fact]
    public async Task A_session_idle_longer_than_the_idle_timeout_ends_and_use_is_recorded_at_most_once_a_minute()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        (await PolicyAsync(t, new { idleMinutes = 45 })).EnsureSuccessStatusCode();

        var active = await NewSessionAsync(t, "employee");
        await AgeAsync(t, active, "last_seen_at", TimeSpan.FromMinutes(40));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, active));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from identity.user_session where token_hash = @h and last_seen_at > now() - interval '1 minute'", ("h", SessionTokens.Hash(active))));

        await AgeAsync(t, active, "last_seen_at", TimeSpan.FromSeconds(20));
        var before = await world.Database.OwnerScalarAsync<DateTime>(
            "select last_seen_at from identity.user_session where token_hash = @h", ("h", SessionTokens.Hash(active)));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, active));
        Assert.Equal(before, await world.Database.OwnerScalarAsync<DateTime>(
            "select last_seen_at from identity.user_session where token_hash = @h", ("h", SessionTokens.Hash(active))));

        var idle = await NewSessionAsync(t, "employee");
        await AgeAsync(t, idle, "last_seen_at", TimeSpan.FromMinutes(46));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, idle));
        Assert.Equal(1, await RevokedAsync(t, idle, SessionRules.IdleTimeout));
        // Ended for good: fresh use does not bring it back.
        await AgeAsync(t, idle, "last_seen_at", TimeSpan.Zero);
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, idle));
    }

    [Fact]
    public async Task A_session_older_than_the_absolute_timeout_ends_even_while_in_use()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        (await PolicyAsync(t, new { absoluteHours = 2 })).EnsureSuccessStatusCode();

        var young = await NewSessionAsync(t, "employee");
        await AgeAsync(t, young, "created_at", TimeSpan.FromMinutes(110));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, young));

        var old = await NewSessionAsync(t, "employee");
        await AgeAsync(t, old, "created_at", TimeSpan.FromMinutes(121));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, old));
        Assert.Equal(1, await RevokedAsync(t, old, SessionRules.AbsoluteTimeout));
    }

    [Fact]
    public async Task The_concurrent_limit_ends_the_oldest_sessions_on_a_new_sign_in()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "other", MembershipTypes.Employee);
        (await PolicyAsync(t, new { maxSessions = 2 })).EnsureSuccessStatusCode();

        var first = await NewSessionAsync(t, "employee");
        var second = await NewSessionAsync(t, "employee");
        var third = await NewSessionAsync(t, "employee");
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, first));
        Assert.Equal(1, await RevokedAsync(t, first, SessionRules.SessionLimit));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, second));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, third));
        // The seeded session counted too, and was the oldest.
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, t["employee"].Token));
        // Another user's sessions are untouched.
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, t["other"].Token));
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_every_session_of_the_caller_only()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "other", MembershipTypes.Employee);
        var phone = await NewSessionAsync(t, "employee");

        var response = await world.Factory.TenantClient(t.Key, t["employee"].Token).PostAsync("/api/v1/me/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("tb_session=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, t["employee"].Token));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, phone));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, t["other"].Token));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SessionRevoked, t["employee"].Id.ToString());
        Assert.Equal(1, await world.Database.OutboxCountAsync("SessionRevoked", t.Id));
    }

    [Fact]
    public async Task Administrators_end_a_users_sessions_and_nobody_reaches_another_tenants_users()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "colleague", MembershipTypes.Employee);

        var denied = await t["colleague"].Client.PostAsync($"/api/v1/identity/users/{t["employee"].Id}/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, t["employee"].Token));

        var crossed = await t["admin"].Client.PostAsync($"/api/v1/identity/users/{other["admin"].Id}/sessions/revoke-all", null);
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(other, other["admin"].Token));

        var ended = await t["admin"].Client.PostAsync($"/api/v1/identity/users/{t["employee"].Id}/sessions/revoke-all", null);
        Assert.Equal(1, (await ended.ReadAsync<System.Text.Json.JsonElement>()).GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(t, t["employee"].Token));
        Assert.Equal(1, await RevokedAsync(t, t["employee"].Token, SessionRules.AdminRevoked));
        Assert.Equal(HttpStatusCode.OK, await MeAsync(t, t["admin"].Token));
    }
}
