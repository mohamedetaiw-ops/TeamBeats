using TeamBeats.Modules.Agents;
using TeamBeats.Primitives;

namespace TeamBeats.Workers;

/// <summary>Assistant and agent retention every 6 hours (spec 014 T001, FR-008; spec 027 section 8): idle conversations, old feedback and old run records.</summary>
public sealed class AssistantRetentionService(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PerTenantRunner(scopes, logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AssistantHistory>().PurgeAsync(sp.GetRequiredService<IClock>().UtcNow, ct), stoppingToken);
            // Agent execution records past retention (spec 027 section 8) and waiting runs whose card expired.
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentRuns>().PurgeAsync(sp.GetRequiredService<IClock>().UtcNow, ct), stoppingToken);
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
