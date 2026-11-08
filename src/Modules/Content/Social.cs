using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public sealed record CommentRequest(string? Body);

public sealed record ReactionRequest(string? Kind);

public sealed record CommentView(Guid Id, Guid ItemId, ContentPerson Author, string Body, string Status, DateTimeOffset CreatedAt, bool Mine, bool Reported);

public sealed record CommentPage(IReadOnlyList<CommentView> Items, bool CommentsOn, bool Moderator);

public sealed record ReactionSummary(Guid ItemId, IReadOnlyDictionary<string, int> Counts, string? Mine);

public static class CommentStatuses
{
    public const string Visible = "visible";
    public const string Held = "held";
    public const string Hidden = "hidden";
    public const string Deleted = "deleted";
}

/// <summary>
/// Reactions and comments of spec 009 US9 (R1 part): anyone who may read a live item reacts and, where the site has
/// comments on, comments; the item's owner (or its creator) hears of each new comment (scenario 1). With moderation on,
/// comments from people without <c>content.edit</c> at the site wait for a moderator, and a comment reported by
/// <see cref="ReportsToHide"/> people is hidden until a moderator looks at it (scenario 4). Mentions and hashtags are R2.
/// </summary>
public sealed class ContentSocial(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IUserDirectory users,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock,
    Items items,
    Sites sites)
{
    public const int BodyMax = 2000;
    public const int ReportsToHide = 3;
    public const int PageSize = 200;
    public const string NewComment = "content.comment.new";

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal) { "like", "celebrate", "support", "insightful" };

    /// <summary>A live item the viewer may read; anything else is not found.</summary>
    private async Task<(ItemRow Item, SiteRow Site)> ReadableAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        var item = await items.RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        if (!ContentStatuses.Readable(item.Status) || !await items.MayReadAsync(item, site, ct))
        {
            throw AppException.NotFound();
        }
        return (item, site);
    }

    private async Task<bool> ModeratesAsync(SiteRow site, CancellationToken ct) => (await authz.CheckAsync(Permissions.ContentEdit, site.Target, ct)).Allowed;

    // ---- Reactions ----

    public async Task<ReactionSummary> ReactionsAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        await ReadableAsync(id, ct);
        var rows = await session.QueryAsync("select kind, count(*)::int, bool_or(user_id = @user) from content.reaction where item_id = @id group by kind",
            new { id, user }, r => (Kind: r.GetString(0), Count: r.GetInt32(1), Mine: r.GetBoolean(2)), ct);
        return new ReactionSummary(id, Kinds.Order(StringComparer.Ordinal).ToDictionary(k => k, k => rows.FirstOrDefault(r => r.Kind == k).Count),
            rows.FirstOrDefault(r => r.Mine).Kind);
    }

    /// <summary>One reaction per person and item; "none" takes it back.</summary>
    public async Task<ReactionSummary> ReactAsync(Guid id, ReactionRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        await ReadableAsync(id, ct);
        if (request.Kind == "none")
        {
            await session.ExecAsync("delete from content.reaction where item_id = @id and user_id = @user", new { id, user }, ct);
        }
        else if (request.Kind is { } kind && Kinds.Contains(kind))
        {
            await session.ExecAsync(
                """
                insert into content.reaction (tenant_id, item_id, user_id, kind, at) values (platform.current_tenant_id(), @id, @user, @kind, @now)
                on conflict (tenant_id, item_id, user_id) do update set kind = excluded.kind, at = excluded.at
                """, new { id, user, kind, now = clock.UtcNow }, ct);
        }
        else
        {
            throw AppException.Validation("kind", "validation.content.reaction");
        }
        return await ReactionsAsync(id, ct);
    }

    // ---- Comments ----

    private const string Columns = "id, item_id, author_user_id, body, status, created_at";

    private sealed record CommentRow(Guid Id, Guid ItemId, Guid Author, string Body, string Status, DateTimeOffset CreatedAt);

    private static CommentRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));

    private readonly Dictionary<Guid, ContentPerson> people = [];

    private async Task<ContentPerson> PersonAsync(Guid id, CancellationToken ct)
    {
        if (!people.TryGetValue(id, out var person))
        {
            people[id] = person = new ContentPerson(id, (await users.GetAsync(id, ct))?.DisplayName ?? string.Empty);
        }
        return person;
    }

    /// <summary>Visible comments, oldest first. Authors also see their own held comments; moderators see held and hidden ones with their status.</summary>
    public async Task<CommentPage> CommentsAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (_, site) = await ReadableAsync(id, ct);
        var moderator = await ModeratesAsync(site, ct);
        var rows = await session.QueryAsync(
            $"""
            select {Columns}, exists (select 1 from content.comment_report p where p.tenant_id = c.tenant_id and p.comment_id = c.id and p.user_id = @user)
            from content.comment c
            where item_id = @id and (status = 'visible' or (status = 'held' and author_user_id = @user) or (@moderator and status in ('held', 'hidden')))
            order by created_at, id limit @limit
            """, new { id, user, moderator, limit = PageSize }, r => (Row: Read(r), Reported: r.GetBoolean(6)), ct);
        var list = new List<CommentView>();
        foreach (var (row, reported) in rows)
        {
            list.Add(new CommentView(row.Id, row.ItemId, await PersonAsync(row.Author, ct), row.Body, row.Status, row.CreatedAt, row.Author == user, reported));
        }
        return new CommentPage(list, site.CommentsMode == "on", moderator);
    }

    public async Task<CommentView> CommentAsync(Guid id, CommentRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (item, site) = await ReadableAsync(id, ct);
        if (site.CommentsMode != "on")
        {
            throw AppException.Conflict("conflict.content.comments_off");
        }
        var body = request.Body?.Trim() ?? string.Empty;
        if (body.Length is 0 or > BodyMax)
        {
            throw AppException.Validation("body", "validation.content.comment");
        }
        var status = site.Moderation && !await ModeratesAsync(site, ct) ? CommentStatuses.Held : CommentStatuses.Visible;
        var comment = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into content.comment (tenant_id, id, item_id, author_user_id, body, status, created_at, updated_at)
            values (platform.current_tenant_id(), @comment, @id, @user, @body, @status, @now, @now)
            """, new { comment, id, user, body, status, now }, ct);
        if (status == CommentStatuses.Visible)
        {
            await TellOwnerAsync(item, user, comment, ct);
        }
        return new CommentView(comment, id, await PersonAsync(user, ct), body, status, now, true, false);
    }

    /// <summary>The owner, or the creator when there is none, hears of a new comment; no one is told of their own.</summary>
    private async Task TellOwnerAsync(ItemRow item, Guid author, Guid comment, CancellationToken ct)
    {
        var recipient = item.OwnerId ?? item.CreatedBy;
        if (recipient != author)
        {
            await notifications.SendAsync(new NotificationRequest([recipient], NotificationCategories.RequestUpdate, NewComment,
                $"{CriticalAnnouncements.ReadLink(item.Id)}#comment-{comment}", DedupeKey: $"content:comment:{comment:N}"), ct);
        }
    }

    private async Task<(CommentRow Comment, ItemRow Item, SiteRow Site)> CommentRowAsync(Guid comment, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync($"select {Columns} from content.comment where id = @comment for update", new { comment }, Read, ct)
            ?? throw AppException.NotFound();
        var (item, site) = await ReadableAsync(row.ItemId, ct);
        return (row, item, site);
    }

    /// <summary>A moderator approves a held comment, or hides any comment (scenario 4). Audited.</summary>
    public async Task<CommentView> ModerateAsync(Guid comment, bool approve, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (row, item, site) = await CommentRowAsync(comment, ct);
        if (!await ModeratesAsync(site, ct))
        {
            throw AppException.Denied();
        }
        if (row.Status == CommentStatuses.Deleted || (approve && row.Status == CommentStatuses.Visible) || (!approve && row.Status == CommentStatuses.Hidden))
        {
            throw AppException.Conflict("conflict.content.comment_state");
        }
        var status = approve ? CommentStatuses.Visible : CommentStatuses.Hidden;
        // An approval starts the report count again, so a comment a moderator has looked at needs new reports to hide it.
        await session.ExecAsync(
            "update content.comment set status = @status, report_count = case when @approve then 0 else report_count end, updated_at = @now where id = @comment",
            new { comment, status, approve, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentCommentModerated, "content_comment", comment.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { item = item.Id, from = row.Status, to = status }), ct);
        if (approve && row.Status == CommentStatuses.Held)
        {
            await TellOwnerAsync(item, row.Author, comment, ct);
        }
        return new CommentView(comment, item.Id, await PersonAsync(row.Author, ct), row.Body, status, row.CreatedAt, row.Author == user, false);
    }

    /// <summary>Reports a comment once per person; at <see cref="ReportsToHide"/> reports a visible comment is hidden until a moderator looks at it.</summary>
    public async Task ReportAsync(Guid comment, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (row, _, _) = await CommentRowAsync(comment, ct);
        if (row.Status != CommentStatuses.Visible || row.Author == user)
        {
            throw AppException.Conflict("conflict.content.comment_state");
        }
        var added = await session.ExecAsync(
            "insert into content.comment_report (tenant_id, comment_id, user_id, at) values (platform.current_tenant_id(), @comment, @user, @now) on conflict do nothing",
            new { comment, user, now = clock.UtcNow }, ct);
        if (added > 0)
        {
            await session.ExecAsync(
                """
                update content.comment set report_count = report_count + 1,
                  status = case when report_count + 1 >= @limit and status = 'visible' then 'hidden' else status end, updated_at = @now
                where id = @comment
                """, new { comment, limit = ReportsToHide, now = clock.UtcNow }, ct);
        }
    }

    /// <summary>The author takes their own comment down.</summary>
    public async Task DeleteAsync(Guid comment, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (row, _, _) = await CommentRowAsync(comment, ct);
        if (row.Author != user)
        {
            throw AppException.Denied();
        }
        await session.ExecAsync("update content.comment set status = 'deleted', updated_at = @now where id = @comment", new { comment, now = clock.UtcNow }, ct);
    }
}
