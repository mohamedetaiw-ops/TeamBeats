using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The public API clock (spec 035 US1 scenario 6): API administrators are told once when a live credential of an
/// active app has 14 days or less left, so a second credential can be added and the app moved to it before the first
/// one stops working. Used client assertion IDs are cleared once they could no longer be replayed anyway, and calls older
/// than 90 days leave the call log.
/// </summary>
public sealed class PublicApiMaintenance(IDbSession session, IAuthorizationProvisioning authorization, INotificationSender notifications, CallLog calls,
    PublicIdempotency idempotency)
{
    public const string ExpiringTemplate = "publicapi.credential_expiring";
    public static readonly TimeSpan AlertBefore = TimeSpan.FromDays(14);
    public const int Batch = 200;

    public sealed record Run(int Alerted, int AssertionsCleared, int CallsPruned = 0);

    public async Task<Run> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            update publicapi.app_credential c set expiry_alerted_at = @now
            from publicapi.app a
            where a.id = c.app_id and a.status = 'active' and c.revoked_at is null and c.expiry_alerted_at is null
              and c.expires_at > @now and c.expires_at <= @horizon
              and c.id in (select x.id from publicapi.app_credential x where x.revoked_at is null and x.expiry_alerted_at is null
                           and x.expires_at > @now and x.expires_at <= @horizon order by x.expires_at limit @batch)
            returning c.id, c.app_id, a.name, c.expires_at
            """, new { now, horizon = now.Add(AlertBefore), batch = Batch },
            r => (Id: r.GetGuid(0), App: r.GetGuid(1), Name: r.GetString(2), Expires: r.GetFieldValue<DateTimeOffset>(3)), ct);
        if (due.Count > 0)
        {
            var admins = await authorization.TenantHoldersAsync(Permissions.ApiManage, ct);
            foreach (var c in due.Where(_ => admins.Count > 0))
            {
                var days = Math.Max(1, (int)Math.Ceiling((c.Expires - now).TotalDays));
                await notifications.SendAsync(new NotificationRequest(admins, NotificationCategories.IntegrationAlert, ExpiringTemplate, $"/admin/api/apps/{c.App}",
                    new Dictionary<string, string> { ["app"] = c.Name, ["days"] = days.ToString(CultureInfo.InvariantCulture) },
                    DedupeKey: $"publicapi:{c.Id:N}:expiring"), ct);
            }
        }
        var cleared = await session.ExecAsync("delete from publicapi.client_assertion where expires_at < @now", new { now }, ct);
        // The call log keeps 90 days (section 6).
        var pruned = await calls.PruneAsync(now, ct);
        await idempotency.PruneAsync(now, ct);
        return new Run(due.Count, cleared, pruned);
    }
}

/// <summary>
/// Runs <see cref="PublicApiMaintenance"/> every hour for every active tenant, each in its own scope and transaction,
/// as the system actor. Hosted by the worker, and by the development host when <c>PublicApi:Maintenance:InProcess</c> is true.
/// </summary>
public sealed class PublicApiMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<PublicApiMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "PublicApi:Maintenance:InProcess";
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
            logger.LogWarning(ex, "Public API maintenance could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<PublicApiMaintenance>().RunDueAsync(now, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Public API maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
