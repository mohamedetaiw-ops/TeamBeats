using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

/// <summary>
/// Runs email and Slack delivery for every tenant, each in its own scope, transaction and tenant context: due deliveries every
/// 5 seconds, digests every minute, retention once a day. Hosted by the worker, and by the development host when
/// <c>Notifications:Delivery:InProcess</c> is true.
/// </summary>
public sealed class NotificationDeliveryWorker(IServiceScopeFactory scopes, IClock clock, ILogger<NotificationDeliveryWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Notifications:Delivery:InProcess";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastDigest = DateTimeOffset.MinValue;
        var lastPurge = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.UtcNow;
            var digest = now - lastDigest >= TimeSpan.FromMinutes(1);
            var purge = now - lastPurge >= TimeSpan.FromHours(24);
            await ForEachTenantAsync(async (services, ct) =>
            {
                var delivery = services.GetRequiredService<EmailDelivery>();
                await delivery.DeliverDueAsync(now, 100, ct);
                await services.GetRequiredService<SlackDelivery>().DeliverDueAsync(now, 100, ct);
                if (digest)
                {
                    await delivery.SendDigestsAsync(now, ct);
                }
                if (purge)
                {
                    await delivery.PurgeAsync(now, ct);
                }
            }, stoppingToken);
            lastDigest = digest ? now : lastDigest;
            lastPurge = purge ? now : lastPurge;
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task ForEachTenantAsync(Func<IServiceProvider, CancellationToken, Task> action, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notification delivery could not list tenants");
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
                await action(scope.ServiceProvider, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification delivery failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
