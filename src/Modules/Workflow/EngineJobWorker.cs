using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

/// <summary>
/// Runs due engine jobs for every active tenant, each in its own scope, transaction and tenant context, every 10 seconds;
/// purges old jobs once a day. Suspended tenants are skipped, so their timers wait and run once on reactivation. Hosted
/// by the worker, and by the development host when <c>Workflow:Jobs:InProcess</c> is true.
/// </summary>
public sealed class EngineJobWorker(IServiceScopeFactory scopes, IClock clock, ILogger<EngineJobWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Workflow:Jobs:InProcess";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            var purge = now - lastPurge >= TimeSpan.FromHours(24);
            await ForEachTenantAsync(async (jobs, ct) =>
            {
                await jobs.RunDueAsync(now, 200, ct);
                if (purge)
                {
                    await jobs.PurgeAsync(now, ct);
                }
            }, stoppingToken);
            lastPurge = purge ? now : lastPurge;
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task ForEachTenantAsync(Func<EngineJobs, CancellationToken, Task> action, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Engine jobs could not list tenants");
            return;
        }

        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await action(scope.ServiceProvider.GetRequiredService<EngineJobs>(), ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Engine jobs failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
