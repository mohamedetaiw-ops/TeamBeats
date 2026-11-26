using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record RoomRequest(Dictionary<string, string>? Names);

public sealed record RoomMessageRequest(string? Body, IReadOnlyList<Guid>? Mentions);

/// <summary>What the viewer may do in a room; worked out on the server so the page only shows it.</summary>
/// <summary>What the reader may do in a room; <see cref="CanSummarize"/> is set on the room itself for a reader with the AI assistant.</summary>
public sealed record RoomRights(bool CanWrite, bool CanArchive, bool CanModerate, bool CanSummarize = false);

public sealed record RoomView(Guid Id, Guid CommunityId, IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> CommunityNames, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? LastMessageAt, RoomRights Rights);

public sealed record RoomMessageView(Guid Id, Guid RoomId, Person Author, string Body, DateTimeOffset CreatedAt, string Status, string? HeldReason, string? RemovedReason,
    bool Mine, bool CanRemove, bool CanReport);

/// <summary>A page of messages, oldest first. <see cref="Cursor"/> is where the next read starts for new messages.</summary>
public sealed record RoomMessagePage(IReadOnlyList<RoomMessageView> Items, string? Cursor);

public sealed record RoomMessageSaved(RoomMessageView Message, IReadOnlyList<Person> NotReachable);

/// <summary>
/// Wakes the live views of a room on this instance when a message is saved, so they read at once instead of at their
/// next poll. Views on other instances read at their next poll; nothing is shared between instances.
/// </summary>
public sealed class RoomSignals
{
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Room), TaskCompletionSource> waiting = new();

    public Task WaitAsync(Guid tenant, Guid room, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = waiting.GetOrAdd((tenant, room), _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        return Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
    }

    public void Signal(Guid tenant, Guid room)
    {
        if (waiting.TryRemove((tenant, room), out var tcs))
        {
            tcs.TrySetResult();
        }
    }
}

/// <summary>
/// Discussion rooms with live chat inside a community (spec 023 R3 US9, FR-027 to FR-029). A room follows its
/// community: whoever reads the community reads the room, whoever may post writes in it, muted members read only, and
/// the community's moderators moderate it. Owners and engagement administrators (who manage the community) open and
/// archive rooms. Messages follow the comment rules: mentions reach only people who can read the community, the
/// keyword filter holds a message for a moderator, three reports from different people hide it, and a moderator
/// removes it with a reason the author reads. Needs <c>engagement.rooms</c>.
/// </summary>
public sealed class Rooms(
    IDbSession session,
    EngagementActivity activity,
    ITenantContext context,
    Communities communities,
    IUserDirectory users,
    INotificationSender notifications,
    IAuditWriter audit,
    KeywordFilters keywords,
    IEntitlementService entitlements,
    RoomSignals signals,
    ITenantSettingsReader tenantSettings,
    IClock clock)
{
    public const int MaxRooms = 20;
    public const int MaxNameLength = Communities.MaxNameLength;
    public const int MaxMessageCharacters = 2_000;
    public const int PageSize = 100;
    public const string MentionTemplate = "engagement.room.mention";
    public const string RemovedTemplate = "engagement.content.removed";

    public static string Link(Guid communityId, Guid roomId) => $"/communities/{communityId}/rooms/{roomId}";

    private sealed record RoomRow(Guid Id, Guid CommunityId, Dictionary<string, string> Names, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastMessageAt);

    private sealed record MessageRow(Guid Id, Guid RoomId, Guid AuthorId, string Body, DateTimeOffset CreatedAt, string Status, string? HeldReason, string? RemovedReason,
        Guid[]? PendingMentions);

    private const string RoomColumns = "id, community_id, names::text, status, created_at, last_message_at";

    private const string MessageColumns = "id, room_id, author_id, body, created_at, status, held_reason, removed_reason, pending_mentions";

    private static RoomRow ReadRoom(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), EngagementJson.Read<Dictionary<string, string>>(r.GetString(2)),
        r.GetString(3), r.GetFieldValue<DateTimeOffset>(4), r.Get<DateTimeOffset?>("last_message_at"));

    private static MessageRow ReadMessage(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4),
        r.GetString(5), r.Get<string?>("held_reason"), r.Get<string?>("removed_reason"), r.IsDBNull(8) ? null : r.GetFieldValue<Guid[]>(8));

    private async Task RequireAsync(CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        await entitlements.DemandAsync(Capabilities.EngagementRooms, ct);
    }

    private async Task<(RoomRow Room, Standing Standing)> ReadableAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var room = await session.QuerySingleOrDefaultAsync($"select {RoomColumns} from engagement.room where id = @id", new { id }, ReadRoom, ct)
            ?? throw AppException.NotFound();
        return (room, await communities.ReadableAsync(room.CommunityId, ct));
    }

    private static bool Writes(RoomRow room, Standing s) => room.Status == "active" && s.Posts;

    private RoomView View(RoomRow r, Standing s) => new(r.Id, r.CommunityId, r.Names, s.Community.Names, r.Status, r.CreatedAt, r.LastMessageAt,
        new RoomRights(Writes(r, s), CanArchive: r.Status == "active" && s.Manages, CanModerate: s.Moderates));

    // ---- Rooms ----

    public async Task<IReadOnlyList<RoomView>> ListAsync(Guid communityId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var s = await communities.ReadableAsync(communityId, ct);
        var rows = await session.QueryAsync(
            $"select {RoomColumns} from engagement.room where community_id = @communityId order by status, coalesce(last_message_at, created_at) desc, id",
            new { communityId }, ReadRoom, ct);
        return rows.Select(r => View(r, s)).ToList();
    }

    public async Task<RoomView> GetAsync(Guid id, CancellationToken ct)
    {
        var (room, s) = await ReadableAsync(id, ct);
        var view = View(room, s);
        return await entitlements.IsEnabledAsync(Capabilities.AiAssistant, ct) ? view with { Rights = view.Rights with { CanSummarize = true } } : view;
    }

    public async Task<RoomView> CreateAsync(Guid communityId, RoomRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var s = await communities.VisibleAsync(communityId, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        if (s.Community.Status != "active")
        {
            throw AppException.Conflict("conflict.engagement.archived");
        }
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        if (request.Names is not { Count: > 0 } given || given.Keys.Any(l => !enabled.Contains(l))
            || given.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > MaxNameLength || n.Any(char.IsControl)))
        {
            throw AppException.Validation("names", "validation.engagement.names");
        }
        var names = given.ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
        var open = await session.ScalarAsync<int>("select count(*)::int from engagement.room where community_id = @communityId and status = 'active'", new { communityId }, ct);
        if (open >= MaxRooms)
        {
            throw AppException.Conflict("conflict.engagement.room_limit");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into engagement.room (tenant_id, id, community_id, names, created_by, created_at)
            values (platform.current_tenant_id(), @id, @communityId, @names::jsonb, @user, @now)
            """, new { id, communityId, names = EngagementJson.Write(names), user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementRoomCreated, "room", id.ToString(), TargetOrgUnitId: s.Community.OrgUnitId,
            Changes: new { community = communityId, names }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>An archived room stays readable and takes no new messages.</summary>
    public async Task<RoomView> ArchiveAsync(Guid id, CancellationToken ct)
    {
        var (room, s) = await ReadableAsync(id, ct);
        if (!s.Manages)
        {
            throw AppException.Denied();
        }
        if (room.Status == "active")
        {
            await session.ExecAsync("update engagement.room set status = 'archived' where id = @id", new { id }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementRoomArchived, "room", id.ToString(), TargetOrgUnitId: s.Community.OrgUnitId,
                Changes: new { community = room.CommunityId }), ct);
        }
        return await GetAsync(id, ct);
    }

    // ---- Messages ----

    public static string Cursor(DateTimeOffset at, Guid id) => $"{at.UtcTicks.ToString(CultureInfo.InvariantCulture)}_{id:N}";

    private static (DateTimeOffset At, Guid Id)? ParseCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return null;
        }
        var parts = cursor.Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && Guid.TryParseExact(parts[1], "N", out var id)
            && ticks is > 0 and < 3155378975999999999
            ? (new DateTimeOffset(ticks, TimeSpan.Zero), id)
            : throw AppException.Validation("cursor", "validation.engagement.cursor");
    }

    private async Task<List<RoomMessageView>> ViewsAsync(IReadOnlyList<MessageRow> rows, Standing s, CancellationToken ct)
    {
        var user = context.RequireUser();
        var found = await users.GetManyAsync(rows.Select(r => r.AuthorId).Distinct().ToList(), ct);
        return rows.Select(r =>
        {
            var mine = r.AuthorId == user;
            var author = found.TryGetValue(r.AuthorId, out var u) ? new Person(r.AuthorId, u.DisplayName, u.Status == "active") : new Person(r.AuthorId, "", false);
            return new RoomMessageView(r.Id, r.RoomId, author, r.Body, r.CreatedAt, r.Status, r.HeldReason, mine ? r.RemovedReason : null, mine,
                CanRemove: mine && r.Status != ContentStatus.Removed, CanReport: r.Status == ContentStatus.Visible && !mine && s.Participates);
        }).ToList();
    }

    /// <summary>
    /// Messages after the cursor, oldest first; without a cursor the latest page. Readers see visible messages, and an
    /// author also sees their own held, hidden or removed ones (with the reason) so they know why others do not.
    /// </summary>
    public async Task<RoomMessagePage> MessagesAsync(Guid roomId, string? after, CancellationToken ct)
    {
        var (_, s) = await ReadableAsync(roomId, ct);
        var from = ParseCursor(after);
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            from is null
                ? $"""
                  select * from (select {MessageColumns} from engagement.room_message
                    where room_id = @roomId and (status = 'visible' or (author_id = @user and status in ('held', 'hidden', 'removed')))
                    order by created_at desc, id desc limit {PageSize}) m order by created_at, id
                  """
                : $"""
                  select {MessageColumns} from engagement.room_message
                  where room_id = @roomId and (status = 'visible' or (author_id = @user and status in ('held', 'hidden', 'removed')))
                    and (created_at, id) > (@at, @id)
                  order by created_at, id limit {PageSize}
                  """,
            new { roomId, user, at = from?.At ?? DateTimeOffset.UnixEpoch, id = from?.Id ?? Guid.Empty }, ReadMessage, ct);
        var cursor = rows.Count > 0 ? Cursor(rows[^1].CreatedAt, rows[^1].Id) : after;
        return new RoomMessagePage(await ViewsAsync(rows, s, ct), cursor);
    }

    public async Task<RoomMessageSaved> SendAsync(Guid roomId, RoomMessageRequest request, CancellationToken ct)
    {
        var (room, s) = await ReadableAsync(roomId, ct);
        if (!Writes(room, s))
        {
            throw room.Status != "active" ? AppException.Conflict("conflict.engagement.room_archived") : AppException.Denied();
        }
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body))
        {
            errors.Add("body", "validation.engagement.body_required");
        }
        else if (body.Length > MaxMessageCharacters || body.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
        {
            errors.Add("body", "validation.engagement.body_too_long");
        }
        var mentioned = (request.Mentions ?? []).Distinct().Where(m => m != user).ToList();
        if (mentioned.Count > Posts.MaxMentions)
        {
            errors.Add("mentions", "validation.engagement.mentions");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        // Microsecond time, as Postgres keeps it, so the cursor of this message matches the stored row.
        var now = new DateTimeOffset(clock.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var held = await keywords.MatchesAsync(body!, ct);
        await session.ExecAsync(
            """
            insert into engagement.room_message (tenant_id, id, room_id, author_id, body, created_at, status, held_reason, pending_mentions)
            values (platform.current_tenant_id(), @id, @roomId, @user, @body, @now, @status, @heldReason, @pending)
            """, new
            {
                id, roomId, user, body, now, status = held ? ContentStatus.Held : ContentStatus.Visible, heldReason = held ? "keyword" : null,
                pending = held ? mentioned.ToArray() : null,
            }, ct);
        IReadOnlyList<Person> missed = held ? [] : await PublishAsync(s.Community, room, id, mentioned, now, ct);
        await activity.ActedAsync("room_message", room.CommunityId, ct);
        var tenant = context.TenantId!.Value;
        session.OnCommitted(() => signals.Signal(tenant, roomId));
        var view = (await ViewsAsync([new MessageRow(id, roomId, user, body!, now, held ? ContentStatus.Held : ContentStatus.Visible, held ? "keyword" : null, null, null)], s, ct))[0];
        return new RoomMessageSaved(view, missed);
    }

    /// <summary>What happens when a message becomes visible: the room's last activity and the mentions.</summary>
    private async Task<IReadOnlyList<Person>> PublishAsync(CommunityRow c, RoomRow room, Guid messageId, IReadOnlyList<Guid> mentioned, DateTimeOffset at, CancellationToken ct)
    {
        await session.ExecAsync("update engagement.room set last_message_at = greatest(coalesce(last_message_at, @at), @at) where id = @id", new { id = room.Id, at }, ct);
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
        if (told.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(told, NotificationCategories.Mention, MentionTemplate, Link(c.Id, room.Id),
                DedupeKey: $"engagement.room_mention:{messageId:N}"), ct);
        }
        return missed;
    }

    /// <summary>The author deletes their own message, also while it waits for a moderator.</summary>
    public async Task DeleteAsync(Guid messageId, CancellationToken ct)
    {
        var row = await MessageAsync(messageId, ct);
        if (row is null || row.Status is ContentStatus.Deleted or ContentStatus.Removed)
        {
            throw AppException.NotFound();
        }
        await ReadableAsync(row.RoomId, ct);
        if (row.AuthorId != context.RequireUser())
        {
            throw AppException.Denied();
        }
        await session.ExecAsync("update engagement.room_message set status = 'deleted', pending_mentions = null where id = @messageId", new { messageId }, ct);
        await session.ExecAsync("update engagement.room_report set resolved_at = @now where message_id = @messageId and resolved_at is null",
            new { messageId, now = clock.UtcNow }, ct);
    }

    private async Task<MessageRow?> MessageAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {MessageColumns} from engagement.room_message where id = @id", new { id }, ReadMessage, ct);

    // ---- Moderation (FR-029) ----

    /// <summary>Reports a visible message the viewer can read; the third open report from a different person hides it.</summary>
    public async Task ReportAsync(Guid messageId, ReportRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var m = await MessageAsync(messageId, ct);
        if (m is null || m.Status != ContentStatus.Visible)
        {
            throw AppException.NotFound();
        }
        var (room, s) = await ReadableAsync(m.RoomId, ct);
        var user = context.RequireUser();
        if (!s.Participates || m.AuthorId == user)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        if (request.Reason is not { } reason || !Moderation.ReportReasons.Contains(reason))
        {
            errors.Add("reason", "validation.engagement.report_reason");
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is not null && (note.Length > Moderation.MaxNote || note.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
        {
            errors.Add("note", "validation.engagement.report_note");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into engagement.room_report (tenant_id, id, message_id, room_id, community_id, reporter_id, reason, note, created_at)
            values (platform.current_tenant_id(), @rid, @messageId, @room, @community, @user, @reason, @note, @now)
            on conflict do nothing
            """, new { rid = Ids.New(), messageId, room = room.Id, community = room.CommunityId, user, reason = request.Reason, note, now = clock.UtcNow }, ct);
        var open = await session.ScalarAsync<int>(
            "select count(distinct reporter_id)::int from engagement.room_report where message_id = @messageId and resolved_at is null", new { messageId }, ct);
        if (open < Moderation.HideThreshold)
        {
            return;
        }
        await session.ExecAsync("update engagement.room_message set status = 'hidden', held_reason = 'reports' where id = @messageId", new { messageId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentHidden, "room_message", messageId.ToString(),
            Changes: new { community = room.CommunityId, room = room.Id, reports = open }), ct);
    }

    /// <summary>The room messages waiting for this moderator: held, hidden, or visible with open reports.</summary>
    internal async Task<IReadOnlyList<ModerationItem>> QueueAsync(Guid? communityId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.EngagementRooms, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync(
            """
            select m.id, m.room_id, r.community_id, m.author_id, m.body, m.status, m.held_reason, m.created_at
            from engagement.room_message m join engagement.room r on r.id = m.room_id
            where (@community::uuid is null or r.community_id = @community)
              and (m.status in ('held', 'hidden')
                   or (m.status = 'visible' and exists (select 1 from engagement.room_report x where x.message_id = m.id and x.resolved_at is null)))
            order by m.created_at, m.id
            limit 500
            """, new { community = communityId },
            r => (Id: r.GetGuid(0), Room: r.GetGuid(1), Community: r.GetGuid(2), Author: r.GetGuid(3), Body: r.GetString(4), Status: r.GetString(5),
                Held: r.Get<string?>("held_reason"), At: r.GetFieldValue<DateTimeOffset>(7)), ct);
        var standings = new Dictionary<Guid, Standing?>();
        foreach (var id in rows.Select(r => r.Community).Distinct())
        {
            var c = await communities.RowAsync(id, ct);
            standings[id] = c is null ? null : await communities.StandingAsync(c, ct);
        }
        var mine = rows.Where(r => standings[r.Community] is { Moderates: true }).ToList();
        if (mine.Count == 0)
        {
            return [];
        }
        var ids = mine.Select(r => r.Id).ToArray();
        var reports = await session.QueryAsync(
            "select message_id, reason, count(*)::int, array_remove(array_agg(note order by created_at), null) from engagement.room_report where message_id = any(@ids) and resolved_at is null group by 1, 2",
            new { ids }, r => (Id: r.GetGuid(0), Reason: r.GetString(1), Count: r.GetInt32(2), Notes: r.GetFieldValue<string[]>(3)), ct);
        var byItem = reports.ToLookup(r => r.Id);
        var people = await users.GetManyAsync(mine.Select(r => r.Author).Distinct().ToList(), ct);
        return mine.Select(r => new ModerationItem("message", r.Id, r.Room, r.Community, standings[r.Community]!.Community.Names,
            people.TryGetValue(r.Author, out var u) ? new Person(r.Author, u.DisplayName, u.Status == "active") : new Person(r.Author, "", false),
            r.Body, null, r.Status, r.Held,
            byItem[r.Id].OrderBy(x => x.Reason, StringComparer.Ordinal).Select(x => new ReportCount(x.Reason, x.Count)).ToList(),
            byItem[r.Id].SelectMany(x => x.Notes).ToList(), r.At)).ToList();
    }

    private async Task<(MessageRow Message, RoomRow Room, Standing Standing)> ModeratedAsync(Guid messageId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var m = await MessageAsync(messageId, ct);
        if (m is null || m.Status is ContentStatus.Deleted or ContentStatus.Removed)
        {
            throw AppException.NotFound();
        }
        var (room, s) = await ReadableAsync(m.RoomId, ct);
        if (!s.Moderates)
        {
            throw AppException.Denied();
        }
        return (m, room, s);
    }

    /// <summary>Shows a held or hidden message, or keeps a reported one, and closes its reports. A held message's mentions are told then.</summary>
    public async Task ApproveAsync(Guid messageId, CancellationToken ct)
    {
        var (m, room, s) = await ModeratedAsync(messageId, ct);
        if (m.Status != ContentStatus.Visible)
        {
            await session.ExecAsync("update engagement.room_message set status = 'visible', held_reason = null, pending_mentions = null where id = @messageId", new { messageId }, ct);
            if (m.HeldReason == "keyword")
            {
                await PublishAsync(s.Community, room, messageId, m.PendingMentions ?? [], m.CreatedAt, ct);
            }
        }
        await session.ExecAsync("update engagement.room_report set resolved_at = @now where message_id = @messageId and resolved_at is null",
            new { messageId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentApproved, "room_message", messageId.ToString(),
            Changes: new { community = room.CommunityId, room = room.Id, from = m.Status }), ct);
    }

    /// <summary>Removes a message with a reason (FR-020): the author is told and reads the reason on the message; audited.</summary>
    public async Task RemoveAsync(Guid messageId, RemoveRequest request, CancellationToken ct)
    {
        var (m, room, _) = await ModeratedAsync(messageId, ct);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > Moderation.MaxNote || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            throw AppException.Validation("reason", "validation.engagement.removal_reason");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update engagement.room_message set status = 'removed', held_reason = null, pending_mentions = null, removed_reason = @reason, removed_by = @user, removed_at = @now
            where id = @messageId
            """, new { messageId, reason, user = context.RequireUser(), now }, ct);
        await session.ExecAsync("update engagement.room_report set resolved_at = @now where message_id = @messageId and resolved_at is null", new { messageId, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentRemoved, "room_message", messageId.ToString(),
            Changes: new { community = room.CommunityId, room = room.Id, author = m.AuthorId, reason }), ct);
        await notifications.SendAsync(new NotificationRequest([m.AuthorId], NotificationCategories.System, RemovedTemplate, Link(room.CommunityId, room.Id),
            DedupeKey: $"engagement.removed:{messageId:N}"), ct);
    }
}

/// <summary>
/// Live delivery of a room (FR-028): reads new messages in a fresh scope as the same person every
/// <see cref="Poll"/>, or as soon as a message is saved on this instance, so access is checked again on every read and a
/// person who loses access stops receiving at once. The stream ends after <see cref="Lifetime"/>; the browser then
/// reconnects and its session is checked again.
/// </summary>
public sealed class RoomStream(IServiceScopeFactory scopes, RoomSignals signals)
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public sealed record Snapshot(Guid TenantId, ActorType Actor, Guid UserId, Guid? SessionId, HostScope Host);

    public static Snapshot Of(ITenantContext context) => new(context.TenantId!.Value, context.ActorType, context.RequireUser(), context.SessionId, context.Host);

    /// <summary>The next messages after the cursor, read as the person; null when they may no longer read the room.</summary>
    public async Task<RoomMessagePage?> NextAsync(Snapshot who, Guid roomId, string? cursor, CancellationToken ct)
    {
        await signals.WaitAsync(who.TenantId, roomId, Poll, ct);
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetHost(who.Host);
        accessor.SetTenant(who.TenantId);
        accessor.SetActor(who.Actor, who.UserId, who.SessionId);
        var session = scope.ServiceProvider.GetRequiredService<IDbSession>();
        try
        {
            var page = await scope.ServiceProvider.GetRequiredService<Rooms>().MessagesAsync(roomId, cursor, ct);
            await session.CommitAsync(ct);
            return page;
        }
        catch (AppException)
        {
            return null;
        }
    }
}
