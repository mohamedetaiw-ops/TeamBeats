using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record AttachmentView(Guid Id, string Kind, string Name, string ContentType, int Bytes, int? Width, int? Height);

internal sealed record AttachmentRow(Guid Id, Guid CommunityId, Guid? PostId, Guid UploaderId, string Kind, string Name, string ContentType, int Bytes,
    int? Width, int? Height, DateTimeOffset CreatedAt)
{
    public AttachmentView View() => new(Id, Kind, Name, ContentType, Bytes, Width, Height);
}

/// <summary>
/// Images and files in posts (spec 023 US1, FR-003). An attachment is uploaded to a community by someone who may post
/// there and goes through the same checks as gallery photos: the malware scan for every file, metadata removal and the
/// content safety check for images. Only a clean scan is stored; an image the safety check flags, or one it could not
/// check, is refused so that a post never shows an unchecked picture. The attachment joins a post when the author saves
/// it, at most 10 images and 5 files per post. Until then only the uploader can open it, and loose uploads older than a
/// day are deleted on that person's next upload. Speak Up posts take none, which <see cref="Posts"/> enforces.
/// </summary>
public sealed class PostAttachments(IDbSession session, ITenantContext context, Communities communities, TenantObjectStorage storage, MediaChecks checks, IClock clock)
{
    public const int MaxImages = 10;
    public const int MaxFiles = 5;
    public const int MaxFileBytes = 25 * 1024 * 1024;
    public const int MaxName = 200;
    private static readonly TimeSpan LooseFor = TimeSpan.FromDays(1);

    /// <summary>The documents a post may carry besides images, by the type the browser sends.</summary>
    public static readonly IReadOnlyDictionary<string, string> FileTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["application/pdf"] = "pdf",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = "zip",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = "zip",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = "zip",
        ["text/plain"] = "text",
        ["text/csv"] = "text",
    };

    private const string Columns = "id, community_id, post_id, uploader_id, kind, name, content_type, bytes, width, height, created_at";

    private static AttachmentRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("post_id"), r.GetGuid(3), r.GetString(4), r.GetString(5),
        r.GetString(6), r.GetInt32(7), r.Get<int?>("width"), r.Get<int?>("height"), r.GetFieldValue<DateTimeOffset>(10));

    internal static string ObjectKey(Guid id) => $"engagement/attachments/{id:N}";

    public async Task<AttachmentView> UploadAsync(Guid communityId, string? name, string? contentType, byte[] data, CancellationToken ct)
    {
        var s = await communities.ReadableAsync(communityId, ct);
        if (!s.Posts)
        {
            throw AppException.Denied();
        }
        var cleanName = CleanName(name);
        var type = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        string kind;
        byte[] stored;
        int? width = null, height = null;
        if (type is "image/png" or "image/jpeg")
        {
            var photo = PhotoFiles.Clean(type, data);
            await ScanAsync(data, ct);
            switch (await checks.SafeAsync(photo, ct))
            {
                case false:
                    throw AppException.Validation("file", "validation.engagement.attachment_unsafe");
                case null:
                    throw AppException.Conflict("conflict.engagement.attachment_check_unavailable");
            }
            (kind, stored, type, width, height) = ("image", photo.Data, photo.ContentType, photo.Width, photo.Height);
        }
        else
        {
            if (!FileTypes.TryGetValue(type, out var shape) || !Looks(shape, data))
            {
                throw AppException.Validation("file", "validation.engagement.attachment_type");
            }
            if (data.Length is 0 or > MaxFileBytes)
            {
                throw AppException.Validation("file", "validation.engagement.attachment_size");
            }
            await ScanAsync(data, ct);
            (kind, stored) = ("file", data);
        }
        var user = context.RequireUser();
        await DropLooseAsync(user, ct);
        var id = Ids.New();
        using (var stream = new MemoryStream(stored, writable: false))
        {
            await storage.PutAsync(ObjectKey(id), stream, type, ct);
        }
        await session.ExecAsync(
            """
            insert into engagement.post_attachment (tenant_id, id, community_id, uploader_id, kind, name, content_type, bytes, width, height, object_key, created_at)
            values (platform.current_tenant_id(), @id, @communityId, @user, @kind, @name, @type, @bytes, @width, @height, @key, @now)
            """, new { id, communityId, user, kind, name = cleanName, type, bytes = stored.Length, width, height, key = ObjectKey(id), now = clock.UtcNow }, ct);
        return new AttachmentView(id, kind, cleanName, type, stored.Length, width, height);
    }

    private async Task ScanAsync(byte[] data, CancellationToken ct)
    {
        var verdict = await checks.ScanAsync(data, ct);
        if (verdict == ScanVerdict.Infected)
        {
            throw AppException.Validation("file", "validation.engagement.photo_infected");
        }
        if (verdict != ScanVerdict.Clean)
        {
            throw AppException.Conflict("conflict.engagement.attachment_scan_unavailable");
        }
    }

    /// <summary>A name to show: the last part of what the browser sent, without control characters, at most 200 characters.</summary>
    internal static string CleanName(string? name)
    {
        var last = (name ?? "").Replace('\\', '/').Split('/').Last();
        var clean = new string(last.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
        {
            throw AppException.Validation("name", "validation.engagement.attachment_name");
        }
        return clean.Length > MaxName ? clean[..MaxName] : clean;
    }

    /// <summary>Whether the bytes are the kind of file the type claims: a PDF header, an Office zip, or text with no NUL bytes.</summary>
    internal static bool Looks(string shape, byte[] data) => shape switch
    {
        "pdf" => data.Length >= 5 && data[0] == '%' && data[1] == 'P' && data[2] == 'D' && data[3] == 'F' && data[4] == '-',
        "zip" => data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4,
        "text" => Array.IndexOf(data, (byte)0) < 0,
        _ => false,
    };

    private async Task DropLooseAsync(Guid user, CancellationToken ct)
    {
        var old = await session.QueryAsync("delete from engagement.post_attachment where uploader_id = @user and post_id is null and created_at < @before returning id",
            new { user, before = clock.UtcNow - LooseFor }, r => r.GetGuid(0), ct);
        foreach (var id in old)
        {
            await storage.DeleteAsync(ObjectKey(id), ct);
        }
    }

    /// <summary>
    /// Joins the chosen attachments to a post in the given order and lets go of any it held before that are not chosen
    /// any more. Each must be the author's own upload to the same community, loose or already on this post.
    /// </summary>
    internal async Task AttachAsync(Guid postId, Guid communityId, IReadOnlyList<Guid>? chosen, CancellationToken ct)
    {
        var wanted = (chosen ?? []).Distinct().ToList();
        var user = context.RequireUser();
        var rows = wanted.Count == 0
            ? []
            : (await session.QueryAsync($"select {Columns} from engagement.post_attachment where id = any(@ids)", new { ids = wanted.ToArray() }, Read, ct)).ToDictionary(r => r.Id);
        if (wanted.Any(id => !rows.TryGetValue(id, out var r) || r.UploaderId != user || r.CommunityId != communityId || (r.PostId is { } p && p != postId)))
        {
            throw AppException.Validation("attachments", "validation.engagement.attachment_unknown");
        }
        if (rows.Values.Count(r => r.Kind == "image") > MaxImages || rows.Values.Count(r => r.Kind == "file") > MaxFiles)
        {
            throw AppException.Validation("attachments", "validation.engagement.attachment_count");
        }
        var dropped = await session.QueryAsync("delete from engagement.post_attachment where post_id = @postId and not (id = any(@keep)) returning id",
            new { postId, keep = wanted.ToArray() }, r => r.GetGuid(0), ct);
        foreach (var id in dropped)
        {
            await storage.DeleteAsync(ObjectKey(id), ct);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            await session.ExecAsync("update engagement.post_attachment set post_id = @postId, position = @i where id = @id", new { postId, i, id = wanted[i] }, ct);
        }
    }

    internal async Task<ILookup<Guid, AttachmentView>> ForPostsAsync(Guid[] postIds, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from engagement.post_attachment where post_id = any(@postIds) order by position", new { postIds }, Read, ct))
        .ToLookup(r => r.PostId!.Value, r => r.View());

    internal async Task<AttachmentRow?> RowAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from engagement.post_attachment where id = @id", new { id }, Read, ct)).FirstOrDefault();

    internal async Task<Stream> OpenAsync(Guid id, CancellationToken ct) =>
        await storage.OpenReadAsync(ObjectKey(id), ct) ?? throw AppException.NotFound();
}
