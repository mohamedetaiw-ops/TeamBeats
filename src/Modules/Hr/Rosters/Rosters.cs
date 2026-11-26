using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Hr.Attendance;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Rosters;

public sealed record ShiftPatternInput(Dictionary<string, string>? Names, string? Start, string? End, int? BreakMinutes, string? Tone, bool? Active);

public sealed record ShiftPatternView(Guid Id, IReadOnlyDictionary<string, string> Names, string Start, string End, int BreakMinutes, string Tone, bool Active, int Version);

public sealed record ShiftPatternsView(IReadOnlyList<ShiftPatternView> Patterns, bool CanManage);

public sealed record RosterEntryInput(Guid UserId, DateOnly Date, Guid? PatternId, bool DayOff);

public sealed record RosterSaveInput(IReadOnlyList<RosterEntryInput>? Entries, int? Version);

/// <summary>
/// One person's day in the planner. <see cref="Leave"/> and <see cref="Holiday"/> say the person is away that day;
/// <see cref="Conflict"/> is true when a shift is planned on such a day (FR-029).
/// </summary>
public sealed record RosterDayView(DateOnly Date, Guid? PatternId, bool DayOff, bool Leave, bool Holiday, bool Conflict);

public sealed record RosterPersonView(PersonRef Person, IReadOnlyList<RosterDayView> Days);

/// <summary>
/// A manager's week. <see cref="Status"/> is <c>new</c> before the first save, then <c>draft</c> or <c>published</c>;
/// <see cref="Changed"/> is true when a published week has changes not yet published.
/// </summary>
public sealed record RosterWeekView(Guid? Id, PersonRef Manager, DateOnly WeekStart, string Status, int Version, bool Changed, DateTimeOffset? PublishedAt,
    IReadOnlyList<RosterPersonView> People, bool CanEdit);

public sealed record MyShiftView(DateOnly Date, ShiftPatternView? Pattern, bool DayOff);

public static class ShiftTones
{
    public static readonly IReadOnlyList<string> All = ["neutral", "info", "success", "warning", "danger"];
}

/// <summary>
/// Published shifts for attendance (FR-029): for a person and day, the shift of the latest published week that plans
/// it. Nothing without <c>hr.rosters</c> in the plan, so attendance keeps the working week.
/// </summary>
public sealed class ShiftRoster(IDbSession session, IEntitlementService entitlements)
{
    public async Task<IReadOnlyDictionary<(Guid User, DateOnly Date), Shift>> ShiftsAsync(IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (userIds.Count == 0 || to < from || !await entitlements.IsEnabledAsync(Capabilities.HrRosters, ct))
        {
            return new Dictionary<(Guid, DateOnly), Shift>();
        }
        var weeks = await session.QueryAsync(
            """
            select published::text from hr.roster_week
            where status = 'published' and published is not null and week_start between @earliest and @to
            order by published_at
            """, new { earliest = from.AddDays(-6), to }, r => r.GetString(0), ct);
        if (weeks.Count == 0)
        {
            return new Dictionary<(Guid, DateOnly), Shift>();
        }
        var patterns = (await session.QueryAsync("select id, start_time, end_time, break_minutes from hr.shift_pattern", null,
            r => (Id: r.GetGuid(0), Shift: new Shift(TimeOnly.FromTimeSpan(r.GetFieldValue<TimeSpan>(1)), TimeOnly.FromTimeSpan(r.GetFieldValue<TimeSpan>(2)), r.GetInt32(3), false)), ct))
            .ToDictionary(p => p.Id, p => p.Shift);
        var wanted = userIds.ToHashSet();
        var shifts = new Dictionary<(Guid, DateOnly), Shift>();
        foreach (var json in weeks)
        {
            foreach (var (user, days) in Rosters.ReadSnapshot(json))
            {
                if (!wanted.Contains(user))
                {
                    continue;
                }
                foreach (var (date, value) in days)
                {
                    if (date < from || date > to)
                    {
                        continue;
                    }
                    if (value == Rosters.Off)
                    {
                        shifts[(user, date)] = Shift.Off;
                    }
                    else if (Guid.TryParse(value, out var pattern) && patterns.TryGetValue(pattern, out var shift))
                    {
                        shifts[(user, date)] = shift;
                    }
                }
            }
        }
        return shifts;
    }
}

/// <summary>
/// Shift rosters (spec 022 US10, FR-029). HR officers of the whole organization keep the tenant's shift patterns. A
/// manager plans a week for their direct reports, sees each person's approved leave and holidays as conflicts, and
/// publishes it: each person with a day planned is told, and a later publish tells only the people whose days changed.
/// HR officers covering the manager see the team's roster; nobody else does.
/// </summary>
public sealed class Rosters(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    INotificationSender notifications,
    IUserDirectory users,
    IReportingLine reporting,
    IWorkingCalendar calendars,
    LeaveLedger people)
{
    public const string Off = "off";
    public const int MaxPatterns = 50;
    public const int MaxShiftDays = 62;
    public const string PublishedTemplate = "hr.roster_published";
    public const string ChangedTemplate = "hr.roster_changed";

    internal static Dictionary<Guid, Dictionary<DateOnly, string>> ReadSnapshot(string? json) =>
        json is null ? [] : (JsonSerializer.Deserialize<Dictionary<Guid, Dictionary<DateOnly, string>>>(json, JsonDefaults.Options) ?? []);

    private static string Time(TimeSpan t) => TimeOnly.FromTimeSpan(t).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static ShiftPatternView ReadPattern(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1), JsonDefaults.Options) ?? [], Time(r.GetFieldValue<TimeSpan>(2)), Time(r.GetFieldValue<TimeSpan>(3)),
        r.GetInt32(4), r.GetString(5), r.GetBoolean(6), r.GetInt32(7));

    private Task<List<ShiftPatternView>> PatternListAsync(CancellationToken ct) =>
        session.QueryAsync("select id, names::text, start_time, end_time, break_minutes, tone, active, version from hr.shift_pattern order by start_time, id", null, ReadPattern, ct);

    private async Task<bool> ManagesPatternsAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.HrManage, AuthzTarget.Tenant, ct)).Allowed;

    // ---- shift patterns ----

    public async Task<ShiftPatternsView> PatternsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrRosters, ct);
        var manage = await ManagesPatternsAsync(ct);
        var list = await PatternListAsync(ct);
        return new ShiftPatternsView(manage ? list : list.Where(p => p.Active).ToList(), manage);
    }

    public async Task<ShiftPatternView> SavePatternAsync(Guid? id, ShiftPatternInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrRosters, ct);
        await authz.DemandAsync(Permissions.HrManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        var names = (input.Names ?? []).Where(n => Locales.LaunchComplete.Contains(n.Key) && !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key, n => n.Value.Trim());
        if (!names.ContainsKey("en") || names.Values.Any(v => v.Length > 60 || v.Any(char.IsControl)))
        {
            errors.Add("names", "validation.hr.shift_name");
        }
        TimeOnly? Parse(string? v) => TimeOnly.TryParseExact(v, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        var start = Parse(input.Start);
        var end = Parse(input.End);
        if (start is null)
        {
            errors.Add("start", "validation.hr.shift_time");
        }
        if (end is null || start is { } s && end <= s)
        {
            errors.Add("end", "validation.hr.shift_end");
        }
        var breakMinutes = input.BreakMinutes ?? 0;
        if (breakMinutes is < 0 or > 240 || start is { } a && end is { } b && breakMinutes >= (b - a).TotalMinutes)
        {
            errors.Add("breakMinutes", "validation.hr.shift_break");
        }
        var tone = input.Tone ?? "neutral";
        if (!ShiftTones.All.Contains(tone))
        {
            errors.Add("tone", "validation.hr.shift_tone");
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        var me = context.RequireUser();
        if (id is { } existing)
        {
            var changed = await session.ExecAsync(
                """
                update hr.shift_pattern set names = @names, start_time = @start, end_time = @end, break_minutes = @breakMinutes, tone = @tone,
                  active = coalesce(@active, active), version = version + 1, updated_by = @me, updated_at = @now
                where id = @existing
                """, new { existing, names = Jsonb.From(names), start = start!.Value.ToTimeSpan(), end = end!.Value.ToTimeSpan(), breakMinutes, tone, active = input.Active, me, now }, ct);
            if (changed == 0)
            {
                throw AppException.NotFound();
            }
        }
        else
        {
            if (await session.ScalarAsync<long>("select count(*) from hr.shift_pattern", null, ct) >= MaxPatterns)
            {
                throw AppException.Conflict("conflict.hr.shift_patterns_full");
            }
            id = Ids.New();
            await session.ExecAsync(
                """
                insert into hr.shift_pattern (tenant_id, id, names, start_time, end_time, break_minutes, tone, active, updated_by, updated_at)
                values (platform.current_tenant_id(), @id, @names, @start, @end, @breakMinutes, @tone, coalesce(@active, true), @me, @now)
                """, new { id, names = Jsonb.From(names), start = start!.Value.ToTimeSpan(), end = end!.Value.ToTimeSpan(), breakMinutes, tone, active = input.Active, me, now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrWorkRuleChanged, "shift_pattern", id.ToString(),
            Changes: new { start = input.Start, end = input.End, breakMinutes, tone, active = input.Active }), ct);
        return (await PatternListAsync(ct)).Single(p => p.Id == id);
    }

    // ---- the planner ----

    private sealed record WeekRow(Guid Id, Guid ManagerId, DateOnly WeekStart, string Status, int Version, string? Published, DateTimeOffset? PublishedAt);

    private async Task<WeekRow?> WeekAsync(Guid manager, DateOnly weekStart, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"select id, manager_id, week_start, status, version, published::text, published_at from hr.roster_week where manager_id = @manager and week_start = @weekStart{(forUpdate ? " for update" : "")}",
            new { manager, weekStart }, r => new WeekRow(r.GetGuid(0), r.GetGuid(1), r.GetFieldValue<DateOnly>(2), r.GetString(3), r.GetInt32(4), r.Get<string?>("published"),
                r.Get<DateTimeOffset?>("published_at")), ct);

    private async Task<Dictionary<Guid, Dictionary<DateOnly, string>>> EntriesAsync(Guid weekId, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select user_id, date, pattern_id from hr.roster_entry where week_id = @weekId", new { weekId },
            r => (User: r.GetGuid(0), Date: r.GetFieldValue<DateOnly>(1), Pattern: r.Get<Guid?>("pattern_id")), ct);
        return rows.GroupBy(r => r.User).ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Date, r => r.Pattern?.ToString() ?? Off));
    }

    private static bool Same(Dictionary<DateOnly, string>? a, Dictionary<DateOnly, string>? b) =>
        (a?.Count ?? 0) == (b?.Count ?? 0) && (a ?? []).All(p => b is not null && b.TryGetValue(p.Key, out var v) && v == p.Value);

    /// <summary>The week of the caller as a manager, or of another manager whom the caller covers as an HR officer.</summary>
    public async Task<RosterWeekView> GetWeekAsync(DateOnly weekStart, Guid? managerId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrRosters, ct);
        var me = context.RequireUser();
        var manager = managerId ?? me;
        if (manager != me && (await people.PersonAsync(manager, ct) is not { } managerPerson || !await people.HrCoversAsync(managerPerson, ct)))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(manager, weekStart, manager == me, ct);
    }

    public async Task<RosterWeekView> SaveWeekAsync(DateOnly weekStart, RosterSaveInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrRosters, ct);
        var me = context.RequireUser();
        var reports = (await reporting.DirectReportsAsync(me, ct)).ToHashSet();
        if (reports.Count == 0)
        {
            throw AppException.Denied();
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('hr.roster:' || @me::text))", new { me }, ct);
        if (await session.ScalarAsync<long>(
                "select count(*) from hr.roster_week where manager_id = @me and week_start <> @weekStart and week_start between @from and @to",
                new { me, weekStart, from = weekStart.AddDays(-6), to = weekStart.AddDays(6) }, ct) > 0)
        {
            throw AppException.Conflict("conflict.hr.roster_overlap");
        }
        var week = await WeekAsync(me, weekStart, true, ct);
        if (input.Version is { } version && week is not null && version != week.Version)
        {
            throw AppException.Conflict("conflict.hr.roster_version");
        }
        var patterns = (await PatternListAsync(ct)).Where(p => p.Active).Select(p => p.Id).ToHashSet();
        var errors = new ValidationErrors();
        var entries = input.Entries ?? [];
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!reports.Contains(e.UserId))
            {
                errors.Add($"entries[{i}].userId", "validation.hr.roster_person");
            }
            if (e.Date < weekStart || e.Date > weekStart.AddDays(6))
            {
                errors.Add($"entries[{i}].date", "validation.hr.roster_date");
            }
            if (e.DayOff == (e.PatternId is not null) || e.PatternId is { } p && !patterns.Contains(p))
            {
                errors.Add($"entries[{i}].patternId", "validation.hr.roster_pattern");
            }
        }
        if (entries.GroupBy(e => (e.UserId, e.Date)).Any(g => g.Count() > 1))
        {
            errors.Add("entries", "validation.hr.roster_duplicate");
        }
        errors.ThrowIfAny();

        var now = clock.UtcNow;
        Guid weekId;
        if (week is null)
        {
            weekId = Ids.New();
            await session.ExecAsync(
                """
                insert into hr.roster_week (tenant_id, id, manager_id, week_start, status, updated_by, updated_at)
                values (platform.current_tenant_id(), @weekId, @me, @weekStart, 'draft', @me, @now)
                """, new { weekId, me, weekStart, now }, ct);
        }
        else
        {
            weekId = week.Id;
            await session.ExecAsync("update hr.roster_week set version = version + 1, updated_by = @me, updated_at = @now where id = @weekId", new { weekId, me, now }, ct);
            await session.ExecAsync("delete from hr.roster_entry where week_id = @weekId", new { weekId }, ct);
        }
        foreach (var e in entries)
        {
            await session.ExecAsync(
                """
                insert into hr.roster_entry (tenant_id, week_id, user_id, date, pattern_id, day_off)
                values (platform.current_tenant_id(), @weekId, @user, @date, @pattern, @dayOff)
                """, new { weekId, user = e.UserId, date = e.Date, pattern = e.PatternId, dayOff = e.DayOff }, ct);
        }
        return await ViewAsync(me, weekStart, true, ct);
    }

    /// <summary>Publishes the saved week: the first publish tells everyone planned, a later one only those whose days changed.</summary>
    public async Task<RosterWeekView> PublishAsync(DateOnly weekStart, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrRosters, ct);
        var me = context.RequireUser();
        if (await WeekAsync(me, weekStart, true, ct) is not { } week)
        {
            throw AppException.NotFound();
        }
        var current = await EntriesAsync(week.Id, ct);
        var before = ReadSnapshot(week.Published);
        var changed = current.Keys.Union(before.Keys).Where(u => !Same(current.GetValueOrDefault(u), before.GetValueOrDefault(u))).ToList();
        if (week.Published is not null && changed.Count == 0)
        {
            throw AppException.Conflict("conflict.hr.roster_unchanged");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            "update hr.roster_week set status = 'published', published = @snapshot, published_at = @now, version = version + 1, updated_by = @me, updated_at = @now where id = @id",
            new { id = week.Id, snapshot = Jsonb.From(current), now, me }, ct);
        var first = week.Published is null;
        await audit.WriteAsync(new AuditEntry(first ? AuditActions.HrRosterPublished : AuditActions.HrRosterChanged, "roster_week", week.Id.ToString(),
            Changes: new { manager = me, week = weekStart, people = changed.Count }), ct);
        var stamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var to = changed.Where(u => u != me).ToList();
        if (to.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(to, NotificationCategories.RequestUpdate, first ? PublishedTemplate : ChangedTemplate, "/attendance",
                new Dictionary<string, string> { ["week"] = weekStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
                DedupeKey: $"hr.roster:{week.Id:N}:{stamp}"), ct);
        }
        return await ViewAsync(me, weekStart, true, ct);
    }

    private async Task<RosterWeekView> ViewAsync(Guid manager, DateOnly weekStart, bool canEdit, CancellationToken ct)
    {
        var week = await WeekAsync(manager, weekStart, false, ct);
        var entries = week is null ? [] : await EntriesAsync(week.Id, ct);
        var ids = (await reporting.DirectReportsAsync(manager, ct)).Union(entries.Keys).Distinct().ToList();
        var persons = await people.PeopleAsync(ids, ct);
        var weekEnd = weekStart.AddDays(6);
        var leave = (await session.QueryAsync(
            """
            select r.user_id, d.date from hr.leave_request_day d join hr.leave_request r on r.id = d.request_id
            where r.user_id = any(@ids) and r.status = 'approved' and d.date between @weekStart and @weekEnd
            """, new { ids = ids.ToArray(), weekStart, weekEnd }, r => (User: r.GetGuid(0), Date: DateOnly.FromDateTime(r.GetDateTime(1))), ct)).ToHashSet();
        var list = new List<RosterPersonView>();
        var byUnit = new Dictionary<Guid, WorkingCalendarSnapshot>();
        WorkingCalendarSnapshot? fallback = null;
        foreach (var id in ids)
        {
            if (!persons.TryGetValue(id, out var person) || !person.Active && !entries.ContainsKey(id))
            {
                continue;
            }
            WorkingCalendarSnapshot calendar;
            if (person.PrimaryUnit is { } unit)
            {
                if (!byUnit.TryGetValue(unit, out calendar!))
                {
                    calendar = byUnit[unit] = await calendars.ForUnitAsync(unit, ct);
                }
            }
            else
            {
                calendar = fallback ??= await calendars.TenantDefaultAsync(ct);
            }
            var mine = entries.GetValueOrDefault(id) ?? [];
            var days = new List<RosterDayView>();
            for (var d = weekStart; d <= weekEnd; d = d.AddDays(1))
            {
                var value = mine.GetValueOrDefault(d);
                Guid? pattern = value is not null && value != Off ? Guid.Parse(value) : null;
                var onLeave = leave.Contains((id, d));
                var holiday = calendar.IsHoliday(d);
                days.Add(new RosterDayView(d, pattern, value == Off, onLeave, holiday, pattern is not null && (onLeave || holiday)));
            }
            list.Add(new RosterPersonView(new PersonRef(id, (await users.GetAsync(id, ct))?.DisplayName ?? id.ToString()), days));
        }
        var changed = week is { Published: not null } && entries.Keys.Union(ReadSnapshot(week.Published).Keys)
            .Any(u => !Same(entries.GetValueOrDefault(u), ReadSnapshot(week.Published).GetValueOrDefault(u)));
        var managerName = (await users.GetAsync(manager, ct))?.DisplayName ?? manager.ToString();
        return new RosterWeekView(week?.Id, new PersonRef(manager, managerName), weekStart, week?.Status ?? "new", week?.Version ?? 0, changed, week?.PublishedAt,
            list.OrderBy(p => p.Person.DisplayName, StringComparer.CurrentCulture).ToList(), canEdit);
    }

    // ---- my shifts ----

    public async Task<IReadOnlyList<MyShiftView>> MyShiftsAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrRosters, ct);
        var start = from ?? await people.TodayAsync(ct);
        var end = to ?? start.AddDays(13);
        if (end < start || end.DayNumber - start.DayNumber >= MaxShiftDays)
        {
            throw AppException.Validation("to", "validation.hr.shift_range");
        }
        var me = context.RequireUser();
        var weeks = await session.QueryAsync(
            "select published::text from hr.roster_week where status = 'published' and published is not null and week_start between @earliest and @end order by published_at",
            new { earliest = start.AddDays(-6), end }, r => r.GetString(0), ct);
        var days = new SortedDictionary<DateOnly, string>();
        foreach (var json in weeks)
        {
            if (ReadSnapshot(json).TryGetValue(me, out var mine))
            {
                foreach (var (date, value) in mine.Where(p => p.Key >= start && p.Key <= end))
                {
                    days[date] = value;
                }
            }
        }
        var patterns = (await PatternListAsync(ct)).ToDictionary(p => p.Id);
        return days.Select(d => new MyShiftView(d.Key, Guid.TryParse(d.Value, out var p) ? patterns.GetValueOrDefault(p) : null, d.Value == Off)).ToList();
    }
}
