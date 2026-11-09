using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Content;

/// <summary>
/// Content items and knowledge articles for search (spec 012 section 2, spec 011 US4 scenario 3). Only published items
/// are given, at their live version: each heading becomes its own unit with its anchor, so an FAQ question and its answer
/// are one unit and citations land on the section. Reading is decided by the same rule as the reader page.
/// </summary>
internal sealed class ContentSearchSource(IDbSession session, IAuthorizationService authz, IEntitlementService entitlements, Items items, Sites sites) : ISearchSource
{
    public string Source => "content";

    public static string Link(Guid id, string? anchor = null) => $"/content/read/{id}" + (anchor is null ? string.Empty : "#" + anchor);

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await items.RowAsync(itemId, false, ct);
        if (item is null || item.Status != ContentStatuses.Published || item.PublishedVersion is not { } number)
        {
            return [];
        }
        var site = await sites.RowAsync(item.SiteId, false, ct);
        var version = await items.VersionAsync(itemId, number, ct);
        if (site is null || version is null)
        {
            return [];
        }
        var kind = item.Type == ContentTypes.Knowledge ? SearchKinds.Knowledge : SearchKinds.Content;
        var updated = item.PublishedAt ?? version.PublishedAt ?? version.CreatedAt;
        var units = new List<SearchUnit>();
        foreach (var (locale, variant) in version.Variants.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            var sections = BodyValidator.Sections(variant.Body);
            var lead = string.Join("\n", new[] { variant.Summary, sections.Count == 0 ? BodyValidator.PlainText(variant.Body) : null }.Where(t => !string.IsNullOrWhiteSpace(t)));
            units.Add(new SearchUnit($"{locale}:", kind, locale, variant.Title, lead, Link(itemId), item.Classification, site.OrgUnitId, number, updated));
            foreach (var s in sections)
            {
                units.Add(new SearchUnit($"{locale}:{s.Anchor}", kind, locale, variant.Title, (s.Heading + "\n" + s.Text).Trim(), Link(itemId, s.Anchor),
                    item.Classification, site.OrgUnitId, number, updated));
            }
        }
        return units;
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select id from content.content_item where status = 'published' and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.ContentCore, ct))
        {
            return new HashSet<Guid>();
        }
        var rows = await session.QueryAsync(
            "select i.id, i.classification, s.org_unit_id from content.content_item i join content.site s on s.id = i.site_id " +
            "where i.id = any(@ids) and i.status = 'published' and i.published_version is not null",
            new { ids = itemIds.ToArray() }, r => (Id: r.GetGuid(0), Classification: r.GetString(1), Unit: r.Get<Guid?>("org_unit_id")), ct);
        var descriptors = rows.Select(r => new ResourceDescriptor(Items.ResourceType, r.Id, r.Unit,
            r.Classification is "public" or "internal" ? ResourceVisibility.Tenant : ResourceVisibility.Restricted, r.Classification));
        return (await authz.FilterAsync(Permissions.ContentView, descriptors, ct)).Select(d => d.ResourceId).ToHashSet();
    }
}
