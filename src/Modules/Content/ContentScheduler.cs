using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>
/// The timed moves of the lifecycle for one tenant: scheduled versions go live, live items past their expiry expire, and
/// expired items archive once the grace period ends (spec 009 section 6). Runs as the system actor.
/// </summary>
public sealed class ContentScheduler(IDbSession session, Items items, Sites sites)
{
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        foreach (var id in await DueAsync("status in ('scheduled', 'published', 'expired') and review_instance_id is null and publish_at is not null and publish_at <= @now", now, limit, ct))
        {
            if (await items.RowAsync(id, forUpdate: true, ct) is { PublishAt: { } at, DraftVersion: { } version, ReviewInstanceId: null } item && at <= now)
            {
                var site = (await sites.RowAsync(item.SiteId, false, ct))!;
                await items.GoLiveAsync(item, site, version, now, ct);
                moved++;
            }
        }
        foreach (var id in await DueAsync("status = 'published' and expires_at is not null and expires_at <= @now", now, limit, ct))
        {
            if (await items.RowAsync(id, forUpdate: true, ct) is { Status: ContentStatuses.Published, ExpiresAt: { } at } item && at <= now)
            {
                await items.ExpireRowAsync(item, (await sites.RowAsync(item.SiteId, false, ct))!, now, ct);
                moved++;
            }
        }
        var graceEnd = now - ContentLimits.ExpiredGrace;
        foreach (var id in await DueAsync("status = 'expired' and expired_at <= @now", graceEnd, limit, ct))
        {
            if (await items.RowAsync(id, forUpdate: true, ct) is { Status: ContentStatuses.Expired } item)
            {
                await items.ArchiveRowAsync(item, (await sites.RowAsync(item.SiteId, false, ct))!, "expired", ct);
                moved++;
            }
        }
        return moved;
    }

    private async Task<List<Guid>> DueAsync(string where, DateTimeOffset now, int limit, CancellationToken ct) =>
        await session.QueryAsync($"select id from content.content_item where {where} order by id limit @limit", new { now, limit }, r => r.GetGuid(0), ct);
}

/// <summary>
/// Runs <see cref="ContentScheduler"/> for every active tenant once a minute, each in its own scope and transaction.
/// Hosted by the worker, and by the development host when <c>Content:Schedule:InProcess</c> is true.
/// </summary>
public sealed class ContentScheduleWorker(IServiceScopeFactory scopes, IClock clock, ILogger<ContentScheduleWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Content:Schedule:InProcess";
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

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
            logger.LogWarning(ex, "Content schedule could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Content schedule failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
