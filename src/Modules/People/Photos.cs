using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.People;

/// <summary>The person's photo after a change: the link to show, or null when the profile shows initials.</summary>
public sealed record PhotoView(string? Photo);

/// <summary>
/// Profile photos (spec 010 FR-013, US3 scenario 3, section 14 "photo abuse"). A person uploads their own PNG or JPEG
/// up to 2 MB; it is checked by its signature, its metadata (EXIF, XMP, text chunks, which may hold a GPS position) is
/// removed, and it is resized to a square PNG at each size in <see cref="Sizes"/> through the conversion service, the
/// same sandboxed service that draws brand asset renditions (spec 011), so no image library parses the file inside the
/// product. Only the resized files are kept, under the tenant's object path; the upload itself is not stored. When the
/// conversion service is not there, nothing changes and the person is told. Everyone who may open the directory sees
/// the photo of an active person (section 7 default: photo visible to everyone in the tenant); a manager or HR over the
/// person's unit (the same people who may correct their fields) can remove it, audited.
/// </summary>
public sealed class ProfilePhotos(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuditWriter audit,
    IOutbox outbox,
    ProfileStore store,
    VisibilityResolver visibility,
    TenantObjectStorage storage,
    IDocumentConverter converter)
{
    public const int MaxBytes = 2 * 1024 * 1024;
    public const int MaxSide = 8000;
    public const string Field = "photo";

    /// <summary>The square sizes kept, in pixels: a small one for lists and avatars, and one for the profile page.</summary>
    public static readonly IReadOnlyList<int> Sizes = [96, 256];

    /// <summary>The link to a person's photo, versioned so a new photo is never served from a cache; null for none.</summary>
    public static string? Url(Guid userId, int? version) => version is { } v ? $"/api/v1/people/{userId:D}/photo?v={v}" : null;

    private static string Key(Guid userId, int version, int size) => $"people/photos/{userId:N}/{version}-{size}.png";

    private async Task BeginAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.PeopleView, ct))
        {
            throw AppException.Denied();
        }
        await store.EnsureFreshAsync(ct);
    }

    public async Task<PhotoView> UploadOwnAsync(string? contentType, byte[] data, CancellationToken ct)
    {
        await BeginAsync(ct);
        var userId = context.RequireUser();
        var clean = PhotoCleaner.Clean(contentType, data, MaxBytes, MaxSide, "file", "validation.people.photo_size", "validation.people.photo_type");
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        if (!converter.Available)
        {
            throw AppException.Conflict("conflict.people.photo_processing");
        }
        var current = await session.QuerySingleOrDefaultAsync(
            "select photo_version from people.profile where user_id = @u for update", new { u = userId }, r => r.Get<int?>("photo_version"), ct);
        var version = (current ?? 0) + 1;
        var source = $"data:{clean.ContentType};base64,{Convert.ToBase64String(clean.Data)}";
        var made = new List<string>();
        try
        {
            foreach (var size in Sizes)
            {
                // A square crop from the middle, so faces stay centred and every avatar has the same shape.
                var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{{margin:0;background:transparent}}img{{display:block;width:{size}px;height:{size}px;object-fit:cover}}</style></head><body><img src=\"{source}\" alt=\"\"></body></html>";
                await using var png = await converter.HtmlToPngAsync(html, size, size, ct) ?? throw AppException.Conflict("conflict.people.photo_processing");
                var key = Key(userId, version, size);
                await storage.PutAsync(key, png, "image/png", ct);
                made.Add(key);
            }
        }
        catch
        {
            foreach (var key in made)
            {
                await storage.DeleteAsync(key, CancellationToken.None);
            }
            throw;
        }
        await session.ExecAsync(
            "update people.profile set photo_version = @version, photo_updated_at = now(), version = version + 1, updated_at = now() where user_id = @u",
            new { version, u = userId }, ct);
        if (current is { } old)
        {
            await DeleteFilesAsync(userId, old, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PeopleProfileUpdated, "person", userId.ToString(), TargetOrgUnitId: row.PrimaryUnitId,
            Changes: new { fields = new[] { Field } }), ct);
        await outbox.EnqueueAsync(new ProfileUpdated(userId, [Field]), ct);
        return new PhotoView(Url(userId, version));
    }

    public async Task<PhotoView> RemoveOwnAsync(CancellationToken ct)
    {
        await BeginAsync(ct);
        var userId = context.RequireUser();
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        if (await ClearAsync(userId, ct))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PeopleProfileUpdated, "person", userId.ToString(), TargetOrgUnitId: row.PrimaryUnitId,
                Changes: new { fields = new[] { Field } }), ct);
            await outbox.EnqueueAsync(new ProfileUpdated(userId, [Field]), ct);
        }
        return new PhotoView(null);
    }

    /// <summary>Removes another person's photo (section 14): for whoever may correct that person's fields, audited.</summary>
    public async Task RemoveAsync(Guid userId, CancellationToken ct)
    {
        await BeginAsync(ct);
        var row = await store.GetAsync(userId, ct) ?? throw AppException.NotFound();
        var viewer = await visibility.ViewerAsync(context.RequirePrincipal(), ct);
        if (!PeopleService.CanCorrect(viewer, row))
        {
            throw AppException.Denied();
        }
        if (await ClearAsync(userId, ct))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PeoplePhotoRemoved, "person", userId.ToString(), TargetOrgUnitId: row.PrimaryUnitId), ct);
            await outbox.EnqueueAsync(new ProfileUpdated(userId, [Field]), ct);
        }
    }

    /// <summary>The photo of an active person at the nearest kept size, for anyone who may open the directory.</summary>
    public async Task<Stream> OpenAsync(Guid userId, int? size, CancellationToken ct)
    {
        await BeginAsync(ct);
        var row = await store.GetAsync(userId, ct);
        if (row is not { Active: true, PhotoVersion: { } version })
        {
            throw AppException.NotFound();
        }
        var wanted = Sizes.FirstOrDefault(s => s >= (size ?? Sizes[^1]), Sizes[^1]);
        return await storage.OpenReadAsync(Key(userId, version, wanted), ct) ?? throw AppException.NotFound();
    }

    private async Task<bool> ClearAsync(Guid userId, CancellationToken ct)
    {
        var current = await session.QuerySingleOrDefaultAsync(
            "select photo_version from people.profile where user_id = @u for update", new { u = userId }, r => r.Get<int?>("photo_version"), ct);
        if (current is not { } old)
        {
            return false;
        }
        await session.ExecAsync(
            "update people.profile set photo_version = null, photo_updated_at = now(), version = version + 1, updated_at = now() where user_id = @u",
            new { u = userId }, ct);
        await DeleteFilesAsync(userId, old, ct);
        return true;
    }

    private async Task DeleteFilesAsync(Guid userId, int version, CancellationToken ct)
    {
        foreach (var size in Sizes)
        {
            await storage.DeleteAsync(Key(userId, version, size), ct);
        }
    }
}
