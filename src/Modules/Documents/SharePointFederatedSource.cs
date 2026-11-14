using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// SharePoint as a federated search source (spec 012 US5, FR-016, FR-017): the files of the SharePoint connected
/// libraries (011) the person may open in TeamBeats, searched live in Microsoft 365 with their own delegated sign-in, so
/// SharePoint's permissions decide what they find. Each result links to the file in SharePoint and names its library.
/// The whole document library behind a connected library is searched, even when the library shows one folder of it,
/// because SharePoint has already decided that the person may read each file found.
/// </summary>
internal sealed class SharePointFederatedSource(IDbSession session, IEntitlementService entitlements, Libraries libraries, IDocumentRepository repositories,
    RequestLocale locale) : IFederatedSource
{
    public const string Name = "sharepoint";
    public const int MaxLibraries = 200;

    public string Key => Name;

    public async Task<FederatedAnswer> QueryAsync(string query, int limit, CancellationToken deadline, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.DocsCore, ct) || await libraries.Access.IsGuestAsync(ct))
        {
            return new FederatedAnswer("none", []);
        }
        var microsoft = (await repositories.SourcesAsync(ct)).Where(s => s.Kind == ConnectionKinds.Microsoft365).Select(s => s.ConnectionId).ToHashSet();
        var candidates = await session.QueryAsync(
            "select id from documents.library where source = 'connected' and status = 'active' order by created_at limit @max", new { max = MaxLibraries },
            r => r.GetGuid(0), ct);
        // The libraries this person may open in TeamBeats, each with where it lives in SharePoint.
        var visible = new List<(Guid Connection, string Container, string Name)>();
        foreach (var id in candidates)
        {
            try
            {
                var (library, _) = await libraries.VisibleAsync(id, ct);
                if (library.Connected is { } source && microsoft.Contains(source.ConnectionId))
                {
                    visible.Add((source.ConnectionId, source.ContainerId, library.Names.GetValueOrDefault(locale.Locale) ?? library.Names.Values.FirstOrDefault() ?? string.Empty));
                }
            }
            catch (AppException)
            {
                // Not for this person.
            }
        }
        if (visible.Count == 0)
        {
            return new FederatedAnswer("none", []);
        }
        var hits = new List<FederatedHit>();
        var states = new List<string>();
        foreach (var connection in visible.GroupBy(v => v.Connection))
        {
            var names = connection.GroupBy(v => v.Container, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
            var found = await repositories.SearchAsUserAsync(connection.Key, names.Keys.ToList(), query, limit, deadline, ct);
            states.Add(found.State);
            hits.AddRange(found.Hits.Where(h => h.Item.WebLink is not null).Select(h => new FederatedHit(h.Item.Name, h.Item.WebLink!, h.Summary,
                names.GetValueOrDefault(h.Item.ContainerId), h.Item.MediaType, h.Item.ModifiedAt)));
        }
        // One connection answering is enough to show results; otherwise the first reason it could not.
        var state = states.Contains("ok") ? "ok" : states.FirstOrDefault(s => s is "not_connected" or "reconnect") ?? (states.All(s => s == "not_found") ? "ok" : "unavailable");
        return new FederatedAnswer(state, hits.Take(limit).ToList());
    }
}
