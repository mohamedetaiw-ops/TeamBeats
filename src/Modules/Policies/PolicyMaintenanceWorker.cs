using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Every minute, for every active tenant in its own scope and transaction: puts approved policy versions in force on
/// their effective date (spec 025 US1 scenario 3), sends campaign reminders and marks members overdue (US2 scenario 3). Runs as the system actor. Hosted by the worker, and by the
/// development host when <c>Policies:Maintenance:InProcess</c> is true.
/// </summary>
public sealed class PolicyMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<PolicyMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Policies:Maintenance:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Policy maintenance could not list tenants");
            return;
        }
        var now = clock.UtcNow;
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<PolicyApprovals>().RunDueAsync(now, 200, ct);
                await scope.ServiceProvider.GetRequiredService<PolicyCampaigns>().RunDueAsync(now, 1000, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Policy maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
