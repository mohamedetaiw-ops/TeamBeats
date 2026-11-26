using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// One arm of an experiment: people who saw it, and of them who completed a task flow or opened an item after first
/// seeing it. The counts and rates are null when fewer people than the minimum group size saw the arm.
/// </summary>
public sealed record ExperimentArmResult(string Arm, int People, int? CompletedTask, int? OpenedItem, double? CompletionRate);

public sealed record ExperimentResultsView(Guid ExperimentId, int MinGroupSize, IReadOnlyList<ExperimentArmResult> Arms);

/// <summary>
/// Results of a governed experience experiment (spec 021 R3, US9 scenario 8, FR-022) from the exposure events the page
/// renderer sends. Counted by person per arm with the minimum group size, never per person, for whole tenant analytics
/// viewers under <c>analytics.experiments</c>.
/// </summary>
public sealed class ExperimentResults(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, AnalyticsPolicies policies)
{
    public async Task<ExperimentResultsView> GetAsync(Guid experimentId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExperiments, ct);
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var min = (await policies.GetAsync(ct)).MinGroupSize;
        // Each person once, in the arm they first saw; then whether they completed a task or opened an item after that.
        var rows = await session.QueryAsync(
            """
            with exposed as (
              select user_key, (array_agg(fields->>'arm' order by occurred_at))[1] as arm, min(occurred_at) as first_seen
              from analytics.event where name = @exposure and fields->>'experimentId' = @id and user_key is not null group by user_key)
            select x.arm, count(*),
              count(*) filter (where exists (select 1 from analytics.event e where e.user_key = x.user_key and e.name = @task
                and e.fields->>'outcome' = 'complete' and e.occurred_at >= x.first_seen)),
              count(*) filter (where exists (select 1 from analytics.event e where e.user_key = x.user_key and e.name = @viewed
                and e.occurred_at >= x.first_seen))
            from exposed x group by x.arm
            """, new { exposure = AnalyticsEvents.ExperimentExposure, id = experimentId.ToString(), task = AnalyticsEvents.TaskOutcome, viewed = AnalyticsEvents.ContentViewed },
            r => (Arm: r.GetString(0), People: (int)r.GetInt64(1), Completed: (int)r.GetInt64(2), Opened: (int)r.GetInt64(3)), ct);
        var arms = AnalyticsEvents.ExperimentArms.Order(StringComparer.Ordinal).Select(arm =>
        {
            var row = rows.FirstOrDefault(r => r.Arm == arm);
            return row.People < min || row.People == 0
                ? new ExperimentArmResult(arm, row.People, null, null, null)
                : new ExperimentArmResult(arm, row.People, row.Completed, row.Opened, Math.Round((double)row.Completed / row.People, 3));
        }).ToList();
        return new ExperimentResultsView(experimentId, min, arms);
    }
}
