using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Files of connected libraries for search (spec 011 FR-027, spec 012): the name, the folder path and the type from the
/// derived copy that <see cref="ConnectedSync"/> keeps. At query time a person reads an item only when the library's
/// audience includes them, they are not a guest, and the mirrored SharePoint readers list them by email; mirrored
/// readers only ever narrow access (ADR-0016). Results link to a TeamBeats page that sends the person on to SharePoint,
/// where SharePoint checks access again.
/// </summary>
internal sealed class ConnectedSearchSource(IDbSession session, IEntitlementService entitlements, DocumentAccess access, ITenantContext context,
    IUserDirectory users) : ISearchSource
{
    public string Source => ConnectedSync.SearchSource;

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select i.name, i.media_type, i.modified_at, i.library_id, l.org_unit_id, l.classification_default,
              (with recursive up as (
                 select p.parent_remote_id, p.name, 1 as depth from documents.connected_item p where p.library_id = i.library_id and p.remote_id = i.parent_remote_id
                 union all
                 select p.parent_remote_id, p.name, u.depth + 1 from documents.connected_item p join up u on p.library_id = i.library_id and p.remote_id = u.parent_remote_id
                 where u.depth < 20)
               select string_agg(name, ' / ' order by depth desc) from up) as path
            from documents.connected_item i join documents.library l on l.id = i.library_id
            where i.id = @itemId and not i.folder and l.status = 'active' and l.source = 'connected'
            """, new { itemId },
            r => new
            {
                Name = r.GetString(0), Type = r.Get<string>("media_type"), Modified = r.Get<DateTimeOffset?>("modified_at"), Library = r.GetGuid(3),
                Unit = r.Get<Guid?>("org_unit_id"), Classification = r.GetString(5), Path = r.Get<string>("path"),
            }, ct);
        if (row is null)
        {
            return [];
        }
        var text = string.Join("\n", new[] { row.Path, row.Type }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var locale = row.Name.Any(c => c is >= '؀' and <= 'ۿ') ? "ar" : "en";
        return [new SearchUnit(string.Empty, SearchKinds.Document, locale, row.Name, text, Link(row.Library, itemId), row.Classification, row.Unit, null, row.Modified)];
    }

    public static string Link(Guid libraryId, Guid itemId) => $"/documents/libraries/{libraryId}/connected/{itemId}";

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync("select id from documents.connected_item where not folder and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var allowed = new HashSet<Guid>();
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.DocsCore, ct) || context.ActorId is not { } user
            || await access.IsGuestAsync(ct) || (await users.GetAsync(user, ct))?.Email is not { Length: > 0 } email)
        {
            return allowed;
        }
        var rows = await session.QueryAsync(
            "select id, library_id from documents.connected_item where id = any(@ids) and not folder and (anyone or @email = any(readers))",
            new { ids = itemIds.ToArray(), email = email.ToLowerInvariant() }, r => (Id: r.GetGuid(0), Library: r.GetGuid(1)), ct);
        foreach (var group in rows.GroupBy(r => r.Library))
        {
            if (await access.LibraryAsync(group.Key, ct) is not { Connected: not null } library || !library.Active)
            {
                continue;
            }
            var securable = DocumentAccess.LibrarySecurable(library);
            if ((await access.RightsAsync(library, [securable], ct)).TryGetValue(library.Id, out var rights) && rights.View)
            {
                allowed.UnionWith(group.Select(r => r.Id));
            }
        }
        return allowed;
    }
}
