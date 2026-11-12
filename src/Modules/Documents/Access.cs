using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Documents;

internal sealed record LibraryRow(
    Guid Id,
    IReadOnlyDictionary<string, string> Names,
    string Scope,
    Guid? ScopeId,
    Guid? OrgUnitId,
    IReadOnlyList<string>? AllowedExtensions,
    int? MaxFileMb,
    string ClassificationDefault,
    string Status,
    int Version)
{
    public const string Columns = "id, names::text, scope, scope_id, org_unit_id, allowed_extensions, max_file_mb, classification_default, status, row_version";

    public static LibraryRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), r.GetString(2),
        r.Get<Guid?>("scope_id"), r.Get<Guid?>("org_unit_id"), r.Get<string[]>("allowed_extensions"), r.Get<int?>("max_file_mb"), r.GetString(7), r.GetString(8), r.GetInt32(9));

    /// <summary>Where library-wide decisions (create, manage) are taken: the owning unit, or the tenant.</summary>
    public AuthzTarget ScopeTarget => OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    /// <summary>A space library: its permissions follow the space (spec 029 FR-006) and cannot be changed in Documents.</summary>
    public bool InSpace => Scope == LibraryScopes.Space;

    public bool Active => Status == "active";
}

/// <summary>A node whose permissions apply to the documents under it: the library, or a folder or document after a break.</summary>
internal sealed record Securable(string Type, Guid Id, string Classification);

/// <summary>
/// What the caller may do at one securable. Managing includes contributing, and contributing includes reading, so a
/// library manager is never locked out of the files they manage.
/// </summary>
public sealed record DocRights(bool View, bool Contribute, bool Manage)
{
    public static readonly DocRights None = new(false, false, false);
}

/// <summary>
/// Turns libraries, folders and documents into authorization decisions (constitution III). Libraries at tenant scope are
/// open to every member for reading; unit and site libraries are restricted and start with an ACL entry for the unit's
/// members. Contributors and managers come from role assignments at the owning unit or from ACL entries.
/// </summary>
internal sealed class DocumentAccess(IDbSession session, IAuthorizationService authz)
{
    /// <summary>
    /// A space library is restricted and contained by its space, so the space roles decide who reads, adds files and
    /// manages; tenant libraries are read by every member.
    /// </summary>
    public static ResourceDescriptor Describe(LibraryRow library, Securable s) =>
        new(s.Type, s.Id, library.OrgUnitId,
            s.Type == Securables.Library && library.OrgUnitId is null && !library.InSpace ? ResourceVisibility.Tenant : ResourceVisibility.Restricted, s.Classification,
            library.InSpace && library.ScopeId is { } space ? new ResourceRef(LibraryScopes.SpaceResource, space) : null);

    public Task<LibraryRow?> LibraryAsync(Guid id, CancellationToken ct, bool forUpdate = false) =>
        session.QuerySingleOrDefaultAsync($"select {LibraryRow.Columns} from documents.library where id = @id" + (forUpdate ? " for update" : ""), new { id }, LibraryRow.Read, ct);

    public static Securable LibrarySecurable(LibraryRow library) => new(Securables.Library, library.Id, library.ClassificationDefault);

    /// <summary>The library and every folder or document with its own permissions in it.</summary>
    public async Task<IReadOnlyList<Securable>> SecurablesAsync(LibraryRow library, CancellationToken ct)
    {
        var broken = await session.QueryAsync(
            """
            select 'doc_folder', id, null::text as classification from documents.folder where library_id = @id and not inherits
            union all
            select 'doc_document', id, classification from documents.document where library_id = @id and not inherits and deleted_at is null
            """, new { id = library.Id }, r => new Securable(r.GetString(0), r.GetGuid(1), r.Get<string>("classification") ?? library.ClassificationDefault), ct);
        return [LibrarySecurable(library), .. broken];
    }

    /// <summary>Rights at each securable, in three batch decisions however many there are (spec 004 FR-012).</summary>
    public async Task<IReadOnlyDictionary<Guid, DocRights>> RightsAsync(LibraryRow library, IReadOnlyList<Securable> securables, CancellationToken ct)
    {
        var descriptors = securables.Select(s => Describe(library, s)).ToList();
        async Task<HashSet<Guid>> Allowed(string permission) =>
            (await authz.FilterAsync(permission, descriptors, ct)).Select(d => d.ResourceId).ToHashSet();
        var manage = await Allowed(Permissions.DocManage);
        var contribute = await Allowed(Permissions.DocContribute);
        var view = await Allowed(Permissions.DocView);
        // An archived library (the library of an archived space) is read-only (spec 029 FR-013).
        return securables.ToDictionary(s => s.Id, s =>
        {
            var m = library.Active && manage.Contains(s.Id);
            var c = m || (library.Active && contribute.Contains(s.Id));
            return new DocRights(c || view.Contains(s.Id), c, m);
        });
    }

    public async Task<DocRights> RightsAsync(LibraryRow library, Securable securable, CancellationToken ct) =>
        (await RightsAsync(library, [securable], ct))[securable.Id];

    /// <summary>The securable a folder's contents use, read from the folder row.</summary>
    public async Task<Securable> SecurableOfFolderAsync(LibraryRow library, Guid? folderId, CancellationToken ct)
    {
        if (folderId is null)
        {
            return LibrarySecurable(library);
        }
        var row = await session.QuerySingleOrDefaultAsync("select securable_type, securable_id from documents.folder where id = @folderId and library_id = @lib",
            new { folderId, lib = library.Id }, r => (Type: r.GetString(0), Id: r.GetGuid(1)), ct);
        if (row.Type is null)
        {
            throw AppException.NotFound();
        }
        return new Securable(row.Type, row.Id, library.ClassificationDefault);
    }
}

/// <summary>
/// Describes document securables for the shared ACL editor (spec 004 resource grants). A folder or document that still
/// inherits has no ACL of its own, so it is not found until a library manager breaks inheritance.
/// </summary>
internal abstract class DocumentDescriptorProvider(IDbSession session, DocumentAccess access) : IResourceDescriptorProvider
{
    public abstract string ResourceType { get; }

    public string ManagePermission => Permissions.DocManage;

    protected abstract string Lookup { get; }

    public async Task<ResourceDescriptor?> DescribeAsync(Guid resourceId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(Lookup, new { id = resourceId }, r => (Library: r.GetGuid(0), Classification: r.Get<string>("classification")), ct);
        // A space library's audience is the space; its permissions are not edited here (spec 029 FR-006).
        if (row.Library == Guid.Empty || await access.LibraryAsync(row.Library, ct) is not { } library || library.InSpace)
        {
            return null;
        }
        return DocumentAccess.Describe(library, new Securable(ResourceType, resourceId, row.Classification ?? library.ClassificationDefault));
    }
}

internal sealed class LibraryDescriptorProvider(IDbSession session, DocumentAccess access) : DocumentDescriptorProvider(session, access)
{
    public override string ResourceType => Securables.Library;

    protected override string Lookup => "select id, null::text as classification from documents.library where id = @id";
}

internal sealed class FolderDescriptorProvider(IDbSession session, DocumentAccess access) : DocumentDescriptorProvider(session, access)
{
    public override string ResourceType => Securables.Folder;

    protected override string Lookup => "select library_id, null::text as classification from documents.folder where id = @id and not inherits";
}

internal sealed class DocumentDescriptorProviderForFiles(IDbSession session, DocumentAccess access) : DocumentDescriptorProvider(session, access)
{
    public override string ResourceType => Securables.Document;

    protected override string Lookup => "select library_id, classification from documents.document where id = @id and not inherits and deleted_at is null";
}
