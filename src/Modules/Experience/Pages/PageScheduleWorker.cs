using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Pages;

/// <summary>
/// Every 30 seconds, for every active tenant in its own scope and transaction: puts scheduled page releases live and ends
/// due expiries (spec 032 R2 T025), so a release goes live within a minute of its time. Runs as the system actor. Hosted
/// by the worker, and by the development host when <c>Experience:Schedule:InProcess</c> is true.
/// </summary>
public sealed class PageScheduleWorker(IServiceScopeFactory scopes, IClock clock, ILogger<PageScheduleWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Experience:Schedule:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

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
            logger.LogWarning(ex, "Page schedule could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<PageSchedules>().RunDueAsync(now, 200, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Page schedule failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
