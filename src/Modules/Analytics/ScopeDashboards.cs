using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

public static class ScopeKinds
{
    public const string Unit = "unit";
    public const string Location = "location";
}

/// <summary>A dashboard the viewer may open: a unit (with its subtree) or a location.</summary>
public sealed record ScopeRef(string Kind, string Key, IReadOnlyDictionary<string, string> Names);

public sealed record ScopeList(IReadOnlyList<ScopeRef> Units, IReadOnlyList<ScopeRef> Locations);

/// <summary>
/// One tile. A share has <see cref="Of"/> and <see cref="Rate"/>; a count (open requests) has no <see cref="Of"/> and its
/// rate is per person, so it compares with the group. Value, Of and Rate are null when there is not enough data.
/// <see cref="GroupRate"/> is the same figure for the whole organization, null when it could reveal one other scope.
/// </summary>
public sealed record ScopeTile(string Key, int? Value, int? Of, double? Rate, double? GroupRate, bool Enough);

/// <summary>A scope's dashboard. <paramref name="Kpis"/> are the KPI tiles of a location (spec 021 R3), null for a unit or without <c>analytics.kpi_tiles</c>.</summary>
public sealed record ScopeDashboardView(ScopeRef Scope, DateOnly From, DateOnly To, int MinGroupSize, int People, bool Enough, bool GroupShown,
    IReadOnlyList<ScopeTile> Tiles, IReadOnlyList<KpiTileView>? Kpis = null);

/// <summary>
/// Location and unit dashboards (spec 021 US6, FR-010, T014): adoption, communications reach, policy compliance, open
/// requests and breaches, and attendance exceptions for one scope, beside the same figures for the whole organization.
/// A unit dashboard needs <c>analytics.view</c> at the unit; a location dashboard needs it for the whole tenant, since
/// locations are not an authorization scope. Under the minimum group size of active people, every tile says "not enough
/// data". The organization figure is shown only when the people outside the scope reach the minimum and sit in at least
/// two other units or locations, so it can never be one other scope's number. Needs <c>analytics.location_dashboards</c>.
/// </summary>
public sealed class ScopeDashboards(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context,
    AnalyticsPolicies policies, IOrganizationHierarchy organization, ITenantMemberships memberships, IProfiles profiles, Pseudonyms pseudonyms,
    IEnumerable<IAnalyticsScopeSource> sources, KpiTiles kpis, IClock clock)
{
    public const int DefaultDays = 30;
    public const string Adoption = "adoption";
    public const string Reach = "reach";

    private async Task<OrgCoverage> DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsLocationDashboards, ct);
        context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.AnalyticsView, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        return coverage;
    }

    private sealed record Person(Guid Id, Guid? Unit, IReadOnlySet<Guid> Units, string? Location);

    /// <summary>Active members with their primary unit, every unit they are in, and their location.</summary>
    private async Task<IReadOnlyList<Person>> PeopleAsync(CancellationToken ct)
    {
        var active = (await memberships.ListAsync(ct)).Where(m => m.Status == MembershipStatus.Active).Select(m => m.UserId).ToList();
        var units = (await organization.ListActiveMembershipsAsync(ct)).ToLookup(m => m.UserId);
        var placements = active.Count == 0 ? new Dictionary<Guid, PersonPlacement>() : await profiles.PlacementsAsync(active, ct);
        return active.Select(id =>
        {
            var list = units[id].ToList();
            var primary = (list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault())?.OrgUnitId;
            var location = placements.GetValueOrDefault(id)?.LocationKey;
            return new Person(id, primary, list.Select(m => m.OrgUnitId).ToHashSet(), string.IsNullOrEmpty(location) ? null : location);
        }).ToList();
    }

    public async Task<ScopeList> ListAsync(CancellationToken ct)
    {
        var coverage = await DemandAsync(ct);
        var people = await PeopleAsync(ct);
        // A whole tenant viewer gets every unit with people in its subtree; a scoped viewer the units they cover.
        var unitIds = coverage.WholeTenant
            ? (await organization.GetPathsAsync(people.SelectMany(p => p.Units).Distinct(), ct)).Values.SelectMany(p => p).Distinct().ToList()
            : coverage.Units.ToList();
        var names = unitIds.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(unitIds, ct);
        var units = names.Select(n => new ScopeRef(ScopeKinds.Unit, n.Key.ToString(), n.Value))
            .OrderBy(u => u.Names.GetValueOrDefault("en") ?? u.Key, StringComparer.CurrentCulture).ToList();
        var none = new Dictionary<string, string>();
        var locations = coverage.WholeTenant
            ? people.Select(p => p.Location).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(l => new ScopeRef(ScopeKinds.Location, l, none)).ToList()
            : [];
        return new ScopeList(units, locations);
    }

    public async Task<ScopeDashboardView> UnitAsync(Guid unit, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var coverage = await DemandAsync(ct);
        var names = await organization.GetNamesAsync([unit], ct);
        if (!names.TryGetValue(unit, out var name))
        {
            throw AppException.NotFound();
        }
        if (!coverage.WholeTenant && !coverage.Units.Contains(unit))
        {
            throw AppException.Denied();
        }
        var subtree = await organization.GetSubtreesAsync([unit], ct);
        var people = await PeopleAsync(ct);
        var inside = people.Where(p => p.Units.Overlaps(subtree)).ToList();
        var outside = people.Where(p => !p.Units.Overlaps(subtree)).ToList();
        var otherScopes = outside.Select(p => p.Unit?.ToString() ?? "none").Distinct(StringComparer.Ordinal).Count();
        return await ViewAsync(new ScopeRef(ScopeKinds.Unit, unit.ToString(), name), inside, people, outside.Count, otherScopes, from, to, ct);
    }

    public async Task<ScopeDashboardView> LocationAsync(string location, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var coverage = await DemandAsync(ct);
        if (!coverage.WholeTenant)
        {
            throw AppException.Denied();
        }
        var people = await PeopleAsync(ct);
        var inside = people.Where(p => p.Location == location).ToList();
        if (inside.Count == 0)
        {
            throw AppException.NotFound();
        }
        var outside = people.Where(p => p.Location != location).ToList();
        var otherScopes = outside.Select(p => p.Location ?? "").Distinct(StringComparer.Ordinal).Count();
        var view = await ViewAsync(new ScopeRef(ScopeKinds.Location, location, new Dictionary<string, string>()), inside, people, outside.Count, otherScopes, from, to, ct);
        var until = new DateTimeOffset(view.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        return view with { Kpis = await kpis.ForLocationAsync(location, until, ct) };
    }

    private async Task<ScopeDashboardView> ViewAsync(ScopeRef scope, IReadOnlyList<Person> inside, IReadOnlyList<Person> everyone, int outside, int otherScopes,
        DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (start > end || end.DayNumber - start.DayNumber > Workspace.MaxDays)
        {
            throw AppException.Validation("from", "validation.analytics.range");
        }
        var since = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var until = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var min = (await policies.GetAsync(ct)).MinGroupSize;
        var enough = inside.Count >= min;
        var groupShown = enough && outside >= min && otherScopes >= 2;

        var mine = enough ? await FiguresAsync(inside.Select(p => p.Id).ToList(), since, until, ct) : [];
        var group = groupShown ? await FiguresAsync(everyone.Select(p => p.Id).ToList(), since, until, ct) : [];
        var keys = enough ? mine.Select(f => f.Key).ToList() : await KeysAsync(ct);
        var tiles = keys.Select(key =>
        {
            var m = mine.FirstOrDefault(f => f.Key == key);
            var g = group.FirstOrDefault(f => f.Key == key);
            return Tile(key, m, inside.Count, g, everyone.Count, min);
        }).ToList();
        return new ScopeDashboardView(scope, start, end, min, inside.Count, enough, groupShown, tiles);
    }

    /// <summary>A tile from the scope's figure and the organization's; a share under the minimum is not enough data.</summary>
    internal static ScopeTile Tile(string key, AnalyticsScopeFigure? mine, int people, AnalyticsScopeFigure? group, int everyone, int min)
    {
        double? Rate(AnalyticsScopeFigure? f, int count) => f switch
        {
            null => null,
            { Of: { } of } when of >= min => Math.Round((double)f.Value / of, 4),
            { Of: not null } => null,
            _ when count >= min => Math.Round((double)f.Value / count, 4),
            _ => null,
        };
        var rate = Rate(mine, people);
        return rate is null
            ? new ScopeTile(key, null, null, null, null, false)
            : new ScopeTile(key, mine!.Value, mine.Of, rate, Rate(group, everyone), true);
    }

    /// <summary>The tiles the package has, for a scope too small to compute them.</summary>
    private async Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct)
    {
        var keys = new List<string> { Adoption, Reach };
        if (await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            keys.Add(AnalyticsScopeFigures.Compliance);
        }
        keys.Add(AnalyticsScopeFigures.OpenRequests);
        keys.Add(AnalyticsScopeFigures.Breaches);
        if (await entitlements.IsEnabledAsync(Capabilities.HrAttendance, ct))
        {
            keys.Add(AnalyticsScopeFigures.AttendanceExceptions);
        }
        return keys;
    }

    private async Task<IReadOnlyList<AnalyticsScopeFigure>> FiguresAsync(IReadOnlyList<Guid> people, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var keys = new List<string>(people.Count);
        foreach (var id in people)
        {
            keys.Add(await pseudonyms.KeyAsync(id, ct));
        }
        var counts = await session.QueryAsync(
            """
            select count(distinct user_key), count(distinct user_key) filter (where name = @viewed) from analytics.event
            where occurred_at >= @from and occurred_at < @to and user_key = any(@keys)
            """, new { keys = keys.ToArray(), from, to, viewed = AnalyticsEvents.ContentViewed }, r => (Active: (int)r.GetInt64(0), Viewers: (int)r.GetInt64(1)), ct);
        var (active, viewers) = counts.Count == 0 ? (0, 0) : counts[0];
        var figures = new List<AnalyticsScopeFigure> { new(Adoption, active, people.Count), new(Reach, viewers, people.Count) };
        foreach (var source in sources)
        {
            figures.AddRange(await source.FiguresAsync(people, from, to, ct));
        }
        return figures;
    }
}
