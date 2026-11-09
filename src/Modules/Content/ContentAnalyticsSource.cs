using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>
/// Content items for item analytics (spec 021 US1, spec 009). The analytics of an item are for the people who look after
/// it: its author, its owner or backup owner, and those who edit or publish at its site. An item the person may not read
/// is not found; one they read without those rights is denied. The audience is the people the item targets now, and for
/// a critical announcement that asks for confirmation, the counted audience with their confirmations.
/// </summary>
internal sealed class ContentAnalyticsSource(IDbSession session, ITenantContext context, IEntitlementService entitlements, Items items, Sites sites,
    CriticalAnnouncements critical, IAudiences audiences, INotificationSender notifications) : IAnalyticsItemSource
{
    public const string Reminder = "content.analytics.reminder";

    public string Source => "content";

    private async Task<(ItemRow Item, SiteRow Site)> LookedAfterAsync(Guid itemId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        var item = await items.RowAsync(itemId, false, ct);
        if (item is not { Live: true, PublishedVersion: not null } || await sites.RowAsync(item.SiteId, false, ct) is not { } site)
        {
            throw AppException.NotFound();
        }
        var edits = await items.HoldsAsync(Permissions.ContentEdit, site, ct) || await items.HoldsAsync(Permissions.ContentPublish, site, ct);
        var mine = item.CreatedBy == user || item.OwnerId == user || item.BackupOwnerId == user;
        if (mine || edits)
        {
            return (item, site);
        }
        if (await items.MayReadAsync(item, site, ct))
        {
            throw AppException.Denied();
        }
        throw AppException.NotFound();
    }

    public async Task<AnalyticsItem> DescribeAsync(Guid itemId, CancellationToken ct = default)
    {
        var (item, _) = await LookedAfterAsync(itemId, ct);
        var version = await items.VersionAsync(itemId, item.PublishedVersion!.Value, ct);
        var titles = version?.Variants.ToDictionary(v => v.Key, v => v.Value.Title, StringComparer.Ordinal) ?? [];
        var confirms = item.Critical is { Confirms: true };
        IReadOnlySet<Guid> audience;
        IReadOnlySet<Guid> acknowledged = new HashSet<Guid>();
        if (item.Critical is not null)
        {
            var (_, people) = await critical.StatusAsync(itemId, ct);
            audience = people.Select(p => p.UserId).ToHashSet();
            if (confirms)
            {
                acknowledged = people.Where(p => p.At is not null).Select(p => p.UserId).ToHashSet();
            }
        }
        else
        {
            audience = await audiences.MembersAsync(item.Audience, ct);
        }
        var reactions = await session.ScalarAsync<int>("select count(*)::int from content.reaction where item_id = @itemId", new { itemId }, ct);
        var comments = await session.ScalarAsync<int>("select count(*)::int from content.comment where item_id = @itemId and status = @visible",
            new { itemId, visible = CommentStatuses.Visible }, ct);
        return new AnalyticsItem(itemId, titles, item.PublishedAt, audience, confirms, acknowledged, reactions, comments);
    }

    public async Task<int> RemindAsync(Guid itemId, IReadOnlyCollection<Guid> people, CancellationToken ct = default)
    {
        var described = await DescribeAsync(itemId, ct);
        var targets = people.Where(described.Audience.Contains).Distinct().ToArray();
        var item = (await items.RowAsync(itemId, false, ct))!;
        var category = item.Critical is not null ? NotificationCategories.CriticalAnnouncement : NotificationCategories.Announcement;
        var sent = 0;
        foreach (var chunk in targets.Chunk(500))
        {
            var result = await notifications.SendAsync(new NotificationRequest(chunk, category, Reminder, CriticalAnnouncements.ReadLink(itemId),
                DedupeKey: $"content:{itemId:N}:remind"), ct);
            sent += result.Created + result.Grouped;
        }
        return sent;
    }
}
