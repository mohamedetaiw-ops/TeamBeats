using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

public sealed record ConnectedSettingsRequest(bool? UploadEnabled);

public sealed record ConnectedRenameRequest(string? Item, string? Name);

/// <summary>
/// The outcome of a write to a connected library. <paramref name="State"/> is <c>ok</c> with the item as SharePoint now
/// has it, or why nothing was written: <c>not_connected</c> or <c>reconnect</c> (the person must connect their
/// Microsoft 365 account), <c>forbidden</c> (SharePoint refuses them), <c>conflict</c> (the name is taken),
/// <c>not_found</c>, <c>too_large</c> or <c>unavailable</c>.
/// </summary>
public sealed record ConnectedWriteView(string State, ConnectedItemView? Item);

/// <summary>
/// Writes to connected libraries (spec 011 FR-029). When a library manager turns uploads on, people who may contribute
/// to the library in TeamBeats upload files, add new versions and rename items, and each write runs as the person in
/// SharePoint, so SharePoint's own permissions decide too. TeamBeats keeps no copy of the content; the library is
/// synced again straight away so search and the last synced listing catch up.
/// </summary>
public sealed partial class Libraries
{
    /// <summary>The largest file uploaded to a connected library in one request, as SharePoint takes in one upload.</summary>
    public const long MaxConnectedUploadBytes = 250L * 1024 * 1024;

    /// <summary>Turns uploads on or off for a connected library; for its managers.</summary>
    public async Task<LibraryView> ConnectedSettingsAsync(Guid id, ConnectedSettingsRequest request, CancellationToken ct)
    {
        var library = await ManagedAsync(id, ct, forUpdate: true);
        if (library.Connected is null)
        {
            throw AppException.NotFound();
        }
        if (request.UploadEnabled is not { } upload)
        {
            throw AppException.Validation("uploadEnabled", "validation.required");
        }
        await session.ExecAsync("update documents.library set upload_enabled = @upload where id = @id", new { id, upload }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocConnectedSettings, Securables.Library, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { uploadEnabled = upload }), ct);
        var updated = (await Access.LibraryAsync(id, ct))!;
        return await ViewAsync(updated, await Access.RightsAsync(updated, DocumentAccess.LibrarySecurable(updated), ct), ct);
    }

    /// <summary>Uploads a file into a folder of a connected library as the caller; a file of the same name gets a new version.</summary>
    public async Task<ConnectedWriteView> UploadConnectedAsync(Guid id, string? folder, string? name, Stream content, long? length, CancellationToken ct)
    {
        var (library, source) = await WritableAsync(id, ct);
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        {
            errors.Add("name", "validation.documents.file_name");
        }
        if (length is not > 0)
        {
            errors.Add("file", "validation.documents.file_empty");
        }
        else if (length > MaxConnectedUploadBytes)
        {
            errors.Add("file", "validation.documents.file_size");
        }
        if ((folder?.Length ?? 0) > MaxIdLength)
        {
            errors.Add("folder", "validation.documents.connected_folder");
        }
        errors.ThrowIfAny();
        var write = await repositories.UploadAsUserAsync(source.ConnectionId, source.ContainerId, folder ?? source.FolderId, name!.Trim(), content, length!.Value,
            await EmailAsync(ct), ct);
        return await WrittenAsync(library, write, AuditActions.DocConnectedUploaded, ct);
    }

    /// <summary>Renames a file or folder of a connected library as the caller.</summary>
    public async Task<ConnectedWriteView> RenameConnectedAsync(Guid id, ConnectedRenameRequest request, CancellationToken ct)
    {
        var (library, source) = await WritableAsync(id, ct);
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(request.Item) || request.Item.Length > MaxIdLength || request.Item == source.FolderId)
        {
            errors.Add("item", "validation.documents.connected_item");
        }
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            errors.Add("name", "validation.documents.file_name");
        }
        errors.ThrowIfAny();
        var write = await repositories.RenameAsUserAsync(source.ConnectionId, request.Item!, request.Name!.Trim(), await EmailAsync(ct), ct);
        if (write.Item is { } item && item.ContainerId != source.ContainerId)
        {
            return new ConnectedWriteView("not_found", null);
        }
        return await WrittenAsync(library, write, AuditActions.DocConnectedRenamed, ct);
    }

    /// <summary>A connected library with uploads on, where the caller may contribute in TeamBeats. SharePoint decides the rest.</summary>
    private async Task<(LibraryRow Library, ConnectedSource Source)> WritableAsync(Guid id, CancellationToken ct)
    {
        var (library, source) = await ConnectedVisibleAsync(id, ct);
        if (!library.Active)
        {
            throw AppException.Conflict("conflict.documents.library_archived");
        }
        if (!source.UploadEnabled)
        {
            throw AppException.Conflict("conflict.documents.connected_read_only");
        }
        if (!(await Access.RightsAsync(library, DocumentAccess.LibrarySecurable(library), ct)).Contribute)
        {
            throw AppException.Denied();
        }
        return (library, source);
    }

    private async Task<ConnectedWriteView> WrittenAsync(LibraryRow library, RepositoryWrite write, string action, CancellationToken ct)
    {
        if (write is not { State: "ok", Item: { } item })
        {
            return new ConnectedWriteView(write.State == "ok" ? "unavailable" : write.State, null);
        }
        await audit.WriteAsync(new AuditEntry(action, Securables.Library, library.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { item = item.Id, name = item.Name }), ct);
        // Synced again now, so search and the last synced listing show the change without waiting for the next round.
        await outbox.EnqueueAsync(new ConnectedLibrarySyncRequested(library.Id, false), ct);
        return new ConnectedWriteView("ok", new ConnectedItemView(item.Id, item.Name, item.Folder, item.MediaType, item.Size, item.ModifiedAt, item.WebLink));
    }
}
