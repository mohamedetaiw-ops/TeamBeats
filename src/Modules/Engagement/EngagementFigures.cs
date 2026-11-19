using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Engagement;

/// <summary>
/// Counts for the analytics engagement dashboard (spec 021 section 2) from published posts, visible comments, reactions,
/// visible recognition, survey responses and poll votes in the period. Removed content is left out.
/// </summary>
public sealed class EngagementFigures(IDbSession session) : IEngagementFigures
{
    public async Task<EngagementSummary> SummaryAsync(DateTimeOffset from, DateTimeOffset to, int limit, CancellationToken ct = default)
    {
        var args = new { from, to, limit = Math.Clamp(limit, 1, 100) };
        const string Activity = """
            select p.community_id, p.author_id, p.published_at as at, 'post' as kind from engagement.post p
            where p.status = 'published' and p.published_at >= @from and p.published_at < @to
            union all
            select p.community_id, c.author_id, c.created_at, 'comment' from engagement.comment c join engagement.post p on p.id = c.post_id
            where c.status = 'visible' and p.status = 'published' and c.created_at >= @from and c.created_at < @to
            """;
        var totals = (await session.QueryAsync(
            $"""
            with a as ({Activity})
            select count(*) filter (where kind = 'post'), count(*) filter (where kind = 'comment'),
              (select count(*) from engagement.reaction where created_at >= @from and created_at < @to),
              (select count(distinct u) from (select author_id as u from a union select user_id from engagement.reaction where created_at >= @from and created_at < @to) x)
            from a
            """, args, r => (Posts: r.GetInt64(0), Comments: r.GetInt64(1), Reactions: r.GetInt64(2), Contributors: r.GetInt64(3)), ct))[0];
        var weeks = await session.QueryAsync(
            $"""
            with a as ({Activity})
            select to_char(date_trunc('week', at at time zone 'UTC'), 'YYYY-MM-DD'), count(*), count(distinct author_id) from a group by 1 order by 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var communities = await session.QueryAsync(
            $"""
            with a as ({Activity})
            select c.id, c.names::text, count(a.*),
              (select count(*) from engagement.community_member m where m.community_id = c.id and m.status = 'active')
            from engagement.community c join a on a.community_id = c.id
            where c.status = 'active'
            group by c.id, c.names::text order by 3 desc, 1 limit @limit
            """, args, r => new GroupFigure(r.GetGuid(0).ToString(), r.GetInt64(2), r.GetInt64(3), EngagementJson.Read<Dictionary<string, string>>(r.GetString(1))), ct);
        const string Given = """
            select r.id, r.value_id, r.created_at, x.user_id from engagement.recognition r join engagement.recognition_recipient x on x.recognition_id = r.id
            where r.status = 'visible' and r.created_at >= @from and r.created_at < @to
            """;
        var recognition = (await session.QueryAsync(
            $"with g as ({Given}) select count(distinct id), count(distinct user_id) from g", args, r => (Count: r.GetInt64(0), People: r.GetInt64(1)), ct))[0];
        var recognitionWeeks = await session.QueryAsync(
            $"""
            with g as ({Given})
            select to_char(date_trunc('week', created_at at time zone 'UTC'), 'YYYY-MM-DD'), count(distinct id), count(distinct user_id) from g group by 1 order by 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var values = await session.QueryAsync(
            $"""
            with g as ({Given})
            select v.key, v.names::text, count(distinct g.id), count(distinct g.user_id)
            from g join engagement.recognition_value v on v.id = g.value_id
            group by v.key, v.names::text, v.position order by 3 desc, v.position, v.key
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(2), r.GetInt64(3), EngagementJson.Read<Dictionary<string, string>>(r.GetString(1))), ct);
        var surveys = await session.QueryAsync(
            """
            select s.id, s.title, count(*) from engagement.survey s join engagement.survey_response r on r.survey_id = s.id
            where s.status in ('open', 'closed') and r.submitted_at >= @from and r.submitted_at < @to
            group by s.id, s.title order by 3 desc, 1 limit @limit
            """, args, r => new GroupFigure(r.GetGuid(0).ToString(), r.GetInt64(2), r.GetInt64(2), Label: r.GetString(1)), ct);
        var polls = (await session.QueryAsync(
            """
            select count(*), coalesce(sum((select count(*) from engagement.poll_voted v where v.poll_id = p.id)), 0)::bigint
            from engagement.poll p where p.created_at >= @from and p.created_at < @to
            """, args, r => (Count: r.GetInt64(0), Voters: r.GetInt64(1)), ct))[0];
        return new EngagementSummary(totals.Posts, totals.Comments, totals.Reactions, totals.Contributors, weeks, communities,
            recognition.Count, recognition.People, recognitionWeeks, values, surveys, polls.Count, polls.Voters);
    }
}
