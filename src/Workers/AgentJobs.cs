using TeamBeats.Modules.Agents;
using TeamBeats.Primitives;

namespace TeamBeats.Workers;

/// <summary>Daily assistant retention (spec 014 T001, FR-008): idle conversations past the tenant period, and old feedback.</summary>
public sealed class AssistantRetentionService(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PerTenantRunner(scopes, logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AssistantHistory>().PurgeAsync(sp.GetRequiredService<IClock>().UtcNow, ct), stoppingToken);
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
