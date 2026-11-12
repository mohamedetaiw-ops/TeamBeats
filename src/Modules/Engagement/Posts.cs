using System.Globalization;
using System.Text.RegularExpressions;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record PostRequest(Guid? ChannelId, BodyDocument? Body, IReadOnlyList<Guid>? Mentions);

public sealed record CommentRequest(string? Body, Guid? ParentId, IReadOnlyList<Guid>? Mentions);

public sealed record ReactionRequest(string? Kind);

public sealed record Person(Guid Id, string DisplayName, bool Active);

public sealed record ReactionCounts(IReadOnlyDictionary<string, int> Counts, string? Mine);

public sealed record PostView(
    Guid Id,
    Guid CommunityId,
    Guid ChannelId,
    Person Author,
    BodyDocument Body,
    IReadOnlyList<string> Hashtags,
    IReadOnlyList<Person> Mentions,
    bool Pinned,
    DateTimeOffset PublishedAt,
    DateTimeOffset? EditedAt,
    int CommentCount,
    ReactionCounts Reactions,
    bool Following,
    bool Mine,
    bool CanEdit,
    bool CanPin,
    bool CanComment);

public sealed record FeedPage(IReadOnlyList<PostView> Items, string? NextCursor);

/// <summary>The post as saved, with the people the author mentioned who cannot read it and so were not told (edge case).</summary>
public sealed record PostSaved(PostView Post, IReadOnlyList<Person> NotReachable);

public sealed record CommentView(Guid Id, Guid PostId, Guid? ParentId, Person Author, string Body, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, ReactionCounts Reactions,
    bool Mine, bool CanRemove);

public sealed record CommentSaved(CommentView Comment, IReadOnlyList<Person> NotReachable);

public sealed record HashtagPage(string Tag, IReadOnlyList<PostView> Items);

internal sealed record PostRow(Guid Id, Guid CommunityId, Guid ChannelId, Guid AuthorId, BodyDocument Body, string Text, string Status, bool Pinned,
    DateTimeOffset PublishedAt, DateTimeOffset? EditedAt, int CommentCount)
{
    public const string Columns = "id, community_id, channel_id, author_id, body::text, text, status, pinned, published_at, edited_at, comment_count";

    public static PostRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), EngagementJson.Read<BodyDocument>(r.GetString(4)),
        r.GetString(5), r.GetString(6), r.GetBoolean(7), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("edited_at"), r.GetInt32(10));
}

/// <summary>
/// Posts, comments, reactions, mentions, hashtags and following in communities (spec 023 US1, FR-003, FR-004). A post
/// is the spec 009 block document in a channel; mentions name people chosen in the composer, and only those who can
/// read the community are told (the author hears who was not). Hashtags are read from the text. Followers (the author,
/// commenters and anyone who chose to follow) hear of new comments at most once an hour per thread, grouped.
/// </summary>
public sealed partial class Posts(
    IDbSession session,
    ITenantContext context,
    Communities communities,
    IUserDirectory users,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxPostCharacters = 10_000;
    public const int MaxCommentCharacters = 2_000;
    public const int MaxMentions = 20;
    public const int MaxHashtags = 20;
    public const int PageSize = 20;
    public const int CommentPage = 200;
    public static readonly TimeSpan FollowerQuiet = TimeSpan.FromHours(1);

    public const string MentionTemplate = "engagement.mention";
    public const string CommentTemplate = "engagement.comment.new";

    /// <summary>Block types a post may hold. Images and files come with galleries and media (US7).</summary>
    public static readonly IReadOnlySet<string> PostBlocks = new HashSet<string>(StringComparer.Ordinal)
        { BlockTypes.Paragraph, BlockTypes.Heading, BlockTypes.List, BlockTypes.Quote, BlockTypes.Link };

    public static readonly IReadOnlySet<string> ReactionKinds = new HashSet<string>(StringComparer.Ordinal) { "like", "celebrate", "support", "insightful", "funny" };

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])#([\p{L}\p{N}_]{1,60})")]
    private static partial Regex HashtagPattern();

    /// <summary>The hashtags of a text, lower case, in the order they first appear.</summary>
    public static IReadOnlyList<string> Hashtags(string text) =>
        HashtagPattern().Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(MaxHashtags).ToList();

    public static string Link(Guid communityId, Guid postId) => $"/communities/{communityId}/posts/{postId}";

    private async Task<PostRow?> RowAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {PostRow.Columns} from engagement.post where id = @id", new { id }, PostRow.Read, ct);

    /// <summary>A published post the viewer may read, with their standing in its community.</summary>
    private async Task<(PostRow Post, Standing Standing)> ReadableAsync(Guid id, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        var post = await RowAsync(id, ct);
        if (post is null || post.Status != "published")
        {
            throw AppException.NotFound();
        }
        return (post, await communities.ReadableAsync(post.CommunityId, ct));
    }

    // ---- Views ----

    private async Task<Dictionary<Guid, Person>> PeopleAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        var found = await users.GetManyAsync(list, ct);
        // Authors who left keep their name, shown as a former employee (edge case).
        return list.ToDictionary(id => id, id => found.TryGetValue(id, out var u) ? new Person(id, u.DisplayName, u.Status == "active") : new Person(id, "", false));
    }

    private async Task<Dictionary<Guid, ReactionCounts>> ReactionsAsync(string type, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            "select target_id, kind, count(*)::int, bool_or(user_id = @user) from engagement.reaction where target_type = @type and target_id = any(@ids) group by 1, 2",
            new { type, ids = ids.ToArray(), user }, r => (Id: r.GetGuid(0), Kind: r.GetString(1), Count: r.GetInt32(2), Mine: r.GetBoolean(3)), ct);
        return ids.ToDictionary(id => id, id =>
        {
            var mine = rows.Where(r => r.Id == id).ToList();
            return new ReactionCounts(ReactionKinds.Order(StringComparer.Ordinal).ToDictionary(k => k, k => mine.FirstOrDefault(r => r.Kind == k).Count),
                mine.FirstOrDefault(r => r.Mine).Kind);
        });
    }

    private async Task<List<PostView>> ViewsAsync(IReadOnlyList<PostRow> rows, Func<Guid, Standing> standing, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        var user = context.RequireUser();
        var ids = rows.Select(r => r.Id).ToArray();
        var tags = (await session.QueryAsync("select post_id, tag from engagement.post_hashtag where post_id = any(@ids) order by tag", new { ids },
            r => (Post: r.GetGuid(0), Tag: r.GetString(1)), ct)).ToLookup(t => t.Post, t => t.Tag);
        var mentions = (await session.QueryAsync("select post_id, user_id from engagement.mention where post_id = any(@ids) and comment_id is null", new { ids },
            r => (Post: r.GetGuid(0), User: r.GetGuid(1)), ct)).ToLookup(m => m.Post, m => m.User);
        var following = (await session.QueryAsync("select post_id from engagement.post_follow where post_id = any(@ids) and user_id = @user", new { ids, user },
            r => r.GetGuid(0), ct)).ToHashSet();
        var people = await PeopleAsync(rows.Select(r => r.AuthorId).Concat(mentions.SelectMany(m => m)), ct);
        var reactions = await ReactionsAsync("post", ids, ct);
        return rows.Select(r =>
        {
            var s = standing(r.CommunityId);
            var mine = r.AuthorId == user;
            return new PostView(r.Id, r.CommunityId, r.ChannelId, people[r.AuthorId], r.Body, tags[r.Id].ToList(), mentions[r.Id].Select(m => people[m]).ToList(),
                r.Pinned, r.PublishedAt, r.EditedAt, r.CommentCount, reactions[r.Id], following.Contains(r.Id), mine,
                CanEdit: mine && s.Posts, CanPin: s.Moderates && s.Community.Status == "active", CanComment: s.Posts);
        }).ToList();
    }

    private static string Cursor(PostRow row) => $"{row.PublishedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{row.Id:N}";

    private static (DateTimeOffset At, Guid Id)? ParseCursor(string? cursor)
    {
        if (cursor is null)
        {
            return null;
        }
        var parts = cursor.Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParseExact(parts[1], "N", out var id)
            && ticks is > 0 and < 3155378975999999999
            ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
            : throw AppException.Validation("cursor", "validation.engagement.cursor");
    }

    /// <summary>
    /// The community feed, newest first, pinned posts at the top of the first page (keyset on published time and ID).
    /// The home widget reads the same query (FR-022).
    /// </summary>
    public async Task<FeedPage> FeedAsync(Guid communityId, Guid? channelId, string? cursor, int? limit, CancellationToken ct)
    {
        var s = await communities.ReadableAsync(communityId, ct);
        var after = ParseCursor(cursor);
        var size = Math.Clamp(limit ?? PageSize, 1, 50);
        var pinned = after is null
            ? await session.QueryAsync(
                $"select {PostRow.Columns} from engagement.post where community_id = @communityId and status = 'published' and pinned and (@channelId::uuid is null or channel_id = @channelId) order by published_at desc, id desc limit 10",
                new { communityId, channelId }, PostRow.Read, ct)
            : [];
        var rows = await session.QueryAsync(
            $"""
            select {PostRow.Columns} from engagement.post
            where community_id = @communityId and status = 'published' and not pinned and (@channelId::uuid is null or channel_id = @channelId)
              and (@at::timestamptz is null or (published_at, id) < (@at, @id))
            order by published_at desc, id desc limit @take
            """, new { communityId, channelId, at = after?.At, id = after?.Id ?? Guid.Empty, take = size + 1 }, PostRow.Read, ct);
        var page = rows.Take(size).ToList();
        var views = await ViewsAsync(pinned.Concat(page).ToList(), _ => s, ct);
        return new FeedPage(views, rows.Count > size ? Cursor(page[^1]) : null);
    }

    public async Task<PostView> GetAsync(Guid id, CancellationToken ct)
    {
        var (post, s) = await ReadableAsync(id, ct);
        return (await ViewsAsync([post], _ => s, ct))[0];
    }

    /// <summary>
    /// Posts with a hashtag that the viewer can read, newest first (US1 scenario 3), from every community they read.
    /// Tags are per tenant: row-level security keeps other tenants' posts out.
    /// </summary>
    public async Task<HashtagPage> HashtagAsync(string tag, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        var key = tag.Trim().TrimStart('#').ToLowerInvariant();
        if (Hashtags("#" + key) is not [var normal] || normal != key)
        {
            throw AppException.Validation("tag", "validation.engagement.hashtag");
        }
        var rows = await session.QueryAsync(
            $"""
            select {string.Join(", ", PostRow.Columns.Split(", ").Select(c => "p." + c))} from engagement.post p join engagement.post_hashtag h on h.post_id = p.id
            where h.tag = @key and p.status = 'published' order by p.published_at desc, p.id desc limit 200
            """, new { key }, PostRow.Read, ct);
        var standings = new Dictionary<Guid, Standing>();
        foreach (var communityId in rows.Select(r => r.CommunityId).Distinct())
        {
            var c = await communities.RowAsync(communityId, ct);
            if (c is not null && await communities.StandingAsync(c, ct) is { Reads: true } s)
            {
                standings[communityId] = s;
            }
        }
        var readable = rows.Where(r => standings.ContainsKey(r.CommunityId)).Take(PageSize * 2).ToList();
        return new HashtagPage(key, await ViewsAsync(readable, c => standings[c], ct));
    }

    // ---- Writing ----

    private static void ValidateBody(BodyDocument? body, ValidationErrors errors)
    {
        if (body?.Blocks is not { Count: > 0 } blocks)
        {
            errors.Add("body", "validation.engagement.body_required");
            return;
        }
        if (blocks.Any(b => !PostBlocks.Contains(b.Type)))
        {
            errors.Add("body", "validation.engagement.body_block");
            return;
        }
        BodyValidator.Validate(body, new HashSet<string>(), "body", errors);
        var text = BodyValidator.PlainText(body);
        if (string.IsNullOrWhiteSpace(text))
        {
            errors.Add("body", "validation.engagement.body_required");
        }
        else if (text.Length > MaxPostCharacters)
        {
            errors.Add("body", "validation.engagement.body_too_long");
        }
    }

    private static List<Guid> Mentioned(IReadOnlyList<Guid>? mentions, Guid author, ValidationErrors errors)
    {
        var list = (mentions ?? []).Distinct().Where(m => m != author).ToList();
        if (list.Count > MaxMentions)
        {
            errors.Add("mentions", "validation.engagement.mentions");
        }
        return list;
    }

    /// <summary>
    /// Tells the mentioned people who can read the community (FR-004) and returns those who cannot. Mentions of people
    /// outside the tenant never match, since users are read inside the tenant only.
    /// </summary>
    private async Task<IReadOnlyList<Person>> MentionAsync(CommunityRow c, Guid postId, Guid? commentId, IReadOnlyList<Guid> mentioned, CancellationToken ct)
    {
        if (mentioned.Count == 0)
        {
            return [];
        }
        var known = await users.GetManyAsync(mentioned.ToList(), ct);
        var told = new List<Guid>();
        var missed = new List<Person>();
        foreach (var userId in mentioned)
        {
            if (!known.TryGetValue(userId, out var u) || u.IsTest)
            {
                throw AppException.Validation("mentions", "validation.engagement.mention_unknown");
            }
            if (u.Status == "active" && await communities.CanReadAsync(c, userId, ct))
            {
                told.Add(userId);
            }
            else
            {
                missed.Add(new Person(userId, u.DisplayName, u.Status == "active"));
            }
        }
        var now = clock.UtcNow;
        foreach (var userId in told)
        {
            await session.ExecAsync(
                """
                insert into engagement.mention (tenant_id, post_id, comment_id, user_id, created_at) values (platform.current_tenant_id(), @postId, @commentId, @userId, @now)
                on conflict do nothing
                """, new { postId, commentId, userId, now }, ct);
        }
        if (told.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(told, NotificationCategories.Mention, MentionTemplate, Link(c.Id, postId),
                DedupeKey: $"engagement.mention:{postId:N}:{commentId:N}"), ct);
        }
        return missed;
    }

    private async Task FollowAsync(Guid postId, Guid userId, CancellationToken ct) =>
        await session.ExecAsync(
            """
            insert into engagement.post_follow (tenant_id, post_id, user_id, created_at) values (platform.current_tenant_id(), @postId, @userId, @now)
            on conflict do nothing
            """, new { postId, userId, now = clock.UtcNow }, ct);

    private async Task SaveHashtagsAsync(Guid postId, string text, CancellationToken ct)
    {
        await session.ExecAsync("delete from engagement.post_hashtag where post_id = @postId", new { postId }, ct);
        foreach (var tag in Hashtags(text))
        {
            await session.ExecAsync("insert into engagement.post_hashtag (tenant_id, post_id, tag) values (platform.current_tenant_id(), @postId, @tag)", new { postId, tag }, ct);
        }
    }

    public async Task<PostSaved> CreateAsync(Guid communityId, PostRequest request, CancellationToken ct)
    {
        var s = await communities.ReadableAsync(communityId, ct);
        if (!s.Posts)
        {
            throw AppException.Denied();
        }
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        ValidateBody(request.Body, errors);
        var mentioned = Mentioned(request.Mentions, user, errors);
        var channel = request.ChannelId is { } chosen
            ? await session.ScalarAsync<Guid?>("select id from engagement.channel where id = @chosen and community_id = @communityId", new { chosen, communityId }, ct)
            : await session.ScalarAsync<Guid?>("select id from engagement.channel where community_id = @communityId and key = @key",
                new { communityId, key = Communities.GeneralChannel }, ct);
        if (channel is null)
        {
            errors.Add("channelId", "validation.engagement.channel");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        var text = BodyValidator.PlainText(request.Body!);
        await session.ExecAsync(
            """
            insert into engagement.post (tenant_id, id, community_id, channel_id, author_id, body, text, published_at)
            values (platform.current_tenant_id(), @id, @communityId, @channel, @user, @body, @text, @now)
            """, new { id, communityId, channel, user, body = EngagementJson.Jsonb(request.Body), text, now = clock.UtcNow }, ct);
        await SaveHashtagsAsync(id, text, ct);
        await FollowAsync(id, user, ct);
        var missed = await MentionAsync(s.Community, id, null, mentioned, ct);
        await outbox.EnqueueAsync(new PostPublished(id, communityId, channel!.Value), ct);
        await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, id), ct);
        return new PostSaved(await GetAsync(id, ct), missed);
    }

    public async Task<PostSaved> UpdateAsync(Guid id, PostRequest request, CancellationToken ct)
    {
        var (post, s) = await ReadableAsync(id, ct);
        var user = context.RequireUser();
        if (post.AuthorId != user || !s.Posts)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        ValidateBody(request.Body, errors);
        var mentioned = Mentioned(request.Mentions, user, errors);
        errors.ThrowIfAny();
        var text = BodyValidator.PlainText(request.Body!);
        await session.ExecAsync("update engagement.post set body = @body, text = @text, edited_at = @now, row_version = row_version + 1 where id = @id",
            new { id, body = EngagementJson.Jsonb(request.Body), text, now = clock.UtcNow }, ct);
        await SaveHashtagsAsync(id, text, ct);
        // Only people newly mentioned are told.
        var already = (await session.QueryAsync("select user_id from engagement.mention where post_id = @id and comment_id is null", new { id }, r => r.GetGuid(0), ct)).ToHashSet();
        var missed = await MentionAsync(s.Community, id, null, mentioned.Where(m => !already.Contains(m)).ToList(), ct);
        await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, id), ct);
        return new PostSaved(await GetAsync(id, ct), missed);
    }

    /// <summary>The author deletes their own post. Moderator removal with a reason is US8.</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var (post, _) = await ReadableAsync(id, ct);
        if (post.AuthorId != context.RequireUser())
        {
            throw AppException.Denied();
        }
        await session.ExecAsync("update engagement.post set status = 'removed', row_version = row_version + 1 where id = @id", new { id }, ct);
        await outbox.EnqueueAsync(new PostRemoved(id, post.CommunityId), ct);
        await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, id), ct);
    }

    public async Task<PostView> PinAsync(Guid id, bool pinned, CancellationToken ct)
    {
        var (post, s) = await ReadableAsync(id, ct);
        if (!s.Moderates || s.Community.Status != "active")
        {
            throw AppException.Denied();
        }
        await session.ExecAsync("update engagement.post set pinned = @pinned where id = @id", new { id, pinned }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementPostPinned, "post", id.ToString(), Changes: new { community = post.CommunityId, pinned }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<PostView> SetFollowingAsync(Guid id, bool follow, CancellationToken ct)
    {
        await ReadableAsync(id, ct);
        var user = context.RequireUser();
        if (follow)
        {
            await FollowAsync(id, user, ct);
        }
        else
        {
            await session.ExecAsync("delete from engagement.post_follow where post_id = @id and user_id = @user", new { id, user }, ct);
        }
        return await GetAsync(id, ct);
    }

    // ---- Reactions ----

    public async Task<ReactionCounts> ReactAsync(string targetType, Guid targetId, ReactionRequest request, CancellationToken ct)
    {
        var postId = targetType == "post" ? targetId
            : await session.ScalarAsync<Guid?>("select post_id from engagement.comment where id = @targetId and status = 'visible'", new { targetId }, ct) ?? throw AppException.NotFound();
        var (_, s) = await ReadableAsync(postId, ct);
        if (!s.Participates || s.Community.Status != "active")
        {
            throw AppException.Denied();
        }
        var user = context.RequireUser();
        if (request.Kind is null)
        {
            await session.ExecAsync("delete from engagement.reaction where target_type = @targetType and target_id = @targetId and user_id = @user", new { targetType, targetId, user }, ct);
        }
        else if (!ReactionKinds.Contains(request.Kind))
        {
            throw AppException.Validation("kind", "validation.engagement.reaction");
        }
        else
        {
            await session.ExecAsync(
                """
                insert into engagement.reaction (tenant_id, target_type, target_id, user_id, kind, created_at)
                values (platform.current_tenant_id(), @targetType, @targetId, @user, @kind, @now)
                on conflict (tenant_id, target_type, target_id, user_id) do update set kind = excluded.kind, created_at = excluded.created_at
                """, new { targetType, targetId, user, kind = request.Kind, now = clock.UtcNow }, ct);
        }
        return (await ReactionsAsync(targetType, [targetId], ct))[targetId];
    }

    // ---- Comments ----

    public async Task<IReadOnlyList<CommentView>> CommentsAsync(Guid postId, CancellationToken ct)
    {
        var (_, s) = await ReadableAsync(postId, ct);
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            $"select id, parent_id, author_id, body, created_at, edited_at from engagement.comment where post_id = @postId and status = 'visible' order by created_at, id limit {CommentPage}",
            new { postId }, r => (Id: r.GetGuid(0), Parent: r.Get<Guid?>("parent_id"), Author: r.GetGuid(2), Body: r.GetString(3), At: r.GetFieldValue<DateTimeOffset>(4),
                Edited: r.Get<DateTimeOffset?>("edited_at")), ct);
        var people = await PeopleAsync(rows.Select(r => r.Author), ct);
        var reactions = rows.Count == 0 ? [] : await ReactionsAsync("comment", rows.Select(r => r.Id).ToList(), ct);
        return rows.Select(r => new CommentView(r.Id, postId, r.Parent, people[r.Author], r.Body, r.At, r.Edited, reactions[r.Id], r.Author == user,
            CanRemove: r.Author == user)).ToList();
    }

    public async Task<CommentSaved> CommentAsync(Guid postId, CommentRequest request, CancellationToken ct)
    {
        var (post, s) = await ReadableAsync(postId, ct);
        if (!s.Posts)
        {
            throw AppException.Denied();
        }
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            errors.Add("body", "validation.engagement.body_required");
        }
        else if (body.Length > MaxCommentCharacters || body.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            errors.Add("body", "validation.engagement.body_too_long");
        }
        // One reply level: a reply answers a top-level comment of the same post.
        if (request.ParentId is { } parent && !await session.ScalarAsync<bool>(
                "select exists (select 1 from engagement.comment where id = @parent and post_id = @postId and parent_id is null and status = 'visible')", new { parent, postId }, ct))
        {
            errors.Add("parentId", "validation.engagement.reply_level");
        }
        var mentioned = Mentioned(request.Mentions, user, errors);
        errors.ThrowIfAny();
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into engagement.comment (tenant_id, id, post_id, parent_id, author_id, body, created_at)
            values (platform.current_tenant_id(), @id, @postId, @parent, @user, @body, @now)
            """, new { id, postId, parent = request.ParentId, user, body, now }, ct);
        await session.ExecAsync("update engagement.post set comment_count = comment_count + 1 where id = @postId", new { postId }, ct);
        var missed = await MentionAsync(s.Community, postId, id, mentioned, ct);
        await TellFollowersAsync(s.Community, post, user, mentioned, ct);
        await FollowAsync(postId, user, ct);
        await outbox.EnqueueAsync(new CommentAdded(id, postId, post.CommunityId), ct);
        var view = (await CommentsAsync(postId, ct)).First(c => c.Id == id);
        return new CommentSaved(view, missed);
    }

    /// <summary>
    /// Followers hear of a new comment at most once an hour per thread (US1 scenario 4); within the hour the
    /// notification they have is grouped. People who can no longer read the community are not told and stop following.
    /// </summary>
    private async Task TellFollowersAsync(CommunityRow c, PostRow post, Guid commenter, IReadOnlyCollection<Guid> mentioned, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var followers = await session.QueryAsync("select user_id, notified_at from engagement.post_follow where post_id = @id and user_id <> @commenter",
            new { id = post.Id, commenter }, r => (User: r.GetGuid(0), At: r.Get<DateTimeOffset?>("notified_at")), ct);
        var due = new List<Guid>();
        foreach (var f in followers.Where(f => !mentioned.Contains(f.User) && (f.At is null || now - f.At >= FollowerQuiet)))
        {
            if (await communities.CanReadAsync(c, f.User, ct))
            {
                due.Add(f.User);
            }
            else
            {
                await session.ExecAsync("delete from engagement.post_follow where post_id = @id and user_id = @user", new { id = post.Id, user = f.User }, ct);
            }
        }
        if (due.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(due, NotificationCategories.Comment, CommentTemplate, Link(c.Id, post.Id), GroupKey: $"engagement.post:{post.Id:N}"), ct);
        await session.ExecAsync("update engagement.post_follow set notified_at = @now where post_id = @id and user_id = any(@due)", new { id = post.Id, due = due.ToArray(), now }, ct);
    }

    public async Task DeleteCommentAsync(Guid commentId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select post_id, author_id from engagement.comment where id = @commentId and status = 'visible'", new { commentId },
            r => (Post: r.GetGuid(0), Author: r.GetGuid(1)), ct);
        if (row == default)
        {
            throw AppException.NotFound();
        }
        await ReadableAsync(row.Post, ct);
        if (row.Author != context.RequireUser())
        {
            throw AppException.Denied();
        }
        var removed = await session.ExecAsync("update engagement.comment set status = 'removed' where (id = @commentId or parent_id = @commentId) and status = 'visible'",
            new { commentId }, ct);
        await session.ExecAsync("update engagement.post set comment_count = greatest(comment_count - @removed, 0) where id = @post", new { post = row.Post, removed }, ct);
    }
}
