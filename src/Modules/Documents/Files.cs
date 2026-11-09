using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
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
    bool Publish);

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
    int Version);

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
    IClock clock)
{
    public const int MaxNameLength = 200;
    public const int MaxPage = 200;
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    public const string QuarantinedTemplate = "documents.quarantined";

    private DocumentAccess Access => libraries.Access;

    private static string ObjectKey(Guid library, Guid document, Guid version) => $"documents/{library:N}/{document:N}/{version:N}";

    private static string CleanName(string? name)
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
            if (await session.ScalarAsync<bool>(
                """
                select exists (select 1 from documents.document_version v join documents.document d on d.id = v.document_id
                  where v.library_id = @lib and v.content_hash = @hash and v.scan_state <> 'infected' and d.deleted_at is null)
                """, new { lib = library.Id, hash }, ct))
            {
                throw AppException.Conflict("conflict.documents.duplicate");
            }
        }
        catch
        {
            await storage.DeleteAsync(key, ct);
            throw;
        }

        string scan;
        await using (var stored = await storage.OpenReadAsync(key, ct) ?? throw new InvalidOperationException("Stored file is missing."))
        {
            scan = await scanner.ScanAsync(stored, ct) switch
            {
                ScanVerdict.Clean => ScanStates.Clean,
                ScanVerdict.Infected => ScanStates.Infected,
                _ => ScanStates.Failed,
            };
        }

        var checkIn = complete && scan == ScanStates.Clean;
        var (major, minor, state) = !checkIn ? (0, 0, VersionStates.Pending) : request.Publish ? (1, 0, VersionStates.Effective) : (0, 1, VersionStates.Draft);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into documents.document (tenant_id, id, library_id, folder_id, type_id, name, metadata, classification, owner_user_id, checked_in,
              checked_out_by, checked_out_at, securable_type, securable_id, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @documentId, @lib, @folder, @type, @name, @metadata, @classification, @user, @checkIn,
              @holder, @heldAt, @securableType, @securable, @user, @now, @now)
            """,
            new
            {
                documentId, lib = library.Id, folder = request.FolderId, type = request.TypeId, name, metadata = new Jsonb(DocJson.Write(values)), classification, user,
                checkIn, holder = checkIn ? (Guid?)null : user, heldAt = checkIn ? (DateTimeOffset?)null : now, securableType = securable.Type, securable = securable.Id, now,
            }, ct);
        await session.ExecAsync(
            """
            insert into documents.document_version (tenant_id, id, document_id, library_id, major, minor, object_key, content_hash, size_bytes, content_type, file_name,
              author_user_id, effective_from, state, scan_state, created_at)
            values (platform.current_tenant_id(), @versionId, @documentId, @lib, @major, @minor, @key, @hash, @size, @contentType, @name, @user, @effective, @state, @scan, @now)
            """,
            new
            {
                versionId, documentId, lib = library.Id, major, minor, key, hash, size, contentType, name, user,
                effective = state == VersionStates.Effective ? now : (DateTimeOffset?)null, state, scan, now,
            }, ct);
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

    /// <summary>Streams the body to storage, hashing and counting as it goes; anything over the limit is removed and refused.</summary>
    private async Task<(string Hash, long Size)> StoreAsync(string key, Stream body, string contentType, long maxBytes, CancellationToken ct)
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
    private async Task QuotaAsync(long incoming, CancellationToken ct)
    {
        if (await entitlements.GetLimitAsync(Capabilities.DocsStorageGb, ct) is not { } gb)
        {
            return;
        }
        var used = await session.ScalarAsync<long>("select coalesce(sum(size_bytes), 0)::bigint from documents.document_version", null, ct);
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
    private async Task QuarantinedAsync(LibraryRow library, Guid documentId, Guid uploader, CancellationToken ct)
    {
        var recipients = (await provisioning.TenantHoldersAsync(Permissions.DocManage, ct)).Append(uploader).Distinct().ToList();
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SecurityNotice, QuarantinedTemplate,
            $"/documents/libraries/{library.Id}", DedupeKey: $"documents.quarantined:{documentId}"), ct);
    }

    private async Task<IReadOnlyList<FieldDefinition>> FieldsAsync(Guid libraryId, Guid? typeId, ValidationErrors errors, CancellationToken ct)
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
            "checked_out_at, pending_version_id, current_version_id, effective_version_id, inherits, securable_type, securable_id, row_version";

        public static DocRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("folder_id"), r.Get<Guid?>("type_id"), r.GetString(4),
            r.Get<string>("number"), r.GetString(6), r.GetString(7), r.GetGuid(8), r.GetBoolean(9), r.Get<Guid?>("checked_out_by"), r.Get<DateTimeOffset?>("checked_out_at"),
            r.Get<Guid?>("pending_version_id"), r.Get<Guid?>("current_version_id"), r.Get<Guid?>("effective_version_id"), r.GetBoolean(15), r.GetString(16), r.GetGuid(17), r.GetInt32(18));
    }

    /// <summary>
    /// The document and what the caller may do with it, or not found. A first upload still missing fields shows only to
    /// its uploader; contributors see the latest checked-in version; readers only a document with an effective version.
    /// </summary>
    internal async Task<(LibraryRow Library, DocRow Doc, DocRights Rights)> VisibleAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        var user = context.RequireUser();
        var doc = await session.QuerySingleOrDefaultAsync($"select {DocRow.Columns} from documents.document where id = @id and deleted_at is null" + (forUpdate ? " for update" : ""),
            new { id }, DocRow.Read, ct) ?? throw AppException.NotFound();
        var library = await Access.LibraryAsync(doc.LibraryId, ct) ?? throw AppException.NotFound();
        var rights = await Access.RightsAsync(library, new Securable(doc.SecurableType, doc.Securable, doc.Classification), ct);
        var visible = doc.CheckedIn ? rights.Contribute || (rights.View && doc.Effective is not null) : doc.CheckedOutBy == user;
        if (!visible)
        {
            throw AppException.NotFound();
        }
        return (library, doc, rights);
    }

    private async Task<VersionView?> VersionAsync(Guid? id, CancellationToken ct) =>
        id is null ? null : await session.QuerySingleOrDefaultAsync(
            "select id, major, minor, state, scan_state, size_bytes, content_type, file_name, note, author_user_id, created_at from documents.document_version where id = @id",
            new { id }, r => new VersionView(r.GetGuid(0), r.GetInt32(1), r.GetInt32(2), VersionLabels.Of(r.GetInt32(1), r.GetInt32(2)), r.GetString(3), r.GetString(4),
                r.GetInt64(5), r.GetString(6), r.GetString(7), r.Get<string>("note"), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10)), ct);

    public async Task<DocumentView> GetAsync(Guid id, CancellationToken ct)
    {
        var (library, doc, rights) = await VisibleAsync(id, ct);
        return await ViewAsync(library, doc, rights, ct);
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
            holder ? await VersionAsync(doc.Pending, ct) : null, doc.Inherits, rights, doc.Version);
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
        var user = context.RequireUser();
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
              or (d.checked_in and d.effective_version_id is not null and d.securable_id = any(@view))
              or (not d.checked_in and d.checked_out_by = @user))
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
            lib = library.Id, contribute, view, user, folder = query.FolderId, type = query.TypeId,
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
        var user = context.RequireUser();
        var (_, doc, rights) = await VisibleAsync(documentId, ct);
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
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expires = clock.UtcNow.Add(TokenLifetime);
        await session.ExecAsync(
            """
            delete from documents.download_token where expires_at < @now;
            insert into documents.download_token (tenant_id, token_hash, user_id, document_id, version_id, expires_at)
            values (platform.current_tenant_id(), @hash, @user, @documentId, @version, @expires)
            """, new { now = clock.UtcNow, hash = SHA256.HashData(Encoding.ASCII.GetBytes(token)), user, documentId, version = target, expires }, ct);
        return new DownloadTokenView(token, $"/api/v1/documents/files/{token}", expires);
    }

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
            where t.token_hash = @hash and t.user_id = @user and t.expires_at > @now and d.deleted_at is null
            """, new { hash = SHA256.HashData(Encoding.ASCII.GetBytes(token)), user, now = clock.UtcNow },
            r => (Doc: r.GetGuid(0), Version: r.GetGuid(1), Key: r.GetString(2), Type: r.GetString(3), Name: r.GetString(4), Size: r.GetInt64(5), Scan: r.GetString(6),
                Classification: r.GetString(7), Library: r.GetGuid(8)), ct);
        if (row.Key is null || row.Scan != ScanStates.Clean)
        {
            throw AppException.NotFound();
        }
        // The key is built by this module, so it is relative to the tenant's prefix; strip nothing, add nothing.
        var content = await storage.OpenReadAsync(row.Key, ct) ?? throw AppException.NotFound();
        if (row.Classification is "confidential" or "restricted")
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.DocDownloaded, Securables.Document, row.Doc.ToString(),
                Changes: new { version = row.Version, library = row.Library, size = row.Size }), ct);
        }
        await outbox.EnqueueAsync(new DocumentDownloaded(row.Doc, row.Library, user, row.Size), ct);
        return (content, row.Type, row.Name);
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
