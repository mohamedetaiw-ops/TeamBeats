using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>One dashboard of the workspace and the package capability it needs (FR-001, FR-002).</summary>
public sealed record DashboardInfo(string Key, string Capability, IReadOnlyList<string> Metrics);

/// <summary>A metric the query endpoint answers: the filters it accepts and how it can be broken down.</summary>
public sealed record MetricInfo(string Key, string Measure, IReadOnlyList<string> Filters, IReadOnlyList<string> GroupBy);

public sealed record WorkspaceUnit(Guid Id, IReadOnlyDictionary<string, string> Names);

/// <summary>What the viewer may open in the analytics workspace, and the choices of the shared filter bar.</summary>
public sealed record WorkspaceView(
    IReadOnlyList<DashboardInfo> Dashboards,
    bool WholeTenant,
    IReadOnlyList<WorkspaceUnit> Units,
    int MinGroupSize,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> MembershipTypes,
    IReadOnlyList<MetricInfo> Metrics);

/// <summary>The shared filters of FR-006. Dates are UTC days and inclusive; text filters are exact.</summary>
public sealed record MetricFilters(
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? UnitId = null,
    bool? Subtree = null,
    string? Location = null,
    string? Channel = null,
    string? Locale = null,
    string? MembershipType = null,
    Guid? SiteId = null,
    string? ContentType = null);

public sealed record MetricQuery(string? Metric, MetricFilters? Filters, string? GroupBy);

/// <summary>One ISO week; the value is null when fewer people than the minimum group size are behind it.</summary>
public sealed record MetricWeek(DateOnly Week, int? Value);

/// <summary>One group of a breakdown; <c>other</c> holds the groups under the minimum group size.</summary>
public sealed record MetricGroup(string Group, IReadOnlyDictionary<string, string> Names, int Value);

/// <summary>
/// The answer to one metric query. <paramref name="Suppressed"/> is true when the whole selection has fewer people than
/// the minimum group size: then nothing but that fact is returned ("not enough data").
/// </summary>
public sealed record MetricResult(string Metric, string? GroupBy, DateOnly From, DateOnly To, int MinGroupSize, bool Suppressed, int? Total,
    IReadOnlyList<MetricWeek> Weeks, IReadOnlyList<MetricGroup> Groups);

/// <summary>
/// The analytics workspace (spec 021 R2 T010): a section per dashboard the viewer may open, and one metric query with
/// the shared filters (FR-006), the viewer's scope of <c>analytics.view</c>, and the minimum group size with
/// complementary suppression (FR-004). Reads only the analytics read model; module dashboards add their metrics here.
/// </summary>
public sealed class Workspace(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context,
    AnalyticsPolicies policies, IOrganizationHierarchy organization, IClock clock)
{
    public const string Overview = "overview";

    public const string ActivePeople = "active_people";
    public const string ContentViews = "content_views";

    public const int MaxDays = 400;
    public const int DefaultWeeks = 5;

    private static readonly string[] CommonFilters = ["from", "to", "unitId", "subtree", "location", "channel", "locale", "membershipType"];

    public static readonly IReadOnlyList<MetricInfo> Metrics =
    [
        new(ActivePeople, "people", CommonFilters, ["unit", "channel", "locale", "membership_type", "location"]),
        new(ContentViews, "views", [.. CommonFilters, "siteId", "contentType"], ["unit", "channel", "locale", "membership_type", "location", "content_type"]),
    ];

    public static readonly IReadOnlyList<DashboardInfo> Dashboards =
    [
        new(Overview, Capabilities.AnalyticsCore, [ActivePeople, ContentViews]),
    ];

    /// <summary>The SQL of each breakdown, from a fixed list; never from the request.</summary>
    private static readonly IReadOnlyDictionary<string, string> Dimensions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["unit"] = "coalesce(e.org_unit_id, @noUnit)::text",
        ["channel"] = "e.channel",
        ["locale"] = "coalesce(e.locale, 'none')",
        ["membership_type"] = "coalesce(e.membership_type, 'none')",
        ["location"] = "coalesce(e.location, 'none')",
        ["content_type"] = "coalesce(e.fields->>'contentType', 'none')",
    };

    public static readonly IReadOnlyList<string> Channels = ["web", "mobile", "teams"];

    private async Task<OrgCoverage> CoverageAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.AnalyticsView, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    private async Task<IReadOnlyList<DashboardInfo>> OpenDashboardsAsync(CancellationToken ct)
    {
        var open = new List<DashboardInfo>();
        foreach (var d in Dashboards)
        {
            if (await entitlements.IsEnabledAsync(d.Capability, ct))
            {
                open.Add(d);
            }
        }
        return open;
    }

    public async Task<WorkspaceView> GetAsync(CancellationToken ct)
    {
        var coverage = await CoverageAsync(ct);
        var policy = await policies.GetAsync(ct);
        // A whole-tenant viewer picks from the units that have data; a scoped viewer from the units they cover.
        var ids = coverage.WholeTenant
            ? (await session.QueryAsync("select distinct org_unit_id from analytics.adoption_weekly where org_unit_id <> @whole and org_unit_id <> @noUnit",
                new { whole = AnalyticsAggregator.WholeTenant, noUnit = AnalyticsAggregator.NoUnit }, r => r.GetGuid(0), ct)).ToList()
            : coverage.Units.ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(ids, ct);
        var units = ids.Where(names.ContainsKey).Select(id => new WorkspaceUnit(id, names[id]))
            .OrderBy(u => u.Names.GetValueOrDefault("en") ?? u.Names.Values.FirstOrDefault() ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var dashboards = await OpenDashboardsAsync(ct);
        var metrics = Metrics.Where(m => dashboards.Any(d => d.Metrics.Contains(m.Key))).ToList();
        return new WorkspaceView(dashboards, coverage.WholeTenant, units, policy.MinGroupSize, Channels, MembershipTypes.All.Order(StringComparer.Ordinal).ToList(), metrics);
    }

    private sealed record Selection(DateOnly From, DateOnly To, bool Restrict, Guid[] Units, string Location, string Channel, string Locale, string MembershipType,
        Guid SiteId, string ContentType);

    private async Task<Selection> SelectAsync(MetricInfo metric, MetricFilters f, OrgCoverage coverage, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        void Unsupported(string field, bool given)
        {
            if (given && !metric.Filters.Contains(field))
            {
                errors.Add($"filters.{field}", "validation.analytics.filter");
            }
        }
        Unsupported("siteId", f.SiteId is not null);
        Unsupported("contentType", !string.IsNullOrEmpty(f.ContentType));
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var to = f.To ?? today;
        var from = f.From ?? Adoption.WeekOf(to).AddDays(-7 * (DefaultWeeks - 1));
        if (from > to || to.DayNumber - from.DayNumber > MaxDays || to > today.AddDays(1))
        {
            errors.Add("filters.from", "validation.analytics.range");
        }
        if (f.Channel is { Length: > 0 } channel && !Channels.Contains(channel))
        {
            errors.Add("filters.channel", "validation.analytics.filter");
        }
        if (f.MembershipType is { Length: > 0 } type && !MembershipTypes.All.Contains(type))
        {
            errors.Add("filters.membershipType", "validation.analytics.filter");
        }
        errors.ThrowIfAny();

        // The unit filter must stay inside the viewer's scope; without one, a scoped viewer sees their units only.
        Guid[] units = [];
        var restrict = false;
        if (f.UnitId is { } unit)
        {
            if (!coverage.Covers(unit))
            {
                throw AppException.Denied();
            }
            // A unit this tenant does not have (another tenant's, or a deleted one) is a bad filter, not an empty result.
            if (!(await organization.GetNamesAsync([unit], ct)).ContainsKey(unit))
            {
                throw AppException.Validation("filters.unitId", "validation.analytics.filter");
            }
            var set = f.Subtree == false ? new HashSet<Guid> { unit } : (await organization.GetSubtreesAsync([unit], ct)).ToHashSet();
            set.Add(unit);
            units = coverage.WholeTenant ? set.ToArray() : set.Where(u => coverage.Covers(u)).ToArray();
            restrict = true;
        }
        else if (!coverage.WholeTenant)
        {
            units = coverage.Units.ToArray();
            restrict = true;
        }
        return new Selection(from, to, restrict, units, f.Location ?? "", f.Channel ?? "", f.Locale ?? "", f.MembershipType ?? "", f.SiteId ?? Guid.Empty,
            f.ContentType ?? "");
    }

    private static string EventName(string metric) => metric == ContentViews ? AnalyticsEvents.ContentViewed : "";

    private const string Where =
        """
        e.user_key is not null
          and e.occurred_at >= (@from::timestamp at time zone 'UTC') and e.occurred_at < ((@to + 1)::timestamp at time zone 'UTC')
          and (@name = '' or e.name = @name)
          and (not @restrict or coalesce(e.org_unit_id, @noUnit) = any(@units))
          and (@location = '' or e.location = @location)
          and (@channel = '' or e.channel = @channel)
          and (@locale = '' or e.locale = @locale)
          and (@membershipType = '' or e.membership_type = @membershipType)
          and (@siteId = @noUnit or e.fields->>'siteId' = @siteId::text)
          and (@contentType = '' or e.fields->>'contentType' = @contentType)
        """;

    public async Task<MetricResult> QueryAsync(MetricQuery query, CancellationToken ct)
    {
        var metric = Metrics.FirstOrDefault(m => m.Key == query.Metric) ?? throw AppException.Validation("metric", "validation.analytics.metric");
        var coverage = await CoverageAsync(ct);
        var dashboard = Dashboards.First(d => d.Metrics.Contains(metric.Key));
        await entitlements.DemandAsync(dashboard.Capability, ct);
        if (query.GroupBy is { Length: > 0 } g && !metric.GroupBy.Contains(g))
        {
            throw AppException.Validation("groupBy", "validation.analytics.group_by");
        }
        var policy = await policies.GetAsync(ct);
        var min = policy.MinGroupSize;
        var s = await SelectAsync(metric, query.Filters ?? new MetricFilters(), coverage, ct);
        var args = new
        {
            from = s.From, to = s.To, name = EventName(metric.Key), restrict = s.Restrict, units = s.Units, noUnit = AnalyticsAggregator.NoUnit,
            location = s.Location, channel = s.Channel, locale = s.Locale, membershipType = s.MembershipType, siteId = s.SiteId, contentType = s.ContentType,
        };
        var people = metric.Measure == "people";

        var total = await session.QueryAsync(
            $"select count(distinct e.user_key)::int, count(*)::int from analytics.event e where {Where}", args, r => (People: r.GetInt32(0), Events: r.GetInt32(1)), ct);
        var (totalPeople, totalEvents) = total[0];
        if (totalPeople < min)
        {
            return new MetricResult(metric.Key, query.GroupBy, s.From, s.To, min, true, null, [], []);
        }

        var weeks = (await session.QueryAsync(
            $"""
            select date_trunc('week', e.occurred_at at time zone 'UTC')::date, count(distinct e.user_key)::int, count(*)::int
            from analytics.event e where {Where} group by 1 order by 1
            """, args, r => (Week: r.GetFieldValue<DateOnly>(0), People: r.GetInt32(1), Events: r.GetInt32(2)), ct))
            .Select(w => new MetricWeek(w.Week, w.People < min ? null : people ? w.People : w.Events)).ToList();

        var groups = new List<MetricGroup>();
        if (query.GroupBy is { Length: > 0 } groupBy)
        {
            var rows = await session.QueryAsync(
                $"select {Dimensions[groupBy]}, e.user_key, count(*)::int from analytics.event e where {Where} group by 1, 2",
                args, r => (Group: r.GetString(0), Person: r.GetString(1), Events: r.GetInt32(2)), ct);
            var merged = MergeGroups(rows, min, people);
            groups = await NamedAsync(merged, groupBy, ct);
        }
        return new MetricResult(metric.Key, query.GroupBy, s.From, s.To, min, false, people ? totalPeople : totalEvents, weeks, groups);
    }

    /// <summary>
    /// Folds groups with fewer distinct people than the minimum into "other", and keeps folding the smallest shown groups
    /// while "other" itself is under it, so no group can be worked out by subtracting (complementary suppression). The
    /// people in "other" are counted exactly, never added up across groups.
    /// </summary>
    internal static IReadOnlyList<(string Group, int Value)> MergeGroups(IEnumerable<(string Group, string Person, int Events)> rows, int minimum, bool people)
    {
        var byGroup = rows.GroupBy(r => r.Group, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (People: g.Select(r => r.Person).ToHashSet(StringComparer.Ordinal), Events: g.Sum(r => r.Events)), StringComparer.Ordinal);
        var otherPeople = new HashSet<string>(StringComparer.Ordinal);
        var otherEvents = 0;
        void Fold(string key)
        {
            otherPeople.UnionWith(byGroup[key].People);
            otherEvents += byGroup[key].Events;
            byGroup.Remove(key);
        }
        foreach (var key in byGroup.Where(g => g.Value.People.Count < minimum).Select(g => g.Key).ToList())
        {
            Fold(key);
        }
        while (otherPeople.Count > 0 && otherPeople.Count < minimum && byGroup.Count > 0)
        {
            Fold(byGroup.OrderBy(g => g.Value.People.Count).ThenBy(g => g.Key, StringComparer.Ordinal).First().Key);
        }
        var result = byGroup.Select(g => (g.Key, people ? g.Value.People.Count : g.Value.Events))
            .OrderByDescending(g => g.Item2).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        // When "other" is still under the minimum there was nothing left to fold: it is left out rather than shown small.
        if (otherPeople.Count >= minimum)
        {
            result.Add((ItemAnalytics.Other, people ? otherPeople.Count : otherEvents));
        }
        return result;
    }

    private async Task<List<MetricGroup>> NamedAsync(IReadOnlyList<(string Group, int Value)> merged, string groupBy, CancellationToken ct)
    {
        var empty = new Dictionary<string, string>();
        if (groupBy != "unit")
        {
            return merged.Select(m => new MetricGroup(m.Group, empty, m.Value)).ToList();
        }
        var ids = merged.Select(m => Guid.TryParse(m.Group, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(ids, ct);
        return merged.Select(m =>
        {
            if (Guid.TryParse(m.Group, out var id))
            {
                return id == Guid.Empty ? new MetricGroup(ItemAnalytics.NoUnit, empty, m.Value) : new MetricGroup(m.Group, names.GetValueOrDefault(id) ?? empty, m.Value);
            }
            return new MetricGroup(m.Group, empty, m.Value);
        }).ToList();
    }
}
