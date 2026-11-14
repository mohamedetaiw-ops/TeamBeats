using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Documents for search (spec 012 section 2, spec 011): the version readers get (the latest major version in force),
/// never minor drafts, deleted, withdrawn or archived documents (FR-011, US6). The unit holds the name, the number, metadata values and
/// the text extracted at upload. Reading is decided by the library, folder and document rights of spec 011, in batch.
/// </summary>
internal sealed class DocumentSearchSource(IDbSession session, IEntitlementService entitlements, DocumentAccess access) : ISearchSource
{
    public string Source => "documents";

    private const string Readable = "d.deleted_at is null and d.checked_in and d.effective_version_id is not null and d.disposition is distinct from 'archived'";

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            $"""
            select d.name, d.number, d.metadata::text, d.classification, l.org_unit_id, v.major, v.text_extract, v.file_name, coalesce(v.effective_from, v.created_at)
            from documents.document d
            join documents.library l on l.id = d.library_id
            join documents.document_version v on v.id = d.effective_version_id
            where d.id = @itemId and {Readable}
            """, new { itemId },
            r => new
            {
                Name = r.GetString(0), Number = r.Get<string>("number"), Metadata = r.GetString(2), Classification = r.GetString(3), Unit = r.Get<Guid?>("org_unit_id"),
                Major = r.GetInt32(5), Text = r.Get<string>("text_extract"), File = r.GetString(7), At = r.GetFieldValue<DateTimeOffset>(8),
            }, ct);
        if (row is null)
        {
            return [];
        }
        var text = string.Join("\n", new[] { row.Number, row.File, MetadataText(row.Metadata), row.Text }.Where(t => !string.IsNullOrWhiteSpace(t)));
        var locale = SearchLocale(row.Name + " " + row.Text);
        return [new SearchUnit(string.Empty, SearchKinds.Document, locale, row.Name, text, DocumentReleases.Link(itemId), row.Classification, row.Unit, row.Major, row.At)];
    }

    /// <summary>Documents have no locale of their own; Arabic text is treated as Arabic, anything else as English for stemming.</summary>
    private static string SearchLocale(string text) => text.Any(c => c is >= '؀' and <= 'ۿ') ? "ar" : "en";

    private static string MetadataText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var values = new List<string>();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        Walk(p.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var i in e.EnumerateArray())
                    {
                        Walk(i);
                    }
                    break;
                case JsonValueKind.String:
                    values.Add(e.GetString()!);
                    break;
            }
        }
        Walk(doc.RootElement);
        return string.Join(" ", values);
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync($"select d.id from documents.document d where {Readable} and (@after::uuid is null or d.id > @after) order by d.id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var allowed = new HashSet<Guid>();
        if (itemIds.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.DocsCore, ct))
        {
            return allowed;
        }
        var rows = await session.QueryAsync(
            $"select d.id, d.library_id, d.securable_type, d.securable_id, d.classification from documents.document d where d.id = any(@ids) and {Readable}",
            new { ids = itemIds.ToArray() }, r => (Id: r.GetGuid(0), Library: r.GetGuid(1), Type: r.GetString(2), Securable: r.GetGuid(3), Classification: r.GetString(4)), ct);
        foreach (var group in rows.GroupBy(r => r.Library))
        {
            if (await access.LibraryAsync(group.Key, ct) is not { } library || library.Status != "active")
            {
                continue;
            }
            var securables = group.Select(r => new Securable(r.Type, r.Securable, r.Type == Securables.Document ? r.Classification : library.ClassificationDefault))
                .DistinctBy(s => s.Id).ToList();
            var rights = await access.RightsAsync(library, securables, ct);
            var closed = await access.ClosedAsync(library, ct);
            allowed.UnionWith(group.Where(r => rights.TryGetValue(r.Securable, out var right) && right.View && !closed.Contains(r.Classification, StringComparer.Ordinal))
                .Select(r => r.Id));
        }
        return allowed;
    }
}
