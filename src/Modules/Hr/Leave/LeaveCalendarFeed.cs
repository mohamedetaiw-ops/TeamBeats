using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>
/// The calendar feed of spec 037 over the working calendars and leave requests of this module. Leave types follow the
/// sensitivity rule of spec 022 section 6: a sensitive type is shown to the person, anyone in their manager chain and
/// HR officers covering them; to everyone else the range is just "on leave". Pending leave is shown only to the person
/// and their managers. Nothing here is cached across requests, so a cancellation or a manager change shows at once.
/// </summary>
public sealed class LeaveCalendarFeed(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    IReportingLine reporting,
    WorkingCalendar calendars,
    LeaveConfig config,
    LeaveLedger ledger) : IHrCalendarFeed
{
    public const int MaxDays = 366;
    public const int MaxPeople = 1000;

    public async Task<IReadOnlyList<HolidayEntry>> HolidaysAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from || to.DayNumber - from.DayNumber >= MaxDays)
        {
            return [];
        }
        var snapshot = await calendars.ForUserAsync(userId, ct);
        if (snapshot.CalendarId is not { } calendarId)
        {
            return [];
        }
        return await session.QueryAsync(
            "select date, name::text, half_day from hr.calendar_holiday where calendar_id = @calendarId and date between @from and @to order by date",
            new { calendarId, from, to },
            r => new HolidayEntry(DateOnly.FromDateTime(r.GetDateTime(0)), System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) ?? [], r.GetBoolean(2)), ct);
    }

    public async Task<IReadOnlyList<LeaveRange>> LeaveAsync(IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (userIds.Count == 0 || userIds.Count > MaxPeople || to < from || to.DayNumber - from.DayNumber >= MaxDays
            || !await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            return [];
        }
        var me = context.RequireUser();
        var rows = await session.QueryAsync(
            """
            select id, user_id, leave_type_id, start_date, end_date, start_half, end_half, status
            from hr.leave_request
            where user_id = any(@ids) and status in ('approved', 'pending') and start_date <= @to and end_date >= @from
            order by start_date, user_id
            """,
            new { ids = userIds.Distinct().ToArray(), from, to },
            r => (Id: r.GetGuid(0), User: r.GetGuid(1), Type: r.GetGuid(2), Start: DateOnly.FromDateTime(r.GetDateTime(3)), End: DateOnly.FromDateTime(r.GetDateTime(4)),
                StartHalf: r.GetBoolean(5), EndHalf: r.GetBoolean(6), Status: r.GetString(7)), ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var types = (await config.TypesAsync(ct)).ToDictionary(t => t.Id);
        var managed = new Dictionary<Guid, bool>();
        async Task<bool> ManagesAsync(Guid user)
        {
            if (!managed.TryGetValue(user, out var value))
            {
                value = await reporting.IsManagerOfAsync(me, user, ct);
                managed[user] = value;
            }
            return value;
        }
        var hrCovers = new Dictionary<Guid, bool>();
        async Task<bool> HrCoversAsync(Guid user)
        {
            if (!hrCovers.TryGetValue(user, out var value))
            {
                value = await ledger.PersonAsync(user, ct) is { } person && await ledger.HrCoversAsync(person, ct);
                hrCovers[user] = value;
            }
            return value;
        }
        var list = new List<LeaveRange>();
        foreach (var row in rows)
        {
            var own = row.User == me;
            var manages = !own && await ManagesAsync(row.User);
            if (row.Status == LeaveStatuses.Pending && !own && !manages)
            {
                continue;
            }
            var type = types.GetValueOrDefault(row.Type);
            var showType = type is not null && (!type.Sensitive || own || manages || await HrCoversAsync(row.User));
            list.Add(new LeaveRange(row.User, row.Start, row.End.AddDays(1), row.StartHalf, row.EndHalf, row.Status, own || manages ? row.Id : null,
                showType ? type!.Key : null, showType ? type!.Names : null));
        }
        return list;
    }
}
