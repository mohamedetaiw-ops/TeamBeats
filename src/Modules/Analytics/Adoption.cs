using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>Active people in one ISO week, for the whole tenant.</summary>
public sealed record AdoptionWeek(DateOnly Week, int ActiveUsers);

/// <summary>Active people of one unit in the latest week; <c>other</c> holds the units under the minimum group size.</summary>
public sealed record AdoptionUnit(string Group, IReadOnlyDictionary<string, string> Names, int ActiveUsers);

/// <summary>
/// Personal layout use in one week: how many people customized or reset a page, and how many times. All three are left
/// out (null) when fewer than the minimum group size did it.
/// </summary>
public sealed record PersonalizationWeek(DateOnly Week, int? People, int? Customized, int? Reset);

/// <summary>Adoption basics for tenant administrators (spec 021 T007, plan "Adoption basics").</summary>
public sealed record AdoptionView(
    int MinGroupSize,
    IReadOnlyList<AdoptionWeek> Weeks,
    DateOnly? UnitsWeek,
    IReadOnlyList<AdoptionUnit> Units,
    IReadOnlyList<PersonalizationWeek> Personalization,
    IReadOnlyList<TemplateUsage> Templates);

/// <summary>
/// Adoption basics (spec 021 T007): weekly active people for the last 12 weeks, active people by unit in the latest week
/// with units under the minimum group size merged into "other", personal layout use from <c>personalization.changed</c>
/// events, and pages per template and version as Experience reports them. For holders of <c>analytics.view</c> for the whole tenant.
/// </summary>
public sealed class Adoption(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context,
    AnalyticsPolicies policies, IOrganizationHierarchy organization, ITemplateUsage templates, IClock clock)
{
    public const int Weeks = 12;

    /// <summary>The Monday of the ISO week of a day.</summary>
    public static DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Unit counts under the minimum merged into "other", which takes the smallest shown units too while it is still under it.</summary>
    internal static IReadOnlyList<(string Group, int Count)> MergeCounts(IEnumerable<(string Group, int Count)> units, int minimum)
    {
        var shown = units.Where(u => u.Count > 0).ToList();
        var other = 0;
        foreach (var small in shown.Where(u => u.Count < minimum).ToList())
        {
            other += small.Count;
            shown.Remove(small);
        }
        while (other > 0 && other < minimum && shown.Count > 0)
        {
            var smallest = shown.OrderBy(u => u.Count).ThenBy(u => u.Group, StringComparer.Ordinal).First();
            other += smallest.Count;
            shown.Remove(smallest);
        }
        var result = shown.OrderByDescending(u => u.Count).ThenBy(u => u.Group, StringComparer.Ordinal).ToList();
        if (other > 0)
        {
            result.Add((ItemAnalytics.Other, other));
        }
        return result;
    }

    public async Task<AdoptionView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var policy = await policies.GetAsync(ct);
        var since = WeekOf(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime)).AddDays(-7 * (Weeks - 1));

        var weeks = await session.QueryAsync(
            "select week, active_users from analytics.adoption_weekly where org_unit_id = @whole and week >= @since order by week",
            new { whole = AnalyticsAggregator.WholeTenant, since }, r => new AdoptionWeek(r.GetFieldValue<DateOnly>(0), r.GetInt32(1)), ct);

        var latest = weeks.Count == 0 ? (DateOnly?)null : weeks[^1].Week;
        var units = new List<AdoptionUnit>();
        if (latest is { } week)
        {
            var rows = await session.QueryAsync(
                "select org_unit_id, active_users from analytics.adoption_weekly where week = @week and org_unit_id <> @whole",
                new { week, whole = AnalyticsAggregator.WholeTenant },
                r => (Group: r.GetGuid(0) == AnalyticsAggregator.NoUnit ? ItemAnalytics.NoUnit : r.GetGuid(0).ToString(), Count: r.GetInt32(1)), ct);
            var merged = MergeCounts(rows, policy.MinGroupSize);
            var ids = merged.Select(m => m.Group).Where(g => Guid.TryParse(g, out _)).Select(Guid.Parse).ToList();
            var names = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(ids, ct);
            units = merged.Select(m => new AdoptionUnit(m.Group,
                Guid.TryParse(m.Group, out var id) && names.TryGetValue(id, out var n) ? n : new Dictionary<string, string>(), m.Count)).ToList();
        }

        var personalization = (await session.QueryAsync(
            """
            select date_trunc('week', occurred_at at time zone 'UTC')::date as week, count(distinct user_key)::int,
              count(*) filter (where fields->>'action' = 'customize')::int, count(*) filter (where fields->>'action' = 'reset')::int
            from analytics.event where name = @name and occurred_at >= (@since::timestamp at time zone 'UTC')
            group by 1 order by 1
            """, new { name = AnalyticsEvents.PersonalizationChanged, since },
            r => new PersonalizationWeek(r.GetFieldValue<DateOnly>(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)), ct))
            .Select(w => w.People < policy.MinGroupSize ? w with { People = null, Customized = null, Reset = null } : w)
            .ToList();

        return new AdoptionView(policy.MinGroupSize, weeks, latest, units, personalization, await templates.UsageAsync(ct));
    }
}
