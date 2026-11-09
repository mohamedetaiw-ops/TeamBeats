using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record CheckInRequest(string? Note, bool Publish);

public sealed record ForceCheckInRequest(string? Reason);

public sealed record RestoreRequest(Guid VersionId, string? Note);

public sealed record VersionCompareView(string From, string To, IReadOnlyList<DiffSegment> Segments);

public sealed record BinItem(Guid Id, string Name, Guid? FolderId, DateTimeOffset DeletedAt, Guid? DeletedBy, DateTimeOffset PurgeAt);

/// <summary>
/// Editing with check-out and versions (spec 011 US2, FR-004, FR-005, FR-018). One person at a time holds a document;
/// they may save a working copy while they edit, then check in a minor version or publish the next major one. Library
/// managers can force a check-in with a reason, and the holder's working copy is kept as a separate draft for them.
/// Versions are never changed or removed except by the recycle bin purge; restoring an old one makes a new minor version.
/// </summary>
public sealed class DocumentVersions(
    IDbSession session,
    ITenantContext context,
    DocumentFiles files,
    Libraries libraries,
    IAuthorizationProvisioning acl,
    TenantObjectStorage storage,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxNoteLength = 1000;
    public const int MaxCompareBytes = 1024 * 1024;
    public static readonly TimeSpan CheckOutLimit = TimeSpan.FromDays(14);
    public const string CheckOutReminderTemplate = "documents.checkout_reminder";

    private static readonly IReadOnlySet<string> TextTypes = new HashSet<string>(StringComparer.Ordinal) { "text/plain", "text/csv", "text/markdown" };

    private static AppException HeldBy(DocumentFiles.DocRow doc) =>
        new(ProblemCodes.Conflict, 409, "conflict.documents.checked_out", null,
            new Dictionary<string, object?> { ["checkedOutBy"] = doc.CheckedOutBy, ["since"] = doc.CheckedOutAt });

    /// <summary>A checked-in document the caller contributes to and nobody else holds.</summary>
    private async Task<(LibraryRow Library, DocumentFiles.DocRow Doc)> EditableAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc, rights) = await files.VisibleAsync(id, ct, forUpdate: true);
        if (!doc.CheckedIn)
        {
            throw AppException.Conflict("conflict.documents.not_checked_in");
        }
        if (!rights.Contribute)
        {
            throw AppException.Denied();
        }
        if (doc.CheckedOutBy is { } holder && holder != user)
        {
            throw HeldBy(doc);
        }
        if (doc.ReleaseInstance is not null)
        {
            // The version under approval is the one approvers see; it may not change under them.
            throw AppException.Conflict("conflict.documents.in_release");
        }
        return (library, doc);
    }

    private static string? Note(string? note)
    {
        var text = note?.Trim();
        if (text is { Length: > MaxNoteLength })
        {
            throw AppException.Validation("note", "validation.documents.note");
        }
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public async Task<DocumentView> CheckOutAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc) = await EditableAsync(id, ct);
        if (doc.CheckedOutBy == user)
        {
            return await files.GetAsync(id, ct);
        }
        await session.ExecAsync("update documents.document set checked_out_by = @user, checked_out_at = @now, checkout_reminded_at = null where id = @id",
            new { id, user, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocCheckedOut, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId), ct);
        return await files.GetAsync(id, ct);
    }

    /// <summary>A file arriving for a version or working copy: stored under a new key, scanned, refused unless clean.</summary>
    private async Task<(Guid VersionId, string Key, string Hash, long Size, string ContentType, string Name)> ReceiveAsync(LibraryRow library, DocumentFiles.DocRow doc,
        string? fileName, Stream body, long? declared, CancellationToken ct)
    {
        var name = DocumentFiles.CleanName(fileName);
        var rules = await libraries.TenantRulesAsync(ct);
        var extension = FileTypes.ExtensionOf(name);
        if (!Libraries.EffectiveExtensions(library, rules).Contains(extension) || !FileTypes.ContentTypes.TryGetValue(extension, out var contentType))
        {
            throw AppException.Validation("file", "validation.documents.file_type");
        }
        var maxBytes = Libraries.EffectiveMaxMb(library, rules) * 1024L * 1024L;
        if (declared > maxBytes)
        {
            throw AppException.Validation("file", "validation.documents.file_size");
        }
        await files.QuotaAsync(declared ?? 0, ct);
        var versionId = Ids.New();
        var key = DocumentFiles.ObjectKey(library.Id, doc.Id, versionId);
        var (hash, size) = await files.StoreAsync(key, body, contentType, maxBytes, ct);
        try
        {
            await files.QuotaAsync(size, ct);
            if (await files.DuplicateAsync(library.Id, hash, doc.Id, ct))
            {
                throw AppException.Conflict("conflict.documents.duplicate");
            }
            var scan = await files.ScanStoredAsync(key, ct);
            if (scan == ScanStates.Infected)
            {
                await files.QuarantinedAsync(library, doc.Id, context.RequireUser(), ct);
                throw AppException.Validation("file", "validation.documents.file_infected");
            }
            if (scan != ScanStates.Clean)
            {
                throw AppException.Validation("file", "validation.documents.scan_failed");
            }
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        return (versionId, key, hash, size, contentType, name);
    }

    /// <summary>Saves what the holder is working on without checking in. It replaces any earlier working copy.</summary>
    public async Task<DocumentView> SaveWorkingCopyAsync(Guid id, string? fileName, Stream body, long? declared, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc) = await EditableAsync(id, ct);
        if (doc.CheckedOutBy != user)
        {
            throw AppException.Conflict("conflict.documents.check_out_first");
        }
        var received = await ReceiveAsync(library, doc, fileName, body, declared, ct);
        await DropWorkingCopyAsync(doc, ct);
        await InsertVersionAsync(library, doc.Id, received.VersionId, 0, 0, received.Key, received.Hash, received.Size, received.ContentType, received.Name, null,
            VersionStates.Pending, ct);
        await session.ExecAsync("update documents.document set pending_version_id = @v where id = @id", new { id, v = received.VersionId }, ct);
        return await files.GetAsync(id, ct);
    }

    /// <summary>
    /// Checks in a new version: the file in the request, or else the saved working copy. A minor version (2.1) unless
    /// published, which makes the next major version (3.0) the one readers get. A person who has not checked the
    /// document out may still check in at once when nobody else holds it.
    /// </summary>
    public async Task<DocumentView> CheckInAsync(Guid id, CheckInRequest request, string? fileName, Stream? body, long? declared, CancellationToken ct)
    {
        var (library, doc) = await EditableAsync(id, ct);
        var note = Note(request.Note);
        if (request.Publish && await libraries.ControlledOfAsync(doc.TypeId, ct) is not null)
        {
            throw AppException.Conflict("conflict.documents.controlled_release");
        }
        Guid versionId;
        if (body is not null)
        {
            var received = await ReceiveAsync(library, doc, fileName, body, declared, ct);
            await DropWorkingCopyAsync(doc, ct);
            versionId = received.VersionId;
            await InsertVersionAsync(library, doc.Id, versionId, 0, 0, received.Key, received.Hash, received.Size, received.ContentType, received.Name, note, VersionStates.Pending, ct);
        }
        else if (doc.Pending is { } working)
        {
            versionId = working;
            await session.ExecAsync("update documents.document_version set note = @note where id = @working", new { working, note }, ct);
        }
        else
        {
            throw AppException.Validation("file", "validation.documents.checkin_file");
        }
        var label = await NumberAsync(library, doc, versionId, request.Publish, ct);
        await session.ExecAsync(
            "update documents.document set checked_out_by = null, checked_out_at = null, pending_version_id = null, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocCheckedIn, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { version = versionId, label, note }), ct);
        return await files.GetAsync(id, ct);
    }

    /// <summary>Gives a version its number and makes it current; a published one becomes effective and supersedes the last.</summary>
    private async Task<string> NumberAsync(LibraryRow library, DocumentFiles.DocRow doc, Guid versionId, bool publish, CancellationToken ct)
    {
        var current = await files.VersionAsync(doc.Current, ct) ?? throw new InvalidOperationException("A checked-in document has a current version.");
        var (major, minor) = publish ? (current.Major + 1, 0) : (current.Major, current.Minor + 1);
        var now = clock.UtcNow;
        if (publish && doc.Effective is { } previous)
        {
            await session.ExecAsync("update documents.document_version set state = 'superseded' where id = @previous", new { previous }, ct);
        }
        await session.ExecAsync("update documents.document_version set major = @major, minor = @minor, state = @state, effective_from = @effective where id = @versionId",
            new { versionId, major, minor, state = publish ? VersionStates.Effective : VersionStates.Draft, effective = publish ? now : (DateTimeOffset?)null }, ct);
        await session.ExecAsync(
            "update documents.document set current_version_id = @versionId, effective_version_id = case when @publish then @versionId else effective_version_id end where id = @id",
            new { id = doc.Id, versionId, publish }, ct);
        var label = VersionLabels.Of(major, minor);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocVersionCreated, Securables.Document, doc.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { version = versionId, label, published = publish }), ct);
        if (publish)
        {
            await outbox.EnqueueAsync(new DocumentVersionPublished(doc.Id, library.Id, versionId, major, minor), ct);
        }
        return label;
    }

    private Task InsertVersionAsync(LibraryRow library, Guid documentId, Guid versionId, int major, int minor, string key, string hash, long size, string contentType,
        string fileName, string? note, string state, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into documents.document_version (tenant_id, id, document_id, library_id, major, minor, object_key, content_hash, size_bytes, content_type, file_name,
              note, author_user_id, state, scan_state, created_at)
            values (platform.current_tenant_id(), @versionId, @documentId, @lib, @major, @minor, @key, @hash, @size, @contentType, @fileName, @note, @user, @state, 'clean', @now)
            """,
            new { versionId, documentId, lib = library.Id, major, minor, key, hash, size, contentType, fileName, note, user = context.RequireUser(), state, now = clock.UtcNow }, ct);

    /// <summary>Removes a working copy that was never checked in; it is not a version, so nothing is lost from history.</summary>
    private async Task DropWorkingCopyAsync(DocumentFiles.DocRow doc, CancellationToken ct)
    {
        if (doc.Pending is not { } working || !doc.CheckedIn)
        {
            return;
        }
        var key = await session.ScalarAsync<string>("select object_key from documents.document_version where id = @working and state = 'pending'", new { working }, ct);
        await session.ExecAsync("update documents.document set pending_version_id = null where id = @id", new { id = doc.Id }, ct);
        await session.ExecAsync("delete from documents.download_token where version_id = @working; delete from documents.document_version where id = @working and state = 'pending'",
            new { working }, ct);
        if (key is not null)
        {
            session.OnCommitted(() => _ = storage.DeleteAsync(key, CancellationToken.None));
        }
    }

    /// <summary>The holder lets go without a new version; a saved working copy is removed.</summary>
    public async Task<DocumentView> DiscardAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (_, doc) = await EditableAsync(id, ct);
        if (doc.CheckedOutBy != user)
        {
            throw AppException.Conflict("conflict.documents.check_out_first");
        }
        await DropWorkingCopyAsync(doc, ct);
        await session.ExecAsync("update documents.document set checked_out_by = null, checked_out_at = null where id = @id", new { id }, ct);
        return await files.GetAsync(id, ct);
    }

    /// <summary>
    /// A library manager releases someone else's check-out with a reason (US2 scenario 3). The holder's working copy is
    /// kept as a separate draft document that only they see, so their changes are not lost.
    /// </summary>
    public async Task<DocumentView> ForceCheckInAsync(Guid id, ForceCheckInRequest request, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxNoteLength)
        {
            throw AppException.Validation("reason", "validation.documents.reason");
        }
        var (library, doc, rights) = await files.VisibleAsync(id, ct, forUpdate: true);
        if (!rights.Manage)
        {
            throw AppException.Denied();
        }
        if (doc.CheckedOutBy is not { } holder || !doc.CheckedIn)
        {
            throw AppException.Conflict("conflict.documents.not_checked_out");
        }
        Guid? draft = null;
        if (doc.Pending is { } working)
        {
            draft = Ids.New();
            var name = await session.ScalarAsync<string>("select file_name from documents.document_version where id = @working", new { working }, ct) ?? doc.Name;
            await session.ExecAsync(
                """
                insert into documents.document (tenant_id, id, library_id, folder_id, type_id, name, metadata, classification, owner_user_id, checked_in,
                  checked_out_by, checked_out_at, pending_version_id, securable_type, securable_id, created_by, created_at, updated_at)
                select tenant_id, @draft, library_id, folder_id, type_id, @name, metadata, classification, @holder, false,
                  @holder, @now, @working, securable_type, securable_id, @holder, @now, @now
                from documents.document where id = @id
                """, new { id, draft, name, holder, working, now = clock.UtcNow }, ct);
            await session.ExecAsync("update documents.document set pending_version_id = null where id = @id; update documents.document_version set document_id = @draft where id = @working",
                new { id, draft, working }, ct);
        }
        await session.ExecAsync("update documents.document set checked_out_by = null, checked_out_at = null, row_version = row_version + 1 where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocForcedCheckIn, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { holder, reason, keptDraft = draft }), ct);
        return await files.GetAsync(id, ct);
    }

    // History

    /// <summary>Every version for contributors, newest first; readers see only the effective one (FR-006).</summary>
    public async Task<IReadOnlyList<VersionView>> VersionsAsync(Guid id, CancellationToken ct)
    {
        var (_, doc, rights) = await files.VisibleAsync(id, ct);
        if (!rights.Contribute)
        {
            return doc.Effective is { } e && await files.VersionAsync(e, ct) is { } effective ? [effective] : [];
        }
        var ids = await session.QueryAsync("select id from documents.document_version where document_id = @id and state <> 'pending' order by major desc, minor desc",
            new { id }, r => r.GetGuid(0), ct);
        var list = new List<VersionView>();
        foreach (var v in ids)
        {
            list.Add((await files.VersionAsync(v, ct))!);
        }
        return list;
    }

    /// <summary>Shows what was added and removed between two versions of a text document (US2 scenario 4).</summary>
    public async Task<VersionCompareView> CompareAsync(Guid id, Guid from, Guid to, CancellationToken ct)
    {
        var (_, _, rights) = await files.VisibleAsync(id, ct);
        if (!rights.Contribute)
        {
            throw AppException.Denied();
        }
        async Task<(string Label, string Text)> LoadAsync(Guid versionId)
        {
            var row = await session.QuerySingleOrDefaultAsync(
                "select major, minor, object_key, content_type, size_bytes from documents.document_version where id = @versionId and document_id = @id and state <> 'pending'",
                new { versionId, id }, r => (Label: VersionLabels.Of(r.GetInt32(0), r.GetInt32(1)), Key: r.GetString(2), Type: r.GetString(3), Size: r.GetInt64(4)), ct);
            if (row.Key is null)
            {
                throw AppException.NotFound();
            }
            if (!TextTypes.Contains(row.Type))
            {
                throw AppException.Validation("from", "validation.documents.compare_format");
            }
            if (row.Size > MaxCompareBytes)
            {
                throw AppException.Validation("from", "validation.documents.compare_too_large");
            }
            await using var stream = await storage.OpenReadAsync(row.Key, ct) ?? throw AppException.NotFound();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return (row.Label, await reader.ReadToEndAsync(ct));
        }
        var left = await LoadAsync(from);
        var right = await LoadAsync(to);
        return new VersionCompareView(left.Label, right.Label, TextDiff.Words(left.Text, right.Text));
    }

    /// <summary>Makes a new minor version with an older version's content (US2 scenario 5). Nothing is overwritten.</summary>
    public async Task<DocumentView> RestoreAsync(Guid id, RestoreRequest request, CancellationToken ct)
    {
        var (library, doc) = await EditableAsync(id, ct);
        var note = Note(request.Note);
        var source = await session.QuerySingleOrDefaultAsync(
            "select major, minor, object_key, content_hash, size_bytes, content_type, file_name, scan_state from documents.document_version where id = @v and document_id = @id and state <> 'pending'",
            new { v = request.VersionId, id }, r => (Major: r.GetInt32(0), Minor: r.GetInt32(1), Key: r.GetString(2), Hash: r.GetString(3), Size: r.GetInt64(4), Type: r.GetString(5),
                Name: r.GetString(6), Scan: r.GetString(7)), ct);
        if (source.Key is null)
        {
            throw AppException.Validation("versionId", "validation.documents.version_unknown");
        }
        if (source.Scan != ScanStates.Clean)
        {
            throw AppException.Conflict("conflict.documents.not_clean");
        }
        var versionId = Ids.New();
        // The stored file is reused: versions never change, so two versions may share one object.
        await InsertVersionAsync(library, id, versionId, 0, 0, source.Key, source.Hash, source.Size, source.Type, source.Name,
            note ?? $"{VersionLabels.Of(source.Major, source.Minor)}", VersionStates.Pending, ct);
        var label = await NumberAsync(library, doc, versionId, publish: false, ct);
        await session.ExecAsync("update documents.document set updated_at = @now, row_version = row_version + 1 where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocRestored, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { from = request.VersionId, fromLabel = VersionLabels.Of(source.Major, source.Minor), version = versionId, label }), ct);
        return await files.GetAsync(id, ct);
    }

    // Recycle bin (FR-018)

    /// <summary>Moves a document to the recycle bin. Contributors may, and the uploader of a waiting first upload may too.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc, rights) = await files.VisibleAsync(id, ct, forUpdate: true);
        var waiting = !doc.CheckedIn && doc.CheckedOutBy == user;
        if (!waiting && !rights.Contribute)
        {
            throw AppException.Denied();
        }
        if (doc.CheckedIn && doc.CheckedOutBy is { } holder && holder != user)
        {
            throw HeldBy(doc);
        }
        if (doc.ReleaseInstance is not null)
        {
            throw AppException.Conflict("conflict.documents.in_release");
        }
        await DropWorkingCopyAsync(doc, ct);
        await session.ExecAsync(
            "update documents.document set deleted_at = @now, deleted_by = @user, checked_out_by = case when checked_in then null else checked_out_by end, checked_out_at = case when checked_in then null else checked_out_at end where id = @id",
            new { id, user, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocDeleted, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = library.Id, name = doc.Name }), ct);
        await outbox.EnqueueAsync(new DocumentDeleted(id, library.Id), ct);
    }

    /// <summary>Deleted documents of a library: all of them for its managers, the caller's own deletions for others.</summary>
    public async Task<IReadOnlyList<BinItem>> RecycleBinAsync(Guid libraryId, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, _) = await libraries.VisibleAsync(libraryId, ct);
        var rights = await libraries.Access.RightsAsync(library, await libraries.Access.SecurablesAsync(library, ct), ct);
        var days = (await libraries.TenantRulesAsync(ct)).RecycleDays;
        var rows = await session.QueryAsync(
            "select id, name, folder_id, deleted_at, deleted_by, securable_id from documents.document where library_id = @libraryId and deleted_at is not null order by deleted_at desc, id",
            new { libraryId }, r => (Item: new BinItem(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("folder_id"), r.GetFieldValue<DateTimeOffset>(3), r.Get<Guid?>("deleted_by"),
                r.GetFieldValue<DateTimeOffset>(3).AddDays(days)), Securable: r.GetGuid(5)), ct);
        // A securable broken after deletion is still in the list; one removed with its document falls back to the library.
        var libraryRights = rights[library.Id];
        return rows.Where(r =>
        {
            var at = rights.TryGetValue(r.Securable, out var found) ? found : libraryRights;
            return at.Manage || (at.Contribute && r.Item.DeletedBy == user);
        }).Select(r => r.Item).ToList();
    }

    /// <summary>Brings a document back from the recycle bin, for whoever deleted it or a library manager.</summary>
    public async Task<DocumentView> UndeleteAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var row = await session.QuerySingleOrDefaultAsync(
            "select library_id, deleted_by, securable_type, securable_id, classification from documents.document where id = @id and deleted_at is not null for update",
            new { id }, r => (Library: r.GetGuid(0), DeletedBy: r.Get<Guid?>("deleted_by"), Type: r.GetString(2), Securable: r.GetGuid(3), Classification: r.GetString(4)), ct);
        if (row.Type is null)
        {
            throw AppException.NotFound();
        }
        var (library, _) = await libraries.VisibleAsync(row.Library, ct);
        var rights = await libraries.Access.RightsAsync(library, new Securable(row.Type, row.Securable, row.Classification), ct);
        if (!rights.Manage && !(rights.Contribute && row.DeletedBy == user))
        {
            throw rights.View ? AppException.Denied() : AppException.NotFound();
        }
        await session.ExecAsync("update documents.document set deleted_at = null, deleted_by = null, updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocRestoredFromBin, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId), ct);
        return await files.GetAsync(id, ct);
    }

    // Background work for one tenant

    /// <summary>Purges documents that stayed in the recycle bin past the tenant's period, with their versions and files.</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var days = (await libraries.TenantRulesAsync(ct)).RecycleDays;
        var due = await session.QueryAsync(
            "select id, library_id, inherits from documents.document where deleted_at is not null and deleted_at <= @cutoff order by deleted_at limit @limit for update skip locked",
            new { cutoff = now.AddDays(-days), limit }, r => (Id: r.GetGuid(0), Library: r.GetGuid(1), Inherits: r.GetBoolean(2)), ct);
        foreach (var (id, libraryId, inherits) in due)
        {
            var keys = await session.QueryAsync("""
                select object_key from documents.document_version where document_id = @id
                union select preview_key from documents.document_version where document_id = @id and preview_key is not null
                union select controlled_key from documents.document_version where document_id = @id and controlled_key is not null
                """, new { id }, r => r.GetString(0), ct);
            await session.ExecAsync(
                """
                delete from documents.download_token where document_id = @id;
                delete from documents.read_confirmation where document_id = @id;
                update documents.document set pending_version_id = null, current_version_id = null, effective_version_id = null where id = @id;
                delete from documents.document_version where document_id = @id;
                delete from documents.document where id = @id;
                """, new { id }, ct);
            if (!inherits)
            {
                await acl.ClearAclAsync(Securables.Document, id, ct);
            }
            await audit.WriteAsync(new AuditEntry(AuditActions.DocPurged, Securables.Document, id.ToString(), Changes: new { library = libraryId, versions = keys.Count }), ct);
            session.OnCommitted(() =>
            {
                foreach (var key in keys)
                {
                    _ = storage.DeleteAsync(key, CancellationToken.None);
                }
            });
        }
        return due.Count;
    }

    /// <summary>Reminds the holder and the owner, once a day, of a check-out older than the limit (section 4 edge cases).</summary>
    public async Task<int> RemindCheckOutsAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var due = await session.QueryAsync(
            """
            select id, library_id, checked_out_by, owner_user_id from documents.document
            where checked_in and checked_out_by is not null and deleted_at is null and checked_out_at <= @since
              and (checkout_reminded_at is null or checkout_reminded_at <= @day)
            order by checked_out_at limit @limit for update skip locked
            """, new { since = now - CheckOutLimit, day = now.AddDays(-1), limit },
            r => (Id: r.GetGuid(0), Library: r.GetGuid(1), Holder: r.GetGuid(2), Owner: r.GetGuid(3)), ct);
        foreach (var (id, libraryId, holder, owner) in due)
        {
            await notifications.SendAsync(new NotificationRequest(new[] { holder, owner }.Distinct().ToList(), NotificationCategories.DocumentUpdate, CheckOutReminderTemplate,
                $"/documents/{id}", DedupeKey: $"documents.checkout:{id}:{now:yyyy-MM-dd}"), ct);
            await session.ExecAsync("update documents.document set checkout_reminded_at = @now where id = @id", new { id, now }, ct);
        }
        return due.Count;
    }
}
