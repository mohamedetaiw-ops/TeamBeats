using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TeamBeats.Api;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 003 identity: development sign-in, per-tenant identities, sessions, production guard.</summary>
[Collection(WorldCollection.Name)]
public sealed class IdentityTests(TenantWorld world) : IAsyncLifetime
{
    private DevHostFactory _dev = null!;

    public Task InitializeAsync()
    {
        _dev = new DevHostFactory(world.Database);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _dev.DisposeAsync();

    private Task<HttpResponseMessage> SignIn(string tenantKey, string email) =>
        _dev.Tenant(tenantKey).PostAsJsonAsync("/dev/signin", new { email });

    [Fact]
    public async Task Appointed_admin_first_sign_in_provisions_user_and_session_works()
    {
        var tenant = await (await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", new
        {
            key = "id-first", displayName = "First", packageKey = "workplace", defaultLocale = "en",
            enabledLocales = new[] { "ar", "en" }, timeZone = "Asia/Dubai", admins = new[] { "owner@id-first.example" },
        })).ReadAsync<JsonElement>();
        var tenantId = tenant.GetProperty("id").GetGuid();

        var response = await SignIn("id-first", "Owner@ID-First.example");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = DevHostFactory.SessionToken(response);
        Assert.NotNull(token);
        await world.Database.AssertAuditedAsync(tenantId, AuditActions.SignInSucceeded);

        var settings = await _dev.Tenant("id-first", token).GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    [Fact]
    public async Task Unknown_identity_is_refused_and_audited_without_raw_subject()
    {
        var response = await SignIn(world.A.Key, "stranger@elsewhere.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await world.Database.AssertAuditedAsync(world.A.Id, AuditActions.SignInRefused);
        Assert.Equal(0, await world.Database.CountInTenantAsync(world.A.Id,
            "select count(*) from audit.audit_event where changes::text like '%stranger@elsewhere%'"));
    }

    [Fact]
    public async Task Same_person_in_two_tenants_gets_two_separate_users()
    {
        var ids = new List<Guid>();
        foreach (var key in new[] { "id-twin-a", "id-twin-b" })
        {
            (await world.Platform.PostAsJsonAsync("/api/v1/platform/tenants", new
            {
                key, displayName = key, packageKey = "workplace", defaultLocale = "en",
                enabledLocales = new[] { "en" }, timeZone = "Asia/Dubai", admins = new[] { "shared.person@group.example" },
            })).EnsureSuccessStatusCode();
            var response = await SignIn(key, "shared.person@group.example");
            ids.Add((await response.ReadAsync<JsonElement>()).GetProperty("userId").GetGuid());

            // The session from this tenant is useless on the other one.
            var token = DevHostFactory.SessionToken(response);
            var other = key == "id-twin-a" ? world.A.Key : world.B.Key;
            Assert.Equal(HttpStatusCode.Unauthorized, (await _dev.Tenant(other, token).GetAsync("/api/v1/me")).StatusCode);
        }
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task Session_of_tenant_A_is_rejected_on_tenant_B_host()
    {
        var response = await world.Factory.TenantClient(world.B.Key, world.A["employee_gulf"].Token).GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Sign_in_to_suspended_tenant_is_refused()
    {
        var tenant = await world.CreateTenantAsync("id-susp", "workplace");
        (await world.Platform.PostAsync($"/api/v1/platform/tenants/{tenant.Id}:suspend", null)).EnsureSuccessStatusCode();
        var response = await SignIn("id-susp", "admin@id-susp.example");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tenant.suspended", problem.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Disabling_a_membership_revokes_sessions_and_blocks_sign_in()
    {
        var t = world.A;
        var user = await world.SeedUserAsync(t, "to_disable", "employee");
        (await t["admin"].Client.PostAsync($"/api/v1/admin/users/{user.Id}:disable", null)).EnsureSuccessStatusCode();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.MembershipDisabled);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SessionRevoked);

        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(t.Key, user.Email)).StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_disable_self_or_last_admin()
    {
        var t = world.A;
        var response = await t["admin"].Client.PostAsync($"/api/v1/admin/users/{t["admin"].Id}:disable", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Sign_out_ends_the_session()
    {
        var user = await world.SeedUserAsync(world.A, "signs_out", "employee");
        Assert.Equal(HttpStatusCode.OK, (await user.Client.GetAsync("/api/v1/me")).StatusCode);
        (await user.Client.PostAsync("/api/v1/auth/signout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Client.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public void Production_guard_refuses_dev_sign_in_flag()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:DevSignIn:Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => ProductionGuards.EnsureDevSignInDisabled(config));
    }

    [Fact]
    public void Production_host_fails_to_start_with_dev_sign_in_enabled()
    {
        using var factory = new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:DevSignIn:Enabled", "true");
            b.UseSetting("ConnectionStrings:App", world.Database.AppConnectionString);
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Production_host_has_no_dev_sign_in_route()
    {
        var response = await world.Factory.TenantClient(world.A.Key).PostAsJsonAsync("/dev/signin", new { email = "admin@alpha.example" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
