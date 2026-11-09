using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record DocumentSettingsView(IReadOnlyList<string> AllowedExtensions, int MaxFileMb, IReadOnlyList<string> KnownExtensions);

public sealed record DocumentSettingsRequest(IReadOnlyList<string>? AllowedExtensions, int? MaxFileMb);

public sealed record LibraryRequest(
    IReadOnlyDictionary<string, string>? Names,
    string? Scope,
    Guid? ScopeId,
    IReadOnlyList<string>? AllowedExtensions,
    int? MaxFileMb,
    string? ClassificationDefault,
    int? Version);

public sealed record DocumentTypeView(Guid Id, IReadOnlyDictionary<string, string> Names, IReadOnlyList<FieldDefinition> Fields, string Status, int Version);

public sealed record DocumentTypeRequest(IReadOnlyDictionary<string, string>? Names, IReadOnlyList<FieldDefinition>? Fields, string? Status, int? Version);

public sealed record LibraryView(
    Guid Id,
    IReadOnlyDictionary<string, string> Names,
    string Scope,
    Guid? ScopeId,
    Guid? OrgUnitId,
    IReadOnlyList<string>? AllowedExtensions,
    IReadOnlyList<string> EffectiveExtensions,
    int? MaxFileMb,
    int EffectiveMaxFileMb,
    string ClassificationDefault,
    string Status,
    int Version,
    IReadOnlyList<DocumentTypeView> Types,
    DocRights Rights);

public sealed record LibrarySummary(Guid Id, IReadOnlyDictionary<string, string> Names, string Scope, Guid? ScopeId, Guid? OrgUnitId, DocRights Rights);

public sealed record FolderRequest(Guid? ParentId, string? Name);

public sealed record FolderView(Guid Id, Guid? ParentId, string Name, bool Inherits, DocRights Rights);

/// <summary>A library query people can save and share (US1 scenario 5).</summary>
public sealed record ItemQuery(
    Guid? FolderId = null,
    bool Recursive = false,
    Guid? TypeId = null,
    string? Text = null,
    IReadOnlyDictionary<string, string>? Filters = null,
    string? Sort = null,
    bool Descending = false);

public sealed record SavedViewRequest(IReadOnlyDictionary<string, string>? Names, ItemQuery? Query);

public sealed record SavedViewView(Guid Id, IReadOnlyDictionary<string, string> Names, ItemQuery Query);

public sealed record BreakRequest(bool Confirm = false);

/// <summary>
/// Libraries, document types, folders, saved views and permission breaks (spec 011 US1, FR-001 to FR-003). Library
/// managers (<c>doc.manage</c> at the scope) create and configure libraries; tenant upload rules need <c>doc.manage</c>
/// at tenant scope. Nothing here works without <c>docs.core</c>.
/// </summary>
public sealed class Libraries(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning acl,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ISiteDirectory sites,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxNameLength = 120;
    public const int MaxViews = 50;

    /// <summary>A permission change reaching more documents than this asks for confirmation first (section 14).</summary>
    public const int ConfirmAbove = 500;

    internal DocumentAccess Access { get; } = new(session, authz);

    // Settings

    internal async Task<DocumentSettingsView> TenantRulesAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select allowed_extensions, max_file_mb from documents.settings", null,
            r => new DocumentSettingsView(r.GetFieldValue<string[]>(0), r.GetInt32(1), FileTypes.Defaults), ct)
        ?? new DocumentSettingsView(FileTypes.Defaults, FileTypes.DefaultMaxFileMb, FileTypes.Defaults);

    public async Task<DocumentSettingsView> SettingsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        await authz.DemandAsync(Permissions.DocManage, AuthzTarget.Tenant, ct);
        return await TenantRulesAsync(ct);
    }

    public async Task<DocumentSettingsView> UpdateSettingsAsync(DocumentSettingsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        await authz.DemandAsync(Permissions.DocManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        var extensions = Extensions(request.AllowedExtensions, FileTypes.Defaults, "allowedExtensions", errors) ?? FileTypes.Defaults;
        var max = request.MaxFileMb ?? FileTypes.DefaultMaxFileMb;
        if (max is < 1 or > FileTypes.DefaultMaxFileMb)
        {
            errors.Add("maxFileMb", "validation.documents.max_file_mb");
        }
        errors.ThrowIfAny();
        var before = await TenantRulesAsync(ct);
        await session.ExecAsync(
            """
            insert into documents.settings (tenant_id, allowed_extensions, max_file_mb, updated_at, updated_by)
            values (platform.current_tenant_id(), @extensions, @max, @now, @by)
            on conflict (tenant_id) do update set allowed_extensions = excluded.allowed_extensions, max_file_mb = excluded.max_file_mb,
              updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { extensions = extensions.ToArray(), max, now = clock.UtcNow, by = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocSettingsChanged, "document_settings", null,
            Changes: new { before = new { before.AllowedExtensions, before.MaxFileMb }, after = new { allowedExtensions = extensions, maxFileMb = max } }), ct);
        return new DocumentSettingsView(extensions, max, FileTypes.Defaults);
    }

    /// <summary>Normalized extensions, each one allowed by the outer list; null when none were given.</summary>
    private static IReadOnlyList<string>? Extensions(IReadOnlyList<string>? given, IReadOnlyList<string> outer, string field, ValidationErrors errors)
    {
        if (given is null)
        {
            return null;
        }
        var list = given.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (list.Count == 0 || list.Any(e => !outer.Contains(e)))
        {
            errors.Add(field, "validation.documents.extensions");
        }
        return list;
    }

    // Libraries

    private static void Names(IReadOnlyDictionary<string, string>? names, string field, ValidationErrors errors)
    {
        if (names is null || names.Count == 0 || names.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > MaxNameLength))
        {
            errors.Add(field, "validation.documents.names");
        }
    }

    private static Dictionary<string, string> Trimmed(IReadOnlyDictionary<string, string> names) => names.ToDictionary(n => n.Key, n => n.Value.Trim());

    public async Task<IReadOnlyList<LibrarySummary>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        var libraries = await session.QueryAsync($"select {LibraryRow.Columns} from documents.library where status = 'active' order by created_at, id", null, LibraryRow.Read, ct);
        var list = new List<LibrarySummary>();
        foreach (var library in libraries)
        {
            // A person granted only one folder of a library still finds the library.
            var rights = await Access.RightsAsync(library, await Access.SecurablesAsync(library, ct), ct);
            var own = rights[library.Id];
            var summary = new DocRights(rights.Values.Any(r => r.View), own.Contribute, own.Manage);
            if (summary.View)
            {
                list.Add(new LibrarySummary(library.Id, library.Names, library.Scope, library.ScopeId, library.OrgUnitId, summary));
            }
        }
        return list;
    }

    /// <summary>The library if the caller may read anything in it; otherwise not found, so its existence is not revealed.</summary>
    internal async Task<(LibraryRow Library, DocRights Rights)> VisibleAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        var library = await Access.LibraryAsync(id, ct) ?? throw AppException.NotFound();
        var all = await Access.RightsAsync(library, await Access.SecurablesAsync(library, ct), ct);
        if (!all.Values.Any(r => r.View))
        {
            throw AppException.NotFound();
        }
        return (library, all[library.Id]);
    }

    internal async Task<LibraryRow> ManagedAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        var (library, rights) = await VisibleAsync(id, ct);
        if (!rights.Manage)
        {
            throw AppException.Denied();
        }
        return forUpdate ? (await Access.LibraryAsync(id, ct, forUpdate: true))! : library;
    }

    public async Task<LibraryView> GetAsync(Guid id, CancellationToken ct)
    {
        var (library, rights) = await VisibleAsync(id, ct);
        return await ViewAsync(library, rights, ct);
    }

    private async Task<LibraryView> ViewAsync(LibraryRow library, DocRights rights, CancellationToken ct)
    {
        var rules = await TenantRulesAsync(ct);
        var types = await TypesAsync(library.Id, ct);
        return new LibraryView(library.Id, library.Names, library.Scope, library.ScopeId, library.OrgUnitId, library.AllowedExtensions,
            EffectiveExtensions(library, rules), library.MaxFileMb, EffectiveMaxMb(library, rules), library.ClassificationDefault, library.Status,
            library.Version, types, rights);
    }

    internal static IReadOnlyList<string> EffectiveExtensions(LibraryRow library, DocumentSettingsView rules) =>
        library.AllowedExtensions is { } own ? own.Where(rules.AllowedExtensions.Contains).ToList() : rules.AllowedExtensions;

    internal static int EffectiveMaxMb(LibraryRow library, DocumentSettingsView rules) => Math.Min(library.MaxFileMb ?? rules.MaxFileMb, rules.MaxFileMb);

    /// <summary>Resolves the org unit a scope gives the library, or records why the scope is not valid.</summary>
    private async Task<(bool Ok, Guid? Unit)> ScopeUnitAsync(string? scope, Guid? scopeId, ValidationErrors errors, CancellationToken ct)
    {
        switch (scope)
        {
            case LibraryScopes.Tenant when scopeId is null:
                return (true, null);
            case LibraryScopes.OrgUnit when scopeId is { } unit && await hierarchy.UnitExistsAsync(unit, ct):
                return (true, unit);
            case LibraryScopes.Site when scopeId is { } site && await sites.FindAsync(site, ct) is { } found:
                return (true, found.OrgUnitId);
            default:
                errors.Add("scope", "validation.documents.scope");
                return (false, null);
        }
    }

    public async Task<LibraryView> CreateAsync(LibraryRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        context.RequireUser();
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        var (ok, unit) = await ScopeUnitAsync(request.Scope, request.ScopeId, errors, ct);
        if (!ok)
        {
            errors.ThrowIfAny();
        }
        // Creating needs doc.manage where the library will live; a manager of one unit cannot create for another.
        await authz.DemandAsync(Permissions.DocManage, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);
        var rules = await TenantRulesAsync(ct);
        var extensions = Extensions(request.AllowedExtensions, rules.AllowedExtensions, "allowedExtensions", errors);
        if (request.MaxFileMb is { } max && (max < 1 || max > rules.MaxFileMb))
        {
            errors.Add("maxFileMb", "validation.documents.max_file_mb");
        }
        var classification = request.ClassificationDefault ?? "internal";
        if (!ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classificationDefault", "validation.documents.classification");
        }
        errors.ThrowIfAny();

        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into documents.library (tenant_id, id, names, scope, scope_id, org_unit_id, allowed_extensions, max_file_mb, classification_default, created_by)
            values (platform.current_tenant_id(), @id, @names, @scope, @scopeId, @unit, @extensions, @max, @classification, @by)
            """,
            new
            {
                id, names = new Jsonb(DocJson.Write(Trimmed(request.Names!))), scope = request.Scope, scopeId = request.ScopeId, unit,
                extensions = extensions?.ToArray(), max = request.MaxFileMb, classification, by = context.ActorId,
            }, ct);
        if (unit is { } owning)
        {
            // Unit and site libraries are read by the unit's members unless a manager changes the ACL (section 7).
            await acl.AddAclEntryAsync(Securables.Library, id, "org_unit", owning, Permissions.DocView, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DocLibraryCreated, Securables.Library, id.ToString(), TargetOrgUnitId: unit,
            Changes: new { names = request.Names, scope = request.Scope, scopeId = request.ScopeId, allowedExtensions = extensions, maxFileMb = request.MaxFileMb, classification }), ct);
        var library = (await Access.LibraryAsync(id, ct))!;
        return await ViewAsync(library, await Access.RightsAsync(library, DocumentAccess.LibrarySecurable(library), ct), ct);
    }

    public async Task<LibraryView> UpdateAsync(Guid id, LibraryRequest request, CancellationToken ct)
    {
        var library = await ManagedAsync(id, ct, forUpdate: true);
        if (request.Version != library.Version)
        {
            throw AppException.Conflict("conflict.documents.stale");
        }
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        if (request.Scope is { } scope && (scope != library.Scope || request.ScopeId != library.ScopeId))
        {
            // Moving a library to another scope changes who can read it; R1 keeps the scope fixed.
            errors.Add("scope", "validation.documents.scope_fixed");
        }
        var rules = await TenantRulesAsync(ct);
        var extensions = Extensions(request.AllowedExtensions, rules.AllowedExtensions, "allowedExtensions", errors);
        if (request.MaxFileMb is { } max && (max < 1 || max > rules.MaxFileMb))
        {
            errors.Add("maxFileMb", "validation.documents.max_file_mb");
        }
        var classification = request.ClassificationDefault ?? library.ClassificationDefault;
        if (!ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classificationDefault", "validation.documents.classification");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update documents.library set names = @names, allowed_extensions = @extensions, max_file_mb = @max, classification_default = @classification,
              updated_at = @now, row_version = row_version + 1
            where id = @id
            """,
            new { id, names = new Jsonb(DocJson.Write(Trimmed(request.Names!))), extensions = extensions?.ToArray(), max = request.MaxFileMb, classification, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocLibraryChanged, Securables.Library, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new
            {
                before = new { library.Names, library.AllowedExtensions, library.MaxFileMb, library.ClassificationDefault },
                after = new { names = request.Names, allowedExtensions = extensions, maxFileMb = request.MaxFileMb, classificationDefault = classification },
            }), ct);
        return await GetAsync(id, ct);
    }

    // Document types

    internal async Task<IReadOnlyList<DocumentTypeView>> TypesAsync(Guid libraryId, CancellationToken ct) =>
        await session.QueryAsync("select id, names::text, fields::text, status, row_version from documents.document_type where library_id = @libraryId order by created_at, id",
            new { libraryId },
            r => new DocumentTypeView(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), DocJson.Read<List<FieldDefinition>>(r.GetString(2)), r.GetString(3), r.GetInt32(4)), ct);

    public async Task<DocumentTypeView> CreateTypeAsync(Guid libraryId, DocumentTypeRequest request, CancellationToken ct)
    {
        var library = await ManagedAsync(libraryId, ct);
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        var fields = MetadataRules.Definitions(request.Fields, errors);
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync(
            "insert into documents.document_type (tenant_id, id, library_id, names, fields) values (platform.current_tenant_id(), @id, @libraryId, @names, @fields)",
            new { id, libraryId, names = new Jsonb(DocJson.Write(Trimmed(request.Names!))), fields = new Jsonb(DocJson.Write(fields)) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocTypeChanged, "document_type", id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = libraryId, names = request.Names, fields }), ct);
        return (await TypesAsync(libraryId, ct)).Single(t => t.Id == id);
    }

    public async Task<DocumentTypeView> UpdateTypeAsync(Guid typeId, DocumentTypeRequest request, CancellationToken ct)
    {
        var libraryId = await session.ScalarAsync<Guid?>("select library_id from documents.document_type where id = @typeId", new { typeId }, ct) ?? throw AppException.NotFound();
        var library = await ManagedAsync(libraryId, ct);
        var current = (await TypesAsync(libraryId, ct)).Single(t => t.Id == typeId);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.documents.stale");
        }
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        var fields = MetadataRules.Definitions(request.Fields, errors);
        var status = request.Status ?? current.Status;
        if (status is not ("active" or "retired"))
        {
            errors.Add("status", "validation.documents.type_status");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            "update documents.document_type set names = @names, fields = @fields, status = @status, updated_at = @now, row_version = row_version + 1 where id = @typeId",
            new { typeId, names = new Jsonb(DocJson.Write(Trimmed(request.Names!))), fields = new Jsonb(DocJson.Write(fields)), status, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocTypeChanged, "document_type", typeId.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { before = new { current.Names, current.Fields, current.Status }, after = new { names = request.Names, fields, status } }), ct);
        return (await TypesAsync(libraryId, ct)).Single(t => t.Id == typeId);
    }

    // Folders

    public async Task<IReadOnlyList<FolderView>> FoldersAsync(Guid libraryId, CancellationToken ct)
    {
        var (library, _) = await VisibleAsync(libraryId, ct);
        var rights = await Access.RightsAsync(library, await Access.SecurablesAsync(library, ct), ct);
        var rows = await session.QueryAsync("select id, parent_id, name, inherits, securable_id, path from documents.folder where library_id = @libraryId order by array_length(path, 1), lower(name)",
            new { libraryId }, r => (Id: r.GetGuid(0), Parent: r.Get<Guid?>("parent_id"), Name: r.GetString(2), Inherits: r.GetBoolean(3), Securable: r.GetGuid(4)), ct);
        // A folder shows when the caller can read it; a hidden folder's children with their own grants still show.
        return rows.Where(f => rights.TryGetValue(f.Securable, out var r) && r.View)
            .Select(f => new FolderView(f.Id, f.Parent, f.Name, f.Inherits, rights[f.Securable])).ToList();
    }

    public async Task<FolderView> CreateFolderAsync(Guid libraryId, FolderRequest request, CancellationToken ct)
    {
        var (library, _) = await VisibleAsync(libraryId, ct);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200 || name.IndexOfAny(['/', '\\']) >= 0 || name.Any(char.IsControl))
        {
            throw AppException.Validation("name", "validation.documents.folder_name");
        }
        Guid[] parentPath = [];
        if (request.ParentId is { } parentId)
        {
            parentPath = await session.ScalarAsync<Guid[]>("select path from documents.folder where id = @parentId and library_id = @libraryId",
                new { parentId, libraryId }, ct) ?? throw AppException.Validation("parentId", "validation.documents.folder_parent");
        }
        var securable = await Access.SecurableOfFolderAsync(library, request.ParentId, ct);
        var rights = await Access.RightsAsync(library, securable, ct);
        if (!rights.Contribute)
        {
            throw rights.View ? AppException.Denied() : AppException.NotFound();
        }
        var id = Ids.New();
        try
        {
            await session.ExecAsync(
                """
                insert into documents.folder (tenant_id, id, library_id, parent_id, name, path, securable_type, securable_id, created_by)
                values (platform.current_tenant_id(), @id, @libraryId, @parent, @name, @path, @type, @securable, @by)
                """, new { id, libraryId, parent = request.ParentId, name, path = parentPath.Append(id).ToArray(), type = securable.Type, securable = securable.Id, by = context.ActorId }, ct);
        }
        catch (Npgsql.PostgresException ex) when (ex.IsUniqueViolation())
        {
            throw AppException.Conflict("conflict.documents.folder_exists");
        }
        return new FolderView(id, request.ParentId, name, true, rights);
    }

    // Saved views

    public async Task<IReadOnlyList<SavedViewView>> ViewsAsync(Guid libraryId, CancellationToken ct)
    {
        await VisibleAsync(libraryId, ct);
        return await session.QueryAsync("select id, names::text, query::text from documents.saved_view where library_id = @libraryId order by created_at, id", new { libraryId },
            r => new SavedViewView(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), DocJson.Read<ItemQuery>(r.GetString(2))), ct);
    }

    internal async Task<ItemQuery?> ViewQueryAsync(Guid libraryId, Guid viewId, CancellationToken ct) =>
        await session.ScalarAsync<string>("select query::text from documents.saved_view where id = @viewId and library_id = @libraryId", new { viewId, libraryId }, ct) is { } json
            ? DocJson.Read<ItemQuery>(json) : null;

    public async Task<SavedViewView> CreateViewAsync(Guid libraryId, SavedViewRequest request, CancellationToken ct)
    {
        var library = await ManagedAsync(libraryId, ct);
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        var query = request.Query ?? new ItemQuery();
        await ItemQueries.ValidateAsync(query, await TypesAsync(library.Id, ct), errors);
        errors.ThrowIfAny();
        if (await session.ScalarAsync<long>("select count(*) from documents.saved_view where library_id = @libraryId", new { libraryId }, ct) >= MaxViews)
        {
            throw AppException.Validation("names", "validation.documents.too_many_views");
        }
        var id = Ids.New();
        await session.ExecAsync(
            "insert into documents.saved_view (tenant_id, id, library_id, names, query, created_by) values (platform.current_tenant_id(), @id, @libraryId, @names, @query, @by)",
            new { id, libraryId, names = new Jsonb(DocJson.Write(Trimmed(request.Names!))), query = new Jsonb(DocJson.Write(query)), by = context.RequireUser() }, ct);
        return new SavedViewView(id, Trimmed(request.Names!), query);
    }

    public async Task DeleteViewAsync(Guid viewId, CancellationToken ct)
    {
        var libraryId = await session.ScalarAsync<Guid?>("select library_id from documents.saved_view where id = @viewId", new { viewId }, ct) ?? throw AppException.NotFound();
        await ManagedAsync(libraryId, ct);
        await session.ExecAsync("delete from documents.saved_view where id = @viewId", new { viewId }, ct);
    }

    // Permission breaks (FR-001)

    /// <summary>
    /// Gives a folder its own permissions, starting from a copy of what it inherited. Folders and documents below that
    /// inherit follow it. Needs <c>doc.manage</c> on what the folder uses now.
    /// </summary>
    public async Task<FolderView> BreakFolderAsync(Guid folderId, BreakRequest request, CancellationToken ct)
    {
        var (library, folder) = await FolderForManagerAsync(folderId, ct);
        if (!folder.Inherits)
        {
            throw AppException.Conflict("conflict.documents.already_broken");
        }
        await ConfirmAsync(library.Id, folder.Securable, folder.Path, request, ct);
        await acl.CopyAclAsync(folder.SecurableType, folder.Securable, Securables.Folder, folderId, ct);
        await MoveSecurableAsync(folderId, folder.Securable, Securables.Folder, folderId, ct);
        await session.ExecAsync("update documents.folder set inherits = false where id = @folderId", new { folderId }, ct);
        await PermissionAuditAsync(AuditActions.DocPermissionsBroken, library, Securables.Folder, folderId, folder.Securable, ct);
        return (await FoldersAsync(library.Id, ct)).Single(f => f.Id == folderId);
    }

    /// <summary>Ends a folder's own permissions: it and what follows it use the parent's again, and its ACL is removed.</summary>
    public async Task<FolderView> InheritFolderAsync(Guid folderId, BreakRequest request, CancellationToken ct)
    {
        var (library, folder) = await FolderForManagerAsync(folderId, ct);
        if (folder.Inherits)
        {
            throw AppException.Conflict("conflict.documents.not_broken");
        }
        var parent = await Access.SecurableOfFolderAsync(library, folder.Parent, ct);
        await ConfirmAsync(library.Id, folderId, folder.Path, request, ct);
        await MoveSecurableAsync(folderId, folderId, parent.Type, parent.Id, ct);
        await session.ExecAsync("update documents.folder set inherits = true where id = @folderId", new { folderId }, ct);
        await acl.ClearAclAsync(Securables.Folder, folderId, ct);
        await PermissionAuditAsync(AuditActions.DocPermissionsChanged, library, Securables.Folder, folderId, parent.Id, ct);
        return (await FoldersAsync(library.Id, ct)).Single(f => f.Id == folderId);
    }

    /// <summary>Gives one document its own permissions, copied from what it inherited.</summary>
    public async Task<DocRights> BreakDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var (library, doc) = await DocumentForManagerAsync(documentId, ct);
        if (!doc.Inherits)
        {
            throw AppException.Conflict("conflict.documents.already_broken");
        }
        await acl.CopyAclAsync(doc.SecurableType, doc.Securable, Securables.Document, documentId, ct);
        await session.ExecAsync("update documents.document set inherits = false, securable_type = 'doc_document', securable_id = @documentId where id = @documentId", new { documentId }, ct);
        await PermissionAuditAsync(AuditActions.DocPermissionsBroken, library, Securables.Document, documentId, doc.Securable, ct);
        return await Access.RightsAsync(library, new Securable(Securables.Document, documentId, doc.Classification), ct);
    }

    public async Task<DocRights> InheritDocumentAsync(Guid documentId, CancellationToken ct)
    {
        var (library, doc) = await DocumentForManagerAsync(documentId, ct);
        if (doc.Inherits)
        {
            throw AppException.Conflict("conflict.documents.not_broken");
        }
        var parent = await Access.SecurableOfFolderAsync(library, doc.Folder, ct);
        await session.ExecAsync("update documents.document set inherits = true, securable_type = @type, securable_id = @id where id = @documentId",
            new { documentId, type = parent.Type, id = parent.Id }, ct);
        await acl.ClearAclAsync(Securables.Document, documentId, ct);
        await PermissionAuditAsync(AuditActions.DocPermissionsChanged, library, Securables.Document, documentId, parent.Id, ct);
        return await Access.RightsAsync(library, parent, ct);
    }

    private sealed record FolderRow(Guid Id, Guid? Parent, Guid[] Path, bool Inherits, string SecurableType, Guid Securable);

    private async Task<(LibraryRow Library, FolderRow Folder)> FolderForManagerAsync(Guid folderId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        var row = await session.QuerySingleOrDefaultAsync("select library_id, id, parent_id, path, inherits, securable_type, securable_id from documents.folder where id = @folderId for update",
            new { folderId }, r => (Library: r.GetGuid(0), Folder: new FolderRow(r.GetGuid(1), r.Get<Guid?>("parent_id"), r.GetFieldValue<Guid[]>(3), r.GetBoolean(4), r.GetString(5), r.GetGuid(6))), ct);
        if (row.Folder is null || await Access.LibraryAsync(row.Library, ct) is not { } library)
        {
            throw AppException.NotFound();
        }
        var rights = await Access.RightsAsync(library, new Securable(row.Folder.SecurableType, row.Folder.Securable, library.ClassificationDefault), ct);
        if (!rights.Manage)
        {
            throw rights.View ? AppException.Denied() : AppException.NotFound();
        }
        return (library, row.Folder);
    }

    private sealed record DocumentRow(Guid? Folder, bool Inherits, string SecurableType, Guid Securable, string Classification);

    private async Task<(LibraryRow Library, DocumentRow Document)> DocumentForManagerAsync(Guid documentId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        var row = await session.QuerySingleOrDefaultAsync(
            "select library_id, folder_id, inherits, securable_type, securable_id, classification from documents.document where id = @documentId and deleted_at is null and checked_in for update",
            new { documentId }, r => (Library: r.GetGuid(0), Doc: new DocumentRow(r.Get<Guid?>("folder_id"), r.GetBoolean(2), r.GetString(3), r.GetGuid(4), r.GetString(5))), ct);
        if (row.Doc is null || await Access.LibraryAsync(row.Library, ct) is not { } library)
        {
            throw AppException.NotFound();
        }
        var rights = await Access.RightsAsync(library, new Securable(row.Doc.SecurableType, row.Doc.Securable, row.Doc.Classification), ct);
        if (!rights.Manage)
        {
            throw rights.View ? AppException.Denied() : AppException.NotFound();
        }
        return (library, row.Doc);
    }

    /// <summary>Asks for confirmation before a change that reaches more than <see cref="ConfirmAbove"/> documents.</summary>
    private async Task ConfirmAsync(Guid libraryId, Guid securable, Guid[] path, BreakRequest request, CancellationToken ct)
    {
        var folderId = path[^1];
        var count = await session.ScalarAsync<long>(
            """
            select count(*) from documents.document d
            where d.library_id = @libraryId and d.deleted_at is null and d.securable_id = @securable
              and d.folder_id in (select id from documents.folder where library_id = @libraryId and @folderId = any(path))
            """, new { libraryId, securable, folderId }, ct);
        if (count > ConfirmAbove && !request.Confirm)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.documents.confirm_permission_change", null,
                new Dictionary<string, object?> { ["documents"] = count });
        }
    }

    /// <summary>Points the folder's subtree, and the documents in it, from one securable to another where they follow it.</summary>
    private Task MoveSecurableAsync(Guid folderId, Guid from, string toType, Guid to, CancellationToken ct) =>
        session.ExecAsync(
            """
            with subtree as (select id from documents.folder where @folderId = any(path) and securable_id = @from)
            , folders as (update documents.folder set securable_type = @toType, securable_id = @to where id in (select id from subtree))
            update documents.document set securable_type = @toType, securable_id = @to
            where securable_id = @from and folder_id in (select id from subtree)
            """, new { folderId, from, toType, to }, ct);

    private async Task PermissionAuditAsync(string action, LibraryRow library, string type, Guid id, Guid previous, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditEntry(action, type, id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { library = library.Id, before = previous, after = action == AuditActions.DocPermissionsBroken ? id : previous }), ct);
        await outbox.EnqueueAsync(new DocumentPermissionsChanged(library.Id, type, id), ct);
    }
}

/// <summary>Checks that a query names only fields the library's types define, with values that fit them.</summary>
internal static class ItemQueries
{
    public static readonly IReadOnlyList<string> BuiltInSorts = ["name", "modified"];

    public static Task ValidateAsync(ItemQuery query, IReadOnlyList<DocumentTypeView> types, ValidationErrors errors)
    {
        var fields = Fields(types);
        foreach (var (key, _) in query.Filters ?? new Dictionary<string, string>())
        {
            var field = key.EndsWith(".from", StringComparison.Ordinal) || key.EndsWith(".to", StringComparison.Ordinal) ? key[..key.LastIndexOf('.')] : key;
            if (!fields.ContainsKey(field))
            {
                errors.Add($"filters.{key}", "validation.documents.unknown_field");
            }
        }
        if (query.Sort is { } sort && !BuiltInSorts.Contains(sort) && !fields.ContainsKey(sort))
        {
            errors.Add("sort", "validation.documents.unknown_field");
        }
        if (query.Text is { Length: > 200 })
        {
            errors.Add("text", "validation.documents.query_text");
        }
        if (query.TypeId is { } type && types.All(t => t.Id != type))
        {
            errors.Add("typeId", "validation.documents.type_unknown");
        }
        return Task.CompletedTask;
    }

    /// <summary>Every field key of the library's types, with the first definition that uses it.</summary>
    public static IReadOnlyDictionary<string, FieldDefinition> Fields(IReadOnlyList<DocumentTypeView> types)
    {
        var fields = new Dictionary<string, FieldDefinition>(StringComparer.Ordinal);
        foreach (var f in types.SelectMany(t => t.Fields))
        {
            fields.TryAdd(f.Key, f);
        }
        return fields;
    }
}
