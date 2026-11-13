using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Every 5 minutes, for every active tenant, in its own scope and transaction: starts the scheduled service requests
/// that are due (spec 016 FR-014) and marks requests at risk of or past their service level target (FR-015). Runs as
/// the system actor; a request starts once per schedule and period, and each alert goes once, whatever the number of hosts. Hosted by the worker, and by the development host when <c>Services:Schedules:InProcess</c> is true.
/// </summary>
public sealed class ServiceScheduleWorker(IServiceScopeFactory scopes, ILogger<ServiceScheduleWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Services:Schedules:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

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
            logger.LogWarning(ex, "Service schedules could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<ServiceTriggers>().RunSchedulesAsync(ct);
                await scope.ServiceProvider.GetRequiredService<ServiceLevels>().CheckAsync(ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Service schedules failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
