using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// Visible community comments for sentiment (spec 021 US8); the topic is the community's name and its key the community's
/// id, so the community page can show its own trend (spec 023 R3 FR-033).
/// </summary>
public sealed class CommunityTextSource(IDbSession session) : IAnalyticsTextSource
{
    public string Source => AnalyticsTextSources.CommunityComment;

    public async Task<IReadOnlyList<AnalyticsText>> AfterAsync(AnalyticsTextCursor? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select c.created_at, c.id, c.body, c.author_id,
              coalesce(m.names->>'en', (select v from jsonb_each_text(m.names) e(k, v) limit 1), 'community'), m.classification, m.id
            from engagement.comment c
              join engagement.post p on p.id = c.post_id
              join engagement.community m on m.id = p.community_id
            where c.status = 'visible' and (@any = false or (c.created_at, c.id) > (@at, @id))
            order by c.created_at, c.id limit @limit
            """, new { any = after is not null, at = after?.At ?? DateTimeOffset.UnixEpoch, id = after?.Id ?? Guid.Empty, limit },
            r => new AnalyticsText(new AnalyticsTextCursor(r.GetFieldValue<DateTimeOffset>(0), r.GetGuid(1)), r.GetString(2),
                DateOnly.FromDateTime(r.GetFieldValue<DateTimeOffset>(0).UtcDateTime), r.GetGuid(3), null, r.GetString(4), r.GetString(5), r.GetGuid(6).ToString("N")), ct);
}

/// <summary>Visible room messages for sentiment (spec 023 R3 US11 scenario 3); the topic is the room's community.</summary>
public sealed class RoomTextSource(IDbSession session) : IAnalyticsTextSource
{
    public string Source => AnalyticsTextSources.CommunityRoom;

    public async Task<IReadOnlyList<AnalyticsText>> AfterAsync(AnalyticsTextCursor? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select x.created_at, x.id, x.body, x.author_id,
              coalesce(m.names->>'en', (select v from jsonb_each_text(m.names) e(k, v) limit 1), 'community'), m.classification, m.id
            from engagement.room_message x
              join engagement.room o on o.id = x.room_id
              join engagement.community m on m.id = o.community_id
            where x.status = 'visible' and (@any = false or (x.created_at, x.id) > (@at, @id))
            order by x.created_at, x.id limit @limit
            """, new { any = after is not null, at = after?.At ?? DateTimeOffset.UnixEpoch, id = after?.Id ?? Guid.Empty, limit },
            r => new AnalyticsText(new AnalyticsTextCursor(r.GetFieldValue<DateTimeOffset>(0), r.GetGuid(1)), r.GetString(2),
                DateOnly.FromDateTime(r.GetFieldValue<DateTimeOffset>(0).UtcDateTime), r.GetGuid(3), null, r.GetString(4), r.GetString(5), r.GetGuid(6).ToString("N")), ct);
}

/// <summary>
/// Published anonymous Speak Up posts for sentiment (spec 023 R3 US11 scenario 2, FR-032), only while an engagement
/// administrator has let AI read anonymous content. Nothing about the author goes: no person and no unit, so the label
/// counts as one anonymous text. While the setting is off nothing is read and the source waits where it was.
/// </summary>
public sealed class SpeakUpTextSource(IDbSession session) : IAnalyticsTextSource
{
    public string Source => AnalyticsTextSources.SpeakUp;

    public async Task<IReadOnlyList<AnalyticsText>> AfterAsync(AnalyticsTextCursor? after, int limit, CancellationToken ct = default)
    {
        var on = await session.QuerySingleOrDefaultAsync("select anonymous_content from engagement.ai_setting", null, r => (bool?)r.GetBoolean(0), ct) ?? false;
        if (!on)
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select p.published_at, p.id, p.text,
              coalesce(m.names->>'en', (select v from jsonb_each_text(m.names) e(k, v) limit 1), 'community'), m.classification, m.id
            from engagement.post p
              join engagement.community m on m.id = p.community_id
            where p.anonymous and p.status = 'published' and length(p.text) > 0 and (@any = false or (p.published_at, p.id) > (@at, @id))
            order by p.published_at, p.id limit @limit
            """, new { any = after is not null, at = after?.At ?? DateTimeOffset.UnixEpoch, id = after?.Id ?? Guid.Empty, limit },
            r => new AnalyticsText(new AnalyticsTextCursor(r.GetFieldValue<DateTimeOffset>(0), r.GetGuid(1)), r.GetString(2),
                DateOnly.FromDateTime(r.GetFieldValue<DateTimeOffset>(0).UtcDateTime), null, null, r.GetString(3), r.GetString(4), r.GetGuid(5).ToString("N")), ct);
    }
}

/// <summary>
/// Survey text answers for sentiment (spec 021 US8 scenario 2), only once the survey has closed. An anonymous answer
/// carries no person and no time: only the group it already counts in and the day the survey closed. A named answer
/// carries its author. The topic is the survey title. Answers are read in the order of the survey's close, then id.
/// </summary>
public sealed class SurveyTextSource(IDbSession session) : IAnalyticsTextSource
{
    public string Source => AnalyticsTextSources.SurveyAnswer;

    public async Task<IReadOnlyList<AnalyticsText>> AfterAsync(AnalyticsTextCursor? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            """
            select s.closed_at, a.id, a.text, case when s.anonymous then null else r.user_id end, r.group_unit_id, s.title
            from engagement.survey_answer a
              join engagement.survey_response r on r.id = a.response_id
              join engagement.survey s on s.id = r.survey_id
            where s.status = 'closed' and s.closed_at is not null and a.text is not null
              and (@any = false or (s.closed_at, a.id) > (@at, @id))
            order by s.closed_at, a.id limit @limit
            """, new { any = after is not null, at = after?.At ?? DateTimeOffset.UnixEpoch, id = after?.Id ?? Guid.Empty, limit },
            r => new AnalyticsText(new AnalyticsTextCursor(r.GetFieldValue<DateTimeOffset>(0), r.GetGuid(1)), r.GetString(2),
                DateOnly.FromDateTime(r.GetFieldValue<DateTimeOffset>(0).UtcDateTime), r.IsDBNull(3) ? null : r.GetGuid(3), r.IsDBNull(4) ? null : r.GetGuid(4),
                r.GetString(5), "internal"), ct);
}
