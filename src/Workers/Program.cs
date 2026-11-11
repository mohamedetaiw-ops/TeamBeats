using TeamBeats.Api;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Telemetry;

namespace TeamBeats.Workers;

/// <summary>
/// Worker host: outbox dispatch, message consumers, audit sealing and retention.
/// <c>TeamBeats.Workers migrate</c> applies database migrations as <c>tb_owner</c> and exits.
/// </summary>
public sealed class WorkerProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        ProductionGuards.EnsureDevSignInDisabled(builder.Configuration);
        ProductionGuards.EnsureMailIsSentInProduction(builder.Environment, builder.Configuration);

        if (args.Contains("migrate"))
        {
            var owner = builder.Configuration.GetConnectionString("Owner")
                ?? throw new InvalidOperationException("ConnectionStrings:Owner is required for migrations.");
            await MigrationRunner.RunAsync(owner, TeamBeatsModules.Migrations);
            return;
        }

        builder.Services.AddTeamBeatsCore(builder.Configuration);
        builder.Services.AddTeamBeatsTelemetry(builder.Configuration, "teambeats-workers");
        builder.Services.AddHealthChecks();
        builder.Services.AddHostedService<OutboxDispatcherService>();
        if (MessagingServiceCollectionExtensions.UsesRabbitMq(builder.Configuration))
        {
            builder.Services.AddHostedService<RabbitMqConsumerService>();
        }
        builder.Services.AddHostedService<AuditSealerService>();
        builder.Services.AddHostedService<AuditRetentionService>();
        // Staging turns this off: its development host delivers into the captured mailbox itself.
        if (builder.Configuration.GetValue("Workers:NotificationDelivery:Enabled", true))
        {
            builder.Services.AddHostedService<TeamBeats.Modules.Notifications.NotificationDeliveryWorker>();
        }
        builder.Services.AddHostedService<TeamBeats.Modules.Workflow.EngineJobWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Integrations.Runtime.ConnectorJobWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Content.ContentScheduleWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Documents.DocumentMaintenanceWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Policies.PolicyMaintenanceWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Hr.Leave.LeaveMaintenanceWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Hr.Journeys.JourneyWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Analytics.AnalyticsMaintenanceWorker>();
        builder.Services.AddHostedService<TeamBeats.Modules.Search.SearchBootstrapWorker>();
        builder.Services.AddHostedService<AssistantRetentionService>();
        // Staging turns this off: its fake directory lives in the development host, which runs the sync itself.
        // "Sync now" requests are then handed to that host's scheduler instead of reaching for a directory this host lacks.
        if (builder.Configuration.GetValue("Workers:DirectorySync:Enabled", true))
        {
            builder.Services.AddHostedService<DirectorySyncSchedulerService>();
        }
        else
        {
            TeamBeats.Modules.Identity.Directory.DirectorySyncHandOff.DeferDirectorySyncRequests(builder.Services);
        }

        var app = builder.Build();
        app.MapHealthChecks("/health/live");
        await app.RunAsync();
    }
}
