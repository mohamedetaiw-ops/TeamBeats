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

    /// <summary>How long one tick may keep draining tenants whose due jobs fill whole batches (spec 017: 1,000 jobs a minute).</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(8);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastPurge = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            var purge = now - lastPurge >= TimeSpan.FromHours(24);
            var busy = await ForEachTenantAsync(null, async (jobs, ct) =>
            {
                var n = await jobs.RunDueAsync(now, ConnectorJobs.BatchSize, ct);
                if (purge)
                {
                    await jobs.PurgeAsync(now, ct);
                }
                return n;
            }, stoppingToken);
            lastPurge = purge ? now : lastPurge;
            await DrainAsync(busy, stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    /// <summary>
    /// Runs further batches, each in its own transaction, for the tenants whose last batch was full, until none is full
    /// or the budget is spent. Every tenant gets one batch per tick before any tenant gets a second.
    /// </summary>
    private async Task DrainAsync(IReadOnlyList<TenantInfo> busy, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (busy.Count > 0 && watch.Elapsed < DrainBudget)
        {
            busy = await ForEachTenantAsync(busy, (jobs, c) => jobs.RunBatchAsync(clock.UtcNow, c), ct);
        }
    }

    /// <summary>Runs <paramref name="action"/> for each active tenant (or each of <paramref name="only"/>); returns the tenants whose batch was full.</summary>
    private async Task<IReadOnlyList<TenantInfo>> ForEachTenantAsync(IReadOnlyList<TenantInfo>? only, Func<ConnectorJobs, CancellationToken, Task<int>> action, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        if (only is not null)
        {
            tenants = only;
        }
        else
        {
            try
            {
                await using var listScope = scopes.CreateAsyncScope();
                tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Connector jobs could not list tenants");
                return [];
            }
        }

        var full = new List<TenantInfo>();
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                var ran = await action(scope.ServiceProvider.GetRequiredService<ConnectorJobs>(), ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
                if (ran >= ConnectorJobs.BatchSize)
                {
                    full.Add(tenant);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Connector jobs failed for tenant {TenantId}", tenant.Id);
            }
        }
        return full;
    }
}

/// <summary>
/// The worker's view of the runtime: on every tick due attempts, sync agent status, expired payload captures, the daily
/// clock data imports and finance payment status checks; once a day the purge and the credential expiry check.
/// </summary>
public sealed class ConnectorJobs(ConnectorRuntime runtime, RunHistory history, PackConnections connections, Webhooks.InboundWebhooks inbound,
    SyncAgents.SyncAgentService agents, PayloadCapture capture,
    ClockData.ClockImports clockImports, ClockData.ClockImportStore clockData, Finance.FinanceHandOffs finance, Webhooks.KpiFeed kpis)
{
    /// <summary>Due attempts run in one transaction.</summary>
    public const int BatchSize = 50;

    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await agents.RefreshAsync(now, ct);
        await capture.PurgeAsync(now, ct);
        await clockImports.ScheduleDueAsync(now, ct);
        await finance.CheckDueAsync(now, 20, ct);
        return await runtime.RunDueAsync(now, Math.Min(limit, BatchSize), ct);
    }

    /// <summary>One more batch of due attempts, without the housekeeping of a tick.</summary>
    public Task<int> RunBatchAsync(DateTimeOffset now, CancellationToken ct) => runtime.RunDueAsync(now, BatchSize, ct);

    public async Task PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        await history.PurgeAsync(now, ct);
        await connections.RefreshExpiryAsync(null, ct);
        await inbound.PurgeAsync(now, ct);
        await clockData.PurgeAsync(now, ct);
        await kpis.PurgeAsync(now, ct);
    }
}
