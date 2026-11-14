using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>Where a connected library's content lives: the connection, the document library and an optional folder in it.</summary>
internal sealed record ConnectedSource(Guid ConnectionId, string ContainerId, string? ContainerName, string? FolderId, bool UploadEnabled);

internal static class LibrarySources
{
    public const string TeamBeats = "teambeats";
    public const string Connected = "connected";
}

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
    int Version,
    bool GuestConfidential = false)
{
    public const string Columns = "id, names::text, scope, scope_id, org_unit_id, allowed_extensions, max_file_mb, classification_default, status, row_version, guest_confidential, " +
        "view_only, guest_sharing, source, connection_id, container_id, container_name, remote_folder_id, upload_enabled";

    public static LibraryRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), DocJson.Read<Dictionary<string, string>>(r.GetString(1)), r.GetString(2),
        r.Get<Guid?>("scope_id"), r.Get<Guid?>("org_unit_id"), r.Get<string[]>("allowed_extensions"), r.Get<int?>("max_file_mb"), r.GetString(7), r.GetString(8), r.GetInt32(9), r.GetBoolean(10))
    {
        ViewOnly = r.GetBoolean(11),
        GuestSharing = r.GetBoolean(12),
        Connected = r.GetString(13) == LibrarySources.Connected && r.Get<Guid?>("connection_id") is { } connection
            ? new ConnectedSource(connection, r.GetString(15), r.Get<string>("container_name"), r.Get<string>("remote_folder_id"), r.GetBoolean(18))
            : null,
    };

    /// <summary>Spec 011 US9: where a connected library's content lives; null for a TeamBeats library.</summary>
    public ConnectedSource? Connected { get; init; }

    /// <summary>
    /// Records, controlled documents, holds, watermark, guest sharing, the recycle bin and TeamBeats folders apply to
    /// TeamBeats libraries only (FR-030): a connected library refuses them with the reason.
    /// </summary>
    public void RequireOwn()
    {
        if (Connected is not null)
        {
            throw AppException.Conflict("conflict.documents.connected_library");
        }
    }

    /// <summary>Spec 011 FR-016: readers preview with a watermark and never download.</summary>
    public bool ViewOnly { get; init; }

    /// <summary>Spec 011 FR-017: managers may share folders read-only with guest spaces.</summary>
    public bool GuestSharing { get; init; }

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
internal sealed class DocumentAccess(IDbSession session, IAuthorizationService authz, ITenantContext context, ITenantMemberships memberships,
    TeamBeats.Modules.Spaces.Contracts.ISpaceAccess spaces)
{
    private static readonly string[] Open = [];
    private static readonly string[] GuestClosed = ["confidential", "restricted"];
    private static readonly string[] GuestRestricted = ["restricted"];
    private bool? _guest;

    /// <summary>
    /// The classifications whose files the caller may not open in this library, however their rights read. Guests never
    /// open Restricted files (spec 026 FR-011) and open Confidential ones only in a space library whose tenant rule and
    /// owner both allow it (spec 029 FR-008); everyone else has nothing closed.
    /// </summary>
    public async Task<string[]> ClosedAsync(LibraryRow library, CancellationToken ct) =>
        await IsGuestAsync(ct) ? (library.InSpace && library.GuestConfidential ? GuestRestricted : GuestClosed) : Open;

    /// <summary>Whether the caller is a guest of the tenant (spec 026 FR-009).</summary>
    public async Task<bool> IsGuestAsync(CancellationToken ct)
    {
        _guest ??= context.ActorId is { } user && await memberships.GetAsync(user, ct) is { MembershipType: MembershipTypes.Guest };
        return _guest.Value;
    }

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
        view.UnionWith(await SharedWithGuestAsync(library, securables, ct));
        // An archived library (the library of an archived space) is read-only (spec 029 FR-013).
        return securables.ToDictionary(s => s.Id, s =>
        {
            var m = library.Active && manage.Contains(s.Id);
            var c = m || (library.Active && contribute.Contains(s.Id));
            return new DocRights(c || view.Contains(s.Id), c, m);
        });
    }

    /// <summary>
    /// Spec 011 FR-017: a guest reads a shared folder, and folders and documents with their own permissions under it,
    /// while a live share names a space the guest reads. Shares grant reading only, and only to guests.
    /// </summary>
    private async Task<IReadOnlySet<Guid>> SharedWithGuestAsync(LibraryRow library, IReadOnlyList<Securable> securables, CancellationToken ct)
    {
        if (library.InSpace || !library.GuestSharing || !await IsGuestAsync(ct))
        {
            return new HashSet<Guid>();
        }
        var spaces = await LiveShareSpacesAsync(library.Id, ct);
        if (spaces.Count == 0)
        {
            return new HashSet<Guid>();
        }
        var ids = securables.Where(s => s.Type != Securables.Library).Select(s => s.Id).ToArray();
        return (await session.QueryAsync(
            """
            select f.id from documents.folder f
            where f.id = any(@ids) and exists (select 1 from documents.guest_share s
              where s.library_id = @lib and s.space_id = any(@spaces) and s.revoked_at is null and s.expires_at > now() and s.folder_id = any(f.path))
            union
            select d.id from documents.document d join documents.folder f on f.id = d.folder_id
            where d.id = any(@ids) and exists (select 1 from documents.guest_share s
              where s.library_id = @lib and s.space_id = any(@spaces) and s.revoked_at is null and s.expires_at > now() and s.folder_id = any(f.path))
            """, new { ids, lib = library.Id, spaces = spaces.ToArray() }, r => r.GetGuid(0), ct)).ToHashSet();
    }

    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _shareSpaces = [];

    /// <summary>The spaces with a live share in the library that the caller reads now, asked once per library and request.</summary>
    public async Task<IReadOnlyList<Guid>> LiveShareSpacesAsync(Guid libraryId, CancellationToken ct)
    {
        if (_shareSpaces.TryGetValue(libraryId, out var known))
        {
            return known;
        }
        var candidates = await session.QueryAsync(
            "select distinct space_id from documents.guest_share where library_id = @libraryId and revoked_at is null and expires_at > now()",
            new { libraryId }, r => r.GetGuid(0), ct);
        var reads = new List<Guid>();
        foreach (var space in candidates)
        {
            if (await spaces.ForViewerAsync(space, ct) is { Reads: true, Active: true })
            {
                reads.Add(space);
            }
        }
        _shareSpaces[libraryId] = reads;
        return reads;
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
