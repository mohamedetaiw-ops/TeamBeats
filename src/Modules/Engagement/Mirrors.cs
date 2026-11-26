using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public static class MirrorTargets
{
    public const string Slack = "slack";
    public const string Teams = "teams";
}

public sealed record MirrorRequest(string? ChannelId, bool Excerpt = false);

public sealed record MirrorView(string Target, string ChannelId, bool Excerpt, DateTimeOffset CreatedAt, DateTimeOffset? LastSentAt, string? LastFailure,
    DateTimeOffset? LastFailureAt);

/// <summary>
/// A community's mirrors and whether it may be mirrored at all: only an open community of internal classification
/// without a unit or space, whose content every employee may read (FR-030). Slack lists the channels the app can post to.
/// </summary>
public sealed record MirrorsView(bool Eligible, IReadOnlyList<MirrorView> Mirrors, bool SlackAvailable, IReadOnlyList<SlackChannel> SlackChannels, bool TeamsAvailable);

/// <summary>
/// One-way mirroring of a community's new posts to a Slack or Teams channel (spec 023 R3 US10, FR-030). Only engagement
/// administrators (<c>engagement.manage</c> at tenant scope) set mirrors, and only on communities every employee may
/// read, because a channel's members are not checked against the community. Each new published post goes once to each
/// mirror as a short notice: the author, the community and a link that needs sign in, and the first
/// <see cref="ExcerptLength"/> characters of the post only when the mirror allows an excerpt. Anonymous posts are never
/// mirrored. Slack goes through the spec 017 adapter and Teams through the TeamBeats bot (spec 015); a refusal is kept as
/// the mirror's last failure and later posts are still tried. Needs <c>engagement.mirroring</c>.
/// </summary>
public sealed partial class Mirrors(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    Communities communities,
    ISlackMessaging slack,
    ITeamsMessaging teams,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    ILocalizer localizer,
    IAuditWriter audit,
    IClock clock) : IMessageHandler<PostPublished>
{
    public const int ExcerptLength = 200;

    [GeneratedRegex("^C[A-Z0-9]{8,19}$")]
    private static partial Regex SlackChannel();

    [GeneratedRegex(@"^19:[A-Za-z0-9_\-]{1,150}@thread\.(tacv2|skype)$")]
    private static partial Regex TeamsChannel();

    internal static bool Eligible(CommunityRow c) =>
        c.Status == "active" && c.Kind == CommunityKinds.Open && c.OrgUnitId is null && c.SpaceId is null && c.Classification == "internal";

    private async Task<Standing> ManagedAsync(Guid communityId, CancellationToken ct)
    {
        await communities.RequireAsync(ct);
        await entitlements.DemandAsync(Capabilities.EngagementMirroring, ct);
        var s = await communities.VisibleAsync(communityId, ct);
        if (!(await authz.CheckAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        return s;
    }

    private async Task<IReadOnlyList<MirrorView>> ListAsync(Guid communityId, CancellationToken ct) =>
        await session.QueryAsync(
            "select target, channel_id, excerpt, created_at, last_sent_at, last_failure, last_failure_at from engagement.mirror where community_id = @communityId order by target",
            new { communityId }, r => new MirrorView(r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3), r.Get<DateTimeOffset?>("last_sent_at"),
                r.Get<string?>("last_failure"), r.Get<DateTimeOffset?>("last_failure_at")), ct);

    public async Task<MirrorsView> GetAsync(Guid communityId, CancellationToken ct)
    {
        var s = await ManagedAsync(communityId, ct);
        var status = await slack.StatusAsync(context.RequireUser(), ct);
        IReadOnlyList<SlackChannel> channels = [];
        if (status.Available)
        {
            try
            {
                channels = await slack.ChannelsAsync(ct);
            }
            catch (AppException)
            {
                // Slack did not answer: the page still shows the mirrors, and the channel can be typed in.
            }
        }
        return new MirrorsView(Eligible(s.Community), await ListAsync(communityId, ct), status.Available, channels, await teams.AvailableAsync(ct));
    }

    public async Task<MirrorsView> SetAsync(Guid communityId, string target, MirrorRequest request, CancellationToken ct)
    {
        var s = await ManagedAsync(communityId, ct);
        if (target is not (MirrorTargets.Slack or MirrorTargets.Teams))
        {
            throw AppException.NotFound();
        }
        if (!Eligible(s.Community))
        {
            throw AppException.Conflict("conflict.engagement.mirror_not_allowed");
        }
        var channel = request.ChannelId?.Trim() ?? "";
        if (!(target == MirrorTargets.Slack ? SlackChannel() : TeamsChannel()).IsMatch(channel))
        {
            throw AppException.Validation("channelId", target == MirrorTargets.Slack ? "validation.slack.channel" : "validation.engagement.teams_channel");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into engagement.mirror (tenant_id, id, community_id, target, channel_id, excerpt, created_by, created_at)
            values (platform.current_tenant_id(), @id, @communityId, @target, @channel, @excerpt, @user, @now)
            on conflict (tenant_id, community_id, target) do update set channel_id = excluded.channel_id, excerpt = excluded.excerpt,
              created_by = excluded.created_by, created_at = excluded.created_at, last_failure = null, last_failure_at = null
            """, new { id = Ids.New(), communityId, target, channel, excerpt = request.Excerpt, user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementMirrorSet, "community", communityId.ToString(),
            Changes: new { target, channel, excerpt = request.Excerpt }), ct);
        return await GetAsync(communityId, ct);
    }

    public async Task<MirrorsView> RemoveAsync(Guid communityId, string target, CancellationToken ct)
    {
        await ManagedAsync(communityId, ct);
        if (await session.ExecAsync("delete from engagement.mirror where community_id = @communityId and target = @target", new { communityId, target }, ct) > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementMirrorRemoved, "community", communityId.ToString(), Changes: new { target }), ct);
        }
        return await GetAsync(communityId, ct);
    }

    // ---- Sending ----

    /// <summary>Sends a newly published post to the community's mirrors, once each. Runs from the outbox in the post's tenant.</summary>
    public async Task HandleAsync(PostPublished message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.EngagementMirroring, ct))
        {
            return;
        }
        var mirrors = await session.QueryAsync(
            """
            select m.id, m.target, m.channel_id, m.excerpt from engagement.mirror m
            where m.community_id = @community
              and not exists (select 1 from engagement.mirror_post x where x.mirror_id = m.id and x.post_id = @post)
            order by m.target
            """, new { community = message.CommunityId, post = message.PostId },
            r => (Id: r.GetGuid(0), Target: r.GetString(1), Channel: r.GetString(2), Excerpt: r.GetBoolean(3)), ct);
        if (mirrors.Count == 0)
        {
            return;
        }
        var post = await session.QuerySingleOrDefaultAsync(
            "select author_id, text from engagement.post where id = @id and status = 'published' and not anonymous and author_id is not null",
            new { id = message.PostId }, r => (Author: r.GetGuid(0), Text: r.GetString(1)), ct);
        var community = await communities.RowAsync(message.CommunityId, ct);
        if (post == default || community is null || !Eligible(community))
        {
            return;
        }
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var author = (await users.GetManyAsync([post.Author], ct)).TryGetValue(post.Author, out var u) ? u.DisplayName : "";
        var name = community.Names.GetValueOrDefault(locale) ?? community.Names.GetValueOrDefault("en") ?? community.Names.Values.FirstOrDefault() ?? "";
        var title = localizer.Get("engagement.mirror.notice", locale).Replace("{author}", author, StringComparison.Ordinal).Replace("{community}", name, StringComparison.Ordinal);
        var excerpt = post.Text.Length > ExcerptLength ? post.Text[..(ExcerptLength - 1)].TrimEnd() + "…" : post.Text;
        var link = Posts.Link(message.CommunityId, message.PostId);
        foreach (var m in mirrors)
        {
            var text = m.Excerpt ? $"{title}\n{excerpt}" : title;
            string? failure;
            if (m.Target == MirrorTargets.Slack)
            {
                failure = await slack.ShareAsync(m.Channel, text, link, $"engagement-mirror:{m.Id:N}:{message.PostId:N}", ct) switch
                {
                    SlackSendOutcome.Sent => null,
                    SlackSendOutcome.Unavailable => "unavailable",
                    SlackSendOutcome.Failed => "channel_unavailable",
                    _ => "unreachable",
                };
            }
            else
            {
                var card = new TeamsCard(title, m.Excerpt ? excerpt : "", link, localizer.Get("engagement.mirror.open", locale), [], null, Locales.IsRightToLeft(locale));
                failure = await teams.PostToChannelAsync(m.Channel, card, ct) switch
                {
                    TeamsSendOutcome.Sent => null,
                    TeamsSendOutcome.Unavailable => "unavailable",
                    TeamsSendOutcome.NotInstalled or TeamsSendOutcome.NotLinked => "channel_unavailable",
                    _ => "unreachable",
                };
            }
            var now = clock.UtcNow;
            if (failure is null)
            {
                await session.ExecAsync(
                    "insert into engagement.mirror_post (tenant_id, mirror_id, post_id, sent_at) values (platform.current_tenant_id(), @mirror, @post, @now) on conflict do nothing",
                    new { mirror = m.Id, post = message.PostId, now }, ct);
                await session.ExecAsync("update engagement.mirror set last_sent_at = @now where id = @id", new { id = m.Id, now }, ct);
            }
            else
            {
                await session.ExecAsync("update engagement.mirror set last_failure = @failure, last_failure_at = @now where id = @id", new { id = m.Id, failure, now }, ct);
            }
        }
    }
}
