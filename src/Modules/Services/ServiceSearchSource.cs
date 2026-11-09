using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Catalogue services for search (spec 012 section 2, spec 016): published services with their names, descriptions and
/// synonyms per locale. A person finds a service only when its audience includes them, as in the catalogue (FR-001).
/// </summary>
internal sealed class ServiceSearchSource(IDbSession session, IEntitlementService entitlements, Catalogue catalogue) : ISearchSource
{
    public string Source => "services";

    public static string Link(Guid id) => $"/services/{id}";

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var row = await session.QuerySingleOrDefaultAsync($"select {Catalogue.Columns} from services.service where id = @itemId and status = 'published'",
            new { itemId }, Catalogue.Read, ct);
        if (row is null)
        {
            return [];
        }
        return [.. row.Names.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n =>
        {
            var text = string.Join("\n", new[]
            {
                row.Descriptions.GetValueOrDefault(n.Key),
                row.Synonyms.TryGetValue(n.Key, out var synonyms) ? string.Join(", ", synonyms) : null,
            }.Where(t => !string.IsNullOrWhiteSpace(t)));
            return new SearchUnit(n.Key, SearchKinds.Service, n.Key, n.Value, text, Link(row.Id), row.Classification, row.OwnerOrgUnitId, row.CurrentVersion, row.UpdatedAt);
        })];
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default)
    {
        if (after is null)
        {
            await catalogue.SyncModuleServicesAsync(ct);
        }
        return await session.QueryAsync("select id from services.service where status = 'published' and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);
    }

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.WorkflowCatalogue, ct))
        {
            return new HashSet<Guid>();
        }
        var facts = await catalogue.ViewerAsync(ct);
        var rows = await session.QueryAsync($"select {Catalogue.Columns} from services.service where id = any(@ids) and status = 'published'",
            new { ids = itemIds.ToArray() }, Catalogue.Read, ct);
        return rows.Where(r => facts.Matches(r.Audience)).Select(r => r.Id).ToHashSet();
    }
}
