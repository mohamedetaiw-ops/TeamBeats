using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Whether "Edit in browser" is offered on a document, for the signed-in person. <see cref="State"/> is
/// <c>unavailable</c> (not an Office file, not theirs to edit, or the connection feature is not there), <c>ready</c>,
/// <c>editing</c> (with <see cref="EditUrl"/>), or a Microsoft 365 state the person can act on such as
/// <c>not_connected</c> or <c>reconnect</c>.
/// </summary>
public sealed record OfficeEditView(string State, string? EditUrl, DateTimeOffset? StartedAt)
{
    public const string Unavailable = "unavailable";
    public const string Ready = "ready";
    public const string Editing = "editing";
}

/// <summary>Check-in of the edited copy: the version note, whether to publish the next major version, and the reason for a sensitive file.</summary>
public sealed record OfficeCheckInRequest(string? Note, bool Publish, string? SensitiveReason = null);

/// <summary>
/// Office editing in the browser through Microsoft 365 (spec 011 FR-010, US2 scenario 6). Starting checks the document
/// out and places a copy of the working file in the contributor's own OneDrive, which Office for the web opens.
/// Checking in reads the edited copy back through the normal check-in, so it is scanned and becomes a TeamBeats version
/// with the contributor as author, then the copy is removed. Discarding releases the check-out and removes the copy.
/// </summary>
public sealed class OfficeEditing(
    IDbSession session,
    ITenantContext context,
    DocumentFiles files,
    DocumentVersions versions,
    TenantObjectStorage storage,
    IOfficeEditor editor,
    IAuditWriter audit,
    IClock clock)
{
    /// <summary>The Office Open XML formats Office for the web edits.</summary>
    public static readonly IReadOnlySet<string> Formats = new HashSet<string>(StringComparer.Ordinal) { "docx", "xlsx", "pptx" };

    /// <summary>The largest file placed in one request (Microsoft Graph's simple upload limit).</summary>
    public const long MaxBytes = 250L * 1024 * 1024;

    private sealed record EditRow(Guid UserId, string ItemId, string EditUrl, DateTimeOffset StartedAt);

    private Task<EditRow?> RowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync("select user_id, item_id, edit_url, started_at from documents.office_edit where document_id = @id", new { id },
            r => new EditRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3)), ct);

    private async Task<(string Name, string Key, long Size)?> WorkingFileAsync(DocumentFiles.DocRow doc, CancellationToken ct)
    {
        var version = doc.Pending ?? doc.Current;
        if (version is null)
        {
            return null;
        }
        var row = await session.QuerySingleOrDefaultAsync("select file_name, object_key, size_bytes from documents.document_version where id = @version",
            new { version }, r => (Name: r.GetString(0), Key: r.GetString(1), Size: r.GetInt64(2)), ct);
        return row == default ? null : row;
    }

    private static bool Editable(DocumentFiles.DocRow doc) =>
        doc.CheckedIn && !doc.Archived && doc.ReleaseInstance is null && doc.Classification != "restricted";

    public async Task<OfficeEditView> GetAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (_, doc, rights) = await files.VisibleAsync(id, ct);
        if (await RowAsync(id, ct) is { } row)
        {
            return row.UserId == user ? new OfficeEditView(OfficeEditView.Editing, row.EditUrl, row.StartedAt) : new OfficeEditView(OfficeEditView.Unavailable, null, null);
        }
        var file = await WorkingFileAsync(doc, ct);
        if (!rights.Contribute || !Editable(doc) || (doc.CheckedOutBy is { } holder && holder != user) || file is not { } f
            || !Formats.Contains(FileTypes.ExtensionOf(f.Name)) || f.Size > MaxBytes)
        {
            return new OfficeEditView(OfficeEditView.Unavailable, null, null);
        }
        var state = await editor.StateAsync(ct);
        return state switch
        {
            M365States.Ok => new OfficeEditView(OfficeEditView.Ready, null, null),
            M365States.NotInPlan or M365States.TurnedOff => new OfficeEditView(OfficeEditView.Unavailable, null, null),
            _ => new OfficeEditView(state, null, null),
        };
    }

    /// <summary>Checks the document out to the caller and opens the working file in Office for the web.</summary>
    public async Task<OfficeEditView> StartAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (await RowAsync(id, ct) is { } existing)
        {
            if (existing.UserId != user)
            {
                throw AppException.Conflict("conflict.documents.checked_out");
            }
            return new OfficeEditView(OfficeEditView.Editing, existing.EditUrl, existing.StartedAt);
        }
        // The normal check-out applies every rule: contribute right, nobody else holding it, not archived or in approval.
        await versions.CheckOutAsync(id, ct);
        var (library, doc, _) = await files.VisibleAsync(id, ct, forUpdate: true);
        if (doc.Classification == "restricted")
        {
            throw AppException.Conflict("conflict.documents.office_restricted");
        }
        var file = await WorkingFileAsync(doc, ct) ?? throw AppException.Conflict("conflict.documents.office_format");
        if (!Formats.Contains(FileTypes.ExtensionOf(file.Name)))
        {
            throw AppException.Conflict("conflict.documents.office_format");
        }
        if (file.Size > MaxBytes)
        {
            throw AppException.Validation("file", "validation.documents.office_size");
        }
        OfficeEditCopy copy;
        await using (var content = await storage.OpenReadAsync(file.Key, ct) ?? throw new InvalidOperationException("A version's file is in storage."))
        {
            copy = await editor.PlaceAsync(file.Name, content, file.Size, ct);
        }
        if (copy.State != M365States.Ok)
        {
            throw Unavailable(copy.State);
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.office_edit (tenant_id, document_id, user_id, item_id, edit_url, version_id, started_at)
            values (platform.current_tenant_id(), @id, @user, @item, @url, @version, @now)
            """, new { id, user, item = copy.ItemId, url = copy.EditUrl, version = doc.Pending ?? doc.Current, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocOfficeEditStarted, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId), ct);
        return new OfficeEditView(OfficeEditView.Editing, copy.EditUrl, now);
    }

    /// <summary>
    /// Reads the edited copy back and checks it in as a new version. The returned call removes the copy; run it after
    /// the commit, so the copy is never removed for a check-in that did not happen.
    /// </summary>
    public async Task<(DocumentView View, Func<CancellationToken, Task<bool>> RemoveCopy)> CheckInAsync(Guid id, CheckInRequest request, CancellationToken ct)
    {
        var row = await MineAsync(id, ct);
        await using var content = await editor.ReadAsync(row.ItemId, ct);
        if (content.State == OfficeEditContent.Gone)
        {
            throw AppException.Conflict("conflict.documents.office_copy_gone");
        }
        if (content.State != M365States.Ok || content.Content is null)
        {
            throw Unavailable(content.State);
        }
        var name = await session.ScalarAsync<string>("select v.file_name from documents.document_version v join documents.office_edit e on e.version_id = v.id where e.document_id = @id",
            new { id }, ct);
        await session.ExecAsync("delete from documents.office_edit where document_id = @id", new { id }, ct);
        var view = await versions.CheckInAsync(id, request, name, content.Content, content.Length, ct);
        return (view, await EndAsync(id, row, "checked_in", ct));
    }

    /// <summary>Ends the edit without a new version: the check-out is released; the returned call removes the copy after the commit.</summary>
    public async Task<(DocumentView View, Func<CancellationToken, Task<bool>> RemoveCopy)> DiscardAsync(Guid id, CancellationToken ct)
    {
        var row = await MineAsync(id, ct);
        await session.ExecAsync("delete from documents.office_edit where document_id = @id", new { id }, ct);
        var view = await versions.DiscardAsync(id, ct);
        return (view, await EndAsync(id, row, "discarded", ct));
    }

    private async Task<EditRow> MineAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        // Visibility first, so a document the caller cannot see answers 404 whether or not it is being edited.
        await files.VisibleAsync(id, ct, forUpdate: true);
        var row = await RowAsync(id, ct);
        return row is not null && row.UserId == user ? row : throw AppException.Conflict("conflict.documents.office_not_editing");
    }

    private async Task<Func<CancellationToken, Task<bool>>> EndAsync(Guid id, EditRow row, string outcome, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditEntry(AuditActions.DocOfficeEditEnded, Securables.Document, id.ToString(), Changes: new { outcome }), ct);
        // A copy that cannot be removed stays in the person's own OneDrive folder; nothing in TeamBeats depends on it.
        return await editor.RemoverAsync(row.ItemId, ct);
    }

    private static AppException Unavailable(string state) =>
        new(ProblemCodes.Conflict, 409, $"conflict.documents.office_{(state is M365States.NotConnected or M365States.Reconnect ? "reconnect" : "unavailable")}", null,
            new Dictionary<string, object?> { ["state"] = state });
}
