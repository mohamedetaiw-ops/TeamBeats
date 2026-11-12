using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Entitlements.Contracts;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// Community posts for search (spec 023 FR-023, US1 scenario 5). A published post is one unit in the tenant's default
/// language with its text and comments left out; who may read it is decided at query time by the same rule as the
/// community page, so closed and approval communities stay with their members.
/// </summary>
internal sealed class EngagementSearchSource(IDbSession session, IEntitlementService entitlements, ITenantSettingsReader tenantSettings, Communities communities) : ISearchSource
{
    public const string Name = "engagement";

    public string Source => Name;

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select p.community_id, p.text, p.published_at, coalesce(p.edited_at, p.published_at), c.names::text, c.org_unit_id, c.status, c.classification
            from engagement.post p join engagement.community c on c.id = p.community_id where p.id = @itemId and p.status = 'published'
            """, new { itemId }, r => (Community: r.GetGuid(0), Text: r.GetString(1), Updated: r.GetFieldValue<DateTimeOffset>(3), Names: r.GetString(4),
                Unit: r.Get<Guid?>("org_unit_id"), Status: r.GetString(6), Classification: r.GetString(7)), ct);
        if (row == default)
        {
            return [];
        }
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var names = EngagementJson.Read<Dictionary<string, string>>(row.Names);
        var title = names.GetValueOrDefault(locale) ?? names.Values.FirstOrDefault() ?? "";
        return [new SearchUnit("post", SearchKinds.Discussion, locale, title, row.Text, Posts.Link(row.Community, itemId), row.Classification, row.Unit, null, row.Updated)];
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select id from engagement.post where status = 'published' and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.EngagementCommunities, ct))
        {
            return new HashSet<Guid>();
        }
        var rows = await session.QueryAsync("select id, community_id from engagement.post where id = any(@ids) and status = 'published'", new { ids = itemIds.ToArray() },
            r => (Id: r.GetGuid(0), Community: r.GetGuid(1)), ct);
        var readable = new HashSet<Guid>();
        foreach (var group in rows.GroupBy(r => r.Community))
        {
            if (await communities.RowAsync(group.Key, ct) is { } c && (await communities.StandingAsync(c, ct)).Reads)
            {
                readable.UnionWith(group.Select(g => g.Id));
            }
        }
        return readable;
    }
}
