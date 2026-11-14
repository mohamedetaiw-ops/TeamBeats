using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>
/// Call record retention (spec 013 section 8, US4 R2): records older than 25 months are folded into one daily row per
/// unit, use case, agent, model, provider, classification and outcome, without the person, and deleted. The usage
/// view, forecast and export read both, so totals do not change when records are folded. Runs in batches so one tenant
/// with a long history never holds a long transaction.
/// </summary>
public sealed class UsageRetention(IDbSession session)
{
    public const int KeepMonths = 25;
    public const int Batch = 5_000;

    public static DateTimeOffset Cutoff(DateTimeOffset now)
    {
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return month.AddMonths(-KeepMonths);
    }

    /// <summary>Folds one batch of old records and returns how many were folded; zero means the tenant is done.</summary>
    public Task<long> RunBatchAsync(DateTimeOffset now, CancellationToken ct) => session.ScalarAsync<long>(
        """
        with gone as (
          delete from ai.usage_record
           where id in (select id from ai.usage_record where at < @cutoff order by at limit @batch)
          returning at, unit_id, user_id, use_case, agent_id, model, provider, classification, decision, denial, charged_to, input_tokens, output_tokens, cost
        ), folded as (
          insert into ai.usage_daily (tenant_id, day, unit_id, use_case, agent_id, model, provider, classification, decision, denial, charged_to,
                                      requests, input_tokens, output_tokens, cost, users)
          select platform.current_tenant_id(), (at at time zone 'UTC')::date, unit_id, use_case, agent_id, model, provider, classification, decision, denial, charged_to,
                 count(*), coalesce(sum(input_tokens), 0), coalesce(sum(output_tokens), 0), coalesce(sum(cost), 0), count(distinct user_id)
            from gone
           group by 2, 3, 4, 5, 6, 7, 8, 9, 10, 11
          on conflict on constraint usage_daily_key do update set
            requests = ai.usage_daily.requests + excluded.requests,
            input_tokens = ai.usage_daily.input_tokens + excluded.input_tokens,
            output_tokens = ai.usage_daily.output_tokens + excluded.output_tokens,
            cost = ai.usage_daily.cost + excluded.cost,
            users = greatest(ai.usage_daily.users, excluded.users)
          returning 1
        )
        select count(*) from gone
        """, new { cutoff = Cutoff(now), batch = Batch }, ct);
}

/// <summary>Runs <see cref="UsageRetention"/> for every active tenant once a day, a committed batch at a time.</summary>
public sealed class UsageRetentionWorker(IServiceScopeFactory scopes, IClock clock, ILogger<UsageRetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromHours(24);
    public const int MaxBatchesPerTenant = 200;

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
            logger.LogWarning(ex, "AI usage retention could not list tenants");
            return;
        }
        var now = clock.UtcNow;
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                for (var i = 0; i < MaxBatchesPerTenant; i++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                    context.SetTenant(tenant.Id);
                    context.SetActor(ActorType.System, null);
                    var folded = await scope.ServiceProvider.GetRequiredService<UsageRetention>().RunBatchAsync(now, ct);
                    await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
                    if (folded < UsageRetention.Batch)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "AI usage retention failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
