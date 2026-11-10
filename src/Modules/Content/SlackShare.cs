using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>A link to share: the channel and an optional note above the item's title.</summary>
public sealed record SlackShareRequest(string? ChannelId, string? Note);

public sealed record SlackChannelsView(IReadOnlyList<SlackChannel> Channels);

/// <summary>
/// Communicators share a content link to a Slack channel they choose (spec 017 US8 scenario 1). Only a published item
/// that every member of the tenant may read can be shared: Public or Internal and aimed at everyone, because a channel's
/// members are not checked against the item's audience. Only the title, the note and the link leave TeamBeats; the link
/// still needs sign-in. Sharing needs <c>content.publish</c> at the item's site and is audited.
/// </summary>
public sealed class ContentSlackShare(Items items, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, ISlackMessaging slack,
    IAuditWriter audit)
{
    public const int MaxNote = 500;

    public async Task<SlackChannelsView> ChannelsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.ContentPublish, ct))
        {
            throw AppException.Denied();
        }
        return new SlackChannelsView(await slack.ChannelsAsync(ct));
    }

    public async Task ShareAsync(Guid id, SlackShareRequest request, CancellationToken ct)
    {
        var (item, site) = await items.ManagedAsync(id, false, ct);
        if (!await items.HoldsAsync(Permissions.ContentPublish, site, ct))
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        if (request.ChannelId is not { Length: >= 9 and <= 20 } channel || channel[0] != 'C' || !channel.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)))
        {
            errors.Add("channelId", "validation.slack.channel");
            channel = "";
        }
        var note = request.Note?.Trim();
        if (note is { Length: > MaxNote })
        {
            errors.Add("note", "validation.slack.note_too_long");
        }
        errors.ThrowIfAny();
        if (item.Status != ContentStatuses.Published || item.PublishedVersion is not { } published)
        {
            throw AppException.Conflict("conflict.content.share_not_published");
        }
        if (item.Classification is not ("public" or "internal") || !item.Audience.IsEveryone)
        {
            throw AppException.Conflict("conflict.content.share_restricted");
        }
        var version = await items.VersionAsync(item.Id, published, ct);
        var title = version?.Variants.GetValueOrDefault(item.PrimaryLocale)?.Title ?? version?.Variants.Values.FirstOrDefault()?.Title ?? "";
        var text = string.IsNullOrEmpty(note) ? title : $"{note}\n{title}";
        var outcome = await slack.ShareAsync(channel, text, $"/content/read/{item.Id}", $"content-share:{item.Id:N}:{Ids.New():N}", ct);
        switch (outcome)
        {
            case SlackSendOutcome.Sent:
                await audit.WriteAsync(new AuditEntry(AuditActions.ContentShared, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                    Changes: new { to = "slack", channel }), ct);
                return;
            case SlackSendOutcome.Unavailable:
                throw AppException.Conflict("conflict.slack.unavailable");
            case SlackSendOutcome.Failed:
                throw AppException.Conflict("conflict.slack.channel_unavailable");
            default:
                throw AppException.Conflict("conflict.slack.unreachable");
        }
    }
}
