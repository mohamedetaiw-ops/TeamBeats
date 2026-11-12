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

public sealed record ReportRequest(string? Reason, string? Note);

public sealed record RemoveRequest(string? Reason);

public sealed record MuteRequest(Guid UserId, DateTimeOffset? Until, string? Reason);

public sealed record MuteView(Guid UserId, string DisplayName, DateTimeOffset Until, string? Reason);

public sealed record ReportCount(string Reason, int Count);

/// <summary>
/// One item in a moderator's queue: a post or comment held by the keyword filter or Speak Up review, hidden after
/// reports, or still published with open reports; or an anonymous Speak Up post (type "anonymous", no author, only the
/// day it came in). PostId and CommunityId point to where the item lives; for an anonymous post PostId is empty. Report notes are shown; reporters are not, so the queue cannot be used to pressure them.
/// </summary>
public sealed record ModerationItem(
    string Type,
    Guid Id,
    Guid PostId,
    Guid CommunityId,
    IReadOnlyDictionary<string, string> CommunityNames,
    Person? Author,
    string Text,
    BodyDocument? Body,
    string Status,
    string? HeldReason,
    IReadOnlyList<ReportCount> Reports,
    IReadOnlyList<string> Notes,
    DateTimeOffset CreatedAt);

/// <summary>
/// Moderation (spec 023 US8, FR-018 to FR-021). Anyone who takes part reports a post or comment they can read; the
/// third open report from a different person hides it until a moderator decides. Moderators of the community (the
/// owner or moderator relation, or <c>engagement.moderate</c> in its scope) approve an item, which publishes it and closes
/// its reports, or remove it with a reason, which the author reads on the item. Removal is audited and takes the post
/// out of search through the outbox. Moderators also mute a member in one community for a set time.
/// </summary>
public sealed class Moderation(
    IDbSession session,
    ITenantContext context,
    Communities communities,
    Posts posts,
    IUserDirectory users,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    SpeakUp speakUp,
    IClock clock)
{
    public const int HideThreshold = 3;
    public const int MaxNote = 500;
    public static readonly TimeSpan MaxMute = TimeSpan.FromDays(365);
    public const string RemovedTemplate = "engagement.content.removed";
    public const string MutedTemplate = "engagement.member.muted";

    public static readonly IReadOnlySet<string> ReportReasons = new HashSet<string>(StringComparer.Ordinal)
        { "spam", "harassment", "inappropriate", "misinformation", "other" };

    private sealed record Target(string Type, Guid Id, PostRow Post, Guid AuthorId, string Status, string? HeldReason, Guid[]? PendingMentions, Guid? ParentId);

    private static void RequireType(string type)
    {
        if (type is not ("post" or "comment"))
        {
            throw AppException.NotFound();
        }
    }

    private async Task<Target> TargetAsync(string type, Guid id, CancellationToken ct)
    {
        RequireType(type);
        await communities.RequireAsync(ct);
        if (type == "post")
        {
            var post = await posts.RowAsync(id, ct) ?? throw AppException.NotFound();
            return new Target(type, id, post, post.AuthorId, post.Status, post.HeldReason, post.PendingMentions, null);
        }
        var c = await session.QuerySingleOrDefaultAsync(
            "select post_id, author_id, status, held_reason, pending_mentions, parent_id from engagement.comment where id = @id", new { id },
            r => (Post: r.GetGuid(0), Author: r.GetGuid(1), Status: r.GetString(2), Held: r.Get<string?>("held_reason"),
                Pending: r.IsDBNull(4) ? null : r.GetFieldValue<Guid[]>(4), Parent: r.Get<Guid?>("parent_id")), ct);
        if (c == default)
        {
            throw AppException.NotFound();
        }
        var parent = await posts.RowAsync(c.Post, ct) ?? throw AppException.NotFound();
        return new Target(type, id, parent, c.Author, c.Status, c.Held, c.Pending, c.Parent);
    }

    private static bool Live(Target t) => t.Status is ContentStatus.Published or ContentStatus.Visible;

    // ---- Reports ----

    /// <summary>
    /// Reports an item the viewer can read (FR-018). A person has one open report per item; reporting again changes
    /// nothing. The third open report hides the item until a moderator decides. The answer is the same either way, so
    /// a reporter cannot tell how many others reported.
    /// </summary>
    public async Task ReportAsync(string type, Guid id, ReportRequest request, CancellationToken ct)
    {
        var t = await TargetAsync(type, id, ct);
        if (!Live(t) || t.Post.Status != ContentStatus.Published)
        {
            throw AppException.NotFound();
        }
        var s = await communities.ReadableAsync(t.Post.CommunityId, ct);
        var user = context.RequireUser();
        if (!s.Participates || t.AuthorId == user)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        if (request.Reason is not { } reason || !ReportReasons.Contains(reason))
        {
            errors.Add("reason", "validation.engagement.report_reason");
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is not null && (note.Length > MaxNote || note.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
        {
            errors.Add("note", "validation.engagement.report_note");
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into engagement.report (tenant_id, id, target_type, target_id, post_id, community_id, reporter_id, reason, note, created_at)
            values (platform.current_tenant_id(), @rid, @type, @id, @post, @community, @user, @reason, @note, @now)
            on conflict do nothing
            """, new { rid = Ids.New(), type, id, post = t.Post.Id, community = t.Post.CommunityId, user, reason = request.Reason, note, now }, ct);
        var open = await session.ScalarAsync<int>(
            "select count(distinct reporter_id)::int from engagement.report where target_type = @type and target_id = @id and resolved_at is null", new { type, id }, ct);
        if (open < HideThreshold)
        {
            return;
        }
        await SetStatusAsync(t, ContentStatus.Hidden, "reports", ct);
        if (type == "comment")
        {
            await ChangeCommentCountAsync(t, -1, ct);
        }
        // The system hides it, not the person who sent the third report: the actor stays out of the entry.
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentHidden, type, id.ToString(),
            Changes: new { community = t.Post.CommunityId, post = t.Post.Id, reports = open }), ct);
    }

    private async Task SetStatusAsync(Target t, string status, string? heldReason, CancellationToken ct)
    {
        await session.ExecAsync($"update engagement.{t.Type} set status = @status, held_reason = @heldReason where id = @id", new { status, heldReason, id = t.Id }, ct);
        if (t.Type == "post")
        {
            await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, t.Id), ct);
        }
    }

    // Replies under a comment count too: they appear and disappear with it.
    private async Task ChangeCommentCountAsync(Target t, int sign, CancellationToken ct)
    {
        var replies = t.ParentId is null
            ? await session.ScalarAsync<int>("select count(*)::int from engagement.comment where parent_id = @id and status = 'visible'", new { id = t.Id }, ct)
            : 0;
        await session.ExecAsync("update engagement.post set comment_count = greatest(comment_count + @delta, 0) where id = @post",
            new { delta = sign * (1 + replies), post = t.Post.Id }, ct);
    }

    private async Task ResolveReportsAsync(Target t, CancellationToken ct) =>
        await session.ExecAsync("update engagement.report set resolved_at = @now where target_type = @type and target_id = @id and resolved_at is null",
            new { type = t.Type, id = t.Id, now = clock.UtcNow }, ct);

    // ---- The queue ----

    /// <summary>
    /// The items waiting for this moderator, oldest first, across the communities they moderate, or one community.
    /// Anyone who moderates nothing gets an empty queue.
    /// </summary>
    public async Task<IReadOnlyList<ModerationItem>> QueueAsync(Guid? communityId, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        var rows = await session.QueryAsync(
            """
            select 'post', p.id, p.id, p.community_id, p.author_id, p.text, p.body::text, p.status, p.held_reason, p.published_at
            from engagement.post p
            where (@community::uuid is null or p.community_id = @community)
              and (p.status in ('held', 'hidden')
                   or (p.status = 'published' and exists (select 1 from engagement.report r where r.target_type = 'post' and r.target_id = p.id and r.resolved_at is null)))
            union all
            select 'comment', c.id, c.post_id, p.community_id, c.author_id, c.body, null, c.status, c.held_reason, c.created_at
            from engagement.comment c join engagement.post p on p.id = c.post_id
            where (@community::uuid is null or p.community_id = @community) and p.status = 'published'
              and (c.status in ('held', 'hidden')
                   or (c.status = 'visible' and exists (select 1 from engagement.report r where r.target_type = 'comment' and r.target_id = c.id and r.resolved_at is null)))
            order by 10, 2
            limit 500
            """, new { community = communityId },
            r => (Type: r.GetString(0), Id: r.GetGuid(1), Post: r.GetGuid(2), Community: r.GetGuid(3), Author: r.IsDBNull(4) ? Guid.Empty : r.GetGuid(4), Text: r.GetString(5),
                Body: r.IsDBNull(6) ? null : EngagementJson.Read<BodyDocument>(r.GetString(6)), Status: r.GetString(7), Held: r.Get<string?>("held_reason"),
                At: r.GetFieldValue<DateTimeOffset>(9)), ct);
        var standings = new Dictionary<Guid, Standing?>();
        foreach (var id in rows.Select(r => r.Community).Distinct())
        {
            var c = await communities.RowAsync(id, ct);
            standings[id] = c is null ? null : await communities.StandingAsync(c, ct);
        }
        var mine = rows.Where(r => standings[r.Community] is { Moderates: true }).ToList();
        var anonymous = await speakUp.QueueAsync(communityId, ct);
        if (mine.Count == 0)
        {
            return anonymous;
        }
        var ids = mine.Select(r => r.Id).ToArray();
        var reports = await session.QueryAsync(
            "select target_id, reason, count(*)::int, array_remove(array_agg(note order by created_at), null) from engagement.report where target_id = any(@ids) and resolved_at is null group by 1, 2",
            new { ids }, r => (Id: r.GetGuid(0), Reason: r.GetString(1), Count: r.GetInt32(2), Notes: r.GetFieldValue<string[]>(3)), ct);
        var byItem = reports.ToLookup(r => r.Id);
        var people = await users.GetManyAsync(mine.Select(r => r.Author).Where(a => a != Guid.Empty).Distinct().ToList(), ct);
        var named = mine.Select(r => new ModerationItem(r.Type, r.Id, r.Post, r.Community, standings[r.Community]!.Community.Names,
            r.Author == Guid.Empty ? null : people.TryGetValue(r.Author, out var u) ? new Person(r.Author, u.DisplayName, u.Status == "active") : new Person(r.Author, "", false),
            r.Text, r.Body, r.Status, r.Held,
            byItem[r.Id].OrderBy(x => x.Reason, StringComparer.Ordinal).Select(x => new ReportCount(x.Reason, x.Count)).ToList(),
            byItem[r.Id].SelectMany(x => x.Notes).ToList(), r.At));
        return named.Concat(anonymous).OrderBy(i => i.CreatedAt).ThenBy(i => i.Id).ToList();
    }

    private async Task<(Target Target, Standing Standing)> ModeratedAsync(string type, Guid id, CancellationToken ct)
    {
        var t = await TargetAsync(type, id, ct);
        if (t.Status is ContentStatus.Deleted or ContentStatus.Removed || (type == "comment" && t.Post.Status != ContentStatus.Published))
        {
            throw AppException.NotFound();
        }
        var c = await communities.RowAsync(t.Post.CommunityId, ct) ?? throw AppException.NotFound();
        var s = await communities.StandingAsync(c, ct);
        if (!s.Reads)
        {
            throw AppException.NotFound();
        }
        if (!s.Moderates)
        {
            throw AppException.Denied();
        }
        return (t, s);
    }

    /// <summary>
    /// Publishes a held or hidden item, or keeps a reported one, and closes its reports. A post held by the keyword
    /// filter is published as new: the people it mentions are told then.
    /// </summary>
    public async Task ApproveAsync(string type, Guid id, CancellationToken ct)
    {
        var (t, s) = await ModeratedAsync(type, id, ct);
        var was = t.Status;
        if (!Live(t))
        {
            var live = type == "post" ? ContentStatus.Published : ContentStatus.Visible;
            await session.ExecAsync($"update engagement.{type} set status = @live, held_reason = null, pending_mentions = null where id = @id", new { live, id }, ct);
            var mentioned = t.PendingMentions ?? [];
            if (type == "post")
            {
                // A post that was never shown (keyword filter or Speak Up review) is published as new.
                if (t.HeldReason is "keyword" or "speak_up")
                {
                    await session.ExecAsync("update engagement.post set published_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
                    await posts.MentionAsync(s.Community, id, null, mentioned, ct);
                    await outbox.EnqueueAsync(new PostPublished(id, t.Post.CommunityId, t.Post.ChannelId), ct);
                }
                await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, id), ct);
            }
            else if (t.HeldReason == "keyword")
            {
                await posts.PublishCommentAsync(s.Community, t.Post, id, t.AuthorId, mentioned, ct);
            }
            else
            {
                await ChangeCommentCountAsync(t, +1, ct);
            }
        }
        await ResolveReportsAsync(t, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentApproved, type, id.ToString(),
            Changes: new { community = t.Post.CommunityId, post = t.Post.Id, from = was }), ct);
    }

    /// <summary>
    /// Removes an item with a reason (FR-020): the author is told and reads the reason on the item, the decision is
    /// audited, and a removed post leaves search through the outbox. Removing a post removes its comments with it.
    /// </summary>
    public async Task RemoveAsync(string type, Guid id, RemoveRequest request, CancellationToken ct)
    {
        var (t, _) = await ModeratedAsync(type, id, ct);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxNote || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            throw AppException.Validation("reason", "validation.engagement.removal_reason");
        }
        var user = context.RequireUser();
        var now = clock.UtcNow;
        if (type == "comment" && t.Status == ContentStatus.Visible)
        {
            await ChangeCommentCountAsync(t, -1, ct);
        }
        await session.ExecAsync(
            $"update engagement.{type} set status = 'removed', held_reason = null, pending_mentions = null, removed_reason = @reason, removed_by = @user, removed_at = @now where id = @id",
            new { id, reason, user, now }, ct);
        await ResolveReportsAsync(t, ct);
        if (type == "post")
        {
            await session.ExecAsync("update engagement.post set pinned = false where id = @id", new { id }, ct);
            await outbox.EnqueueAsync(new PostRemoved(id, t.Post.CommunityId), ct);
            await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, id), ct);
        }
        // An anonymous post has no author to record or tell (FR-005).
        var anonymous = t.AuthorId == Guid.Empty;
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentRemoved, type, id.ToString(),
            Changes: new { community = t.Post.CommunityId, post = t.Post.Id, author = anonymous ? (Guid?)null : t.AuthorId, reason }), ct);
        if (!anonymous)
        {
            await notifications.SendAsync(new NotificationRequest([t.AuthorId], NotificationCategories.System, RemovedTemplate, Posts.Link(t.Post.CommunityId, t.Post.Id),
                DedupeKey: $"engagement.removed:{id:N}"), ct);
        }
    }

    // ---- Mutes ----

    private async Task<Standing> ModeratorOfAsync(Guid communityId, CancellationToken ct)
    {
        var s = await communities.ReadableAsync(communityId, ct);
        if (!s.Moderates)
        {
            throw AppException.Denied();
        }
        return s;
    }

    public async Task<IReadOnlyList<MuteView>> MutesAsync(Guid communityId, CancellationToken ct)
    {
        await ModeratorOfAsync(communityId, ct);
        var rows = await session.QueryAsync("select user_id, until, reason from engagement.mute where community_id = @communityId and until > @now order by until",
            new { communityId, now = clock.UtcNow }, r => (User: r.GetGuid(0), Until: r.GetFieldValue<DateTimeOffset>(1), Reason: r.Get<string?>("reason")), ct);
        var people = await users.GetManyAsync(rows.Select(r => r.User).ToList(), ct);
        return rows.Select(r => new MuteView(r.User, people.TryGetValue(r.User, out var u) ? u.DisplayName : "", r.Until, r.Reason)).ToList();
    }

    /// <summary>
    /// Mutes a member in one community until a time within a year (FR-021). Owners are not muted (an owner settles it
    /// with another owner), and nobody mutes themselves.
    /// </summary>
    public async Task<IReadOnlyList<MuteView>> MuteAsync(Guid communityId, MuteRequest request, CancellationToken ct)
    {
        await ModeratorOfAsync(communityId, ct);
        var now = clock.UtcNow;
        var errors = new ValidationErrors();
        var known = await users.GetManyAsync([request.UserId], ct);
        if (!known.ContainsKey(request.UserId))
        {
            errors.Add("userId", "validation.engagement.user");
        }
        else if (request.UserId == context.RequireUser())
        {
            errors.Add("userId", "validation.engagement.mute_self");
        }
        else if (await session.ScalarAsync<bool>("select exists (select 1 from engagement.community_member where community_id = @communityId and user_id = @u and role = 'owner' and status = 'active')",
                     new { communityId, u = request.UserId }, ct))
        {
            errors.Add("userId", "validation.engagement.mute_owner");
        }
        if (request.Until is not { } until || until <= now || until - now > MaxMute)
        {
            errors.Add("until", "validation.engagement.mute_until");
        }
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is not null && (reason.Length > MaxNote || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
        {
            errors.Add("reason", "validation.engagement.mute_reason");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into engagement.mute (tenant_id, community_id, user_id, until, reason, created_by, created_at)
            values (platform.current_tenant_id(), @communityId, @u, @until, @reason, @by, @now)
            on conflict (tenant_id, community_id, user_id) do update set until = excluded.until, reason = excluded.reason, created_by = excluded.created_by, created_at = excluded.created_at
            """, new { communityId, u = request.UserId, until = request.Until, reason, by = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementMemberMuted, "community", communityId.ToString(),
            Changes: new { member = request.UserId, until = request.Until, reason }), ct);
        await notifications.SendAsync(new NotificationRequest([request.UserId], NotificationCategories.System, MutedTemplate, $"/communities/{communityId}",
            DedupeKey: $"engagement.muted:{communityId:N}:{request.UserId:N}:{request.Until!.Value.UtcTicks}"), ct);
        return await MutesAsync(communityId, ct);
    }

    public async Task<IReadOnlyList<MuteView>> UnmuteAsync(Guid communityId, Guid userId, CancellationToken ct)
    {
        await ModeratorOfAsync(communityId, ct);
        if (await session.ExecAsync("delete from engagement.mute where community_id = @communityId and user_id = @userId", new { communityId, userId }, ct) > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementMemberUnmuted, "community", communityId.ToString(), Changes: new { member = userId }), ct);
        }
        return await MutesAsync(communityId, ct);
    }
}
