using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>
/// Recount of leave when a calendar changes (spec 022 FR-008): a holiday added or removed, a week changed or a calendar
/// applied to other units. Each open request of the people concerned is counted again on their calendar; when its days
/// change, the request days are replaced, an approved request gets a <c>recount</c> ledger row per leave year for the
/// difference, and the person is told.
/// </summary>
public sealed partial class LeaveRequests
{
    public const string RecountedTemplate = "hr.leave_recounted";
    public const int MaxRecountRequests = 2000;

    public async Task<int> RecountAsync(WorkingCalendarChanged change, CancellationToken ct)
    {
        var dates = change.AffectedDates.ToArray();
        var units = change.AffectedUnits.ToArray();
        var today = await ledger.TodayAsync(ct);
        var everyone = dates.Length == 0 && units.Length == 0 && change.CalendarId is { } calendarId
            && await session.ScalarAsync<bool>("select coalesce((select is_tenant_default from hr.working_calendar where id = @calendarId), false)", new { calendarId }, ct);
        Guid[] people = [];
        if (units.Length > 0)
        {
            people = [.. await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync(units, ct), ct)];
        }
        if (dates.Length == 0 && people.Length == 0 && !everyone)
        {
            return 0;
        }
        // A holiday counts back for any open request on that date; a week or assignment change only for leave still to end.
        var ids = await session.QueryAsync(
            """
            select r.id from hr.leave_request r join hr.leave_type t on t.id = r.leave_type_id
            where r.status in ('pending', 'approved') and t.unit = 'day'
              and ((cardinality(@dates) > 0 and exists (select 1 from hr.leave_request_day d where d.request_id = r.id and d.date = any(@dates)))
                or (cardinality(@dates) > 0 and r.start_date <= (select max(x) from unnest(@dates) x) and r.end_date >= (select min(x) from unnest(@dates) x))
                or (r.end_date >= @today and (@everyone or r.user_id = any(@people))))
            order by r.created_at limit @limit
            """, new { dates, people, everyone, today, limit = MaxRecountRequests }, r => r.GetGuid(0), ct);
        var changed = 0;
        foreach (var id in ids)
        {
            if (await RecountOneAsync(id, ct))
            {
                changed++;
            }
        }
        return changed;
    }

    private async Task<bool> RecountOneAsync(Guid id, CancellationToken ct)
    {
        if (await RowAsync(id, true, ct) is not { Status: LeaveStatuses.Pending or LeaveStatuses.Approved } row || await config.TypeAsync(row.TypeId, ct) is not { } type)
        {
            return false;
        }
        var calendar = await calendars.ForUserAsync(row.UserId, ct);
        var now = LeaveMath.Days(calendar, row.Start, row.End, row.StartHalf, row.EndHalf, type.YearStartMonth);
        var before = await session.QueryAsync("select date, fraction, leave_year from hr.leave_request_day where request_id = @id", new { id },
            r => new LeaveDay(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetDecimal(1), r.GetInt32(2)), ct);
        if (before.OrderBy(d => d.Date).SequenceEqual(now.OrderBy(d => d.Date)))
        {
            return false;
        }
        var total = LeaveMath.Total(now);
        if (row.Status == LeaveStatuses.Approved && !await HrOwnsBalancesAsync(ct))
        {
            // Days taken before minus days taken now, per leave year: positive gives days back.
            var back = before.Select(d => (d.LeaveYear, d.Fraction)).Concat(now.Select(d => (d.LeaveYear, -d.Fraction)))
                .GroupBy(d => d.LeaveYear).Select(g => (Year: g.Key, Days: g.Sum(d => d.Item2))).Where(g => g.Days != 0).ToList();
            await ledger.PostRequestAsync(row.UserId, row.TypeId, row.Id, "recount", back, ct);
        }
        await session.ExecAsync("delete from hr.leave_request_day where request_id = @id", new { id }, ct);
        await InsertDaysAsync(id, now, ct);
        await session.ExecAsync("update hr.leave_request set days = @total, updated_at = @at where id = @id", new { id, total, at = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveRecounted, "leave_request", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Number, daysBefore = row.Days, days = total, status = row.Status }), ct);
        if (row.Status == LeaveStatuses.Approved)
        {
            await HandOffAsync(id, HrLeaveHandOffRequest.Changed, ct);
        }
        await notifications.SendAsync(new NotificationRequest(new[] { row.UserId, row.FiledBy }.Distinct().ToArray(), NotificationCategories.RequestUpdate,
            RecountedTemplate, Link(id), DedupeKey: $"hr.leave:{id:N}:recount:{total}"), ct);
        return true;
    }
}

/// <summary>Recounts leave after a calendar change (spec 022 FR-008).</summary>
public sealed class LeaveRecountHandler(LeaveRequests requests) : IMessageHandler<WorkingCalendarChanged>
{
    public Task HandleAsync(WorkingCalendarChanged message, CancellationToken ct) => requests.RecountAsync(message, ct);
}
