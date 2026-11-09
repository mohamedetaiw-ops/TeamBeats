using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>One kind of frontend failure in one release: how often, when, and the latest correlation IDs to look up.</summary>
public sealed record FrontendErrorGroup(string Release, string Kind, string? Component, string? Area, int Count, DateTimeOffset FirstAt,
    DateTimeOffset LastAt, IReadOnlyList<string> CorrelationIds);

/// <summary>A release seen in error reports, with how many reports it has.</summary>
public sealed record FrontendRelease(string Release, int Count, DateTimeOffset LastAt);

public sealed record FrontendErrorsView(int Days, string? Release, IReadOnlyList<FrontendRelease> Releases, IReadOnlyList<FrontendErrorGroup> Groups);

/// <summary>
/// The support list of frontend errors (spec 021 US9 scenario 6, FR-020): <c>ui.error</c> reports of the last 30 days
/// grouped by release, kind, component version and area, filtered by release. Reports carry no message, payload or
/// person, so the list shows nothing a person typed or saw. For holders of <c>analytics.view</c> for the whole tenant.
/// </summary>
public sealed class FrontendErrors(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, IClock clock)
{
    public const int Days = 30;
    public const int CorrelationIds = 5;
    public const int MaxGroups = 200;

    public async Task<FrontendErrorsView> ListAsync(string? release, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var since = clock.UtcNow.AddDays(-Days);
        var name = AnalyticsEvents.UiError;
        var releases = await session.QueryAsync(
            """
            select fields->>'release', count(*)::int, max(occurred_at) from analytics.event
            where name = @name and occurred_at >= @since group by 1 order by 3 desc limit 50
            """, new { name, since }, r => new FrontendRelease(r.GetString(0), r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
        var chosen = string.IsNullOrWhiteSpace(release) ? null : release.Trim();
        var groups = await session.QueryAsync(
            """
            select fields->>'release' as release, fields->>'kind' as kind, fields->>'component' as component, fields->>'area' as area, count(*)::int, min(occurred_at), max(occurred_at),
              (array_remove(array_agg(fields->>'correlationId' order by occurred_at desc), null))[1:@ids]
            from analytics.event
            where name = @name and occurred_at >= @since and (@chosen::text is null or fields->>'release' = @chosen)
            group by 1, 2, 3, 4 order by 5 desc, 7 desc limit @max
            """, new { name, since, chosen, ids = CorrelationIds, max = MaxGroups },
            r => new FrontendErrorGroup(r.GetString(0), r.GetString(1), r.Get<string?>("component"), r.Get<string?>("area"), r.GetInt32(4),
                r.GetFieldValue<DateTimeOffset>(5), r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? [] : r.GetFieldValue<string[]>(7)), ct);
        return new FrontendErrorsView(Days, chosen, releases, groups);
    }
}
