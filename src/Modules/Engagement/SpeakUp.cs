using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record AnonymousPostRequest(Guid? ChannelId, BodyDocument? Body);

/// <summary>What the anonymous author keeps: the token of the one-time link where they read the moderator's decision.</summary>
public sealed record AnonymousReceipt(string ReplyToken);

public sealed record AnonymousReplyRequest(string? Token);

/// <summary>The moderator's decision as the anonymous author reads it: still waiting, published, answered or declined.</summary>
public sealed record AnonymousReply(string Status, string? Answer, string? Reason, Guid CommunityId, Guid? PostId);

/// <summary>Publishing an anonymous post. A moderator may pass changed words, for example to take out a name (US2 scenario 4).</summary>
public sealed record AnonymousPublishRequest(BodyDocument? Body);

public sealed record AnonymousAnswerRequest(string? Answer);

public sealed record AnonymousDeclineRequest(string? Reason);

/// <summary>
/// Anonymous Speak Up (spec 023 US2, FR-005 to FR-007, FR-025). The person is checked once, to see that they may post in
/// the community and are within today's limit, and is never written down: the anonymous post has no user column, only
/// the day it came in and a random id, and nothing is audited or sent as an event when it is posted. The limit counts by
/// a keyed hash of the person; the key changes every day and the old keys are deleted, so the hashes cannot be traced
/// back later. Every anonymous post goes to the review queue, where a moderator publishes it (with no author, and
/// marked when the moderator changed the words), answers it privately or declines it with a reason. The author reads
/// the decision once through the link shown when they posted. Nobody, community owners included, can see who wrote it.
/// </summary>
public sealed class SpeakUp(
    IDbSession session,
    ITenantContext context,
    Communities communities,
    Posts posts,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int DailyLimit = 5;
    public const int MaxAnswer = 2_000;

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---- Posting ----

    public async Task<AnonymousReceipt> SubmitAsync(Guid communityId, AnonymousPostRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.EngagementSpeakUp, ct);
        var s = await communities.ReadableAsync(communityId, ct);
        if (!s.Posts)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        Posts.ValidateBody(request.Body, errors);
        var channel = await posts.ChannelAsync(communityId, request.ChannelId, ct);
        if (channel is not { Kind: ChannelKinds.SpeakUp, AllowAnonymous: true })
        {
            errors.Add("channelId", "validation.engagement.anonymous_channel");
        }
        errors.ThrowIfAny();
        await CountAsync(context.RequireUser(), ct);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        // A random id, not the time-ordered kind, so the id does not tell when the post came in.
        await session.ExecAsync(
            """
            insert into engagement.anon_post (tenant_id, id, community_id, channel_id, body, text, submitted_on, reply_token_hash)
            values (platform.current_tenant_id(), @id, @communityId, @channel, @body, @text, @today, @hash)
            """, new
            {
                id = Guid.NewGuid(), communityId, channel = channel!.Value.Id, body = EngagementJson.Jsonb(request.Body), text = BodyValidator.PlainText(request.Body!),
                today = Today, hash = Hash(token),
            }, ct);
        return new AnonymousReceipt(token);
    }

    /// <summary>
    /// Counts today's anonymous post for this person by a keyed hash (FR-006). A new day gets a new random key and the
    /// keys and counts of earlier days are deleted, so a stored hash cannot be matched to a person afterwards.
    /// </summary>
    private async Task CountAsync(Guid user, CancellationToken ct)
    {
        var today = Today;
        await session.ExecAsync("delete from engagement.anon_limit where day < @today", new { today }, ct);
        await session.ExecAsync("delete from engagement.anon_key where day < @today", new { today }, ct);
        await session.ExecAsync("insert into engagement.anon_key (tenant_id, day, key) values (platform.current_tenant_id(), @today, @key) on conflict do nothing",
            new { today, key = RandomNumberGenerator.GetBytes(32) }, ct);
        var key = (await session.QueryAsync("select key from engagement.anon_key where day = @today", new { today }, r => (byte[])r.GetValue(0), ct))[0];
        var hash = Convert.ToHexString(HMACSHA256.HashData(key, user.ToByteArray()));
        var count = (await session.QueryAsync(
            """
            insert into engagement.anon_limit (tenant_id, day, hash, count) values (platform.current_tenant_id(), @today, @hash, 1)
            on conflict (tenant_id, day, hash) do update set count = engagement.anon_limit.count + 1
            returning count
            """, new { today, hash }, r => r.GetInt32(0), ct))[0];
        if (count > DailyLimit)
        {
            throw AppException.Validation("body", "validation.engagement.anonymous_limit");
        }
    }

    /// <summary>
    /// The decision as the author reads it. Once a decision has been shown the link stops working, so a link that is
    /// found later tells nothing; while the post waits, the link can be opened again.
    /// </summary>
    public async Task<AnonymousReply> ReplyAsync(AnonymousReplyRequest request, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 100)
        {
            throw AppException.NotFound();
        }
        var hash = Hash(request.Token.Trim());
        var rows = await session.QueryAsync(
            "select id, status, answer, decline_reason, community_id, post_id from engagement.anon_post where reply_token_hash = @hash", new { hash },
            r => (Id: r.GetGuid(0), Status: r.GetString(1), Answer: r.Get<string?>("answer"), Reason: r.Get<string?>("decline_reason"), Community: r.GetGuid(4),
                Post: r.Get<Guid?>("post_id")), ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var row = rows[0];
        if (row.Status != "pending")
        {
            await session.ExecAsync("update engagement.anon_post set reply_token_hash = null where id = @id", new { id = row.Id }, ct);
        }
        return new AnonymousReply(row.Status, row.Answer, row.Reason, row.Community, row.Post);
    }

    // ---- The review queue ----

    internal async Task<IReadOnlyList<ModerationItem>> QueueAsync(Guid? communityId, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select id, community_id, body::text, text, submitted_on from engagement.anon_post
            where status = 'pending' and (@community::uuid is null or community_id = @community)
            order by submitted_on, id limit 500
            """, new { community = communityId },
            r => (Id: r.GetGuid(0), Community: r.GetGuid(1), Body: EngagementJson.Read<BodyDocument>(r.GetString(2)), Text: r.GetString(3),
                Day: r.GetFieldValue<DateOnly>(4)), ct);
        var standings = new Dictionary<Guid, Standing?>();
        foreach (var id in rows.Select(r => r.Community).Distinct())
        {
            var c = await communities.RowAsync(id, ct);
            standings[id] = c is null ? null : await communities.StandingAsync(c, ct);
        }
        return rows.Where(r => standings[r.Community] is { Moderates: true })
            .Select(r => new ModerationItem("anonymous", r.Id, Guid.Empty, r.Community, standings[r.Community]!.Community.Names, null, r.Text, r.Body,
                "held", "speak_up", [], [], new DateTimeOffset(r.Day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)))
            .ToList();
    }

    private async Task<(Guid Id, Guid CommunityId, Guid ChannelId, BodyDocument Body, Standing Standing)> PendingAsync(Guid id, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        var rows = await session.QueryAsync("select community_id, channel_id, body::text from engagement.anon_post where id = @id and status = 'pending'", new { id },
            r => (Community: r.GetGuid(0), Channel: r.GetGuid(1), Body: EngagementJson.Read<BodyDocument>(r.GetString(2))), ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var c = await communities.RowAsync(rows[0].Community, ct) ?? throw AppException.NotFound();
        var s = await communities.StandingAsync(c, ct);
        if (!s.Reads)
        {
            throw AppException.NotFound();
        }
        if (!s.Moderates)
        {
            throw AppException.Denied();
        }
        return (id, rows[0].Community, rows[0].Channel, rows[0].Body, s);
    }

    /// <summary>
    /// Publishes an anonymous post into its channel with no author. If the moderator changed the words, the post says
    /// so. The words are then kept only on the post; the queue row keeps the decision for the author's link.
    /// </summary>
    public async Task<Guid> PublishAsync(Guid id, AnonymousPublishRequest request, CancellationToken ct)
    {
        var item = await PendingAsync(id, ct);
        var body = item.Body;
        var edited = false;
        if (request.Body is not null)
        {
            var errors = new ValidationErrors();
            Posts.ValidateBody(request.Body, errors);
            errors.ThrowIfAny();
            edited = EngagementJson.Write(request.Body) != EngagementJson.Write(item.Body);
            body = request.Body;
        }
        var postId = Ids.New();
        var text = BodyValidator.PlainText(body);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into engagement.post (tenant_id, id, community_id, channel_id, author_id, anonymous, edited_by_moderator, body, text, published_at, status)
            values (platform.current_tenant_id(), @postId, @community, @channel, null, true, @edited, @body, @text, @now, 'published')
            """, new { postId, community = item.CommunityId, channel = item.ChannelId, edited, body = EngagementJson.Jsonb(body), text, now }, ct);
        await posts.SaveHashtagsAsync(postId, text, ct);
        await session.ExecAsync(
            "update engagement.anon_post set status = 'published', post_id = @postId, body = null, text = null, decided_at = @now where id = @id", new { id, postId, now }, ct);
        await outbox.EnqueueAsync(new PostPublished(postId, item.CommunityId, item.ChannelId), ct);
        await outbox.EnqueueAsync(new Search.Contracts.SearchItemChanged(EngagementSearchSource.Name, postId), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementContentApproved, "anonymous", id.ToString(),
            Changes: new { community = item.CommunityId, post = postId, edited }), ct);
        return postId;
    }

    /// <summary>Answers an anonymous post privately: the author reads the answer through their link, and nothing is published.</summary>
    public async Task AnswerAsync(Guid id, AnonymousAnswerRequest request, CancellationToken ct)
    {
        var item = await PendingAsync(id, ct);
        var answer = request.Answer?.Trim();
        if (string.IsNullOrEmpty(answer) || answer.Length > MaxAnswer || answer.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            throw AppException.Validation("answer", "validation.engagement.anonymous_answer");
        }
        await session.ExecAsync("update engagement.anon_post set status = 'answered', answer = @answer, body = null, text = null, decided_at = @now where id = @id",
            new { id, answer, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementSpeakUpAnswered, "anonymous", id.ToString(), Changes: new { community = item.CommunityId }), ct);
    }

    /// <summary>Declines an anonymous post with a reason the author reads through their link.</summary>
    public async Task DeclineAsync(Guid id, AnonymousDeclineRequest request, CancellationToken ct)
    {
        var item = await PendingAsync(id, ct);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > Moderation.MaxNote || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            throw AppException.Validation("reason", "validation.engagement.removal_reason");
        }
        await session.ExecAsync(
            "update engagement.anon_post set status = 'declined', decline_reason = @reason, body = null, text = null, decided_at = @now where id = @id",
            new { id, reason, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementSpeakUpDeclined, "anonymous", id.ToString(), Changes: new { community = item.CommunityId, reason }), ct);
    }
}
