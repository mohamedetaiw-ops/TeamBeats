using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Attendance;

public static class AttendanceRequestKinds
{
    public const string Correction = "correction";
    public const string Compensation = "compensation";

    public static readonly IReadOnlyList<string> All = [Correction, Compensation];
}

public sealed record CorrectionPunch(string? Time, string? Direction);

public sealed record AttendanceRequestInput(string? Kind, DateOnly? Date, string? Reason, IReadOnlyList<CorrectionPunch>? Punches);

public sealed record AttendanceRequestRights(bool Withdraw, bool Decide);

public sealed record AttendanceRequestView(
    Guid Id,
    string Kind,
    PersonRef Person,
    DateOnly Date,
    string Reason,
    IReadOnlyList<CorrectionPunch> Punches,
    decimal? Days,
    string? LeaveTypeKey,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    AttendanceRequestRights Rights);

internal sealed record AttendanceRequestRow(
    Guid Id, Guid UserId, Guid? OrgUnitId, string Kind, DateOnly Date, string Reason, List<CorrectionPunch> Punches, decimal? Days, Guid? LeaveTypeId,
    string Status, Guid? InstanceId, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt);

/// <summary>
/// Attendance correction and compensation requests (spec 022 US3 scenarios 3 and 4, FR-010, FR-011). The person asks
/// for a day with a reason; one line manager step on the process engine decides (HR officers of the unit when there is
/// no manager), and nobody decides their own request. An approved correction adds its punches and marks the day
/// corrected; an approved compensation credits the day to the leave type the tenant chose.
/// </summary>
public sealed class AttendanceRequests(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IProcessEngine engine,
    INotificationSender notifications,
    IReportingLine reporting,
    IUserDirectory users,
    IWorkingCalendar calendars,
    AttendanceService attendance,
    LeaveLedger ledger,
    LeaveConfig config)
{
    public const string ProcessKey = "hr.attendance";
    public const int MaxReasonLength = 500;
    public const int MaxPunches = 4;

    /// <summary>How far back a day can be corrected or compensated (smallest safe default; the spec is silent).</summary>
    public const int MaxDaysBack = 60;

    public const string ApprovalWaiting = "hr.attendance_approval_waiting";
    public const string ApprovedTemplate = "hr.attendance_request_approved";
    public const string RejectedTemplate = "hr.attendance_request_rejected";

    public static string Link(Guid id) => $"/attendance/requests/{id}";

    internal static Guid? Parse(string businessKey) => Guid.TryParseExact(businessKey, "N", out var id) ? id : null;

    private const string Columns = "id, user_id, org_unit_id, kind, date, reason, punches::text, days, leave_type_id, status, instance_id, created_at, decided_at";

    private static AttendanceRequestRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("org_unit_id"), r.GetString(3), DateOnly.FromDateTime(r.GetDateTime(4)), r.GetString(5),
        JsonSerializer.Deserialize<List<CorrectionPunch>>(r.GetString(6), JsonDefaults.Options) ?? [], r.Get<decimal?>("days"), r.Get<Guid?>("leave_type_id"),
        r.GetString(9), r.Get<Guid?>("instance_id"), r.GetFieldValue<DateTimeOffset>(11), r.Get<DateTimeOffset?>("decided_at"));

    internal async Task<AttendanceRequestRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.attendance_request where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    /// <summary>The person asks for a correction or a compensation day for one of their own days.</summary>
    public async Task<AttendanceRequestView> CreateAsync(AttendanceRequestInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var me = context.RequireUser();
        if (await ledger.PersonAsync(me, ct) is not { Active: true } person)
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var kind = input.Kind;
        if (kind is null || !AttendanceRequestKinds.All.Contains(kind))
        {
            errors.Add("kind", "validation.attendance.request_kind");
        }
        var today = await ledger.TodayAsync(ct);
        if (input.Date is not { } date || date > today || date < today.AddDays(-MaxDaysBack))
        {
            errors.Add("date", "validation.attendance.request_date");
            date = today;
        }
        var reason = input.Reason?.Trim();
        if (reason is not { Length: > 0 and <= MaxReasonLength } || reason.Any(c => char.IsControl(c) && c is not '\n'))
        {
            errors.Add("reason", "validation.attendance.reason");
        }
        var punches = new List<CorrectionPunch>();
        if (kind == AttendanceRequestKinds.Correction)
        {
            var given = input.Punches ?? [];
            var zone = (await (person.PrimaryUnit is { } u ? calendars.ForUnitAsync(u, ct) : calendars.TenantDefaultAsync(ct))).Zone;
            var nowLocal = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, zone).DateTime);
            foreach (var p in given)
            {
                if (p is null || !TimeOnly.TryParseExact(p.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    || p.Direction is not (PunchDirections.In or PunchDirections.Out) || date == today && time > nowLocal)
                {
                    errors.Add("punches", "validation.attendance.punches");
                    break;
                }
                punches.Add(new CorrectionPunch(time.ToString("HH:mm", CultureInfo.InvariantCulture), p.Direction));
            }
            if (given.Count is 0 or > MaxPunches || punches.Select(p => p.Time).Distinct().Count() != punches.Count)
            {
                errors.Add("punches", "validation.attendance.punches");
            }
        }
        errors.ThrowIfAny();

        decimal? days = null;
        Guid? typeId = null;
        if (kind == AttendanceRequestKinds.Compensation)
        {
            if (await attendance.CompensationTypeKeyAsync(ct) is not { } key || await config.TypeByKeyAsync(key, ct) is not { Active: true } type)
            {
                throw AppException.Conflict("conflict.attendance.compensation_off");
            }
            if (await ledger.HrOwnsAsync(ct))
            {
                throw AppException.Conflict("conflict.leave.hr_owns_balances");
            }
            var (day, rule) = await attendance.DayAsync(person, date, ct);
            var earned = day.Flags.Contains(AttendanceFlags.WorkedDayOff) ? AttendanceMath.CompensationDays(day.HoursWorked, rule) : 0m;
            if (earned == 0m)
            {
                throw AppException.Conflict("conflict.attendance.no_compensation");
            }
            days = earned;
            typeId = type.Id;
        }

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('hr.attendance:' || @me::text))", new { me }, ct);
        var taken = await session.ScalarAsync<long>(
            kind == AttendanceRequestKinds.Correction
                ? "select count(*) from hr.attendance_request where user_id = @me and date = @date and kind = 'correction' and status = 'pending'"
                : "select count(*) from hr.attendance_request where user_id = @me and date = @date and kind = 'compensation' and status in ('pending', 'approved')",
            new { me, date }, ct);
        if (taken > 0)
        {
            throw AppException.Conflict("conflict.attendance.request_exists");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.attendance_request (tenant_id, id, user_id, org_unit_id, kind, date, reason, punches, days, leave_type_id, status, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @me, @unit, @kind, @date, @reason, @punches, @days, @typeId, 'pending', @now, @now)
            """, new { id, me, unit = person.PrimaryUnit, kind, date, reason, punches = Jsonb.From(punches), days, typeId, now }, ct);
        var instance = await engine.StartAsync(await RouteAsync(ct), id.ToString("N"), person.PrimaryUnit, me, [me], me, ct: ct);
        await session.ExecAsync("update hr.attendance_request set instance_id = @instance where id = @id", new { id, instance = instance.Id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrAttendanceRequested, "attendance_request", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { kind, date, punches = punches.Count, days }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>One line manager step; with no manager, HR officers of the unit decide.</summary>
    private async Task<string> RouteAsync(CancellationToken ct)
    {
        if (await engine.GetDefinitionAsync(ProcessKey, null, ct) is null)
        {
            await engine.PublishRouteAsync(ProcessKey, RouteSources.AttendanceRoute,
                new RouteSpec("attendance approval", [new RouteStep("manager", "Line manager", new CandidateRule(Permission: Permissions.HrManage, LineManager: true))]), ct);
        }
        return ProcessKey;
    }

    public async Task<IReadOnlyList<AttendanceRequestView>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        var rows = await session.QueryAsync($"select {Columns} from hr.attendance_request where user_id = @me order by created_at desc limit 100",
            new { me = context.RequireUser() }, Read, ct);
        var list = new List<AttendanceRequestView>();
        foreach (var row in rows)
        {
            list.Add(await ViewAsync(row, ct));
        }
        return list;
    }

    /// <summary>The person, anyone with its approval task, anyone in the person's manager chain and HR officers covering them; else not found.</summary>
    public async Task<AttendanceRequestView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        if (await RowAsync(id, false, ct) is not { } row || !await MayReadAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, ct);
    }

    private async Task<bool> MayReadAsync(AttendanceRequestRow row, CancellationToken ct)
    {
        var me = context.RequireUser();
        return me == row.UserId || await DecidingTaskAsync(row, ct) is not null || await reporting.IsManagerOfAsync(me, row.UserId, ct)
            || await ledger.PersonAsync(row.UserId, ct) is { } person && await ledger.HrCoversAsync(person, ct);
    }

    internal async Task<UserTaskInfo?> DecidingTaskAsync(AttendanceRequestRow row, CancellationToken ct)
    {
        if (row.InstanceId is not { } instance || row.Status != "pending" || row.UserId == context.RequireUser())
        {
            return null;
        }
        foreach (var task in (await engine.InstanceTasksAsync(instance, ct)).Where(t => t.Status == "open"))
        {
            if (await engine.MyTaskAsync(task.Id, ct) is { } mine)
            {
                return mine;
            }
        }
        return null;
    }

    public async Task<AttendanceRequestView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrAttendance, ct);
        if (await RowAsync(id, true, ct) is not { } row || row.UserId != context.RequireUser())
        {
            throw AppException.NotFound();
        }
        if (row.Status != "pending")
        {
            throw AppException.Conflict("conflict.attendance.not_pending");
        }
        if (row.InstanceId is { } instance)
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await session.ExecAsync("update hr.attendance_request set status = 'withdrawn', updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>The approval ended: an approved correction adds its punches, an approved compensation credits the day.</summary>
    internal async Task DecidedAsync(AttendanceRequestRow row, ProcessInstanceInfo instance, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var approved = instance.Outcome == InstanceOutcomes.Approved;
        if (approved && row.Kind == AttendanceRequestKinds.Correction)
        {
            var zone = (await (row.OrgUnitId is { } u ? calendars.ForUnitAsync(u, ct) : calendars.TenantDefaultAsync(ct))).Zone;
            var before = await PunchTimesAsync(row.UserId, row.Date, zone, ct);
            foreach (var p in row.Punches)
            {
                var local = row.Date.ToDateTime(TimeOnly.ParseExact(p.Time!, "HH:mm", CultureInfo.InvariantCulture));
                var at = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
                await session.ExecAsync(
                    """
                    insert into hr.attendance_punch (tenant_id, id, user_id, at, direction, source, remote, request_id, created_at)
                    values (platform.current_tenant_id(), @id, @user, @at, @direction, 'correction', false, @request, @now)
                    """, new { id = Ids.New(), user = row.UserId, at, direction = p.Direction, request = row.Id, now }, ct);
            }
            var after = await PunchTimesAsync(row.UserId, row.Date, zone, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrAttendanceCorrected, "attendance_day", $"{row.UserId}:{row.Date:yyyy-MM-dd}", TargetOrgUnitId: row.OrgUnitId,
                Changes: new { person = row.UserId, date = row.Date, before, after, approver = decider, request = row.Id }), ct);
            await outbox.EnqueueAsync(new AttendanceCorrected(row.Id, row.UserId, row.Date), ct);
        }
        else if (approved && row.Kind == AttendanceRequestKinds.Compensation && row.LeaveTypeId is { } typeId && row.Days is { } days)
        {
            var type = await config.TypeAsync(typeId, ct);
            var year = LeaveMath.LeaveYear(row.Date, type?.YearStartMonth ?? 1);
            await session.ExecAsync(
                """
                insert into hr.leave_ledger (tenant_id, id, user_id, leave_type_id, leave_year, delta, reason, request_id, actor_id, at)
                values (platform.current_tenant_id(), @id, @user, @typeId, @year, @days, 'compensation', @request, @decider, @now)
                """, new { id = Ids.New(), user = row.UserId, typeId, year, days, request = row.Id, decider, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrCompensationCredited, "leave_balance", row.UserId.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { person = row.UserId, date = row.Date, type = type?.Key, year, days, approver = decider, request = row.Id }), ct);
        }
        else if (!approved)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrAttendanceRequestRejected, "attendance_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { row.Kind, date = row.Date, approver = decider, outcome = instance.Outcome }), ct);
        }
        await session.ExecAsync("update hr.attendance_request set status = @status, decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
            new { id = row.Id, status = approved ? "approved" : "rejected", decider, now }, ct);
        await notifications.SendAsync(new NotificationRequest([row.UserId], NotificationCategories.RequestUpdate, approved ? ApprovedTemplate : RejectedTemplate,
            Link(row.Id), new Dictionary<string, string> { ["date"] = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            DedupeKey: $"hr.attendance:{row.Id:N}:decided"), ct);
    }

    private async Task<List<string>> PunchTimesAsync(Guid userId, DateOnly date, TimeZoneInfo zone, CancellationToken ct)
    {
        var start = date.ToDateTime(TimeOnly.MinValue);
        var from = new DateTimeOffset(start, zone.GetUtcOffset(start)).ToUniversalTime();
        var end = date.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var to = new DateTimeOffset(end, zone.GetUtcOffset(end)).ToUniversalTime();
        return await session.QueryAsync("select at, direction from hr.attendance_punch where user_id = @userId and at >= @from and at < @to order by at",
            new { userId, from, to }, r => $"{TimeZoneInfo.ConvertTime(r.GetFieldValue<DateTimeOffset>(0), zone):HH:mm} {r.GetString(1)}", ct);
    }

    private async Task<AttendanceRequestView> ViewAsync(AttendanceRequestRow row, CancellationToken ct)
    {
        var name = (await users.GetAsync(row.UserId, ct))?.DisplayName ?? row.UserId.ToString();
        var typeKey = row.LeaveTypeId is { } typeId ? (await config.TypeAsync(typeId, ct))?.Key : null;
        var me = context.RequireUser();
        var rights = new AttendanceRequestRights(row.UserId == me && row.Status == "pending", await DecidingTaskAsync(row, ct) is not null);
        return new AttendanceRequestView(row.Id, row.Kind, new PersonRef(row.UserId, name), row.Date, row.Reason, row.Punches, row.Days, typeKey, row.Status,
            row.CreatedAt, row.DecidedAt, rights);
    }
}

/// <summary>
/// Attendance requests in the spec 016 task inbox (FR-026): the line manager's task links to the request; nobody
/// decides their own request, directly or as a delegate (section 6).
/// </summary>
public sealed class AttendanceTaskSource(ITenantContext context, IProcessEngine engine, AttendanceRequests requests, IReportingLine reporting,
    INotificationSender notifications, ILocalizer localizer) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public bool Handles(string definitionKey) => definitionKey == AttendanceRequests.ProcessKey;

    private async Task<AttendanceRequestRow?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct) =>
        AttendanceRequests.Parse(task.BusinessKey) is { } id && await requests.RowAsync(id, forUpdate, ct) is { Status: "pending" } row && row.InstanceId == task.InstanceId
            ? row
            : null;

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not { } row)
        {
            return null;
        }
        var date = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var key = row.Kind == AttendanceRequestKinds.Correction ? "hr.attendance.task.correction" : "hr.attendance.task.compensation";
        var title = Locales.LaunchComplete.ToDictionary(l => l, l => localizer.Get(key, l).Replace("{date}", date, StringComparison.Ordinal));
        return new TaskSubject("attendance", title, AttendanceRequests.Link(row.Id) + "?task=" + task.Id, row.OrgUnitId, row.UserId);
    }

    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is { } row && authority != row.UserId;

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not { } row || await engine.GetInstanceAsync(task.InstanceId, ct) is not { Status: "completed" } instance)
        {
            return;
        }
        await requests.DecidedAsync(row, instance, done.CompletedBy ?? context.RequireUser(), ct);
    }

    /// <summary>The line manager hears that a request waits for them; HR officers find fallback tasks in their inbox.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!Handles(message.DefinitionKey) || AttendanceRequests.Parse(message.BusinessKey) is not { } id || await requests.RowAsync(id, false, ct) is not { } row
            || await reporting.ManagerOfAsync(row.UserId, ct) is not { } manager || manager == row.UserId)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest([manager], NotificationCategories.ApprovalWaiting, AttendanceRequests.ApprovalWaiting,
            AttendanceRequests.Link(row.Id) + "?task=" + message.TaskId, new Dictionary<string, string> { ["date"] = row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }
}
