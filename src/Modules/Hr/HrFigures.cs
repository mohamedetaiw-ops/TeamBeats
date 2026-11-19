using System.Text.Json;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Hr;

/// <summary>
/// Counts for the analytics HR dashboard (spec 021 section 2) from leave, attendance, journey, exit interview, letter,
/// data change and expense records. Withdrawn requests are left out; exit interviews that were declined give no reasons.
/// </summary>
public sealed class HrFigures(IDbSession session, IClock clock) : IHrFigures
{
    public const string Letters = "letters";
    public const string DataChanges = "data_changes";
    public const string Expenses = "expenses";

    private static Dictionary<string, string> Names(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    public async Task<HrSummary> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var args = new { from, to, fromDay = DateOnly.FromDateTime(from.UtcDateTime), toDay = DateOnly.FromDateTime(to.UtcDateTime), today };
        var types = await session.QueryAsync(
            """
            select case when t.sensitive then 'sensitive' else t.key end, case when t.sensitive then null else t.names::text end,
              count(*), count(distinct r.user_id), coalesce(sum(r.days) filter (where r.status = 'approved'), 0)::float8
            from hr.leave_request r join hr.leave_type t on t.id = r.leave_type_id
            where r.created_at >= @from and r.created_at < @to and r.status <> 'withdrawn'
            group by 1, 2 order by 3 desc, 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(1) ? null : Names(r.GetString(1)), r.GetDouble(4)), ct);
        var leave = (await session.QueryAsync(
            """
            select count(*), count(distinct user_id), coalesce(sum(days) filter (where status = 'approved'), 0)::float8
            from hr.leave_request where created_at >= @from and created_at < @to and status <> 'withdrawn'
            """, args, r => (Count: r.GetInt64(0), People: r.GetInt64(1), Days: r.GetDouble(2)), ct))[0];
        var weeks = await session.QueryAsync(
            """
            select to_char(date_trunc('week', start_date), 'YYYY-MM-DD'), count(*), count(distinct user_id), coalesce(sum(days), 0)::float8
            from hr.leave_request where status = 'approved' and start_date >= @fromDay and start_date < @toDay
            group by 1 order by 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2), Amount: r.GetDouble(3)), ct);
        var attendance = await session.QueryAsync(
            """
            select kind, count(*), count(distinct user_id) from hr.attendance_request
            where created_at >= @from and created_at < @to and status <> 'withdrawn' group by 1 order by 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var onboarding = await session.QueryAsync(
            """
            select t.id, t.names::text, count(distinct j.id), count(distinct j.user_id),
              count(s.*) filter (where s.status = 'done'), count(s.*) filter (where s.status = 'done' or (s.status <> 'cancelled' and s.scheduled_on <= @today))
            from hr.journey j join hr.journey_template t on t.id = j.template_id left join hr.journey_step s on s.journey_id = j.id
            where j.kind = 'onboarding' and j.status <> 'cancelled' and j.created_at >= @from and j.created_at < @to
            group by t.id, t.names::text order by 3 desc, 1
            """, args, r => new GroupFigure(r.GetGuid(0).ToString(), r.GetInt64(2), r.GetInt64(3), Names(r.GetString(1)),
                r.GetInt64(5) == 0 ? null : Math.Round((double)r.GetInt64(4) / r.GetInt64(5), 4)), ct);
        var interviews = await session.ScalarAsync<long>(
            "select count(*) from hr.exit_interview where not declined and submitted_at >= @from and submitted_at < @to", args, ct);
        var reasons = await session.QueryAsync(
            """
            select reason, count(*), count(distinct e.user_id) from hr.exit_interview e cross join unnest(e.reasons) reason
            where not e.declined and e.submitted_at >= @from and e.submitted_at < @to group by 1 order by 2 desc, 1
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        var requests = await session.QueryAsync(
            """
            select 'letters', count(*), count(distinct user_id) from hr.letter where created_at >= @from and created_at < @to and status <> 'withdrawn'
            union all
            select 'data_changes', count(*), count(distinct user_id) from hr.data_change_request where created_at >= @from and created_at < @to and status <> 'withdrawn'
            union all
            select 'expenses', count(*), count(distinct user_id) from hr.expense_claim where created_at >= @from and created_at < @to and status <> 'withdrawn'
            """, args, r => new GroupFigure(r.GetString(0), r.GetInt64(1), r.GetInt64(2)), ct);
        return new HrSummary(leave.Count, leave.People, leave.Days, types, weeks, attendance, onboarding, interviews, reasons, requests);
    }
}
