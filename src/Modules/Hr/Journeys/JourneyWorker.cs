using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

/// <summary>
/// Every two minutes, for every active tenant with <c>hr.journeys</c> in its own scope and transaction: starts the
/// engine process of each journey step whose scheduled date has come (spec 022 R2 plan, slice 3). Runs as the system
/// actor. Hosted by the worker, and by the development host when <c>Hr:Journeys:InProcess</c> is true.
/// </summary>
public sealed class JourneyWorker(IServiceScopeFactory scopes, ILogger<JourneyWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Hr:Journeys:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);

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
            logger.LogWarning(ex, "Journey steps could not list tenants");
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
                if (!await scope.ServiceProvider.GetRequiredService<IEntitlementService>().IsEnabledAsync(Capabilities.HrJourneys, ct))
                {
                    continue;
                }
                await scope.ServiceProvider.GetRequiredService<JourneyService>().OpenDueAsync(null, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Journey steps failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
