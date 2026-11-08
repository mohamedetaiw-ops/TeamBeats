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
        builder.Services.AddSingleton<StagingHomeSeeder>();
        builder.Services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, ReferencePacks>();
        builder.AddFakeExternalSystems();
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Notifications.NotificationDeliveryWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Notifications.NotificationDeliveryWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Workflow.EngineJobWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Workflow.EngineJobWorker>();
        }
        var app = builder.Build();
        app.UseTeamBeatsApi(endpoints =>
        {
            endpoints.MapDevAuthEndpoints(app.Configuration);
            TeamBeats.Modules.Notifications.NotificationsModule.MapNotificationDevEndpoints(endpoints);
        });
        app.UseFakeExternalSystems();

        if (app.Configuration.GetConnectionString("Owner") is { } owner && app.Configuration.GetValue<bool>("Dev:MigrateOnStart"))
        {
            await MigrationRunner.RunAsync(owner, TeamBeatsModules.Migrations);
        }

        if (app.Configuration.GetValue<bool>(DevAuthModule.EnabledKey))
        {
            // Each seeder commits, and a database session is one transaction, so each gets its own scope.
            if (app.Configuration["Dev:SeedFile"] is { } seed)
            {
                await using var scope = app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DevSeeder>().SeedAsync(seed, CancellationToken.None);
            }
            if (app.Configuration.GetSection(TestTenantSeeder.Section).Get<CreateTenantRequest>() is { Key.Length: > 0 } tenant)
            {
                await using (var scope = app.Services.CreateAsyncScope())
                {
                    await scope.ServiceProvider.GetRequiredService<TestTenantSeeder>().SeedAsync(tenant, CancellationToken.None);
                }
                if (FakeSystemsSetup.Enabled(app.Configuration))
                {
                    await app.Services.GetRequiredService<ScenarioSeeder>().SeedAsync(tenant.Key, CancellationToken.None);
                }
                await app.Services.GetRequiredService<StagingHomeSeeder>().SeedAsync(tenant.Key, CancellationToken.None);
            }
        }

        await app.RunAsync();
    }
}
