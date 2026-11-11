using System.Globalization;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Attendance;

public static class LocationModes
{
    /// <summary>No location is asked for.</summary>
    public const string Off = "off";

    /// <summary>A sign in outside every site is recorded with the remote flag.</summary>
    public const string Flag = "flag";

    /// <summary>A sign in outside every site is refused.</summary>
    public const string Refuse = "refuse";

    public static readonly IReadOnlyList<string> All = [Off, Flag, Refuse];
}

public sealed record AttendanceSite(string Name, double Latitude, double Longitude, int RadiusMeters);

public sealed record AttendanceSettingsView(bool WebSignIn, string LocationMode, IReadOnlyList<AttendanceSite> Sites, int Version, DateTimeOffset? UpdatedAt, bool CanChange,
    string? CompensationTypeKey = null)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

public sealed record AttendanceSettingsRequest(bool? WebSignIn, string? LocationMode, IReadOnlyList<AttendanceSite>? Sites, string? CompensationTypeKey = null);

public sealed record WorkRuleView(Guid Id, Guid? OrgUnitId, string Start, string End, int GraceLateMinutes, int GraceEarlyMinutes, int Version, DateTimeOffset UpdatedAt)
{
    public string ETag => JourneyTemplates.ETag(Version);
}

public sealed record WorkRuleRequest(Guid? OrgUnitId, string? Start, string? End, int? GraceLateMinutes, int? GraceEarlyMinutes);

public sealed record PunchRequest(string? Direction, double? Latitude, double? Longitude);

public sealed record PunchView(Guid Id, DateTimeOffset At, string Direction, bool Remote, string Source);

/// <summary>A day as the person, their managers and HR officers covering them see it (US3 scenario 2).</summary>
public sealed record AttendanceDayView(DateOnly Date, string Kind, string? FirstIn, string? LastOut, decimal HoursWorked, decimal ExpectedHours,
    IReadOnlyList<string> Flags, IReadOnlyList<PunchView> Punches);

public sealed record AttendanceMonthView(PersonRef Person, string Month, WorkRuleView? Rule, IReadOnlyList<AttendanceDayView> Days, AttendanceTotals Totals,
    bool WebSignIn, string LocationMode, PunchView? LastPunch, bool Mine);

public sealed record TeamAttendanceRow(PersonRef Person, AttendanceTotals Totals);

public sealed record TeamAttendanceView(string Month, IReadOnlyList<TeamAttendanceRow> People);

/// <summary>
/// Attendance (spec 022 US3, FR-010 and FR-012): sign in and out on the web with the tenant's optional location check,
/// clock data imported through spec 017, working hours rules per unit, and the days and monthly summary computed from
/// them against the person's working calendar and approved leave. The person sees their own, managers their reports,
/// HR officers the people of the units they cover, and HR exports a month for payroll. Needs <c>hr.attendance</c>.
/// </summary>
public sealed class AttendanceService(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IWorkingCalendar calendars,
    IReportingLine reporting,
    IUserDirectory users,
    IClockData clockData,
    LeaveLedger people,
    LeaveConfig leaveConfig)
{
    public const int MaxSites = 20;
    public const int MaxSiteName = 80;
    public const int MaxRadius = 5000;
    public const int MaxExportPeople = 5000;
    public static readonly TimeSpan MinPunchGap = TimeSpan.FromMinutes(1);

    // ---- settings ----

    private async Task<(bool Web, string Mode, List<AttendanceSite> Sites, int Version, DateTimeOffset? UpdatedAt)> SettingRowAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select web_sign_in, location_mode, sites::text, version, updated_at from hr.attendance_settings", null,
            r => (Web: r.GetBoolean(0), Mode: r.GetString(1), Sites: JsonSerializer.Deserialize<List<AttendanceSite>>(r.GetString(2), JsonDefaults.Options) ?? [],
                Version: r.GetInt32(3), UpdatedAt: r.Get<DateTimeOffset?>("updated_at")), ct);
        return row == default ? (false, LocationModes.Off, [], 0, null) : row;
    }

    private async Task<OrgCoverage> HrCoverageAsync(CancellationToken ct) => await authz.CoverageAsync(Permissions.HrManage, ct);

    public async Task<AttendanceSettingsView> SettingsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var coverage = await HrCoverageAsync(ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var (web, mode, sites, version, updatedAt) = await SettingRowAsync(ct);
        return new AttendanceSettingsView(web, mode, sites, version, updatedAt, coverage.WholeTenant, await CompensationTypeKeyAsync(ct));
    }

    public async Task<AttendanceSettingsView> UpdateSettingsAsync(string? ifMatch, AttendanceSettingsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        if (!(await HrCoverageAsync(ct)).WholeTenant)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var mode = request.LocationMode ?? LocationModes.Off;
        if (!LocationModes.All.Contains(mode))
        {
            errors.Add("locationMode", "validation.attendance.location_mode");
        }
        var sites = (request.Sites ?? []).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (sites.Count > MaxSites || sites.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Trim().Length > MaxSiteName || s.Name.Any(char.IsControl)
                || !names.Add(s.Name.Trim()) || s.Latitude is < -90 or > 90 || s.Longitude is < -180 or > 180 || s.RadiusMeters is < 10 or > MaxRadius
                || double.IsNaN(s.Latitude) || double.IsNaN(s.Longitude)))
        {
            errors.Add("sites", "validation.attendance.sites");
        }
        if (mode != LocationModes.Off && sites.Count == 0)
        {
            errors.Add("sites", "validation.attendance.sites_needed");
        }
        if (request.WebSignIn is null)
        {
            errors.Add("webSignIn", "validation.required");
        }
        var compensation = string.IsNullOrWhiteSpace(request.CompensationTypeKey) ? null : request.CompensationTypeKey.Trim();
        if (compensation is not null && await leaveConfig.TypeByKeyAsync(compensation, ct) is not { Active: true, Unit: LeaveUnits.Day })
        {
            errors.Add("compensationTypeKey", "validation.attendance.compensation_type");
        }
        errors.ThrowIfAny();
        var clean = sites.Select(s => s with { Name = s.Name.Trim() }).ToList();
        var current = await SettingRowAsync(ct);
        if (ifMatch != JourneyTemplates.ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.attendance.changed", null,
                new Dictionary<string, object?> { ["etag"] = JourneyTemplates.ETag(current.Version) });
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.attendance_settings (tenant_id, web_sign_in, location_mode, sites, compensation_type_key, version, updated_by, updated_at)
            values (platform.current_tenant_id(), @web, @mode, @sites, @compensation, 1, @actor, @now)
            on conflict (tenant_id) do update set web_sign_in = @web, location_mode = @mode, sites = @sites, compensation_type_key = @compensation,
              version = hr.attendance_settings.version + 1, updated_by = @actor, updated_at = @now
            """, new { web = request.WebSignIn!.Value, mode, sites = Jsonb.From(clean), compensation, actor = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrAttendanceSettingsChanged, "attendance_settings", "attendance",
            Changes: new { webSignIn = request.WebSignIn, locationMode = mode, sites = clean.Count, compensationType = compensation }), ct);
        return await SettingsAsync(ct);
    }

    /// <summary>The leave type compensation days go to (FR-011), or null when compensation requests are off.</summary>
    internal async Task<string?> CompensationTypeKeyAsync(CancellationToken ct) =>
        await session.ScalarAsync<string?>("select compensation_type_key from hr.attendance_settings", null, ct);

    // ---- working hours rules ----

    private const string RuleColumns = "id, org_unit_id, start_time, end_time, grace_late_minutes, grace_early_minutes, version, updated_at";

    private static WorkRuleView ReadRule(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.Get<Guid?>("org_unit_id"),
        TimeOnly.FromTimeSpan(r.GetTimeSpan(2)).ToString("HH:mm", CultureInfo.InvariantCulture), TimeOnly.FromTimeSpan(r.GetTimeSpan(3)).ToString("HH:mm", CultureInfo.InvariantCulture),
        r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetFieldValue<DateTimeOffset>(7));

    private Task<List<WorkRuleView>> RulesAsync(CancellationToken ct) =>
        session.QueryAsync($"select {RuleColumns} from hr.work_rule order by org_unit_id nulls first", null, ReadRule, ct);

    internal static WorkRule ToRule(WorkRuleView? view) => view is null
        ? WorkRule.Default
        : new WorkRule(TimeOnly.ParseExact(view.Start, "HH:mm", CultureInfo.InvariantCulture), TimeOnly.ParseExact(view.End, "HH:mm", CultureInfo.InvariantCulture),
            view.GraceLateMinutes, view.GraceEarlyMinutes);

    /// <summary>The rule of the nearest unit on the path from the root, else the tenant's, else the built-in default.</summary>
    internal static WorkRuleView? RuleFor(IReadOnlyList<WorkRuleView> rules, IReadOnlyList<Guid> pathFromRoot) =>
        pathFromRoot.Reverse().Select(u => rules.FirstOrDefault(r => r.OrgUnitId == u)).FirstOrDefault(r => r is not null) ?? rules.FirstOrDefault(r => r.OrgUnitId is null);

    public async Task<IReadOnlyList<WorkRuleView>> ListRulesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var coverage = await HrCoverageAsync(ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        return (await RulesAsync(ct)).Where(r => r.OrgUnitId is null || coverage.Covers(r.OrgUnitId)).ToList();
    }

    /// <summary>Saves the rule of a unit, or of the tenant without a unit (tenant-wide HR only), against the version read when it exists.</summary>
    public async Task<WorkRuleView> SaveRuleAsync(string? ifMatch, WorkRuleRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var coverage = await HrCoverageAsync(ct);
        if (request.OrgUnitId is null ? !coverage.WholeTenant : !coverage.Covers(request.OrgUnitId))
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        TimeOnly? Time(string? text) => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
        var start = Time(request.Start);
        var end = Time(request.End);
        if (start is null || end is null || end <= start)
        {
            errors.Add("end", "validation.attendance.hours");
        }
        if (request.GraceLateMinutes is not (>= 0 and <= 120) || request.GraceEarlyMinutes is not (>= 0 and <= 120))
        {
            errors.Add("grace", "validation.attendance.grace");
        }
        errors.ThrowIfAny();
        var existing = (await RulesAsync(ct)).FirstOrDefault(r => r.OrgUnitId == request.OrgUnitId);
        if (existing is not null && ifMatch != existing.ETag)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.attendance.changed", null, new Dictionary<string, object?> { ["etag"] = existing.ETag });
        }
        var now = clock.UtcNow;
        var id = existing?.Id ?? Ids.New();
        var args = new
        {
            id, unit = request.OrgUnitId, start = start!.Value.ToTimeSpan(), end = end!.Value.ToTimeSpan(), late = request.GraceLateMinutes!.Value,
            early = request.GraceEarlyMinutes!.Value, actor = context.RequireUser(), now,
        };
        if (existing is null)
        {
            await session.ExecAsync(
                """
                insert into hr.work_rule (tenant_id, id, org_unit_id, start_time, end_time, grace_late_minutes, grace_early_minutes, version, updated_by, updated_at)
                values (platform.current_tenant_id(), @id, @unit, @start, @end, @late, @early, 1, @actor, @now)
                """, args, ct);
        }
        else
        {
            await session.ExecAsync(
                """
                update hr.work_rule set start_time = @start, end_time = @end, grace_late_minutes = @late, grace_early_minutes = @early, version = version + 1,
                  updated_by = @actor, updated_at = @now
                where id = @id
                """, args, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrWorkRuleChanged, "work_rule", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { start = request.Start, end = request.End, request.GraceLateMinutes, request.GraceEarlyMinutes }), ct);
        return (await RulesAsync(ct)).Single(r => r.Id == id);
    }

    // ---- punches ----

    /// <summary>
    /// The signed-in person signs in or out (US3 scenario 1). With the location check on, the browser's position is
    /// compared with the sites: outside every site the punch is flagged remote or refused. The position is not stored.
    /// </summary>
    public async Task<PunchView> PunchAsync(PunchRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var me = context.RequireUser();
        var (web, mode, sites, _, _) = await SettingRowAsync(ct);
        if (!web)
        {
            throw AppException.Conflict("conflict.attendance.web_off");
        }
        if (request.Direction is not { } direction || !PunchDirections.All.Contains(direction))
        {
            throw AppException.Validation("direction", "validation.attendance.direction");
        }
        var remote = false;
        if (mode != LocationModes.Off)
        {
            if (request.Latitude is not (>= -90 and <= 90) || request.Longitude is not (>= -180 and <= 180))
            {
                throw AppException.Validation("latitude", "validation.attendance.location_needed");
            }
            var inside = sites.Any(s => AttendanceMath.DistanceMeters(request.Latitude!.Value, request.Longitude!.Value, s.Latitude, s.Longitude) <= s.RadiusMeters);
            if (!inside && mode == LocationModes.Refuse)
            {
                throw AppException.Conflict("conflict.attendance.outside_sites");
            }
            remote = !inside;
        }
        var now = clock.UtcNow;
        var last = await LastPunchAsync(me, ct);
        if (last is not null && now - last.At < MinPunchGap)
        {
            throw AppException.Conflict("conflict.attendance.too_soon");
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into hr.attendance_punch (tenant_id, id, user_id, at, direction, source, remote, created_at)
            values (platform.current_tenant_id(), @id, @me, @now, @direction, 'web', @remote, @now)
            """, new { id, me, now, direction, remote }, ct);
        return new PunchView(id, now, direction, remote, PunchSources.Web);
    }

    private async Task<PunchView?> LastPunchAsync(Guid userId, CancellationToken ct) =>
        (await session.QueryAsync("select id, at, direction, remote, source from hr.attendance_punch where user_id = @userId order by at desc limit 1", new { userId },
            ReadPunch, ct)).SingleOrDefault();

    private static PunchView ReadPunch(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetBoolean(3), r.GetString(4));

    /// <summary>
    /// Reads new clock data from Integrations (spec 017 US7) and keeps each punch once. A punch whose direction the
    /// source does not give is paired by time: the opposite of the person's punch before it on the same day.
    /// </summary>
    public async Task<int> ImportClockDataAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrAttendance, ct))
        {
            return 0;
        }
        var after = await session.ScalarAsync<long?>("select last_sequence from hr.attendance_import for update", null, ct) ?? 0;
        var zone = (await calendars.TenantDefaultAsync(ct)).Zone;
        var imported = 0;
        while (true)
        {
            var batch = await clockData.ReadAsync(after, 5000, ct);
            if (batch.Count == 0)
            {
                break;
            }
            foreach (var p in batch.OrderBy(p => p.At))
            {
                var direction = p.Direction is PunchDirections.In or PunchDirections.Out ? p.Direction : await InferAsync(p, zone, ct);
                imported += await session.ExecAsync(
                    """
                    insert into hr.attendance_punch (tenant_id, id, user_id, at, direction, source, remote, connection_id, external_id, created_at)
                    values (platform.current_tenant_id(), @id, @user, @at, @direction, 'clock', false, @connection, @external, @now)
                    on conflict do nothing
                    """, new { id = Ids.New(), user = p.UserId, at = p.At.ToUniversalTime(), direction, connection = p.ConnectionId, external = p.PunchId, now = clock.UtcNow }, ct);
            }
            after = batch.Max(p => p.Sequence);
        }
        await session.ExecAsync(
            """
            insert into hr.attendance_import (tenant_id, last_sequence, updated_at) values (platform.current_tenant_id(), @after, @now)
            on conflict (tenant_id) do update set last_sequence = @after, updated_at = @now
            """, new { after, now = clock.UtcNow }, ct);
        return imported;
    }

    private async Task<string> InferAsync(ClockPunch punch, TimeZoneInfo zone, CancellationToken ct)
    {
        var local = TimeZoneInfo.ConvertTime(punch.At, zone);
        var dayStart = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
        var previous = await session.ScalarAsync<string?>(
            "select direction from hr.attendance_punch where user_id = @user and at >= @dayStart and at < @at order by at desc limit 1",
            new { user = punch.UserId, dayStart, at = punch.At.ToUniversalTime() }, ct);
        return previous == PunchDirections.In ? PunchDirections.Out : PunchDirections.In;
    }

    // ---- reading ----

    internal static (DateOnly From, DateOnly To) MonthRange(string? month, DateOnly today)
    {
        if (month is null)
        {
            return (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1).AddDays(-1));
        }
        if (!DateOnly.TryParseExact(month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        {
            throw AppException.Validation("month", "validation.attendance.month");
        }
        return (first, first.AddMonths(1).AddDays(-1));
    }

    /// <summary>Who may read a person's attendance: themselves, anyone in their manager chain, and HR officers covering their unit.</summary>
    private async Task DemandReadAsync(LeavePerson person, CancellationToken ct)
    {
        var me = context.RequireUser();
        if (me == person.UserId || await reporting.IsManagerOfAsync(me, person.UserId, ct) || await people.HrCoversAsync(person, ct))
        {
            return;
        }
        throw AppException.NotFound();
    }

    public async Task<AttendanceMonthView> MineAsync(string? month, CancellationToken ct) => await PersonAsync(context.RequireUser(), month, ct);

    public async Task<AttendanceMonthView> PersonAsync(Guid userId, string? month, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        if (await people.PersonAsync(userId, ct) is not { } person)
        {
            throw AppException.NotFound();
        }
        await DemandReadAsync(person, ct);
        var today = await people.TodayAsync(ct);
        var (from, to) = MonthRange(month, today);
        var rules = await RulesAsync(ct);
        var rule = RuleFor(rules, person.Path);
        var (days, punches) = await DaysAsync(person, rule, from, to, today, ct);
        var (web, mode, _, _, _) = await SettingRowAsync(ct);
        var name = (await users.GetAsync(userId, ct))?.DisplayName ?? userId.ToString();
        var mine = userId == context.RequireUser();
        return new AttendanceMonthView(new PersonRef(userId, name), from.ToString("yyyy-MM", CultureInfo.InvariantCulture), rule, days, AttendanceMath.Totals(days.Select(ToDay)),
            web, mode, mine ? await LastPunchAsync(userId, ct) : null, mine);
    }

    private static AttendanceDay ToDay(AttendanceDayView d) => new(d.Date, d.Kind, null, null, d.HoursWorked, d.ExpectedHours, d.Flags, d.Punches.Count);

    /// <summary>One day of a person as the month view computes it, with the rule that applied.</summary>
    internal async Task<(AttendanceDayView Day, WorkRule Rule)> DayAsync(LeavePerson person, DateOnly date, CancellationToken ct)
    {
        var view = RuleFor(await RulesAsync(ct), person.Path);
        var (days, _) = await DaysAsync(person, view, date, date, await people.TodayAsync(ct), ct);
        return (days[0], ToRule(view));
    }

    private async Task<(List<AttendanceDayView> Days, List<PunchView> Punches)> DaysAsync(LeavePerson person, WorkRuleView? ruleView, DateOnly from, DateOnly to,
        DateOnly today, CancellationToken ct)
    {
        var calendar = person.PrimaryUnit is { } unit ? await calendars.ForUnitAsync(unit, ct) : await calendars.TenantDefaultAsync(ct);
        var start = Local(from, calendar.Zone);
        var end = Local(to.AddDays(1), calendar.Zone);
        var punches = await session.QueryAsync(
            "select id, at, direction, remote, source from hr.attendance_punch where user_id = @userId and at >= @start and at < @end order by at",
            new { userId = person.UserId, start, end }, ReadPunch, ct);
        var leave = (await session.QueryAsync(
            """
            select d.date, sum(d.fraction) from hr.leave_request_day d join hr.leave_request r on r.id = d.request_id
            where r.user_id = @userId and r.status = 'approved' and d.date between @from and @to group by d.date
            """, new { userId = person.UserId, from, to }, r => (Date: DateOnly.FromDateTime(r.GetDateTime(0)), Fraction: r.GetDecimal(1)), ct))
            .ToDictionary(x => x.Date, x => x.Fraction);
        var corrected = (await session.QueryAsync(
            "select distinct date from hr.attendance_request where user_id = @userId and kind = 'correction' and status = 'approved' and date between @from and @to",
            new { userId = person.UserId, from, to }, r => DateOnly.FromDateTime(r.GetDateTime(0)), ct)).ToHashSet();
        var rule = ToRule(ruleView);
        var byDay = punches.GroupBy(p => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(p.At, calendar.Zone).DateTime)).ToDictionary(g => g.Key, g => g.ToList());
        var days = new List<AttendanceDayView>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var mine = byDay.GetValueOrDefault(d) ?? [];
            var day = AttendanceMath.Evaluate(d, mine.Select(p => new Punch(p.At, p.Direction, p.Remote, p.Source)).ToList(), rule, calendar,
                leave.GetValueOrDefault(d), today);
            days.Add(new AttendanceDayView(d, day.Kind, day.FirstIn?.ToString("HH:mm", CultureInfo.InvariantCulture), day.LastOut?.ToString("HH:mm", CultureInfo.InvariantCulture),
                day.HoursWorked, day.ExpectedHours, corrected.Contains(d) ? [.. day.Flags, AttendanceFlags.Corrected] : day.Flags, mine));
        }
        return (days, punches);
    }

    private static DateTimeOffset Local(DateOnly date, TimeZoneInfo zone)
    {
        var at = date.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(at, zone.GetUtcOffset(at)).ToUniversalTime();
    }

    /// <summary>The month's totals of the viewer's direct reports (US3 scenario 5).</summary>
    public async Task<TeamAttendanceView> TeamAsync(string? month, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var reports = await reporting.DirectReportsAsync(context.RequireUser(), ct);
        var rows = await TotalsAsync(reports, month, ct);
        return new TeamAttendanceView(rows.Month, rows.Rows);
    }

    private async Task<(string Month, List<TeamAttendanceRow> Rows)> TotalsAsync(IReadOnlyCollection<Guid> userIds, string? month, CancellationToken ct)
    {
        var today = await people.TodayAsync(ct);
        var (from, to) = MonthRange(month, today);
        var rules = await RulesAsync(ct);
        var persons = userIds.Count == 0 ? new Dictionary<Guid, LeavePerson>() : await people.PeopleAsync(userIds, ct);
        var names = userIds.Count == 0 ? new Dictionary<Guid, UserInfo>() : await users.GetManyAsync(userIds, ct);
        var rows = new List<TeamAttendanceRow>();
        foreach (var person in persons.Values.Where(p => p.Active))
        {
            var (days, _) = await DaysAsync(person, RuleFor(rules, person.Path), from, to, today, ct);
            rows.Add(new TeamAttendanceRow(new PersonRef(person.UserId, names.TryGetValue(person.UserId, out var u) ? u.DisplayName : person.UserId.ToString()),
                AttendanceMath.Totals(days.Select(ToDay))));
        }
        return (from.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows.OrderBy(r => r.Person.DisplayName, StringComparer.CurrentCulture).ToList());
    }

    /// <summary>
    /// The month for payroll (FR-012): one line per active person in the units the HR officer covers, as CSV with
    /// fixed column names. Audited, since it holds the attendance of many people.
    /// </summary>
    public async Task<(string FileName, byte[] Content)> ExportAsync(string? month, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var coverage = await HrCoverageAsync(ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var all = await people.PeopleAsync(null, ct);
        var covered = all.Values.Where(p => p.Active && (coverage.WholeTenant || p.PrimaryUnit is { } u && coverage.Covers(u))).Select(p => p.UserId).ToList();
        if (covered.Count > MaxExportPeople)
        {
            throw AppException.Conflict("conflict.attendance.export_too_large");
        }
        var (label, rows) = await TotalsAsync(covered, month, ct);
        var csv = new StringBuilder("user_id,name,worked_days,hours_worked,expected_hours,late,early_leave,missing_punch,absent,remote,worked_days_off\n");
        foreach (var r in rows)
        {
            var t = r.Totals;
            csv.Append(CultureInfo.InvariantCulture,
                $"{r.Person.Id},{Csv(r.Person.DisplayName)},{t.WorkedDays},{t.HoursWorked},{t.ExpectedHours},{t.Late},{t.EarlyLeave},{t.MissingPunch},{t.Absent},{t.Remote},{t.WorkedDaysOff}\n");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrAttendanceExported, "attendance_export", label, Changes: new { month = label, people = rows.Count }), ct);
        return ($"attendance-{label}.csv", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray());
    }

    /// <summary>Quotes a value, and neutralises a leading formula character so a spreadsheet never runs it.</summary>
    internal static string Csv(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"") + "\"";
    }
}
