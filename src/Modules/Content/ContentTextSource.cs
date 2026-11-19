using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Content;

/// <summary>
/// Visible comments on content for sentiment (spec 021 US8). The topic is the item's first hashtag, else its content
/// type. The author is named so Analytics can count the comment in their unit and location; Analytics keeps neither.
/// </summary>
public sealed class ContentTextSource(IDbSession session) : IAnalyticsTextSource
{
    public string Source => AnalyticsTextSources.ContentComment;

    public async Task<IReadOnlyList<AnalyticsText>> AfterAsync(AnalyticsTextCursor? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select c.created_at, c.id, c.body, c.author_user_id,
              coalesce((select min(t.tag) from content.item_tag t where t.item_id = c.item_id), 'type:' || i.type), i.classification
            from content.comment c join content.content_item i on i.id = c.item_id
            where c.status = 'visible' and (@any = false or (c.created_at, c.id) > (@at, @id))
            order by c.created_at, c.id limit @limit
            """, new { any = after is not null, at = after?.At ?? DateTimeOffset.UnixEpoch, id = after?.Id ?? Guid.Empty, limit },
            r => new AnalyticsText(new AnalyticsTextCursor(r.GetFieldValue<DateTimeOffset>(0), r.GetGuid(1)), r.GetString(2),
                DateOnly.FromDateTime(r.GetFieldValue<DateTimeOffset>(0).UtcDateTime), r.GetGuid(3), null, r.GetString(4), r.GetString(5)), ct);
}
