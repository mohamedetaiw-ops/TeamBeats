using TeamBeats.Api;
using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;

namespace TeamBeats.Api.DevHost;

/// <summary>
/// Development, Test and Staging host (ADR-0004). Refuses to run in any other environment. Staging is the shared test
/// deployment and must use test-account-mode development sign-in (spec 003 US7).
/// </summary>
public sealed class DevHostProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!DevAuthModule.IsAllowed(builder.Environment))
        {
            throw new InvalidOperationException("The development host only runs in Development, Test or Staging.");
        }

        builder.AddTeamBeatsApi();
        builder.Services.AddDevAuth(builder.Configuration, builder.Environment);
        builder.Services.AddScoped<TestTenantSeeder>();
        var app = builder.Build();
        app.UseTeamBeatsApi(endpoints => endpoints.MapDevAuthEndpoints(app.Configuration));

        if (app.Configuration.GetConnectionString("Owner") is { } owner && app.Configuration.GetValue<bool>("Dev:MigrateOnStart"))
        {
            await MigrationRunner.RunAsync(owner, TeamBeatsModules.Migrations);
        }

        if (app.Configuration.GetValue<bool>(DevAuthModule.EnabledKey))
        {
            await using var scope = app.Services.CreateAsyncScope();
            if (app.Configuration["Dev:SeedFile"] is { } seed)
            {
                await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(seed, CancellationToken.None);
            }
            if (app.Configuration.GetSection(TestTenantSeeder.Section).Get<CreateTenantRequest>() is { Key.Length: > 0 } tenant)
            {
                await scope.ServiceProvider.GetRequiredService<TestTenantSeeder>().SeedAsync(tenant, CancellationToken.None);
            }
        }

        await app.RunAsync();
    }
}
