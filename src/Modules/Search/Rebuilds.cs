using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Search;

public sealed record RebuildView(Guid Id, DateTimeOffset RequestedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int Items, int Units);

/// <summary>
/// Full rebuilds of a tenant's index from the owners (spec 012 US2 scenario 4, FR-012). Every item each owner lists is
/// read again, then rows the rebuild did not touch are removed, so the index ends up exactly what the owners give now.
/// Starting and finishing are operational events (section 13) written to the log.
/// </summary>
public sealed class SearchRebuilds(IDbSession session, ITenantContext context, IAuthorizationService authz, IOutbox outbox, IEnumerable<ISearchSource> sources,
    SearchIndexer indexer, RetrievalStore store, IClock clock, ILogger<SearchRebuilds> logger) : IMessageHandler<SearchRebuildRequested>
{
    public const int PageSize = 200;

    public async Task<RebuildView> RequestAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
        return await QueueAsync(context.RequireUser(), ct);
    }

    internal async Task<RebuildView> QueueAsync(Guid? by, CancellationToken ct)
    {
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync("insert into search.rebuild (tenant_id, id, requested_at, requested_by) values (platform.current_tenant_id(), @id, @now, @by)",
            new { id, now, by }, ct);
        await outbox.EnqueueAsync(new SearchRebuildRequested(id), ct);
        return new RebuildView(id, now, null, null, 0, 0);
    }

    public async Task<IReadOnlyList<RebuildView>> ListAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SearchManage, AuthzTarget.Tenant, ct);
        return await session.QueryAsync(
            "select id, requested_at, started_at, completed_at, items, units from search.rebuild order by requested_at desc limit 20", null,
            r => new RebuildView(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.Get<DateTimeOffset?>("started_at"), r.Get<DateTimeOffset?>("completed_at"),
                r.GetInt32(4), r.GetInt32(5)), ct);
    }

    public async Task HandleAsync(SearchRebuildRequested message, CancellationToken ct)
    {
        var started = clock.UtcNow;
        var changed = await session.ExecAsync("update search.rebuild set started_at = @started where id = @id and started_at is null", new { id = message.RebuildId, started }, ct);
        if (changed == 0)
        {
            return;
        }
        logger.LogInformation("search.rebuild_started tenant {Tenant} rebuild {Rebuild}", context.TenantId, message.RebuildId);
        int items = 0, units = 0;
        foreach (var source in sources)
        {
            Guid? after = null;
            while (true)
            {
                var ids = await source.ItemIdsAsync(after, PageSize, ct);
                foreach (var id in ids)
                {
                    units += await indexer.IndexAsync(source.Source, id, ct);
                    items++;
                }
                if (ids.Count < PageSize)
                {
                    break;
                }
                after = ids[^1];
            }
        }
        // Rows of items no owner lists any more were not touched by this rebuild.
        await session.ExecAsync("delete from search.unit where synced_at < @started", new { started }, ct);
        await session.ExecAsync("delete from search.item_state where synced_at < @started", new { started }, ct);
        if (await store.OpenAsync(ct) is { Status: not RetrievalChangeStatuses.Building })
        {
            // The indexer wrote every listed item to the shadow index of the open change too.
            await session.ExecAsync("delete from search.shadow_unit where synced_at < @started", new { started }, ct);
        }
        await session.ExecAsync("update search.rebuild set completed_at = @now, items = @items, units = @units where id = @id",
            new { id = message.RebuildId, now = clock.UtcNow, items, units }, ct);
        logger.LogInformation("search.rebuild_completed tenant {Tenant} rebuild {Rebuild} items {Items} units {Units}", context.TenantId, message.RebuildId, items, units);
    }
}

/// <summary>
/// Gives every active tenant a first index: a tenant that never had a rebuild gets one (new tenants, and tenants that
/// existed before search). Runs as the system actor every ten minutes. Hosted by the worker, and by the development host
/// when <c>Search:Bootstrap:InProcess</c> is true.
/// </summary>
public sealed class SearchBootstrapWorker(IServiceScopeFactory scopes, ILogger<SearchBootstrapWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Search:Bootstrap:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Search bootstrap could not list tenants");
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
                var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
                if (await session.ScalarAsync<long>("select count(*) from search.rebuild", null, ct) == 0)
                {
                    await scope.ServiceProvider.GetRequiredService<SearchRebuilds>().QueueAsync(null, ct);
                }
                await session.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Search bootstrap failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
