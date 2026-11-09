using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>One day of an item's trend: views and people reading it for the first time.</summary>
public sealed record ItemTrendDay(DateOnly Day, int Views, int Readers);

/// <summary>
/// One group of the breakdown by org unit. <see cref="Group"/> is the unit's ID, or <c>other</c> for the groups under the
/// minimum size merged together; <see cref="Names"/> is empty for <c>other</c> and for people with no unit (<c>none</c>).
/// </summary>
public sealed record ItemGroupView(string Group, IReadOnlyDictionary<string, string> Names, int Audience, int Readers, int? Acknowledged, double Reach);

/// <summary>
/// Reach and acknowledgement of one item (spec 021 US1 scenario 1). Reach is unique readers in the audience over the
/// audience size; acknowledgement is shown only when the item asks for it.
/// </summary>
public sealed record ItemAnalyticsView(
    string Source,
    Guid ItemId,
    IReadOnlyDictionary<string, string> Titles,
    DateTimeOffset? LiveFrom,
    int Audience,
    int Readers,
    double Reach,
    bool AcknowledgementRequired,
    int? Acknowledged,
    double? AcknowledgedShare,
    int Reactions,
    int Comments,
    int MinGroupSize,
    IReadOnlyList<ItemTrendDay> Trend,
    IReadOnlyList<ItemGroupView> Groups);

public sealed record RemindGroupRequest(string? Group);

public sealed record RemindGroupResult(int Sent);

/// <summary>
/// Item analytics (spec 021 US1, plan "Item reach and acknowledgement"). The owning module describes the item and decides
/// who may see it; Analytics adds the readers it rebuilt from <c>content.viewed</c> events. The breakdown by unit never
/// shows a group under the tenant's minimum group size: small groups are merged into "other", and when "other" would
/// still be under the minimum the smallest shown groups join it too (complementary suppression).
/// </summary>
public sealed class ItemAnalytics(IDbSession session, IEnumerable<IAnalyticsItemSource> sources, IEntitlementService entitlements, ITenantContext context,
    AnalyticsPolicies policies, Pseudonyms pseudonyms, IOrganizationHierarchy organization, IClock clock)
{
    public const string Other = "other";
    public const string NoUnit = "none";
    public const int TrendDays = 90;

    private sealed record Person(Guid UserId, string Group, bool Read, bool Acknowledged);

    private async Task<(IAnalyticsItemSource Source, AnalyticsItem Item)> DescribeAsync(string source, Guid itemId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsCore, ct);
        context.RequireUser();
        var owner = sources.FirstOrDefault(s => s.Source == source) ?? throw AppException.NotFound();
        return (owner, await owner.DescribeAsync(itemId, ct));
    }

    /// <summary>Everyone in the audience with their group, whether they read the item and whether they acknowledged it.</summary>
    private async Task<List<Person>> PeopleAsync(string source, AnalyticsItem item, CancellationToken ct)
    {
        var readers = (await session.QueryAsync("select user_key from analytics.item_reader where source = @source and item_id = @item",
            new { source, item = item.ItemId }, r => r.GetString(0), ct)).ToHashSet(StringComparer.Ordinal);
        var units = new Dictionary<Guid, Guid>();
        foreach (var m in (await organization.ListActiveMembershipsAsync(ct)).Where(m => item.Audience.Contains(m.UserId)).OrderByDescending(m => m.IsPrimary))
        {
            units.TryAdd(m.UserId, m.OrgUnitId);
        }
        var people = new List<Person>();
        foreach (var user in item.Audience.Order())
        {
            var group = units.TryGetValue(user, out var unit) ? unit.ToString() : NoUnit;
            people.Add(new Person(user, group, readers.Contains(await pseudonyms.KeyAsync(user, ct)), item.Acknowledged.Contains(user)));
        }
        return people;
    }

    /// <summary>Groups people by unit and merges groups under the minimum size into "other" (US1 scenario 2, edge case on differencing).</summary>
    internal static Dictionary<string, List<T>> Merge<T>(IEnumerable<IGrouping<string, T>> groups, int minimum)
    {
        var shown = groups.ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var other = new List<T>();
        foreach (var key in shown.Where(g => g.Value.Count < minimum).Select(g => g.Key).ToList())
        {
            other.AddRange(shown[key]);
            shown.Remove(key);
        }
        while (other.Count > 0 && other.Count < minimum && shown.Count > 0)
        {
            var smallest = shown.OrderBy(g => g.Value.Count).ThenBy(g => g.Key, StringComparer.Ordinal).First();
            other.AddRange(smallest.Value);
            shown.Remove(smallest.Key);
        }
        if (other.Count > 0)
        {
            shown[Other] = other;
        }
        return shown;
    }

    private static double Share(int part, int whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 4);

    public async Task<ItemAnalyticsView> GetAsync(string source, Guid itemId, CancellationToken ct)
    {
        var (_, item) = await DescribeAsync(source, itemId, ct);
        var policy = await policies.GetAsync(ct);
        var people = await PeopleAsync(source, item, ct);
        var readers = people.Count(p => p.Read);
        var acknowledged = item.AcknowledgementRequired ? people.Count(p => p.Acknowledged) : (int?)null;

        var since = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime).AddDays(-TrendDays + 1);
        var trend = await session.QueryAsync(
            """
            select day, sum(views)::int, sum(readers)::int from analytics.item_daily
            where source = @source and item_id = @itemId and day >= @since
            group by day order by day
            """, new { source, itemId, since }, r => new ItemTrendDay(r.GetFieldValue<DateOnly>(0), r.GetInt32(1), r.GetInt32(2)), ct);

        var merged = Merge(people.GroupBy(p => p.Group), policy.MinGroupSize);
        var unitIds = merged.Keys.Where(k => k is not Other and not NoUnit).Select(Guid.Parse).ToList();
        var names = unitIds.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(unitIds, ct);
        var groups = merged
            .Select(g => new ItemGroupView(g.Key, Guid.TryParse(g.Key, out var unit) && names.TryGetValue(unit, out var n) ? n : new Dictionary<string, string>(),
                g.Value.Count, g.Value.Count(p => p.Read), item.AcknowledgementRequired ? g.Value.Count(p => p.Acknowledged) : null,
                Share(g.Value.Count(p => p.Read), g.Value.Count)))
            .OrderBy(g => g.Group == Other ? 1 : 0).ThenBy(g => g.Reach).ThenBy(g => g.Group, StringComparer.Ordinal)
            .ToList();

        return new ItemAnalyticsView(source, itemId, item.Titles, item.LiveFrom, people.Count, readers, Share(readers, people.Count), item.AcknowledgementRequired,
            acknowledged, acknowledged is { } a ? Share(a, people.Count) : null, item.Reactions, item.Comments, policy.MinGroupSize, trend, groups);
    }

    /// <summary>
    /// "Remind this group" (US1 scenario 3): the owning module reminds the people in the group who have not read the item,
    /// or not acknowledged it when it asks for that. The author never sees who they are.
    /// </summary>
    public async Task<RemindGroupResult> RemindAsync(string source, Guid itemId, RemindGroupRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Group))
        {
            throw AppException.Validation("group", "validation.analytics.group");
        }
        var (owner, item) = await DescribeAsync(source, itemId, ct);
        var policy = await policies.GetAsync(ct);
        var people = await PeopleAsync(source, item, ct);
        var merged = Merge(people.GroupBy(p => p.Group), policy.MinGroupSize);
        if (!merged.TryGetValue(request.Group, out var group))
        {
            throw AppException.Validation("group", "validation.analytics.group");
        }
        var pending = group.Where(p => item.AcknowledgementRequired ? !p.Acknowledged : !p.Read).Select(p => p.UserId).ToList();
        return new RemindGroupResult(pending.Count == 0 ? 0 : await owner.RemindAsync(itemId, pending, ct));
    }
}
