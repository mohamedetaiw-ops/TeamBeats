using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// Rolls raw events into the aggregates (spec 021 T004, section 15: counts fresh within 15 minutes). Each run reads the
/// tenant's events after the last one it read, in sequence order and at most <c>limit</c> at a time:
/// <list type="bullet">
/// <item><c>content.viewed</c> adds views per item, day and unit, records each person's first read of an item, and
/// counts new readers on that day and unit, so unique readers outlive raw events (section 8).</item>
/// <item>Every event with a user key makes its person active in that ISO week: the weeks the batch touches are counted
/// again from raw events, per unit and for the whole tenant, so late events (up to 7 days) land in their own week.</item>
/// </list>
/// The zero UUID stands for no unit and the all-ones UUID for the whole tenant.
/// </summary>
public sealed class AnalyticsAggregator(IDbSession session, IClock clock)
{
    public static readonly Guid NoUnit = Guid.Empty;
    public static readonly Guid WholeTenant = new("ffffffff-ffff-ffff-ffff-ffffffffffff");

    /// <returns>How many events were read.</returns>
    public async Task<int> RunAsync(int limit, CancellationToken ct)
    {
        var last = await session.ScalarAsync<long?>("select last_seq from analytics.aggregate_state", null, ct) ?? 0;
        var upto = await session.ScalarAsync<long?>(
            "select max(seq) from (select seq from analytics.event where seq > @last order by seq limit @limit) b", new { last, limit }, ct);
        if (upto is not { } end)
        {
            return 0;
        }
        var args = new { last, end, noUnit = NoUnit, whole = WholeTenant, viewed = AnalyticsEvents.ContentViewed };

        // First reads: one row per person and item, kept with the unit of that first read.
        await session.ExecAsync(
            """
            with batch as (
              select distinct on (fields->>'source', (fields->>'itemId')::uuid, user_key)
                fields->>'source' as source, (fields->>'itemId')::uuid as item_id, user_key, coalesce(org_unit_id, @noUnit) as unit, occurred_at
              from analytics.event
              where seq > @last and seq <= @end and name = @viewed and user_key is not null
              order by fields->>'source', (fields->>'itemId')::uuid, user_key, occurred_at
            ), added as (
              insert into analytics.item_reader (tenant_id, source, item_id, user_key, org_unit_id, first_at)
              select platform.current_tenant_id(), source, item_id, user_key, unit, occurred_at from batch
              on conflict do nothing
              returning source, item_id, org_unit_id, first_at
            )
            insert into analytics.item_daily (tenant_id, source, item_id, day, org_unit_id, views, readers)
            select platform.current_tenant_id(), source, item_id, (first_at at time zone 'UTC')::date, org_unit_id, 0, count(*)
            from added group by source, item_id, (first_at at time zone 'UTC')::date, org_unit_id
            on conflict (tenant_id, source, item_id, day, org_unit_id) do update set readers = analytics.item_daily.readers + excluded.readers
            """, args, ct);

        // Views per item, day and unit.
        await session.ExecAsync(
            """
            insert into analytics.item_daily (tenant_id, source, item_id, day, org_unit_id, views, readers)
            select platform.current_tenant_id(), fields->>'source', (fields->>'itemId')::uuid, (occurred_at at time zone 'UTC')::date,
              coalesce(org_unit_id, @noUnit), count(*), 0
            from analytics.event
            where seq > @last and seq <= @end and name = @viewed
            group by fields->>'source', (fields->>'itemId')::uuid, (occurred_at at time zone 'UTC')::date, coalesce(org_unit_id, @noUnit)
            on conflict (tenant_id, source, item_id, day, org_unit_id) do update set views = analytics.item_daily.views + excluded.views
            """, args, ct);

        // Active people in each week the batch touches, counted again from the raw events of that week.
        await session.ExecAsync(
            """
            with weeks as (
              select distinct date_trunc('week', occurred_at at time zone 'UTC')::date as week
              from analytics.event where seq > @last and seq <= @end and user_key is not null
            ), counted as (
              select w.week,
                case when grouping(coalesce(e.org_unit_id, @noUnit)) = 1 then @whole else coalesce(e.org_unit_id, @noUnit) end as unit,
                count(distinct e.user_key)::int as active
              from weeks w
              join analytics.event e on e.user_key is not null
                and e.occurred_at >= (w.week::timestamp at time zone 'UTC') and e.occurred_at < ((w.week + 7)::timestamp at time zone 'UTC')
              group by grouping sets ((w.week, coalesce(e.org_unit_id, @noUnit)), (w.week))
            )
            insert into analytics.adoption_weekly (tenant_id, week, org_unit_id, active_users)
            select platform.current_tenant_id(), week, unit, active from counted
            on conflict (tenant_id, week, org_unit_id) do update set active_users = excluded.active_users
            """, args, ct);

        await session.ExecAsync(
            """
            insert into analytics.aggregate_state (tenant_id, last_seq, updated_at) values (platform.current_tenant_id(), @end, @now)
            on conflict (tenant_id) do update set last_seq = excluded.last_seq, updated_at = excluded.updated_at
            """, new { end, now = clock.UtcNow }, ct);
        return await session.ScalarAsync<int>("select count(*)::int from analytics.event where seq > @last and seq <= @end", new { last, end }, ct);
    }
}

/// <summary>Removes raw events older than the tenant's retention (FR-014), at most <c>limit</c> per run. Aggregates stay.</summary>
public sealed class AnalyticsRetention(IDbSession session, AnalyticsPolicies policies)
{
    public async Task<int> PurgeAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var policy = await policies.GetAsync(ct);
        var before = now.AddMonths(-policy.RawRetentionMonths);
        return await session.ExecAsync(
            """
            delete from analytics.event where (tenant_id, seq) in (
              select tenant_id, seq from analytics.event where occurred_at < @before order by occurred_at limit @limit)
            """, new { before, limit }, ct);
    }
}

/// <summary>
/// Every minute, for every active tenant in its own scope and transaction: aggregates new events and removes raw events
/// past retention. Runs as the system actor. Hosted by the worker, and by the development host when
/// <c>Analytics:Maintenance:InProcess</c> is true.
/// </summary>
public sealed class AnalyticsMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<AnalyticsMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Analytics:Maintenance:InProcess";
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
            logger.LogWarning(ex, "Analytics maintenance could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<AnalyticsAggregator>().RunAsync(5000, ct);
                await scope.ServiceProvider.GetRequiredService<AnalyticsRetention>().PurgeAsync(clock.UtcNow, 10_000, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Analytics maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
