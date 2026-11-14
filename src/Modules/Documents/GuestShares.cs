using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

public sealed record GuestShareRequest(Guid? FolderId, Guid? SpaceId, int? Days);

public sealed record GuestShareView(Guid Id, Guid LibraryId, Guid FolderId, string FolderName, Guid SpaceId, string SpaceName, DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt, Guid CreatedBy, DateTimeOffset? RevokedAt, bool Live);

/// <summary>A folder shared with a space, as the space's readers see it: where to open it and until when.</summary>
public sealed record SharedFolderView(Guid ShareId, Guid LibraryId, IReadOnlyDictionary<string, string> LibraryNames, Guid FolderId, string FolderName,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Folders of TeamBeats libraries shared read-only with guest spaces (spec 011 US8, FR-017). A library manager shares a
/// folder of a library that allows it with a space they own, for 1 to 365 days (30 by default). The space's guests read
/// the folder and what is under it until the share expires or is revoked; members keep the access they already had. A
/// folder that inherits its permissions gets its own first, so the share reaches exactly that folder. Never for a
/// Restricted library or a library of a space.
/// </summary>
public sealed class GuestShares(
    IDbSession session,
    ITenantContext context,
    Libraries libraries,
    IEntitlementService entitlements,
    ISpaceAccess spaces,
    IAuditWriter audit,
    IClock clock)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;

    private const string Select =
        """
        select s.id, s.library_id, s.folder_id, f.name, s.space_id, s.expires_at, s.created_at, s.created_by, s.revoked_at
        from documents.guest_share s join documents.folder f on f.id = s.folder_id
        """;

    private sealed record Row(Guid Id, Guid Library, Guid Folder, string FolderName, Guid Space, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, Guid CreatedBy,
        DateTimeOffset? RevokedAt);

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5),
        r.GetFieldValue<DateTimeOffset>(6), r.GetGuid(7), r.Get<DateTimeOffset?>("revoked_at"));

    private async Task<GuestShareView> ViewAsync(Row row, CancellationToken ct) =>
        new(row.Id, row.Library, row.Folder, row.FolderName, row.Space, (await spaces.ForViewerAsync(row.Space, ct))?.Name ?? "", row.ExpiresAt, row.CreatedAt,
            row.CreatedBy, row.RevokedAt, row.RevokedAt is null && row.ExpiresAt > clock.UtcNow);

    /// <summary>The library's shares, live ones first, for its managers.</summary>
    public async Task<IReadOnlyList<GuestShareView>> ListAsync(Guid libraryId, CancellationToken ct)
    {
        await libraries.ManagedAsync(libraryId, ct);
        var rows = await session.QueryAsync(Select + " where s.library_id = @libraryId order by (s.revoked_at is null and s.expires_at > @now) desc, s.created_at desc limit 200",
            new { libraryId, now = clock.UtcNow }, Read, ct);
        var list = new List<GuestShareView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    public async Task<GuestShareView> CreateAsync(Guid libraryId, GuestShareRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsGuestSharing, ct);
        var library = await libraries.ManagedAsync(libraryId, ct, forUpdate: true);
        if (library.InSpace || !library.GuestSharing || library.ClassificationDefault == "restricted")
        {
            throw AppException.Conflict("conflict.documents.guest_sharing_off");
        }
        var errors = new ValidationErrors();
        var days = request.Days ?? DefaultDays;
        if (days is < 1 or > MaxDays)
        {
            errors.Add("days", "validation.documents.share_days");
        }
        var folder = request.FolderId is { } f
            ? await session.QuerySingleOrDefaultAsync("select id, inherits from documents.folder where id = @f and library_id = @libraryId", new { f, libraryId },
                r => (Id: r.GetGuid(0), Inherits: r.GetBoolean(1)), ct)
            : default;
        if (folder.Id == Guid.Empty)
        {
            errors.Add("folderId", "validation.documents.share_folder");
        }
        // The manager shares into a space they own, so the people reached are the ones the space's owners invited.
        var space = request.SpaceId is { } s ? await spaces.ForViewerAsync(s, ct) : null;
        if (space is not { Owns: true, Active: true })
        {
            errors.Add("spaceId", "validation.documents.share_space");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<bool>(
                "select exists (select 1 from documents.guest_share where folder_id = @folder and space_id = @space and revoked_at is null and expires_at > @now)",
                new { folder = folder.Id, space = space!.SpaceId, now = clock.UtcNow }, ct))
        {
            throw AppException.Conflict("conflict.documents.already_shared");
        }
        if (folder.Inherits)
        {
            await libraries.BreakFolderAsync(folder.Id, new BreakRequest(Confirm: true), ct);
        }
        var id = Guid.NewGuid();
        var now = clock.UtcNow;
        var expires = now.AddDays(days);
        await session.ExecAsync(
            """
            insert into documents.guest_share (tenant_id, id, library_id, folder_id, space_id, expires_at, created_at, created_by)
            values (platform.current_tenant_id(), @id, @libraryId, @folder, @space, @expires, @now, @by)
            """, new { id, libraryId, folder = folder.Id, space = space.SpaceId, expires, now, by = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocSharedWithGuest, Securables.Folder, folder.Id.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { share = id, library = libraryId, space = space.SpaceId, expiresAt = expires }), ct);
        return await ViewAsync((await session.QuerySingleOrDefaultAsync(Select + " where s.id = @id", new { id }, Read, ct))!, ct);
    }

    /// <summary>Ends a share at once. Needs managing the library; revoking twice is a conflict.</summary>
    public async Task<GuestShareView> RevokeAsync(Guid shareId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        var row = await session.QuerySingleOrDefaultAsync(Select + " where s.id = @shareId for update of s", new { shareId }, Read, ct) ?? throw AppException.NotFound();
        var library = await libraries.ManagedAsync(row.Library, ct);
        if (row.RevokedAt is not null)
        {
            throw AppException.Conflict("conflict.documents.share_revoked");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update documents.guest_share set revoked_at = @now, revoked_by = @by where id = @shareId",
            new { shareId, now, by = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DocGuestShareRevoked, Securables.Folder, row.Folder.ToString(), TargetOrgUnitId: library.OrgUnitId,
            Changes: new { share = shareId, library = row.Library, space = row.Space }), ct);
        return await ViewAsync(row with { RevokedAt = now }, ct);
    }

    /// <summary>
    /// The live shares of a space, for anyone who reads it (its guests above all), with the libraries' names. A share
    /// whose library no longer allows sharing is gone already, because turning sharing off revokes them.
    /// </summary>
    public async Task<IReadOnlyList<SharedFolderView>> ForSpaceAsync(Guid spaceId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.DocsCore, ct);
        if (await spaces.ForViewerAsync(spaceId, ct) is not { Reads: true })
        {
            throw AppException.NotFound();
        }
        var rows = await session.QueryAsync(
            """
            select s.id, s.library_id, l.names::text, s.folder_id, f.name, s.expires_at
            from documents.guest_share s join documents.folder f on f.id = s.folder_id join documents.library l on l.id = s.library_id
            where s.space_id = @spaceId and s.revoked_at is null and s.expires_at > @now and l.guest_sharing and l.status = 'active'
            order by lower(f.name), s.id
            """, new { spaceId, now = clock.UtcNow },
            r => new SharedFolderView(r.GetGuid(0), r.GetGuid(1), DocJson.Read<Dictionary<string, string>>(r.GetString(2)), r.GetGuid(3), r.GetString(4),
                r.GetFieldValue<DateTimeOffset>(5)), ct);
        return rows;
    }
}
