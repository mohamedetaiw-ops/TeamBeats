using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Removes what TeamBeats derived from a SharePoint document library once it is no longer served (spec 011 data model:
/// the derived copy is deleted with the site or the connection). When an allowed site is removed, each connected
/// library whose document library the connection no longer lists is archived, its synced copy is deleted and search
/// drops its files. When the connection is deleted, all its connected libraries go the same way and its label
/// mapping is removed. The libraries stay archived, so their audience and history remain for the record.
/// </summary>
public sealed class ConnectedCleanup(IDbSession session, IDocumentRepository repositories, IOutbox outbox, IAuditWriter audit)
    : IMessageHandler<DocumentContainersRemoved>, IMessageHandler<ConnectionDeleted>
{
    public async Task HandleAsync(DocumentContainersRemoved message, CancellationToken ct)
    {
        var libraries = await LibrariesAsync(message.ConnectionId, ct);
        if (libraries.Count == 0)
        {
            return;
        }
        // Throws while SharePoint cannot be read, so the message is tried again rather than archiving on a guess.
        var served = (await repositories.ContainersAsync(message.ConnectionId, ct)).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, container) in libraries)
        {
            if (!served.Contains(container))
            {
                await RetireAsync(id, "site_removed", ct);
            }
        }
    }

    public async Task HandleAsync(ConnectionDeleted message, CancellationToken ct)
    {
        foreach (var (id, _) in await LibrariesAsync(message.ConnectionId, ct))
        {
            await RetireAsync(id, "connection_deleted", ct);
        }
        await session.ExecAsync("delete from documents.sensitivity_label_map where connection_id = @c", new { c = message.ConnectionId }, ct);
    }

    private async Task<IReadOnlyList<(Guid Id, string Container)>> LibrariesAsync(Guid connectionId, CancellationToken ct) =>
        await session.QueryAsync(
            "select id, container_id from documents.library where source = 'connected' and connection_id = @connectionId and status = 'active'",
            new { connectionId }, r => (r.GetGuid(0), r.GetString(1)), ct);

    private async Task RetireAsync(Guid libraryId, string reason, CancellationToken ct)
    {
        var files = await session.QueryAsync("delete from documents.connected_item where library_id = @libraryId returning id, folder",
            new { libraryId }, r => (Id: r.GetGuid(0), Folder: r.GetBoolean(1)), ct);
        await session.ExecAsync("delete from documents.connected_sync where library_id = @libraryId", new { libraryId }, ct);
        await session.ExecAsync("update documents.library set status = 'archived' where id = @libraryId", new { libraryId }, ct);
        foreach (var (id, _) in files.Where(f => !f.Folder))
        {
            // The source finds nothing for a removed item, so search drops it.
            await outbox.EnqueueAsync(new SearchItemChanged(ConnectedSync.SearchSource, id), ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocConnectedRemoved, Securables.Library, libraryId.ToString(), Changes: new { reason, items = files.Count }), ct);
    }
}
