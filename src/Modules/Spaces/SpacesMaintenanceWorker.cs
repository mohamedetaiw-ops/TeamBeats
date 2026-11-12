using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

/// <summary>
/// Every minute, for every active tenant with spaces, in its own scope and transaction: sends the day-before due
/// reminders of space tasks (spec 029 US2), and ends, announces and re-sends guest access and invitations (US4). Runs
/// as the system actor. Hosted by the worker, and by the development host when <c>Spaces:Maintenance:InProcess</c> is true.
/// </summary>
public sealed class SpacesMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<SpacesMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Spaces:Maintenance:InProcess";
    public const int RemindersPerTick = 1000;
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
            logger.LogWarning(ex, "Spaces maintenance could not list tenants");
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
                if (!await scope.ServiceProvider.GetRequiredService<IEntitlementService>().IsEnabledAsync(Capabilities.SpacesProjects, ct))
                {
                    continue;
                }
                await scope.ServiceProvider.GetRequiredService<SpaceTasks>().RemindDueAsync(now, RemindersPerTick, ct);
                // Guest access ends on its date even after the plan or the tenant's rule stopped new guests (spec 026 US3 scenario 4).
                var guests = scope.ServiceProvider.GetRequiredService<SpaceGuests>();
                await guests.ExpireAsync(now, RemindersPerTick, ct);
                await guests.RemindExpiringAsync(now, RemindersPerTick, ct);
                await guests.RetryEmailsAsync(now, 100, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Spaces maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
