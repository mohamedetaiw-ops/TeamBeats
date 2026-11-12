using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record AlbumRequest(string? Title, string? Description, bool RequiresApproval);

public sealed record AlbumView(Guid Id, Guid CommunityId, string Title, string? Description, bool RequiresApproval, Person CreatedBy, DateTimeOffset CreatedAt,
    int PhotoCount, Guid? CoverPhotoId);

public sealed record AlbumList(bool CanCreate, IReadOnlyList<AlbumView> Albums);

public sealed record PhotoView(Guid Id, Guid AlbumId, Person Uploader, string? Caption, int Width, int Height, string Status, string? HeldReason, DateTimeOffset CreatedAt,
    IReadOnlyList<Person> Tags, bool CanManage, bool CanModerate, bool TaggedMe);

public sealed record AlbumPage(AlbumView Album, bool CanUpload, bool CanModerate, IReadOnlyList<PhotoView> Photos, IReadOnlyList<PhotoView> Pending);

public sealed record PhotoTagsRequest(IReadOnlyList<Guid>? UserIds);

public static class PhotoStatuses
{
    public const string Pending = "pending";
    public const string Visible = "visible";
    public const string Removed = "removed";
    public const string Deleted = "deleted";
}

/// <summary>
/// Photo galleries (spec 023 US7, FR-017). Members who may post in a community make albums there and add PNG or JPEG
/// photos. Every photo is scanned for malware first: an infected file is refused, and when the scanner cannot answer
/// nothing is stored. Location and other metadata are removed before the file is kept. The content safety check of
/// spec 026 then runs through the AI Gateway. A photo shows once that check passes, unless its album asks for approval;
/// a photo the check flagged, or could not check, waits for a community moderator. Only people who may read the
/// community see its albums. The uploader can tag people who may read the community; they are told when the photo
/// shows and can remove their tag. The uploader removes their own photo; a moderator removes any, which is audited.
/// </summary>
public sealed class Galleries(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    Communities communities,
    IUserDirectory users,
    TenantObjectStorage storage,
    IMalwareScanner scanner,
    IAiGateway gateway,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxTitle = 120;
    public const int MaxDescription = 1000;
    public const int MaxCaption = 300;
    public const int MaxTags = 20;
    public const string TaggedTemplate = "engagement.photo.tagged";

    public static string AlbumPath(Guid communityId, Guid albumId) => $"/communities/{communityId}/albums/{albumId}";

    private static string ObjectKey(Guid id) => $"engagement/photos/{id:N}";

    private async Task RequireAsync(CancellationToken ct) => await entitlements.DemandAsync(Capabilities.EngagementGalleries, ct);

    private sealed record AlbumRow(Guid Id, Guid CommunityId, string Title, string? Description, bool RequiresApproval, Guid CreatedBy, DateTimeOffset CreatedAt);

    private const string AlbumColumns = "id, community_id, title, description, requires_approval, created_by, created_at";

    private static AlbumRow ReadAlbum(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<string?>("description"), r.GetBoolean(4),
        r.GetGuid(5), r.GetFieldValue<DateTimeOffset>(6));

    private sealed record PhotoRow(Guid Id, Guid AlbumId, Guid Uploader, string ContentType, int Width, int Height, string? Caption, string Status, string? HeldReason,
        DateTimeOffset CreatedAt);

    private const string PhotoColumns = "id, album_id, uploader_id, content_type, width, height, caption, status, held_reason, created_at";

    private static PhotoRow ReadPhoto(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5),
        r.Get<string?>("caption"), r.GetString(7), r.Get<string?>("held_reason"), r.GetFieldValue<DateTimeOffset>(9));

    /// <summary>The album and the viewer's standing in its community; anything they may not read is not found.</summary>
    private async Task<(AlbumRow Album, Standing Standing)> AlbumAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var album = await session.QuerySingleOrDefaultAsync($"select {AlbumColumns} from engagement.album where id = @id", new { id }, ReadAlbum, ct)
            ?? throw AppException.NotFound();
        return (album, await communities.ReadableAsync(album.CommunityId, ct));
    }

    private async Task<(PhotoRow Photo, AlbumRow Album, Standing Standing)> PhotoAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await RequireAsync(ct);
        var photo = await session.QuerySingleOrDefaultAsync(
            $"select {PhotoColumns} from engagement.album_photo where id = @id and status in ('pending', 'visible'){(forUpdate ? " for update" : "")}", new { id }, ReadPhoto, ct)
            ?? throw AppException.NotFound();
        var (album, standing) = await AlbumAsync(photo.AlbumId, ct);
        // A waiting photo is for its uploader and the moderators only.
        if (photo.Status == PhotoStatuses.Pending && photo.Uploader != context.RequireUser() && !standing.Moderates)
        {
            throw AppException.NotFound();
        }
        return (photo, album, standing);
    }

    // ---- Albums ----

    public async Task<AlbumList> AlbumsAsync(Guid communityId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var standing = await communities.ReadableAsync(communityId, ct);
        var rows = await session.QueryAsync($"select {AlbumColumns} from engagement.album where community_id = @communityId order by created_at desc limit 100",
            new { communityId }, ReadAlbum, ct);
        return new AlbumList(standing.Posts, await AlbumViewsAsync(rows, ct));
    }

    private async Task<List<AlbumView>> AlbumViewsAsync(IReadOnlyList<AlbumRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        var ids = rows.Select(r => r.Id).ToArray();
        var stats = (await session.QueryAsync(
            """
            select album_id, count(*)::int, (array_agg(id order by created_at desc))[1] from engagement.album_photo
            where album_id = any(@ids) and status = 'visible' group by album_id
            """, new { ids }, r => (Album: r.GetGuid(0), Count: r.GetInt32(1), Cover: r.GetGuid(2)), ct)).ToDictionary(s => s.Album);
        var people = await users.GetManyAsync(rows.Select(r => r.CreatedBy).Distinct().ToList(), ct);
        return rows.Select(r => new AlbumView(r.Id, r.CommunityId, r.Title, r.Description, r.RequiresApproval, P(people, r.CreatedBy), r.CreatedAt,
            stats.TryGetValue(r.Id, out var s) ? s.Count : 0, stats.TryGetValue(r.Id, out var c) ? c.Cover : null)).ToList();
    }

    private static Person P(IReadOnlyDictionary<Guid, UserInfo> people, Guid id) =>
        people.TryGetValue(id, out var u) ? new Person(id, u.DisplayName, u.Status == "active") : new Person(id, "", false);

    public async Task<AlbumView> CreateAlbumAsync(Guid communityId, AlbumRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var standing = await communities.ReadableAsync(communityId, ct);
        if (!standing.Posts)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitle)
        {
            errors.Add("title", "validation.engagement.album_title");
        }
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > MaxDescription)
        {
            errors.Add("description", "validation.engagement.album_description");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into engagement.album (tenant_id, id, community_id, title, description, requires_approval, created_by, created_at)
            values (platform.current_tenant_id(), @id, @communityId, @title, @description, @approval, @user, @now)
            """, new { id, communityId, title, description, approval = request.RequiresApproval, user = context.RequireUser(), now = clock.UtcNow }, ct);
        var row = await session.QuerySingleOrDefaultAsync($"select {AlbumColumns} from engagement.album where id = @id", new { id }, ReadAlbum, ct);
        return (await AlbumViewsAsync([row!], ct)).Single();
    }

    public async Task<AlbumPage> AlbumPageAsync(Guid id, CancellationToken ct)
    {
        var (album, standing) = await AlbumAsync(id, ct);
        var me = context.RequireUser();
        var rows = await session.QueryAsync(
            $"""
            select {PhotoColumns} from engagement.album_photo where album_id = @id
              and (status = 'visible' or (status = 'pending' and (@moderates or uploader_id = @me)))
            order by created_at desc limit 500
            """, new { id, moderates = standing.Moderates, me }, ReadPhoto, ct);
        var views = await PhotoViewsAsync(rows, standing, ct);
        return new AlbumPage((await AlbumViewsAsync([album], ct)).Single(), standing.Posts, standing.Moderates,
            views.Where(v => v.Status == PhotoStatuses.Visible).ToList(), views.Where(v => v.Status == PhotoStatuses.Pending).ToList());
    }

    private async Task<List<PhotoView>> PhotoViewsAsync(IReadOnlyList<PhotoRow> rows, Standing standing, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        var tags = (await session.QueryAsync("select photo_id, user_id from engagement.photo_tag where photo_id = any(@ids)", new { ids = rows.Select(r => r.Id).ToArray() },
            r => (Photo: r.GetGuid(0), User: r.GetGuid(1)), ct)).ToLookup(t => t.Photo, t => t.User);
        var people = await users.GetManyAsync(rows.Select(r => r.Uploader).Concat(tags.SelectMany(t => t)).Distinct().ToList(), ct);
        var me = context.RequireUser();
        return rows.Select(r => new PhotoView(r.Id, r.AlbumId, P(people, r.Uploader), r.Caption, r.Width, r.Height, r.Status,
            r.Uploader == me || standing.Moderates ? r.HeldReason : null, r.CreatedAt,
            tags[r.Id].Select(u => P(people, u)).OrderBy(p => p.DisplayName, StringComparer.CurrentCulture).ToList(),
            r.Uploader == me, standing.Moderates, tags[r.Id].Contains(me))).ToList();
    }

    // ---- Photos ----

    /// <summary>The content safety check of spec 026 through the AI Gateway: true safe, false flagged, null not checked.</summary>
    private async Task<bool?> SafeAsync(CleanPhoto photo, CancellationToken ct)
    {
        if (photo.Data.Length > AiAttachment.MaxBytes)
        {
            return null;
        }
        AiResult result;
        try
        {
            result = await gateway.ChatAsync(new AiRequest(AiUseCases.Summarize,
                [new AiMessage(AiRoles.System, SafetyPrompt), new AiMessage(AiRoles.User, "Check this photo.", Attachments: [new AiAttachment(photo.ContentType, photo.Data)])],
                DataClassification.Internal, ResponseSchema: SafetySchema, MaxTokens: 50, Temperature: 0), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
        if (result.Completion is not { } completion)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(completion.Content);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("safe", out var safe) && safe.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? safe.GetBoolean()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private const string SafetyPrompt =
        "You check photos shared on a company intranet. Answer only with JSON. Set safe to false when the photo shows nudity, sexual content, " +
        "graphic violence or gore, hate symbols, weapons used to threaten, drugs, or anything clearly unfit for a workplace; otherwise set safe to true.";

    private static readonly JsonElement SafetySchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new { safe = new { type = "boolean" } },
        required = new[] { "safe" },
        additionalProperties = false,
    });

    public async Task<PhotoView> UploadAsync(Guid albumId, string? caption, string? contentType, byte[] data, CancellationToken ct)
    {
        var (album, standing) = await AlbumAsync(albumId, ct);
        if (!standing.Posts)
        {
            throw AppException.Denied();
        }
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (caption?.Length > MaxCaption)
        {
            throw AppException.Validation("caption", "validation.engagement.photo_caption");
        }
        var photo = PhotoFiles.Clean(contentType, data);
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict == ScanVerdict.Infected)
        {
            throw AppException.Validation("file", "validation.engagement.photo_infected");
        }
        if (verdict != ScanVerdict.Clean)
        {
            throw AppException.Conflict("conflict.engagement.photo_scan_unavailable");
        }
        var safe = await SafeAsync(photo, ct);
        var held = safe switch { false => "safety", null => "unchecked", true => album.RequiresApproval ? "approval" : null };
        var id = Ids.New();
        using (var stream = new MemoryStream(photo.Data, writable: false))
        {
            await storage.PutAsync(ObjectKey(id), stream, photo.ContentType, ct);
        }
        await session.ExecAsync(
            """
            insert into engagement.album_photo (tenant_id, id, album_id, uploader_id, object_key, content_type, bytes, width, height, caption, status, held_reason, created_at)
            values (platform.current_tenant_id(), @id, @albumId, @user, @key, @type, @bytes, @width, @height, @caption, @status, @held, @now)
            """, new
            {
                id, albumId, user = context.RequireUser(), key = ObjectKey(id), type = photo.ContentType, bytes = photo.Data.Length, width = photo.Width, height = photo.Height,
                caption, status = held is null ? PhotoStatuses.Visible : PhotoStatuses.Pending, held, now = clock.UtcNow,
            }, ct);
        var (row, _, s) = await PhotoAsync(id, false, ct);
        return (await PhotoViewsAsync([row], s, ct)).Single();
    }

    public async Task<(Stream Content, string ContentType)> OpenAsync(Guid id, CancellationToken ct)
    {
        var (photo, _, _) = await PhotoAsync(id, false, ct);
        var content = await storage.OpenReadAsync(ObjectKey(photo.Id), ct) ?? throw AppException.NotFound();
        return (content, photo.ContentType);
    }

    public async Task<PhotoView> ApproveAsync(Guid id, CancellationToken ct)
    {
        var (photo, album, standing) = await PhotoAsync(id, true, ct);
        if (!standing.Moderates)
        {
            throw AppException.Denied();
        }
        if (photo.Status != PhotoStatuses.Pending)
        {
            throw AppException.Conflict("conflict.engagement.photo_not_pending");
        }
        await session.ExecAsync("update engagement.album_photo set status = 'visible', decided_by = @me, decided_at = @now where id = @id",
            new { id, me = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentApproved, "album_photo", id.ToString(), Changes: new { album = album.Id, reason = photo.HeldReason }), ct);
        var tagged = await session.QueryAsync("select user_id from engagement.photo_tag where photo_id = @id", new { id }, r => r.GetGuid(0), ct);
        await NotifyTaggedAsync(photo with { Status = PhotoStatuses.Visible }, album, tagged, ct);
        var (row, _, s) = await PhotoAsync(id, false, ct);
        return (await PhotoViewsAsync([row], s, ct)).Single();
    }

    /// <summary>The uploader deletes their photo; a moderator removes any, which is audited. Tags go with it.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        var (photo, album, standing) = await PhotoAsync(id, true, ct);
        var me = context.RequireUser();
        if (photo.Uploader != me && !standing.Moderates)
        {
            throw AppException.Denied();
        }
        var status = photo.Uploader == me ? PhotoStatuses.Deleted : PhotoStatuses.Removed;
        await session.ExecAsync("update engagement.album_photo set status = @status, decided_by = @me, decided_at = @now where id = @id",
            new { id, status, me, now = clock.UtcNow }, ct);
        await session.ExecAsync("delete from engagement.photo_tag where photo_id = @id", new { id }, ct);
        await storage.DeleteAsync(ObjectKey(id), ct);
        if (status == PhotoStatuses.Removed)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentRemoved, "album_photo", id.ToString(), Changes: new { album = album.Id, uploader = photo.Uploader }), ct);
        }
    }

    // ---- Tags ----

    /// <summary>The uploader names who is in the photo: people who may read the community, at most 20. Newly tagged people hear once it shows.</summary>
    public async Task<PhotoView> SetTagsAsync(Guid id, PhotoTagsRequest request, CancellationToken ct)
    {
        var (photo, album, standing) = await PhotoAsync(id, true, ct);
        var me = context.RequireUser();
        if (photo.Uploader != me)
        {
            throw AppException.Denied();
        }
        var wanted = (request.UserIds ?? []).Distinct().ToList();
        if (wanted.Count > MaxTags)
        {
            throw AppException.Validation("userIds", "validation.engagement.photo_tags");
        }
        var found = await users.GetManyAsync(wanted, ct);
        foreach (var u in wanted)
        {
            if (!found.TryGetValue(u, out var info) || info.Status != "active" || !await communities.CanReadAsync(standing.Community, u, ct))
            {
                throw AppException.Validation("userIds", "validation.engagement.photo_tag_person");
            }
        }
        var current = (await session.QueryAsync("select user_id from engagement.photo_tag where photo_id = @id", new { id }, r => r.GetGuid(0), ct)).ToHashSet();
        var added = wanted.Where(u => !current.Contains(u)).ToList();
        await session.ExecAsync("delete from engagement.photo_tag where photo_id = @id and not (user_id = any(@keep))", new { id, keep = wanted.ToArray() }, ct);
        foreach (var u in added)
        {
            await session.ExecAsync("insert into engagement.photo_tag (tenant_id, photo_id, user_id, tagged_by, created_at) values (platform.current_tenant_id(), @id, @u, @me, @now)",
                new { id, u, me, now = clock.UtcNow }, ct);
        }
        await NotifyTaggedAsync(photo, album, added.Where(u => u != me).ToList(), ct);
        var (row, _, s) = await PhotoAsync(id, false, ct);
        return (await PhotoViewsAsync([row], s, ct)).Single();
    }

    /// <summary>A tagged person takes their own tag off.</summary>
    public async Task UntagMeAsync(Guid id, CancellationToken ct)
    {
        await PhotoAsync(id, true, ct);
        await session.ExecAsync("delete from engagement.photo_tag where photo_id = @id and user_id = @me", new { id, me = context.RequireUser() }, ct);
    }

    private async Task NotifyTaggedAsync(PhotoRow photo, AlbumRow album, IReadOnlyList<Guid> tagged, CancellationToken ct)
    {
        if (photo.Status != PhotoStatuses.Visible || tagged.Count == 0)
        {
            return;
        }
        var me = context.RequireUser();
        var name = (await users.GetManyAsync([me], ct)).TryGetValue(me, out var u) ? u.DisplayName : "";
        await notifications.SendAsync(new NotificationRequest(tagged.Where(t => t != me).ToList(), NotificationCategories.Mention, TaggedTemplate,
            $"{AlbumPath(album.CommunityId, album.Id)}#photo-{photo.Id}", new Dictionary<string, string> { ["album"] = album.Title },
            DedupeKey: $"engagement.photo.tagged:{photo.Id:N}"), ct);
    }
}
