using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Api;
using TeamBeats.Api.DevHost;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 003 US7: the staging test account. The development host runs as Staging with password-mode sign-in, the
/// appsettings.Staging.json test account and seeded test tenant, and a password supplied the way a deployment secret is.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class StagingTestAccountTests(TenantWorld world) : IAsyncLifetime
{
    private const string TenantKey = "staging-demo";
    private const string Password = "admin";

    private DevHostFactory _staging = null!;

    private static Dictionary<string, string?> StagingSettings(string? password = Password) => new()
    {
        // Tests reach tenants by subdomain; the deployed staging host uses dedicated mode from appsettings.Staging.json.
        ["Tenancy:Hosts:Mode"] = "Shared",
        ["Dev:MigrateOnStart"] = "false",
        ["Auth:DevSignIn:TestAccounts:0:Password"] = password,
    };

    public Task InitializeAsync()
    {
        // Starting the host runs DevHostProgram.Main, which seeds the test tenant from Dev:SeedTenant.
        _staging = new DevHostFactory(world.Database, DevAuthModule.StagingEnvironment, StagingSettings());
        _ = _staging.Services;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _staging.DisposeAsync();

    private static async Task<bool> SeedAsync(DevHostFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var request = scope.ServiceProvider.GetRequiredService<IConfiguration>().GetSection(TestTenantSeeder.Section).Get<CreateTenantRequest>()!;
        return await scope.ServiceProvider.GetRequiredService<TestTenantSeeder>().SeedAsync(request, CancellationToken.None);
    }

    private Task<HttpResponseMessage> SignIn(string tenantKey, object body) =>
        _staging.Tenant(tenantKey).PostAsJsonAsync("/dev/signin", body);

    [Fact]
    public async Task Admin_with_password_admin_signs_in_as_tenant_administrator()
    {
        var response = await SignIn(TenantKey, new { username = "admin", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = DevHostFactory.SessionToken(response);
        Assert.NotNull(token);

        // The admin appointment from the seeded tenant was claimed: tenant administration works.
        var settings = await _staging.Tenant(TenantKey, token).GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var users = await _staging.Tenant(TenantKey, token).GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    }

    [Fact]
    public async Task Username_is_case_insensitive_and_password_is_not()
    {
        Assert.Equal(HttpStatusCode.OK, (await SignIn(TenantKey, new { username = "Admin", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(TenantKey, new { username = "admin", password = "ADMIN" })).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_refused_and_audited()
    {
        var response = await SignIn(TenantKey, new { username = "admin", password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(DevHostFactory.SessionToken(response));
        var tenantId = await world.Database.OwnerScalarAsync<Guid>("select id from tenancy.tenant where key = 'staging-demo'");
        await world.Database.AssertAuditedAsync(tenantId, AuditActions.SignInRefused);
        Assert.Equal(0, await world.Database.CountInTenantAsync(tenantId,
            "select count(*) from audit.audit_event where changes::text like '%not-the-password%'"));
    }

    [Fact]
    public async Task Unknown_username_and_passwordless_email_sign_in_are_refused()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(TenantKey, new { username = "root", password = Password })).StatusCode);
        // Open mode's email-only sign-in does not work in password mode, even for the test account's own email.
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(TenantKey, new { email = "admin@staging.example" })).StatusCode);
    }

    [Fact]
    public async Task Test_account_cannot_reach_another_tenant()
    {
        // Same credentials on tenant A's host: the account has no identity there, so sign-in is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(world.A.Key, new { username = "admin", password = Password })).StatusCode);

        // A staging session is useless on tenant A.
        var token = DevHostFactory.SessionToken(await SignIn(TenantKey, new { username = "admin", password = Password }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _staging.Tenant(world.A.Key, token).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Platform_sign_in_does_not_exist_in_password_mode()
    {
        var client = _staging.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri($"https://platform.{ApiFactory.BaseDomain}"),
            HandleCookies = false,
        });
        var response = await client.PostAsJsonAsync("/dev/platform/signin", new { email = TestTenantSeeder.OperatorEmail });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Host_start_seeds_the_tenant_once()
    {
        // The host already seeded on start; a second run (the next deploy) changes nothing.
        Assert.False(await SeedAsync(_staging));
        Assert.Equal(1, await world.Database.OwnerScalarAsync<long>("select count(*) from tenancy.tenant where key = 'staging-demo'"));
    }

    [Fact]
    public void Staging_refuses_to_start_in_open_mode()
    {
        var settings = StagingSettings();
        settings["Auth:DevSignIn:Mode"] = "Open";
        using var factory = new DevHostFactory(world.Database, DevAuthModule.StagingEnvironment, settings);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("must be Password in Staging", Flatten(error));
    }

    [Fact]
    public void Staging_refuses_to_start_without_the_password_secret()
    {
        using var factory = new DevHostFactory(world.Database, DevAuthModule.StagingEnvironment, StagingSettings(password: null));
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("has no password", Flatten(error));
    }

    [Fact]
    public void Development_host_refuses_production_environment()
    {
        using var factory = new DevHostFactory(world.Database, "Production", StagingSettings());
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void Production_guard_refuses_test_accounts_and_test_tenant()
    {
        foreach (var setting in new[] { "Auth:DevSignIn:TestAccounts:0:Username", "Dev:SeedTenant:Key" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [setting] = "admin" }).Build();
            Assert.Throws<InvalidOperationException>(() => ProductionGuards.EnsureDevSignInDisabled(config));
        }
    }

    [Fact]
    public void Production_host_fails_to_start_with_a_test_account_configured()
    {
        using var factory = new WebApplicationFactory<ApiProgram>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Auth:DevSignIn:TestAccounts:0:Username", "admin");
            b.UseSetting("Auth:DevSignIn:TestAccounts:0:Password", Password);
            b.UseSetting("ConnectionStrings:App", world.Database.AppConnectionString);
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Production_host_has_no_test_account_sign_in()
    {
        var response = await world.Factory.TenantClient(world.A.Key).PostAsJsonAsync("/dev/signin", new { username = "admin", password = Password });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string Flatten(Exception error) =>
        string.Join(" | ", Enumerate(error).Select(e => e.Message));

    private static IEnumerable<Exception> Enumerate(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
        {
            yield return error;
            if (error is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Enumerate))
                {
                    yield return inner;
                }
            }
        }
    }
}
