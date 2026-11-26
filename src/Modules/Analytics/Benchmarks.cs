using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// One benchmark: this tenant's own figure for the week and, while at least <see cref="AnalyticsBenchmarks.MinTenants"/>
/// tenants gave one, the quartiles across them. <paramref name="Tenants"/> is how many tenants gave a figure.
/// </summary>
public sealed record BenchmarkFigure(string Metric, DateOnly Week, decimal? Own, int Tenants, decimal? Lower, decimal? Median, decimal? Upper);

/// <summary>
/// Benchmarks for the tenant. <paramref name="Available"/> follows the package; <paramref name="Enabled"/> is the
/// tenant's choice to take part, without which nothing is given or shown.
/// </summary>
public sealed record BenchmarksView(bool Available, bool Enabled, bool CanChange, int MinTenants, IReadOnlyList<BenchmarkFigure> Figures);

public sealed record BenchmarksRequest(bool? Enabled);

/// <summary>
/// Benchmarks across anonymized tenants with consent (spec 021 R3, section 7, open question default opt in only). A
/// tenant administrator opts the tenant in; from then on the maintenance job gives one figure per metric for the last
/// complete week, already aggregated for the whole tenant, and the tenant sees the quartiles across the tenants that
/// gave one. Fewer than five tenants show nothing, no tenant is ever named, and opting out removes every figure given.
/// Figures sit behind security definer functions in the database (docs/architecture/rls-exceptions.md).
/// </summary>
public sealed class AnalyticsBenchmarks(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    ITenantMemberships memberships, AnalyticsPolicies policies, IAuditWriter audit, IClock clock)
{
    public const int MinTenants = 5;
    public const string AuditTarget = "benchmarks";

    /// <summary>Share of active members who were active in the week, in percent.</summary>
    public const string WeeklyActiveShare = "weekly_active_share";

    public static readonly IReadOnlyList<string> Metrics = [WeeklyActiveShare];

    private DateOnly LastWeek() => Adoption.WeekOf(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)).AddDays(-7);

    private Task<bool> EnabledAsync(CancellationToken ct) =>
        session.ScalarAsync<bool?>("select benchmarks from analytics.policy", null, ct).ContinueWith(t => t.Result == true, ct);

    /// <summary>This tenant's figure for a metric and week, or null when its members are fewer than the minimum group size.</summary>
    private async Task<decimal?> OwnAsync(string metric, DateOnly week, CancellationToken ct)
    {
        var members = (await memberships.ListAsync(ct)).Count(m => m.Status == MembershipStatus.Active);
        if (members < await policies.MinGroupSizeAsync(ct))
        {
            return null;
        }
        var active = await session.ScalarAsync<int?>("select active_users from analytics.adoption_weekly where week = @week and org_unit_id = @whole",
            new { week, whole = AnalyticsAggregator.WholeTenant }, ct) ?? 0;
        return metric switch
        {
            WeeklyActiveShare => Math.Round(Math.Min(100m, 100m * active / members), 1),
            _ => null,
        };
    }

    private async Task<bool> MayChangeAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<BenchmarksView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        context.RequireUser();
        var canChange = await MayChangeAsync(ct);
        if (!canChange && !(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var available = await entitlements.IsEnabledAsync(Capabilities.AnalyticsBenchmarks, ct);
        var enabled = available && await EnabledAsync(ct);
        var figures = new List<BenchmarkFigure>();
        if (enabled)
        {
            var week = LastWeek();
            foreach (var metric in Metrics)
            {
                var rows = await session.QueryAsync("select tenants, p25, p50, p75 from analytics.benchmark_figures(@metric, @week)", new { metric, week },
                    r => (Tenants: r.GetInt32(0), P25: r.IsDBNull(1) ? (decimal?)null : r.GetDecimal(1), P50: r.IsDBNull(2) ? (decimal?)null : r.GetDecimal(2),
                        P75: r.IsDBNull(3) ? (decimal?)null : r.GetDecimal(3)), ct);
                var b = rows.Count == 0 ? (Tenants: 0, P25: null, P50: null, P75: null) : rows[0];
                figures.Add(new BenchmarkFigure(metric, week, await OwnAsync(metric, week, ct), b.Tenants, b.P25, b.P50, b.P75));
            }
        }
        return new BenchmarksView(available, enabled, canChange && available, MinTenants, figures);
    }

    /// <summary>A tenant administrator opts the tenant in or out, audited as <c>analytics.benchmarks_changed</c>. Opting out removes every figure given.</summary>
    public async Task<BenchmarksView> ChangeAsync(BenchmarksRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        var user = context.RequireUser();
        if (!await MayChangeAsync(ct))
        {
            throw AppException.Denied();
        }
        if (request.Enabled is not { } enabled)
        {
            throw AppException.Validation("enabled", "validation.analytics.benchmarks_choice");
        }
        if (enabled && !await entitlements.IsEnabledAsync(Capabilities.AnalyticsBenchmarks, ct))
        {
            throw AppException.Validation("enabled", "validation.analytics.benchmarks_package");
        }
        var before = await EnabledAsync(ct);
        await session.ExecAsync(
            """
            insert into analytics.policy (tenant_id, version, updated_at, updated_by, benchmarks, benchmarks_week)
            values (platform.current_tenant_id(), 1, @now, @user, @enabled, null)
            on conflict (tenant_id) do update set benchmarks = excluded.benchmarks, benchmarks_week = null, version = analytics.policy.version + 1,
              updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { now = clock.UtcNow, user, enabled }, ct);
        if (!enabled)
        {
            await session.ScalarAsync<int>("select analytics.withdraw_benchmarks()", null, ct);
        }
        else
        {
            await ContributeAsync(ct);
        }
        if (before != enabled)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AnalyticsBenchmarksChanged, AuditTarget, null,
                Changes: new Dictionary<string, object> { ["enabled"] = new { before, after = enabled } }), ct);
        }
        return await GetAsync(ct);
    }

    /// <summary>
    /// Run by the maintenance job for each tenant: gives this tenant's figures for the last complete week once per week
    /// while it takes part. Returns how many figures were given.
    /// </summary>
    public async Task<int> ContributeAsync(CancellationToken ct)
    {
        var week = LastWeek();
        var state = await session.QueryAsync("select benchmarks, benchmarks_week from analytics.policy", null,
            r => (On: r.GetBoolean(0), Week: r.IsDBNull(1) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(1)), ct);
        if (state.Count == 0 || !state[0].On || state[0].Week == week || !await entitlements.IsEnabledAsync(Capabilities.AnalyticsBenchmarks, ct))
        {
            return 0;
        }
        var given = 0;
        foreach (var metric in Metrics)
        {
            if (await session.ScalarAsync<bool>("select analytics.contribute_benchmark(@metric, @week, @value)",
                new { metric, week, value = await OwnAsync(metric, week, ct) }, ct))
            {
                given++;
            }
        }
        await session.ExecAsync("update analytics.policy set benchmarks_week = @week", new { week }, ct);
        return given;
    }
}
