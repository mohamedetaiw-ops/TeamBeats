using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>
/// Knowledge articles for the knowledge base of spec 011 US4: content items of type <c>knowledge</c> with the spec 009
/// draft, review, publish and version lifecycle. The Documents module calls this contract; it never reads content tables.
/// </summary>
public sealed class KnowledgeContent(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    Items items, Sites sites) : IKnowledgeContent
{
    public async Task<Guid> CreateDraftAsync(KnowledgeDraft draft, CancellationToken ct = default)
    {
        var variants = new Dictionary<string, VariantInput>(StringComparer.Ordinal);
        foreach (var (locale, title) in draft.Titles)
        {
            var headings = draft.Headings.TryGetValue(locale, out var h) ? h : [];
            var blocks = headings.Select(text => new Block(BlockTypes.Heading, [new Inline(text)], Level: 2)).ToList();
            variants[locale] = new VariantInput(title, null, new BodyDocument(blocks));
        }
        var view = await items.CreateAsync(new ItemRequest(draft.SiteId, ContentTypes.Knowledge, null, draft.OwnerId, null, null, draft.Classification,
            null, null, variants, null, null), ct);
        return view.Id;
    }

    /// <summary>The item when it is a readable knowledge article and the caller may read it.</summary>
    private async Task<(ItemRow Item, SiteRow Site)?> PublishedAsync(Guid itemId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var item = await items.RowAsync(itemId, false, ct);
        if (item is null || item.Type != ContentTypes.Knowledge || !ContentStatuses.Readable(item.Status) || item.PublishedVersion is null)
        {
            return null;
        }
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        return await items.MayReadAsync(item, site, ct) ? (item, site) : null;
    }

    private async Task<KnowledgeArticle> ArticleAsync(ItemRow item, SiteRow site, CancellationToken ct)
    {
        var version = (await items.VersionAsync(item.Id, item.PublishedVersion!.Value, ct))!;
        return new KnowledgeArticle(item.Id, site.Id, site.OrgUnitId, version.Variants.ToDictionary(v => v.Key, v => v.Value.Title, StringComparer.Ordinal),
            item.OwnerId, item.PublishedVersion.Value, item.PublishedAt ?? version.PublishedAt ?? version.CreatedAt, item.ReviewDueAt);
    }

    public async Task<KnowledgeArticle?> ReadableAsync(Guid itemId, CancellationToken ct = default) =>
        await PublishedAsync(itemId, ct) is { } found ? await ArticleAsync(found.Item, found.Site, ct) : null;

    public async Task<IReadOnlyList<KnowledgeArticle>> ReadableListAsync(int limit, CancellationToken ct = default)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        limit = Math.Clamp(limit, 1, 100);
        var result = new List<KnowledgeArticle>();
        DateTimeOffset? before = null;
        // Read access is checked per item, so pages are read until the list is full or the articles run out.
        while (result.Count < limit)
        {
            var rows = await session.QueryAsync(
                $"""
                select {Items.Columns} from content.content_item
                where type = @type and status in ('published', 'expired') and published_version is not null
                  and (@before::timestamptz is null or published_at < @before)
                order by published_at desc, id limit 100
                """, new { type = ContentTypes.Knowledge, before }, Items.Read, ct);
            foreach (var row in rows)
            {
                var site = (await sites.RowAsync(row.SiteId, false, ct))!;
                if (result.Count < limit && await items.MayReadAsync(row, site, ct))
                {
                    result.Add(await ArticleAsync(row, site, ct));
                }
            }
            if (rows.Count < 100)
            {
                break;
            }
            before = rows[^1].PublishedAt;
        }
        return result;
    }

    public async Task<bool> MayManageAsync(Guid itemId, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        var item = await items.RowAsync(itemId, false, ct);
        if (item is null || item.Type != ContentTypes.Knowledge)
        {
            return false;
        }
        if (item.OwnerId == user)
        {
            return true;
        }
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        return (await authz.CheckAsync(Permissions.KbEdit, site.Target, ct)).Allowed;
    }

    public async Task<IReadOnlyList<ContentSection>> SectionsAsync(Guid itemId, CancellationToken ct = default)
    {
        if (await PublishedAsync(itemId, ct) is not { } found)
        {
            return [];
        }
        var version = (await items.VersionAsync(itemId, found.Item.PublishedVersion!.Value, ct))!;
        return version.Variants.OrderBy(v => v.Key, StringComparer.Ordinal)
            .SelectMany(v => BodyValidator.Sections(v.Value.Body).Select(s => new ContentSection(v.Key, s.Anchor, s.Level, s.Heading, s.Text)))
            .ToList();
    }
}
