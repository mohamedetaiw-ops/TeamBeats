using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>
/// Every hour, for every active tenant in its own scope and transaction: monthly or yearly accrual and the carry-over of
/// the leave year that ended (spec 022 R2 plan, "Jobs"). Each period is written once per person and type, so running
/// often is safe. Runs as the system actor. Hosted by the worker, and by the development host when
/// <c>Hr:Leave:InProcess</c> is true.
/// </summary>
public sealed class LeaveMaintenanceWorker(IServiceScopeFactory scopes, ILogger<LeaveMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Hr:Leave:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

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
            logger.LogWarning(ex, "Leave accrual could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<LeaveLedger>().RunAccrualAsync(ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Leave accrual failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
