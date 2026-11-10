using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// Runs due connector jobs (spec 017 FR-005) for every active tenant, each in its own scope, transaction and tenant context, every 10 seconds;
/// purges run history and dead letters once a day. Suspended tenants are skipped, so their jobs wait. Hosted
/// by the worker, and by the development host when <c>Integrations:Jobs:InProcess</c> is true.
/// </summary>
public sealed class ConnectorJobWorker(IServiceScopeFactory scopes, IClock clock, ILogger<ConnectorJobWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Integrations:Jobs:InProcess";
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

    private async Task ForEachTenantAsync(Func<ConnectorJobs, CancellationToken, Task> action, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Connector jobs could not list tenants");
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
                await action(scope.ServiceProvider.GetRequiredService<ConnectorJobs>(), ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Connector jobs failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}

/// <summary>The worker's view of the runtime: due attempts and the daily purge.</summary>
public sealed class ConnectorJobs(ConnectorRuntime runtime, RunHistory history)
{
    public Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct) => runtime.RunDueAsync(now, Math.Min(limit, 50), ct);

    public Task PurgeAsync(DateTimeOffset now, CancellationToken ct) => history.PurgeAsync(now, ct);
}
