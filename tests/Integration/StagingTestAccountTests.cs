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
/// Spec 003 US7: the staging test account. The development host runs as Staging with one-click test account sign-in,
/// using the appsettings.Staging.json test account and seeded test tenant. No password and no secret.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class StagingTestAccountTests(TenantWorld world) : IAsyncLifetime
{
    private const string TenantKey = "staging-demo";
    private DevHostFactory _staging = null!;

    private static Dictionary<string, string?> StagingSettings() => new()
    {
        // Tests reach tenants by subdomain; the deployed staging host uses dedicated mode from appsettings.Staging.json.
        ["Tenancy:Hosts:Mode"] = "Shared",
        ["Dev:MigrateOnStart"] = "false",
        // The in-host sync scheduler would also sync other test tenants on the shared database; tests sync explicitly.
        ["Dev:Fakes:RunDirectorySync"] = "false",
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
    public async Task One_click_admin_sign_in_makes_a_tenant_administrator_session()
    {
        var response = await SignIn(TenantKey, new { username = "admin" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = DevHostFactory.SessionToken(response);
        Assert.NotNull(token);
        // Staging opens the fake Outlook connect next, so the admin's mail and calendar are filled after every sign-in.
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("/api/v1/me/outlook/connect?silent=true&returnTo=%2F", body.GetProperty("next").GetString());

        // The admin appointment from the seeded tenant was claimed: tenant administration works.
        var settings = await _staging.Tenant(TenantKey, token).GetAsync("/api/v1/tenant/settings");
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        var users = await _staging.Tenant(TenantKey, token).GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
    }

    [Fact]
    public async Task Username_is_not_case_sensitive()
    {
        Assert.Equal(HttpStatusCode.OK, (await SignIn(TenantKey, new { username = "Admin" })).StatusCode);
    }

    [Fact]
    public async Task Unknown_username_is_refused_and_audited()
    {
        var response = await SignIn(TenantKey, new { username = "root" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(DevHostFactory.SessionToken(response));
        var tenantId = await world.Database.OwnerScalarAsync<Guid>("select id from tenancy.tenant where key = 'staging-demo'");
        await world.Database.AssertAuditedAsync(tenantId, AuditActions.SignInRefused);
    }

    [Fact]
    public async Task Email_sign_in_is_refused_in_test_account_mode()
    {
        // Open mode's email sign-in does not exist on staging: nobody can choose an arbitrary account, not even an
        // appointed admin of another seeded tenant and not even the test account's own email.
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(TenantKey, new { email = "admin@staging.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(world.A.Key, new { email = world.A["admin"].Email })).StatusCode);
    }

    [Fact]
    public async Task Test_account_cannot_reach_another_tenant()
    {
        // Same credentials on tenant A's host: the account has no identity there, so sign-in is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await SignIn(world.A.Key, new { username = "admin" })).StatusCode);

        // A staging session is useless on tenant A.
        var token = DevHostFactory.SessionToken(await SignIn(TenantKey, new { username = "admin" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _staging.Tenant(world.A.Key, token).GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Platform_sign_in_does_not_exist_in_test_account_mode()
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
        Assert.Contains("must be TestAccount in Staging", Flatten(error));
    }

    [Fact]
    public void Staging_refuses_to_start_without_a_test_account()
    {
        var settings = StagingSettings();
        settings["Auth:DevSignIn:TestAccounts:0:Username"] = "";
        settings["Auth:DevSignIn:TestAccounts:0:Email"] = "";
        using var factory = new DevHostFactory(world.Database, DevAuthModule.StagingEnvironment, settings);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("needs a username and an email", Flatten(error));
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
            b.UseSetting("ConnectionStrings:App", world.Database.AppConnectionString);
        });
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public async Task Production_host_has_no_test_account_sign_in()
    {
        var response = await world.Factory.TenantClient(world.A.Key).PostAsJsonAsync("/dev/signin", new { username = "admin" });
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
