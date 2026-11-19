using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Security;

/// <summary>Alerts in the period and the posture checks weaker than the baseline now, for the analytics security dashboard (spec 021).</summary>
public sealed class SecurityTrendFigures(IDbSession session, PostureService posture) : ISecurityTrendFigures
{
    public async Task<SecurityTrendSummary> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var args = new { from, to };
        var totals = (await session.QueryAsync(
            """
            select (select count(*) from security.alert where created_at >= @from and created_at < @to),
              (select count(*) from security.alert where status = 'open')
            """, args, r => (Alerts: r.GetInt64(0), Open: r.GetInt64(1)), ct))[0];
        var weeks = await session.QueryAsync(
            """
            select to_char(date_trunc('week', created_at at time zone 'UTC'), 'YYYY-MM-DD'), count(*), count(distinct subject_user_id)
            from security.alert where created_at >= @from and created_at < @to group by 1 order by 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var kinds = await session.QueryAsync(
            """
            select kind, count(*), count(distinct subject_user_id) from security.alert
            where created_at >= @from and created_at < @to group by 1 order by 2 desc, 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var checks = (await posture.ChecksAsync(ct)).Where(c => c.State != PostureService.Unavailable).ToList();
        return new SecurityTrendSummary(totals.Alerts, totals.Open, weeks, kinds, [.. checks.Where(c => c.State == PostureService.Failed).Select(c => c.Key)], checks.Count);
    }
}
