using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

public static class FollowKinds
{
    public const string Site = "site";
    public const string Topic = "topic";
}

public sealed record FollowRequest(string? Kind, string? Key);

public sealed record FollowView(string Kind, string Key, DateTimeOffset At);

/// <summary>
/// Mentions in published items (spec 009 US9 scenario 2): when a version goes live, each person it mentions for the
/// first time hears of it, but only when they may read the item. People the author could not see were refused when the
/// draft was saved, so a mention never reveals anyone.
/// </summary>
public sealed class ContentMentions(Items items, Sites sites, INotificationSender notifications) : IMessageHandler<ContentPublished>
{
    public const string Mentioned = "content.mention.new";

    public async Task HandleAsync(ContentPublished message, CancellationToken ct)
    {
        if (await items.RowAsync(message.ItemId, false, ct) is not { Status: ContentStatuses.Published } item || item.PublishedVersion != message.Version
            || await sites.RowAsync(item.SiteId, false, ct) is not { } site || await items.VersionAsync(item.Id, message.Version, ct) is not { } version)
        {
            return;
        }
        var people = version.Variants.Values.SelectMany(v => BodyValidator.Mentions(v.Body)).ToHashSet();
        people.Remove(version.Author);
        foreach (var person in people.Order())
        {
            if (await items.UserMayReadAsync(person, item, site, ct))
            {
                await notifications.SendAsync(new NotificationRequest([person], NotificationCategories.Mention, Mentioned, CriticalAnnouncements.ReadLink(item.Id),
                    DedupeKey: $"content:mention:{item.Id:N}:{person:N}"), ct);
            }
        }
    }
}

/// <summary>
/// Following sites and topics (FR-017). A person follows active sites they work in or that reach them, and any topic;
/// the "following" feed then lists permitted items of those sites and tags, with critical items always included, since
/// a preference never hides mandatory content.
/// </summary>
public sealed class ContentFollows(IDbSession session, ITenantContext context, IEntitlementService entitlements, Sites sites, IClock clock)
{
    public const int MaxFollows = 200;

    public async Task<IReadOnlyList<FollowView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        return await session.QueryAsync("select kind, key, created_at from content.follow where user_id = @user order by kind, key", new { user },
            r => new FollowView(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
    }

    private async Task<string> KeyAsync(FollowRequest request, CancellationToken ct)
    {
        switch (request.Kind)
        {
            case FollowKinds.Topic:
                return BodyValidator.NormalizeTag(request.Key?.TrimStart('#')) ?? throw AppException.Validation("key", "validation.content.topic");
            case FollowKinds.Site:
                if (!Guid.TryParse(request.Key, out var id))
                {
                    throw AppException.Validation("key", "validation.content.follow_site");
                }
                var reachable = (await sites.MineAsync(ct)).Any(s => s.Id == id) || (await sites.ListAsync(ct)).Any(s => s.Id == id && s.Status == "active");
                return reachable ? id.ToString() : throw AppException.NotFound();
            default:
                throw AppException.Validation("kind", "validation.content.follow_kind");
        }
    }

    public async Task<FollowView> FollowAsync(FollowRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        var key = await KeyAsync(request, ct);
        if (await session.ScalarAsync<long>("select count(*) from content.follow where user_id = @user", new { user }, ct) >= MaxFollows)
        {
            throw AppException.Conflict("conflict.content.follow_limit");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into content.follow (tenant_id, user_id, kind, key, created_at) values (platform.current_tenant_id(), @user, @kind, @key, @now)
            on conflict do nothing
            """, new { user, kind = request.Kind, key, now }, ct);
        return new FollowView(request.Kind!, key, now);
    }

    public async Task UnfollowAsync(string kind, string key, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        await session.ExecAsync("delete from content.follow where user_id = @user and kind = @kind and key = @key",
            new { user, kind, key = kind == FollowKinds.Topic ? key.ToLowerInvariant() : key }, ct);
    }
}
