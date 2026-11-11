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
        builder.Services.AddSingleton<Demo.DemoDataSeeder>();
        // The demo data seeder dates what it seeds over the past weeks; outside it this is the system clock.
        builder.Services.AddSingleton<TeamBeats.Primitives.IClock, Demo.DemoClock>();
        builder.Services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, ReferencePacks>();
        builder.AddFakeExternalSystems();
        if (builder.Configuration[TeamBeats.Persistence.ClamAvScanner.HostKey] is not { Length: > 0 })
        {
            // Development and staging without ClamAV: the signature scanner flags the EICAR test file only.
            builder.Services.AddSingleton<TeamBeats.Persistence.IMalwareScanner, TeamBeats.Persistence.TestSignatureScanner>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Notifications.NotificationDeliveryWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Notifications.NotificationDeliveryWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Workflow.EngineJobWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Workflow.EngineJobWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Integrations.Runtime.ConnectorJobWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Integrations.Runtime.ConnectorJobWorker>();
        }
        // Local development runs without the workers host, so the development host dispatches the outbox itself when
        // asked; staging leaves this off because its workers app does it. Dispatch takes rows with skip locked, so a
        // workers host running as well does not deliver twice.
        if (builder.Configuration.GetValue<bool>("Messaging:Outbox:InProcess"))
        {
            builder.Services.AddHostedService<TeamBeats.Messaging.OutboxDispatcherService>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Content.ContentScheduleWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Content.ContentScheduleWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Documents.DocumentMaintenanceWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Documents.DocumentMaintenanceWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Policies.PolicyMaintenanceWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Policies.PolicyMaintenanceWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Hr.Leave.LeaveMaintenanceWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Hr.Leave.LeaveMaintenanceWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Analytics.AnalyticsMaintenanceWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Analytics.AnalyticsMaintenanceWorker>();
        }
        if (builder.Configuration.GetValue<bool>(TeamBeats.Modules.Search.SearchBootstrapWorker.InProcessKey))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Search.SearchBootstrapWorker>();
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
                await app.Services.GetRequiredService<Demo.DemoDataSeeder>().SeedAsync(tenant.Key, CancellationToken.None);
            }
        }

        await app.RunAsync();
    }
}
