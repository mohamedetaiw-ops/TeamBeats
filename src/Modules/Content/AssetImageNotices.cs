using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Content;

/// <summary>
/// When a brand asset's usage rights end (spec 011 US5 scenario 2), the owner of each page that shows it hears which page
/// it is, so they replace the image. Readers already no longer see it; the page itself is left as it is. Pages without an
/// owner go to whoever created them.
/// </summary>
public sealed class AssetImageNotices(IDbSession session, INotificationSender notifications) : IMessageHandler<AssetRightsExpired>
{
    public const string Template = "content.asset_image_expired";

    public static string EditLink(Guid item) => $"/content/items/{item}";

    public async Task HandleAsync(AssetRightsExpired message, CancellationToken ct)
    {
        var pages = await session.QueryAsync(
            """
            select distinct on (i.id) i.id, coalesce(i.owner_user_id, i.created_by), coalesce(v.variants -> i.primary_locale ->> 'title', '')
            from content.content_item i
            join content.content_version v on v.item_id = i.id and v.number in (i.published_version, i.draft_version)
            where i.status <> 'archived'
              and jsonb_path_exists(v.variants, '$.**.assetId ? (@ == $id)', jsonb_build_object('id', @id::text))
            order by i.id, v.number desc
            limit 500
            """, new { id = message.AssetId }, r => (Item: r.GetGuid(0), Owner: r.GetGuid(1), Title: r.GetString(2)), ct);
        foreach (var page in pages)
        {
            await notifications.SendAsync(new NotificationRequest([page.Owner], NotificationCategories.RequestUpdate, Template, EditLink(page.Item),
                new Dictionary<string, string> { ["title"] = page.Title },
                DedupeKey: $"content:asset_expired:{message.AssetId:N}:{page.Item:N}"), ct);
        }
    }
}
