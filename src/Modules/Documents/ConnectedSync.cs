using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

/// <summary>Asks for one connected library to be synced; <paramref name="Full"/> walks the whole library again.</summary>
public sealed record ConnectedLibrarySyncRequested(Guid LibraryId, bool Full) : IIntegrationEvent;

/// <summary>
/// Keeps the derived copy of each connected library in step with SharePoint (spec 011 FR-028, FR-031, ADR-0016 section
/// 6): metadata and mirrored readers of every file and folder, never content. Changes are read with the repository's
/// change cursor every two minutes, within the five minute KNO-006 objective, and the whole library is walked again
/// once a day so anything a change missed is put right. When SharePoint cannot be read the library is marked degraded
/// since that moment, the integration administrators are told once, and the last synced copy stays for listing.
/// Mirrored readers only narrow access: a person finds an item in search only when the library's audience includes
/// them and SharePoint lists them, by email, as a reader.
/// </summary>
public sealed class ConnectedSync(
    IDbSession session,
    IDocumentRepository repositories,
    IOutbox outbox,
    IAuthorizationProvisioning roles,
    INotificationSender notifications,
    IClock clock) : IMessageHandler<ConnectedLibrarySyncRequested>
{
    public const string SearchSource = "connected_documents";
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan FullEvery = TimeSpan.FromHours(24);

    /// <summary>A safety bound on one library's copy; a larger library is synced up to this many items.</summary>
    public const int MaxItems = 50_000;

    private const int MaxChangePages = 50;

    /// <summary>Queues a sync for each connected library that is due; called by the maintenance worker every minute.</summary>
    public async Task QueueDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select l.id, s.last_full_at is null or s.last_full_at < @full or s.cursor is null
            from documents.library l left join documents.connected_sync s on s.library_id = l.id
            where l.source = 'connected' and l.status = 'active' and (s.last_sync_at is null or s.last_sync_at < @since)
            order by s.last_sync_at nulls first limit 50
            """, new { since = now - Every, full = now - FullEvery }, r => (Id: r.GetGuid(0), Full: r.GetBoolean(1)), ct);
        foreach (var (id, full) in due)
        {
            // Marked now, so the next tick does not queue the same library while this sync waits in the queue.
            await session.ExecAsync(
                """
                insert into documents.connected_sync (tenant_id, library_id, last_sync_at) values (platform.current_tenant_id(), @id, @now)
                on conflict (tenant_id, library_id) do update set last_sync_at = @now
                """, new { id, now }, ct);
            await outbox.EnqueueAsync(new ConnectedLibrarySyncRequested(id, full), ct);
        }
    }

    public async Task HandleAsync(ConnectedLibrarySyncRequested message, CancellationToken ct)
    {
        var library = await session.QuerySingleOrDefaultAsync(
            """
            select connection_id, container_id, remote_folder_id from documents.library
            where id = @id and source = 'connected' and status = 'active' for update
            """, new { id = message.LibraryId },
            r => new ConnectedSource(r.GetGuid(0), r.GetString(1), null, r.Get<string>("remote_folder_id"), false), ct);
        if (library is null)
        {
            return;
        }
        var state = await session.QuerySingleOrDefaultAsync(
            "select cursor, degraded_since from documents.connected_sync where library_id = @id",
            new { id = message.LibraryId }, r => (Cursor: r.Get<string>("cursor"), Degraded: r.Get<DateTimeOffset?>("degraded_since")), ct);
        var now = clock.UtcNow;
        try
        {
            var full = message.Full || state.Cursor is null;
            var cursor = full ? await FullAsync(message.LibraryId, library, ct) : await ChangesAsync(message.LibraryId, library, state.Cursor!, ct);
            await session.ExecAsync(
                """
                insert into documents.connected_sync (tenant_id, library_id, cursor, last_sync_at, last_success_at, last_full_at)
                values (platform.current_tenant_id(), @id, @cursor, @now, @now, case when @full then @now end)
                on conflict (tenant_id, library_id) do update set cursor = @cursor, last_sync_at = @now, last_success_at = @now,
                  last_full_at = case when @full then @now else documents.connected_sync.last_full_at end, degraded_since = null, reason = null
                """, new { id = message.LibraryId, cursor, now, full }, ct);
        }
        catch (RepositoryUnavailableException ex)
        {
            await session.ExecAsync(
                """
                insert into documents.connected_sync (tenant_id, library_id, last_sync_at, degraded_since, reason)
                values (platform.current_tenant_id(), @id, @now, @now, @reason)
                on conflict (tenant_id, library_id) do update set last_sync_at = @now,
                  degraded_since = coalesce(documents.connected_sync.degraded_since, @now), reason = @reason
                """, new { id = message.LibraryId, now, reason = ex.Reason }, ct);
            if (state.Degraded is null)
            {
                await AlertAsync(message.LibraryId, ct);
            }
        }
    }

    /// <summary>Walks the library from its root, replaces the copy with what is there now, and returns the cursor of the walk's start.</summary>
    private async Task<string> FullAsync(Guid libraryId, ConnectedSource source, CancellationToken ct)
    {
        // The cursor is taken first, so a change made during the walk is read again by the next sync.
        var cursor = (await repositories.ChangesAsync(source.ConnectionId, source.ContainerId, null, ct)).Cursor;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Queue<string?>([source.FolderId]);
        while (folders.Count > 0 && seen.Count < MaxItems)
        {
            var folder = folders.Dequeue();
            string? page = null;
            do
            {
                var list = await repositories.ListAsync(source.ConnectionId, source.ContainerId, folder, page, ct);
                foreach (var item in list.Items)
                {
                    if (seen.Add(item.Id))
                    {
                        await UpsertAsync(libraryId, source, item, folder, ct);
                        if (item.Folder)
                        {
                            folders.Enqueue(item.Id);
                        }
                    }
                }
                page = list.NextCursor;
            }
            while (page is not null && seen.Count < MaxItems);
        }
        var gone = await session.QueryAsync("select id from documents.connected_item where library_id = @libraryId and not (remote_id = any(@seen))",
            new { libraryId, seen = seen.ToArray() }, r => r.GetGuid(0), ct);
        await RemoveAsync(gone, ct);
        return cursor;
    }

    /// <summary>Applies the changes since the cursor and returns the cursor to read from next time.</summary>
    private async Task<string> ChangesAsync(Guid libraryId, ConnectedSource source, string cursor, CancellationToken ct)
    {
        for (var pages = 0; pages < MaxChangePages; pages++)
        {
            var changes = await repositories.ChangesAsync(source.ConnectionId, source.ContainerId, cursor, ct);
            foreach (var change in changes.Changes)
            {
                var existing = await session.QuerySingleOrDefaultAsync("select id from documents.connected_item where library_id = @libraryId and remote_id = @remote",
                    new { libraryId, remote = change.ItemId }, r => (Guid?)r.GetGuid(0), ct);
                if (change.Removed || change.Item is null)
                {
                    if (existing is { } removed)
                    {
                        await RemoveAsync(await SubtreeAsync(removed, ct), ct);
                    }
                    continue;
                }
                // In the library when its parent is the library's root or an item already in the copy.
                var parent = change.Item.ParentId;
                var inside = parent == source.FolderId
                    || (parent is not null && await session.ScalarAsync<bool>(
                        "select exists (select 1 from documents.connected_item where library_id = @libraryId and remote_id = @parent and folder)",
                        new { libraryId, parent }, ct));
                if (inside && change.Item.Id != source.FolderId)
                {
                    await UpsertAsync(libraryId, source, change.Item, parent, ct);
                }
                else if (existing is { } moved)
                {
                    await RemoveAsync(await SubtreeAsync(moved, ct), ct);
                }
            }
            cursor = changes.Cursor;
            if (!changes.More)
            {
                break;
            }
        }
        return cursor;
    }

    /// <summary>Stores one item with its readers, read again with the connection's own access so permissions are current.</summary>
    private async Task UpsertAsync(Guid libraryId, ConnectedSource source, RepositoryItem listed, string? parent, CancellationToken ct)
    {
        var item = await repositories.GetAsync(source.ConnectionId, listed.Id, ct) ?? listed;
        var permissions = item.Permissions ?? [];
        var readers = permissions.Where(p => p.Principal is { Type: "user", Email: not null }).Select(p => p.Principal.Email!.ToLowerInvariant()).Distinct().ToArray();
        var anyone = permissions.Any(p => p.Principal.Type == "anyone");
        var id = await session.ScalarAsync<Guid>(
            """
            insert into documents.connected_item (tenant_id, id, library_id, remote_id, parent_remote_id, name, folder, media_type, size, modified_at,
              remote_version, web_link, readers, anyone, synced_at)
            values (platform.current_tenant_id(), @newId, @libraryId, @remote, @parent, @name, @folder, @type, @size, @modified, @version, @link, @readers, @anyone, @now)
            on conflict (tenant_id, library_id, remote_id) do update set parent_remote_id = @parent, name = @name, folder = @folder, media_type = @type,
              size = @size, modified_at = @modified, remote_version = @version, web_link = @link, readers = @readers, anyone = @anyone, synced_at = @now
            returning id
            """,
            new
            {
                newId = Ids.New(), libraryId, remote = item.Id, parent, name = item.Name, folder = item.Folder, type = item.MediaType, size = item.Size,
                modified = item.ModifiedAt, version = item.Version, link = item.WebLink, readers, anyone, now = clock.UtcNow,
            }, ct);
        if (!item.Folder)
        {
            await outbox.EnqueueAsync(new SearchItemChanged(SearchSource, id), ct);
        }
    }

    /// <summary>An item and everything under it in the copy.</summary>
    private async Task<IReadOnlyList<Guid>> SubtreeAsync(Guid id, CancellationToken ct) =>
        await session.QueryAsync(
            """
            with recursive tree as (
              select library_id, remote_id, id from documents.connected_item where id = @id
              union all
              select c.library_id, c.remote_id, c.id from documents.connected_item c join tree t on c.library_id = t.library_id and c.parent_remote_id = t.remote_id
            )
            select id from tree
            """, new { id }, r => r.GetGuid(0), ct);

    private async Task RemoveAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }
        await session.ExecAsync("delete from documents.connected_item where id = any(@ids)", new { ids = ids.ToArray() }, ct);
        foreach (var id in ids)
        {
            // The source finds nothing for a removed item, so search drops it.
            await outbox.EnqueueAsync(new SearchItemChanged(SearchSource, id), ct);
        }
    }

    private async Task AlertAsync(Guid libraryId, CancellationToken ct)
    {
        var recipients = await roles.TenantHoldersAsync(Permissions.IntegrationManage, ct);
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.IntegrationAlert, "documents.connected_degraded",
            $"/documents/libraries/{libraryId}", DedupeKey: $"connected_degraded:{libraryId}", GroupKey: "connected_degraded"), ct);
    }
}
