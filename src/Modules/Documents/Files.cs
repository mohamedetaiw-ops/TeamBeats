using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>What an upload says about the file besides its content.</summary>
public sealed record UploadRequest(
    string? Name,
    Guid? FolderId,
    Guid? TypeId,
    IReadOnlyDictionary<string, JsonElement>? Metadata,
    string? Classification,
    bool Publish,
    string? SensitiveReason = null);

public sealed record VersionView(
    Guid Id,
    int Major,
    int Minor,
    string Label,
    string State,
    string ScanState,
    long Size,
    string ContentType,
    string FileName,
    string? Note,
    Guid AuthorUserId,
    DateTimeOffset CreatedAt);

public sealed record DocumentView(
    Guid Id,
    Guid LibraryId,
    Guid? FolderId,
    Guid? TypeId,
    string Name,
    string? Number,
    IReadOnlyDictionary<string, JsonElement> Metadata,
    string Classification,
    Guid OwnerUserId,
    bool CheckedIn,
    Guid? CheckedOutBy,
    DateTimeOffset? CheckedOutAt,
    bool Complete,
    VersionView? Current,
    VersionView? Effective,
    VersionView? Pending,
    bool Inherits,
    DocRights Rights,
    int Version,
    ControlledView? Controlled = null,
    bool Downloadable = true);

/// <summary>
/// The controlled document state (spec 011 US3): its number, whether a release is in approval or approved and waiting
/// for its date (with that version, so readers see "new version effective on ..."), the review date and, when the type
/// asks for it, whether the caller has confirmed reading the version in force.
/// </summary>
public sealed record ControlledView(string? Number, string Release, VersionView? Upcoming, DateTimeOffset? ReviewDueAt, bool ReadConfirmationRequired,
    DateTimeOffset? ReadConfirmedAt);

public static class ReleaseStates
{
    public const string None = "none";
    public const string InApproval = "in_approval";
    public const string Scheduled = "scheduled";
}

public sealed record MetadataRequest(IReadOnlyDictionary<string, JsonElement>? Metadata, string? Classification, bool Publish, int? Version);

public sealed record ItemSummary(
    Guid Id,
    string Name,
    Guid? FolderId,
    Guid? TypeId,
    IReadOnlyDictionary<string, JsonElement> Metadata,
    string Classification,
    string? VersionLabel,
    string? State,
    string ScanState,
    bool CheckedIn,
    Guid? CheckedOutBy,
    DateTimeOffset UpdatedAt);

public sealed record ItemPage(IReadOnlyList<ItemSummary> Items, long Total);

public sealed record DownloadTokenView(string Token, string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Files in libraries (spec 011 US1, FR-003, FR-006): uploads checked against the allowlist, the size limit and the
/// storage quota, hashed for duplicates, scanned before anyone can open them; metadata from the document type; the
/// library view with filters; downloads through short-lived tokens bound to the person and the version.
/// </summary>
public sealed class DocumentFiles(
    IDbSession session,
    ITenantContext context,
    Libraries libraries,
    IEntitlementService entitlements,
    IAuthorizationProvisioning provisioning,
    TenantObjectStorage storage,
    IMalwareScanner scanner,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IUserDirectory users,
    IPdfStamp stamp,
    TeamBeats.Modules.Security.Contracts.ISensitiveDataScanner sensitiveData,
    TeamBeats.Modules.Spaces.Contracts.ISpaceGuestReview spaceGuests,
    DocumentText documentText,
    IClock clock)
{
    public const int MaxNameLength = 200;

    /// <summary>The largest PDF a guest downloads with a watermark; larger PDFs are refused to guests (spec 026 US3).</summary>
    public const long MaxStampedBytes = 50L * 1024 * 1024;
    public const int MaxPage = 200;
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    public const string QuarantinedTemplate = "documents.quarantined";

    private DocumentAccess Access => libraries.Access;

    internal static string ObjectKey(Guid library, Guid document, Guid version) => $"documents/{library:N}/{document:N}/{version:N}";

    internal static string CleanName(string? name)
    {
        var file = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        var safe = new string(file.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim();
        if (safe.Length == 0 || safe.Length > MaxNameLength)
        {
            throw AppException.Validation("name", "validation.documents.file_name");
        }
        return safe;
    }

    // Upload

    /// <summary>
    /// Stores a new document from the request body. Complete metadata and a clean scan check it in at once, as 1.0 when
    /// published or 0.1 otherwise; anything else keeps it a checked-out draft only the uploader sees (US1 scenario 1).
    /// </summary>
    public async Task<DocumentView> UploadAsync(Guid libraryId, UploadRequest request, Stream body, long? declaredLength, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, _) = await libraries.VisibleAsync(libraryId, ct);
        library.RequireOwn();
        if (library.Status != "active")
        {
            throw AppException.Conflict("conflict.documents.library_archived");
        }
        var securable = await Access.SecurableOfFolderAsync(library, request.FolderId, ct);
        var rights = await Access.RightsAsync(library, securable, ct);
        if (!rights.Contribute)
        {
            throw rights.View ? AppException.Denied() : AppException.NotFound();
        }

        var name = CleanName(request.Name);
        var rules = await libraries.TenantRulesAsync(ct);
        var extension = FileTypes.ExtensionOf(name);
        if (!Libraries.EffectiveExtensions(library, rules).Contains(extension) || !FileTypes.ContentTypes.TryGetValue(extension, out var contentType))
        {
            throw AppException.Validation("file", "validation.documents.file_type");
        }
        var maxBytes = Libraries.EffectiveMaxMb(library, rules) * 1024L * 1024L;
        if (declaredLength > maxBytes)
        {
            throw AppException.Validation("file", "validation.documents.file_size");
        }
        await QuotaAsync(declaredLength ?? 0, ct);

        var controlled = await libraries.ControlledOfAsync(request.TypeId, ct);
        if (controlled is not null)
        {
            await entitlements.DemandAsync(Capabilities.DocsControlled, ct);
            if (request.Publish)
            {
                throw AppException.Conflict("conflict.documents.controlled_release");
            }
        }
        var errors = new ValidationErrors();
        var fields = await FieldsAsync(library.Id, request.TypeId, errors, ct);
        var (values, complete) = MetadataRules.Values(fields, request.Metadata, errors);
        var classification = request.Classification ?? library.ClassificationDefault;
        if (!ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classification", "validation.documents.classification");
        }
        errors.ThrowIfAny();

        var documentId = Ids.New();
        var versionId = Ids.New();
        var key = ObjectKey(library.Id, documentId, versionId);
        var (hash, size) = await StoreAsync(key, body, contentType, maxBytes, ct);
        try
        {
            await QuotaAsync(size, ct);
            if (await DuplicateAsync(library.Id, hash, null, ct))
            {
                throw AppException.Conflict("conflict.documents.duplicate");
            }
        }
        catch
        {
            await storage.DeleteAsync(key, ct);
            throw;
        }

        var scan = await ScanStoredAsync(key, ct);
        string? text;
        try
        {
            text = await GuardSensitiveAsync(library, key, name, classification, request.SensitiveReason, documentId, ct);
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }

        var checkIn = complete && scan == ScanStates.Clean;
        var (major, minor, state) = !checkIn ? (0, 0, VersionStates.Pending) : request.Publish ? (1, 0, VersionStates.Effective) : (0, 1, VersionStates.Draft);
        var now = clock.UtcNow;
        var number = controlled is not null ? await libraries.NextNumberAsync(controlled, ct) : null;
        await session.ExecAsync(
            """
            insert into documents.document (tenant_id, id, library_id, folder_id, type_id, name, number, metadata, classification, owner_user_id, checked_in,
              checked_out_by, checked_out_at, securable_type, securable_id, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @documentId, @lib, @folder, @type, @name, @number, @metadata, @classification, @user, @checkIn,
              @holder, @heldAt, @securableType, @securable, @user, @now, @now)
            """,
            new
            {
                documentId, lib = library.Id, folder = request.FolderId, type = request.TypeId, name, number, metadata = new Jsonb(DocJson.Write(values)), classification, user,
                checkIn, holder = checkIn ? (Guid?)null : user, heldAt = checkIn ? (DateTimeOffset?)null : now, securableType = securable.Type, securable = securable.Id, now,
            }, ct);
        await RecordsSql.InheritAsync(session, now, ct, ids: [documentId]);
        await session.ExecAsync(
            """
            insert into documents.document_version (tenant_id, id, document_id, library_id, major, minor, object_key, content_hash, size_bytes, content_type, file_name,
              author_user_id, effective_from, state, scan_state, created_at, text_extract)
            values (platform.current_tenant_id(), @versionId, @documentId, @lib, @major, @minor, @key, @hash, @size, @contentType, @name, @user, @effective, @state, @scan, @now, @text)
            """,
            new
            {
                versionId, documentId, lib = library.Id, major, minor, key, hash, size, contentType, name, user,
                effective = state == VersionStates.Effective ? now : (DateTimeOffset?)null, state, scan, now, text,
            }, ct);
        if (scan == ScanStates.Clean)
        {
            await documentText.QueueAsync(versionId, name, text, ct);
        }
        await session.ExecAsync(
            """
            update documents.document set pending_version_id = case when @checkIn then null else @versionId end,
              current_version_id = case when @checkIn then @versionId end,
              effective_version_id = case when @effective then @versionId end
            where id = @documentId
            """, new { documentId, versionId, checkIn, effective = state == VersionStates.Effective }, ct);

        await audit.WriteAsync(new AuditEntry(AuditActions.DocUploaded, Securables.Document, documentId.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = library.Id, version = versionId, label = checkIn ? VersionLabels.Of(major, minor) : null, size, hash, scan, classification }), ct);
        if (state == VersionStates.Effective)
        {
            await outbox.EnqueueAsync(new DocumentVersionPublished(documentId, library.Id, versionId, major, minor), ct);
        }
        if (scan == ScanStates.Infected)
        {
            await QuarantinedAsync(library, documentId, user, ct);
        }
        return await GetAsync(documentId, ct);
    }

    /// <summary>Scans a stored file; anything but a clean verdict leaves it unusable.</summary>
    /// <summary>
    /// Spec 026 US6: the stored file's text is checked for sensitive data before the upload is kept. A library in a
    /// space with guests is judged under the tenant's guest space mode, a library open to the whole tenant under
    /// "everyone", any other under "other". Returns the text for search, or null when the format gives none.
    /// </summary>
    internal async Task<string?> GuardSensitiveAsync(LibraryRow library, string key, string fileName, string classification, string? reason, Guid documentId,
        CancellationToken ct)
    {
        var extension = FileTypes.ExtensionOf(fileName);
        if (!TextExtraction.Supports(extension))
        {
            return null;
        }
        string? text;
        await using (var stored = await storage.OpenReadAsync(key, ct) ?? throw new InvalidOperationException("Stored file is missing."))
        {
            text = await TextExtraction.ReadAsync(stored, extension, ct);
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var target = library.InSpace && library.ScopeId is { } space && await spaceGuests.HasGuestsAsync(space, ct)
            ? TeamBeats.Modules.Security.Contracts.SensitiveTargets.GuestSpace
            : !library.InSpace && library.OrgUnitId is null ? TeamBeats.Modules.Security.Contracts.SensitiveTargets.Everyone
            : TeamBeats.Modules.Security.Contracts.SensitiveTargets.Other;
        await sensitiveData.GuardAsync(target, [new TeamBeats.Modules.Security.Contracts.SensitiveText("file", text)], classification, reason,
            Securables.Document, documentId.ToString(), ct);
        return text;
    }

    internal async Task<string> ScanStoredAsync(string key, CancellationToken ct)
    {
        await using var stored = await storage.OpenReadAsync(key, ct) ?? throw new InvalidOperationException("Stored file is missing.");
        return await scanner.ScanAsync(stored, ct) switch
        {
            ScanVerdict.Clean => ScanStates.Clean,
            ScanVerdict.Infected => ScanStates.Infected,
            _ => ScanStates.Failed,
        };
    }

    /// <summary>Whether another live document of the library already holds this content.</summary>
    internal Task<bool> DuplicateAsync(Guid libraryId, string hash, Guid? except, CancellationToken ct) =>
        session.ScalarAsync<bool>(
            """
            select exists (select 1 from documents.document_version v join documents.document d on d.id = v.document_id
              where v.library_id = @libraryId and v.content_hash = @hash and v.scan_state <> 'infected' and d.deleted_at is null
                and d.id is distinct from @except)
            """, new { libraryId, hash, except }, ct);

    /// <summary>Streams the body to storage, hashing and counting as it goes; anything over the limit is removed and refused.</summary>
    internal async Task<(string Hash, long Size)> StoreAsync(string key, Stream body, string contentType, long maxBytes, CancellationToken ct)
    {
        await using var counted = new HashingStream(body, maxBytes);
        try
        {
            await storage.PutAsync(key, counted, contentType, ct);
        }
        catch (Exception ex) when (ex is FileTooLargeException || ct.IsCancellationRequested)
        {
            await storage.DeleteAsync(key, CancellationToken.None);
            throw ex is FileTooLargeException ? AppException.Validation("file", "validation.documents.file_size") : ex;
        }
        if (counted.Length == 0)
        {
            await storage.DeleteAsync(key, ct);
            throw AppException.Validation("file", "validation.documents.file_empty");
        }
        return (counted.Hash(), counted.Length);
    }

    /// <summary>The tenant's storage limit (<c>docs.storage_gb</c>): every stored version counts, including the one arriving.</summary>
    internal async Task QuotaAsync(long incoming, CancellationToken ct)
    {
        if (await entitlements.GetLimitAsync(Capabilities.DocsStorageGb, ct) is not { } gb)
        {
            return;
        }
        // Brand assets count too (spec 011 US5): they are files the tenant keeps.
        var used = await session.ScalarAsync<long>(
            "select ((select coalesce(sum(size_bytes), 0) from documents.document_version) + (select coalesce(sum(size_bytes), 0) from documents.asset))::bigint", null, ct);
        if (used + incoming > gb * 1024L * 1024L * 1024L)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.documents.storage_full", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.DocsStorageGb, ["limit"] = gb });
        }
    }

    /// <summary>
    /// Tells the uploader and the tenant's library managers that a file was quarantined. Managers holding
    /// <c>doc.manage</c> only at a unit are not reached yet: authorization lists holders at tenant scope only.
    /// </summary>
    internal async Task QuarantinedAsync(LibraryRow library, Guid documentId, Guid uploader, CancellationToken ct)
    {
        var recipients = (await provisioning.TenantHoldersAsync(Permissions.DocManage, ct)).Append(uploader).Distinct().ToList();
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SecurityNotice, QuarantinedTemplate,
            $"/documents/libraries/{library.Id}", DedupeKey: $"documents.quarantined:{documentId}"), ct);
    }

    internal async Task<IReadOnlyList<FieldDefinition>> FieldsAsync(Guid libraryId, Guid? typeId, ValidationErrors errors, CancellationToken ct)
    {
        if (typeId is null)
        {
            return [];
        }
        var type = (await libraries.TypesAsync(libraryId, ct)).FirstOrDefault(t => t.Id == typeId && t.Status == "active");
        if (type is null)
        {
            errors.Add("typeId", "validation.documents.type_unknown");
            return [];
        }
        return type.Fields;
    }

    // Reading

    internal sealed record DocRow(
        Guid Id, Guid LibraryId, Guid? FolderId, Guid? TypeId, string Name, string? Number, string MetadataJson, string Classification, Guid Owner, bool CheckedIn,
        Guid? CheckedOutBy, DateTimeOffset? CheckedOutAt, Guid? Pending, Guid? Current, Guid? Effective, bool Inherits, string SecurableType, Guid Securable, int Version)
    {
        public const string Columns = "id, library_id, folder_id, type_id, name, number, metadata::text, classification, owner_user_id, checked_in, checked_out_by, " +
            "checked_out_at, pending_version_id, current_version_id, effective_version_id, inherits, securable_type, securable_id, row_version, " +
            "release_instance_id, release_version_id, scheduled_version_id, review_due_at, review_instance_id, disposition";

        /// <summary>The release under approval (controlled documents), if any.</summary>
        public Guid? ReleaseInstance { get; init; }

        public Guid? ReleaseVersion { get; init; }

        /// <summary>An approved version waiting for its effective date.</summary>
        public Guid? Scheduled { get; init; }

        public DateTimeOffset? ReviewDueAt { get; init; }

        public Guid? ReviewInstance { get; init; }

        /// <summary>Archived by its retention label (spec 011 US6): kept for contributors, read-only, gone for readers and search.</summary>
        public bool Archived { get; init; }

        public static DocRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("folder_id"), r.Get<Guid?>("type_id"), r.GetString(4),
            r.Get<string>("number"), r.GetString(6), r.GetString(7), r.GetGuid(8), r.GetBoolean(9), r.Get<Guid?>("checked_out_by"), r.Get<DateTimeOffset?>("checked_out_at"),
            r.Get<Guid?>("pending_version_id"), r.Get<Guid?>("current_version_id"), r.Get<Guid?>("effective_version_id"), r.GetBoolean(15), r.GetString(16), r.GetGuid(17), r.GetInt32(18))
        {
            ReleaseInstance = r.Get<Guid?>("release_instance_id"),
            ReleaseVersion = r.Get<Guid?>("release_version_id"),
            Scheduled = r.Get<Guid?>("scheduled_version_id"),
            ReviewDueAt = r.Get<DateTimeOffset?>("review_due_at"),
            ReviewInstance = r.Get<Guid?>("review_instance_id"),
            Archived = r.Get<string>("disposition") == "archived",
        };
    }

    /// <summary>
    /// The document and what the caller may do with it, or not found. A first upload still missing fields shows only to
    /// its uploader; contributors see the latest checked-in version; readers only a document with an effective version.
    /// </summary>
    internal async Task<(LibraryRow Library, DocRow Doc, DocRights Rights)> VisibleAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        var user = context.RequirePrincipal();
        var doc = await session.QuerySingleOrDefaultAsync($"select {DocRow.Columns} from documents.document where id = @id and deleted_at is null" + (forUpdate ? " for update" : ""),
            new { id }, DocRow.Read, ct) ?? throw AppException.NotFound();
        var library = await Access.LibraryAsync(doc.LibraryId, ct) ?? throw AppException.NotFound();
        var rights = await Access.RightsAsync(library, new Securable(doc.SecurableType, doc.Securable, doc.Classification), ct);
        var visible = doc.CheckedIn ? rights.Contribute || (rights.View && doc.Effective is not null && !doc.Archived) : doc.CheckedOutBy == user;
        if (!visible)
        {
            throw AppException.NotFound();
        }
        if ((await Access.ClosedAsync(library, ct)).Contains(doc.Classification, StringComparer.Ordinal))
        {
            throw AppException.Denied("authz.denied.documents_guest_classified");
        }
        return (library, doc, rights);
    }

    /// <summary>The row with no permission check, for background work and the task inbox, which check on their own.</summary>
    internal Task<DocRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {DocRow.Columns} from documents.document where id = @id and deleted_at is null" + (forUpdate ? " for update" : ""),
            new { id }, DocRow.Read, ct);

    internal Task<Guid?> ReleaseSubmitterAsync(Guid id, CancellationToken ct) =>
        session.ScalarAsync<Guid?>("select release_submitted_by from documents.document where id = @id", new { id }, ct);

    internal async Task<VersionView?> VersionAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await session.QuerySingleOrDefaultAsync(
            "select id, major, minor, state, scan_state, size_bytes, content_type, file_name, note, author_user_id, created_at from documents.document_version where id = @id",
            new { id }, r => new VersionView(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), VersionLabels.Of(r.GetInt32(1), r.GetInt32(2)), r.GetString(3), r.GetString(4),
                r.GetInt64(5), r.GetString(6), r.GetString(7), r.Get<string>("note"), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10)), ct);

    public async Task<DocumentView> GetAsync(Guid id, CancellationToken ct)
    {
        var (library, doc, rights) = await VisibleAsync(id, ct);
        await NoteGuestViewAsync(library, id, ct);
        return await ViewAsync(library, doc, rights, ct);
    }

    /// <summary>
    /// Spec 011 US8 scenario 2: every view by a guest of a document in a library outside spaces (which a guest reaches
    /// only through a folder shared with their space) is audited.
    /// </summary>
    internal async Task NoteGuestViewAsync(LibraryRow library, Guid documentId, CancellationToken ct)
    {
        if (library.InSpace || !await Access.IsGuestAsync(ct))
        {
            return;
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocGuestViewed, Securables.Document, documentId.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = library.Id }), ct);
    }

    private async Task<DocumentView> ViewAsync(LibraryRow library, DocRow doc, DocRights rights, CancellationToken ct)
    {
        var metadata = DocJson.Read<Dictionary<string, JsonElement>>(doc.MetadataJson);
        var errors = new ValidationErrors();
        var fields = await FieldsAsync(library.Id, doc.TypeId, errors, ct);
        var holder = doc.CheckedOutBy == context.ActorId;
        // Readers get the effective version only; drafts and the pending upload stay with the people who work on them.
        var current = rights.Contribute ? await VersionAsync(doc.Current, ct) : null;
        return new DocumentView(doc.Id, doc.LibraryId, doc.FolderId, doc.TypeId, doc.Name, doc.Number, metadata, doc.Classification, doc.Owner, doc.CheckedIn,
            doc.CheckedOutBy, doc.CheckedOutAt, MetadataRules.Complete(fields, metadata), current, await VersionAsync(doc.Effective, ct),
            holder ? await VersionAsync(doc.Pending, ct) : null, doc.Inherits, rights, doc.Version, await ControlledViewAsync(doc, ct),
            !ReaderOfViewOnly(library, rights));
    }

    private async Task<ControlledView?> ControlledViewAsync(DocRow doc, CancellationToken ct)
    {
        if (await libraries.ControlledOfAsync(doc.TypeId, ct) is not { } settings)
        {
            return null;
        }
        var release = doc.ReleaseInstance is not null ? ReleaseStates.InApproval : doc.Scheduled is not null ? ReleaseStates.Scheduled : ReleaseStates.None;
        DateTimeOffset? confirmed = null;
        if (settings.ReadConfirmation && doc.Effective is { } effective && context.ActorId is { } user)
        {
            confirmed = await session.QuerySingleOrDefaultAsync(
                "select confirmed_at from documents.read_confirmation where version_id = @effective and user_id = @user", new { effective, user },
                r => r.Get<DateTimeOffset?>("confirmed_at"), ct);
        }
        return new ControlledView(doc.Number, release, await VersionAsync(doc.Scheduled, ct), doc.ReviewDueAt, settings.ReadConfirmation && doc.Effective is not null, confirmed);
    }

    /// <summary>
    /// Changes a document's metadata and classification. For a first upload waiting on required fields, filling them
    /// checks it in (as 1.0 when published). Needs <c>doc.contribute</c>, or being the uploader of that waiting draft.
    /// </summary>
    public async Task<DocumentView> UpdateMetadataAsync(Guid id, MetadataRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (library, doc, rights) = await VisibleAsync(id, ct, forUpdate: true);
        var waiting = !doc.CheckedIn && doc.CheckedOutBy == user;
        if (!waiting && !rights.Contribute)
        {
            throw AppException.Denied();
        }
        if (doc.CheckedIn && doc.CheckedOutBy is { } holder && holder != user)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.documents.checked_out", null,
                new Dictionary<string, object?> { ["checkedOutBy"] = holder, ["since"] = doc.CheckedOutAt });
        }
        if (request.Version != doc.Version)
        {
            throw AppException.Conflict("conflict.documents.stale");
        }
        if (request.Publish && waiting && await libraries.ControlledOfAsync(doc.TypeId, ct) is not null)
        {
            throw AppException.Conflict("conflict.documents.controlled_release");
        }
        var errors = new ValidationErrors();
        var fields = await FieldsAsync(library.Id, doc.TypeId, errors, ct);
        var (values, complete) = MetadataRules.Values(fields, request.Metadata, errors);
        var classification = request.Classification ?? doc.Classification;
        if (!ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classification", "validation.documents.classification");
        }
        errors.ThrowIfAny();

        var before = DocJson.Read<Dictionary<string, JsonElement>>(doc.MetadataJson);
        await session.ExecAsync(
            "update documents.document set metadata = @metadata, classification = @classification, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, metadata = new Jsonb(DocJson.Write(values)), classification, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocMetadataChanged, Securables.Document, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { before = new { metadata = before, classification = doc.Classification }, after = new { metadata = values, classification } }), ct);

        if (waiting && complete && doc.Pending is { } pending
            && await session.ScalarAsync<string>("select scan_state from documents.document_version where id = @pending", new { pending }, ct) == ScanStates.Clean)
        {
            await CheckInFirstAsync(library, id, pending, request.Publish, ct);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>Checks in a waiting first upload as 0.1, or as 1.0 effective when published.</summary>
    private async Task CheckInFirstAsync(LibraryRow library, Guid documentId, Guid versionId, bool publish, CancellationToken ct)
    {
        var (major, minor, state) = publish ? (1, 0, VersionStates.Effective) : (0, 1, VersionStates.Draft);
        var now = clock.UtcNow;
        await session.ExecAsync("update documents.document_version set major = @major, minor = @minor, state = @state, effective_from = @effective where id = @versionId",
            new { versionId, major, minor, state, effective = publish ? now : (DateTimeOffset?)null }, ct);
        await session.ExecAsync(
            """
            update documents.document set checked_in = true, checked_out_by = null, checked_out_at = null, pending_version_id = null,
              current_version_id = @versionId, effective_version_id = case when @publish then @versionId end, updated_at = @now
            where id = @documentId
            """, new { documentId, versionId, publish, now }, ct);
        if (publish)
        {
            await outbox.EnqueueAsync(new DocumentVersionPublished(documentId, library.Id, versionId, major, minor), ct);
        }
    }

    // The library view (US1 scenario 5)

    public async Task<ItemPage> ItemsAsync(Guid libraryId, ItemQuery query, Guid? viewId, int limit, int offset, CancellationToken ct)
    {
        var user = context.RequirePrincipal();
        var (library, _) = await libraries.VisibleAsync(libraryId, ct);
        if (viewId is { } v)
        {
            var saved = await libraries.ViewQueryAsync(library.Id, v, ct) ?? throw AppException.NotFound();
            // The caller's own filters narrow the saved view; they never replace its folder or type.
            query = saved with
            {
                Text = query.Text ?? saved.Text,
                Filters = (saved.Filters ?? new Dictionary<string, string>()).Concat((query.Filters ?? new Dictionary<string, string>()).Where(f => saved.Filters?.ContainsKey(f.Key) != true))
                    .ToDictionary(f => f.Key, f => f.Value),
                Sort = query.Sort ?? saved.Sort,
                Descending = query.Sort is null ? saved.Descending : query.Descending,
            };
        }
        var types = await libraries.TypesAsync(library.Id, ct);
        var errors = new ValidationErrors();
        await ItemQueries.ValidateAsync(query, types, errors);
        errors.ThrowIfAny();
        var fields = ItemQueries.Fields(types);

        var rights = await Access.RightsAsync(library, await Access.SecurablesAsync(library, ct), ct);
        var view = rights.Where(r => r.Value.View).Select(r => r.Key).ToArray();
        var contribute = rights.Where(r => r.Value.Contribute).Select(r => r.Key).ToArray();

        var where = new StringBuilder(
            """
            d.library_id = @lib and d.deleted_at is null
            and ((d.checked_in and d.securable_id = any(@contribute))
              or (d.checked_in and d.effective_version_id is not null and d.disposition is distinct from 'archived' and d.securable_id = any(@view))
              or (not d.checked_in and d.checked_out_by = @user))
            and d.classification <> all(@closed)
            """);
        if (query.Recursive)
        {
            where.Append(query.FolderId is null ? string.Empty : " and d.folder_id in (select id from documents.folder where library_id = @lib and @folder = any(path))");
        }
        else
        {
            where.Append(" and d.folder_id is not distinct from @folder");
        }
        if (query.TypeId is not null)
        {
            where.Append(" and d.type_id = @type");
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            where.Append(" and d.name ilike @text escape '\\'");
        }
        var extra = new List<Npgsql.NpgsqlParameter>();
        var n = 0;
        foreach (var (key, raw) in query.Filters ?? new Dictionary<string, string>())
        {
            var bound = key.EndsWith(".from", StringComparison.Ordinal) ? ">=" : key.EndsWith(".to", StringComparison.Ordinal) ? "<=" : "=";
            var field = fields[bound == "=" ? key : key[..key.LastIndexOf('.')]];
            var k = $"k{n}";
            var p = $"v{n++}";
            extra.Add(new Npgsql.NpgsqlParameter(k, field.Key));
            switch (field.Kind)
            {
                case FieldKinds.Number when decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number):
                    where.Append($" and (d.metadata->>@{k})::numeric {bound} @{p}");
                    extra.Add(new Npgsql.NpgsqlParameter(p, number));
                    break;
                case FieldKinds.Text when bound == "=":
                    where.Append($" and d.metadata->>@{k} ilike @{p} escape '\\'");
                    extra.Add(new Npgsql.NpgsqlParameter(p, "%" + Like(raw) + "%"));
                    break;
                case FieldKinds.Date or FieldKinds.Choice or FieldKinds.Text:
                    where.Append($" and d.metadata->>@{k} {bound} @{p}");
                    extra.Add(new Npgsql.NpgsqlParameter(p, raw));
                    break;
                case FieldKinds.Boolean when bound == "=" && bool.TryParse(raw, out var flag):
                    where.Append($" and (d.metadata->>@{k})::boolean = @{p}");
                    extra.Add(new Npgsql.NpgsqlParameter(p, flag));
                    break;
                default:
                    throw AppException.Validation($"filters.{key}", "validation.documents.field_value");
            }
        }
        var direction = query.Descending ? "desc" : "asc";
        var order = query.Sort switch
        {
            null or "name" => $"lower(d.name) {direction}",
            "modified" => $"d.updated_at {direction}",
            var s when fields[s].Kind == FieldKinds.Number => $"(d.metadata->>@sortKey)::numeric {direction} nulls last",
            _ => $"d.metadata->>@sortKey {direction} nulls last",
        };
        if (query.Sort is { } sortKey && fields.ContainsKey(sortKey))
        {
            extra.Add(new Npgsql.NpgsqlParameter("sortKey", sortKey));
        }

        await using var cmd = await session.CommandAsync(
            $"""
            select d.id, d.name, d.folder_id, d.type_id, d.metadata::text, d.classification, d.checked_in, d.checked_out_by, d.updated_at,
              v.major, v.minor, v.state, coalesce(v.scan_state, p.scan_state, 'clean') as scan_state, count(*) over () as total
            from documents.document d
            left join documents.document_version v on v.id = case when d.securable_id = any(@contribute) then d.current_version_id else d.effective_version_id end
            left join documents.document_version p on p.id = d.pending_version_id
            where {where}
            order by {order}, d.id
            limit @limit offset @offset
            """, ct);
        Sql.Bind(cmd, new
        {
            lib = library.Id, contribute, view, user, folder = query.FolderId, type = query.TypeId, closed = await Access.ClosedAsync(library, ct),
            text = string.IsNullOrWhiteSpace(query.Text) ? null : "%" + Like(query.Text.Trim()) + "%",
            limit = Math.Clamp(limit, 1, MaxPage), offset = Math.Max(offset, 0),
        });
        cmd.Parameters.AddRange(extra.ToArray());
        var items = new List<ItemSummary>();
        long total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var major = r.Get<int?>("major");
                items.Add(new ItemSummary(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("folder_id"), r.Get<Guid?>("type_id"),
                    DocJson.Read<Dictionary<string, JsonElement>>(r.GetString(4)), r.GetString(5),
                    major is { } m ? VersionLabels.Of(m, r.GetInt32(10)) : null, r.Get<string>("state"), r.GetString(12), r.GetBoolean(6),
                    r.Get<Guid?>("checked_out_by"), r.GetFieldValue<DateTimeOffset>(8)));
                total = r.GetInt64(13);
            }
        }
        return new ItemPage(items, total);
    }

    private static string Like(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    // Downloads (plan "Files")

    /// <summary>
    /// Issues a download token after the permission check. Readers get the effective version; contributors any
    /// checked-in version; the uploader their waiting draft. Only clean files are ever served.
    /// </summary>
    public async Task<DownloadTokenView> DownloadTokenAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var target = await DownloadableAsync(documentId, versionId, ct);
        var (token, expires) = await IssueTokenAsync(documentId, target, TokenPurposes.Download, ct);
        return new DownloadTokenView(token, $"/api/v1/documents/files/{token}", expires);
    }

    /// <summary>
    /// The version the caller may open: the one asked for, or by default the current one for contributors and the
    /// effective one for readers. Not found when it is not theirs to see; a conflict while it is not clean.
    /// </summary>
    internal async Task<Guid> OpenableAsync(Guid documentId, Guid? versionId, CancellationToken ct) =>
        (await OpenCheckAsync(documentId, versionId, ct)).Target;

    /// <summary>
    /// The version the caller may take away as a file. In a view-only library (spec 011 FR-016) only contributors and
    /// managers download; readers preview with a watermark, and the API refuses them a file.
    /// </summary>
    internal async Task<Guid> DownloadableAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var (target, viewOnly) = await OpenCheckAsync(documentId, versionId, ct);
        return viewOnly ? throw AppException.Denied("authz.denied.documents_view_only") : target;
    }

    /// <summary>The version to preview and, for a reader of a view-only library, the watermark its preview carries.</summary>
    internal async Task<(Guid Target, string? Watermark)> PreviewableAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var (target, viewOnly) = await OpenCheckAsync(documentId, versionId, ct);
        return (target, viewOnly ? await WatermarkAsync(ct) : null);
    }

    /// <summary>The viewer's name, email and the time, as a view-only preview shows them (spec 011 US8 scenario 1).</summary>
    internal async Task<string> WatermarkAsync(CancellationToken ct)
    {
        var principal = context.RequirePrincipal();
        var who = await users.GetAsync(principal, ct);
        string?[] parts = [who?.DisplayName ?? principal.ToString(), who?.Email, $"{clock.UtcNow:yyyy-MM-dd HH:mm} UTC"];
        return string.Join("  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static bool ReaderOfViewOnly(LibraryRow library, DocRights rights) => library.ViewOnly && !library.InSpace && !rights.Contribute;

    private async Task<(Guid Target, bool ViewOnly)> OpenCheckAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var user = context.RequirePrincipal();
        var (library, doc, rights) = await VisibleAsync(documentId, ct);
        var target = versionId ?? (rights.Contribute ? doc.Current : doc.Effective) ?? doc.Pending;
        var allowed = target is { } t && (t == doc.Effective || (rights.Contribute && doc.CheckedIn) || (t == doc.Pending && doc.CheckedOutBy == user));
        var version = allowed
            ? await session.QuerySingleOrDefaultAsync("select state, scan_state from documents.document_version where id = @t and document_id = @documentId",
                new { t = target, documentId }, r => (State: r.GetString(0), Scan: r.GetString(1)), ct)
            : default;
        if (version.State is null || (version.State == VersionStates.Pending && target != doc.Pending))
        {
            throw AppException.NotFound();
        }
        if (version.Scan != ScanStates.Clean)
        {
            throw AppException.Conflict("conflict.documents.not_clean");
        }
        return (target!.Value, ReaderOfViewOnly(library, rights));
    }

    /// <summary>A random token for one person, one version and one purpose; only its hash is kept.</summary>
    internal async Task<(string Token, DateTimeOffset ExpiresAt)> IssueTokenAsync(Guid documentId, Guid versionId, string purpose, CancellationToken ct,
        string? watermark = null)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.Add(TokenLifetime);
        await session.ExecAsync(
            """
            delete from documents.download_token where expires_at < @now;
            insert into documents.download_token (tenant_id, token_hash, user_id, document_id, version_id, expires_at, purpose, watermark)
            values (platform.current_tenant_id(), @hash, @user, @documentId, @versionId, @expires, @purpose, @watermark)
            """, new { now = clock.UtcNow, hash = TokenHash(token), user = context.RequireUser(), documentId, versionId, expires, purpose, watermark }, ct);
        return (token, expires);
    }

    internal static byte[] TokenHash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>
    /// Opens the file behind a token. The token works only for the person it was issued to, in the tenant it was issued
    /// in, until it expires; anything else is not found.
    /// </summary>
    public async Task<(Stream Content, string ContentType, string FileName)> OpenAsync(string token, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (string.IsNullOrEmpty(token) || token.Length > 100)
        {
            throw AppException.NotFound();
        }
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select t.document_id, t.version_id, v.object_key, v.content_type, v.file_name, v.size_bytes, v.scan_state, d.classification, d.library_id
            from documents.download_token t
            join documents.document_version v on v.id = t.version_id
            join documents.document d on d.id = t.document_id
            where t.token_hash = @hash and t.user_id = @user and t.expires_at > @now and t.purpose = 'download' and d.deleted_at is null
            """, new { hash = TokenHash(token), user, now = clock.UtcNow },
            r => (Doc: r.GetGuid(0), Version: r.GetGuid(1), Key: r.GetString(2), Type: r.GetString(3), Name: r.GetString(4), Size: r.GetInt64(5), Scan: r.GetString(6),
                Classification: r.GetString(7), Library: r.GetGuid(8)), ct);
        if (row.Key is null || row.Scan != ScanStates.Clean)
        {
            throw AppException.NotFound();
        }
        return await ServeAsync(row.Doc, row.Version, row.Key, row.Type, row.Name, row.Size, row.Classification, row.Library, user, ct);
    }

    /// <summary>
    /// The file of a version the caller may open, for the public API (spec 035 <c>documents:read</c>): the same checks as
    /// a download link, made on the call itself, so no link outlives the token that asked for it.
    /// </summary>
    public async Task<(Stream Content, string ContentType, string FileName)> ReadFileAsync(Guid documentId, Guid? versionId, CancellationToken ct)
    {
        var principal = context.RequirePrincipal();
        var target = await DownloadableAsync(documentId, versionId, ct);
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select v.object_key, v.content_type, v.file_name, v.size_bytes, d.classification, d.library_id
            from documents.document_version v join documents.document d on d.id = v.document_id
            where v.id = @target and v.document_id = @documentId and d.deleted_at is null
            """, new { target, documentId },
            r => (Key: r.GetString(0), Type: r.GetString(1), Name: r.GetString(2), Size: r.GetInt64(3), Classification: r.GetString(4), Library: r.GetGuid(5)), ct);
        if (row.Key is null)
        {
            throw AppException.NotFound();
        }
        return await ServeAsync(documentId, target, row.Key, row.Type, row.Name, row.Size, row.Classification, row.Library, principal, ct);
    }

    private async Task<(Stream Content, string ContentType, string FileName)> ServeAsync(Guid doc, Guid version, string key, string type, string name, long size,
        string classification, Guid library, Guid principal, CancellationToken ct)
    {
        // The key is built by this module, so it is relative to the tenant's prefix; strip nothing, add nothing.
        var content = await storage.OpenReadAsync(key, ct) ?? throw AppException.NotFound();
        var guest = await Access.IsGuestAsync(ct);
        if (guest && string.Equals(type, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            content = await StampAsync(content, size, principal, ct);
        }
        if (guest)
        {
            await NoteGuestOpenAsync(doc, library, classification, ct);
        }
        if (guest || classification is "confidential" or "restricted")
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.DocDownloaded, Securables.Document, doc.ToString(),
                Changes: new { version, library, size }), ct);
        }
        // Download volume counts the app itself when an app reads as itself (spec 026 US5 with spec 035).
        await outbox.EnqueueAsync(new DocumentDownloaded(doc, library, principal, size), ct);
        return (content, type, name);
    }

    /// <summary>Tells the Security module when a guest opens a Confidential item (spec 026 US5 scenario 3).</summary>
    internal async Task NoteGuestOpenAsync(Guid doc, Guid library, string classification, CancellationToken ct)
    {
        if (classification != "confidential" || !await Access.IsGuestAsync(ct))
        {
            return;
        }
        var row = await Access.LibraryAsync(library, ct);
        await outbox.EnqueueAsync(new GuestOpenedClassified(doc, library, row is { InSpace: true } ? row.ScopeId : null, context.RequireUser(), classification), ct);
    }

    /// <summary>
    /// Spec 026 US3 scenario 3: a guest's PDF carries the guest's address and the time on every page. A PDF that cannot
    /// carry the stamp, or is too large to stamp, is not handed out to a guest.
    /// </summary>
    private async Task<Stream> StampAsync(Stream content, long size, Guid principal, CancellationToken ct)
    {
        var who = (await users.GetAsync(principal, ct))?.Email ?? principal.ToString();
        return await StampTextAsync(content, size, $"{who}  {clock.UtcNow:yyyy-MM-dd HH:mm} UTC", ct);
    }

    /// <summary>Draws the text on every page of a PDF; a PDF too large or unable to carry it is not handed out.</summary>
    internal async Task<Stream> StampTextAsync(Stream content, long size, string text, CancellationToken ct)
    {
        await using (content)
        {
            if (size > MaxStampedBytes)
            {
                throw AppException.Conflict("conflict.documents.guest_pdf_too_large");
            }
            using var buffer = new MemoryStream((int)Math.Max(size, 0));
            await content.CopyToAsync(buffer, ct);
            var marked = stamp.Apply(buffer.ToArray(), text);
            return marked is null ? throw AppException.Conflict("conflict.documents.guest_pdf_unmarkable") : new MemoryStream(marked, writable: false);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal sealed class FileTooLargeException : Exception;

/// <summary>Passes a stream through while hashing it with SHA-256 and stopping once it grows past a limit.</summary>
internal sealed class HashingStream(Stream inner, long limit) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _length;

    public override long Length => _length;

    public string Hash() => Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Account(buffer.Span[..read]);
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await ReadAsync(buffer.AsMemory(offset, count), cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Account(buffer.AsSpan(offset, read));
        return read;
    }

    private void Account(ReadOnlySpan<byte> data)
    {
        _length += data.Length;
        if (_length > limit)
        {
            throw new FileTooLargeException();
        }
        _hash.AppendData(data);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Position { get => _length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }
        base.Dispose(disposing);
    }
}
