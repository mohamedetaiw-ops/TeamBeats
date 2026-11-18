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
            // Agent memory unused past its agent's retention, and memory of agents that no longer keep it (spec 020 US9).
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentMemory>().PurgeAsync(sp.GetRequiredService<IClock>().UtcNow, ct), stoppingToken);
            // Marketplace ratings past two years (spec 020 section 8).
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentMarketplace>().PurgeAsync(sp.GetRequiredService<IClock>().UtcNow, ct), stoppingToken);
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}

/// <summary>Daily agent briefings (spec 020 US8, FR-014): every minute, the due briefings of each tenant run as their subscribers.</summary>
public sealed class AgentBriefingService(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PerTenantRunner(scopes, logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentBriefingRunner>().RunDueAsync(ct), stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}

/// <summary>Agent runs a restart cut off (spec 020 T021) and agent actions an approver decided (spec 027 US4): every minute, each tenant's runs go on as their people.</summary>
public sealed class AgentRunResumeService(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PerTenantRunner(scopes, logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentRunResumer>().ResumeStalledAsync(ct), stoppingToken);
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentRunResumer>().ApplyApprovalsAsync(ct), stoppingToken);
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AgentSecurityCenter>().ScanAsync(ct), stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
