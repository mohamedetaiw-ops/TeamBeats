using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

public static class LeaveStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Cancelled = "cancelled";
    public const string Withdrawn = "withdrawn";
}

public sealed record LeaveRequestInput(
    string? TypeKey,
    DateOnly Start,
    DateOnly End,
    bool StartHalf = false,
    bool EndHalf = false,
    decimal? Hours = null,
    string? Comment = null,
    Guid? ReplacementUserId = null,
    Guid? DocumentId = null,
    Guid? OnBehalfOf = null);

public sealed record YearBalance(int LeaveYear, decimal Days, decimal Balance, decimal Pending, decimal Available, decimal After);

public sealed record OverlapPerson(Guid UserId, string DisplayName, IReadOnlyList<DateOnly> Dates, string Status, string? TypeKey);

/// <summary>What a request would take before it is submitted (US1 scenarios 1 to 4). <see cref="Problems"/> are the reasons it cannot go in.</summary>
public sealed record LeavePreview(
    string TypeKey,
    string Unit,
    decimal Days,
    IReadOnlyList<DateOnly> Dates,
    IReadOnlyList<YearBalance> Years,
    bool DocumentRequired,
    IReadOnlyList<OverlapPerson> Overlap,
    IReadOnlyList<LeaveProblem> Problems);

/// <summary>A reason a request cannot go in: the resource key, and the message in the caller's language (filled in by the endpoint).</summary>
public sealed record LeaveProblem(string Key, string Message);

public sealed record PersonRef(Guid Id, string DisplayName);

public sealed record LeaveRequestView(
    Guid Id,
    string Number,
    PersonRef Person,
    PersonRef FiledBy,
    string TypeKey,
    IReadOnlyDictionary<string, string> TypeNames,
    string Unit,
    DateOnly Start,
    DateOnly End,
    bool StartHalf,
    bool EndHalf,
    decimal Days,
    string Status,
    string? Comment,
    PersonRef? Replacement,
    string? ReplacementAnswer,
    bool HasDocument,
    string? ChangeKind,
    DateOnly? ChangeEnd,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt,
    LeaveRequestRights Rights,
    IReadOnlyList<OverlapPerson>? Overlap = null);

public sealed record LeaveRequestRights(bool Cancel, bool Shorten, bool AnswerReplacement, bool NameReplacement, bool Decide);

public sealed record MyLeaveView(IReadOnlyList<LeaveBalance> Balances, IReadOnlyList<LeaveTypeView> Types, IReadOnlyList<LeaveRequestView> Requests,
    IReadOnlyList<LeaveRequestView> ReplacementAsks);

public sealed record TeamPerson(Guid UserId, string DisplayName);

public sealed record TeamAvailabilityView(DateOnly From, DateOnly To, IReadOnlyList<TeamPerson> People, IReadOnlyList<LeaveAbsence> Absences);

public sealed record ShortenRequest(DateOnly End, bool EndHalf = false);

public sealed record ReplacementAnswer(bool Accept);

public sealed record ReplacementChange(Guid? UserId);

internal sealed record LeaveRow(
    Guid Id, string Number, Guid UserId, Guid FiledBy, Guid TypeId, Guid? OrgUnitId, DateOnly Start, DateOnly End, bool StartHalf, bool EndHalf,
    decimal Days, string Status, string? Comment, Guid? Replacement, string? ReplacementAnswer, Guid? DocumentId, Guid? InstanceId,
    string? ChangeKind, DateOnly? ChangeEnd, bool ChangeEndHalf, Guid? ChangeInstanceId, int ChangeCount, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt);

/// <summary>
/// Leave requests (spec 022 US1, FR-002 to FR-005, FR-009). A request counts working days on the person's calendar,
/// checks the balance per leave year, shows overlap with names, names a replacement who accepts or declines, and runs
/// its approval on the process engine with one line manager step whose task shows in the inbox. Approval, cancelling
/// and shortening write the ledger and publish <see cref="LeaveApproved"/> and <see cref="LeaveCancelled"/>.
/// </summary>
public sealed class LeaveRequests(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IProcessEngine engine,
    INotificationSender notifications,
    IOrganizationHierarchy hierarchy,
    IReportingLine reporting,
    IUserDirectory users,
    IWorkingCalendar calendars,
    LeaveConfig config,
    LeaveLedger ledger,
    LeaveDocuments documents) : ILeave
{
    public const string ProcessKey = "hr.leave";
    public const int MaxCommentLength = 1000;
    public const int MaxTeamDays = 62;
    public const int MaxTeamPeople = 300;

    public const string ApprovalWaiting = "hr.leave_approval_waiting";
    public const string LeaveApprovedTemplate = "hr.leave_approved";
    public const string LeaveRejectedTemplate = "hr.leave_rejected";
    public const string ChangeApprovedTemplate = "hr.leave_change_approved";
    public const string ChangeRejectedTemplate = "hr.leave_change_rejected";
    public const string ReplacementAsked = "hr.leave_replacement_asked";
    public const string ReplacementDeclined = "hr.leave_replacement_declined";

    public static string Link(Guid id) => $"/leave/requests/{id}";

    /// <summary>The request a task belongs to: its business key starts with the request ID; a change approval adds <c>:c</c> and a number.</summary>
    internal static (Guid Id, bool Change)? Parse(string businessKey) =>
        businessKey.Length >= 32 && Guid.TryParseExact(businessKey[..32], "N", out var id) ? (id, businessKey.Length > 32) : null;

    private const string Columns =
        """
        id, number, user_id, filed_by, leave_type_id, org_unit_id, start_date, end_date, start_half, end_half, days, status, comment, replacement_user_id,
        replacement_answer, document_id, instance_id, change_kind, change_end, change_end_half, change_instance_id, change_count, created_at, decided_at
        """;

    private static LeaveRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetGuid(3), r.GetGuid(4), r.Get<Guid?>("org_unit_id"), DateOnly.FromDateTime(r.GetDateTime(6)),
        DateOnly.FromDateTime(r.GetDateTime(7)), r.GetBoolean(8), r.GetBoolean(9), r.GetDecimal(10), r.GetString(11), r.Get<string?>("comment"),
        r.Get<Guid?>("replacement_user_id"), r.Get<string?>("replacement_answer"), r.Get<Guid?>("document_id"), r.Get<Guid?>("instance_id"),
        r.Get<string?>("change_kind"), r.IsDBNull(18) ? null : DateOnly.FromDateTime(r.GetDateTime(18)), r.GetBoolean(19), r.Get<Guid?>("change_instance_id"),
        r.GetInt32(21), r.GetFieldValue<DateTimeOffset>(22), r.Get<DateTimeOffset?>("decided_at"));

    internal async Task<LeaveRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from hr.leave_request where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct)).SingleOrDefault();

    // ---- Preview and submit ----

    private sealed record Plan(LeavePerson Person, LeaveTypeRow Type, IReadOnlyList<LeaveDay> Days, decimal Total, IReadOnlyList<YearBalance> Years,
        bool DocumentRequired, IReadOnlyList<string> Problems);

    /// <summary>The person a request is for: the caller, or someone they may file for (an HR officer covering them, or their manager).</summary>
    private async Task<LeavePerson> SubjectAsync(Guid? onBehalfOf, CancellationToken ct)
    {
        var me = context.RequireUser();
        var id = onBehalfOf ?? me;
        var person = await ledger.PersonAsync(id, ct);
        if (person is null || !person.Active)
        {
            throw onBehalfOf is null ? AppException.Denied() : AppException.Validation("onBehalfOf", "validation.leave.person_unknown");
        }
        if (id != me && !await ledger.HrCoversAsync(person, ct) && !await reporting.IsManagerOfAsync(me, id, ct))
        {
            throw AppException.Validation("onBehalfOf", "validation.leave.person_unknown");
        }
        return person;
    }

    private async Task<Plan> PlanAsync(LeaveRequestInput input, LeavePerson person, Guid? except, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var type = input.TypeKey is { } key ? await config.TypeByKeyAsync(key, ct) : null;
        if (type is not { Active: true } || !type.Eligible(person.MembershipType))
        {
            throw AppException.Validation("typeKey", "validation.leave.type_unknown");
        }
        var today = await ledger.TodayAsync(ct);
        var currentYear = LeaveMath.LeaveYear(today, type.YearStartMonth);
        if (input.End < input.Start)
        {
            errors.Add("end", "validation.leave.range");
        }
        else if (LeaveMath.LeaveYear(input.Start, type.YearStartMonth) < currentYear || LeaveMath.LeaveYear(input.End, type.YearStartMonth) > currentYear + 1)
        {
            errors.Add("start", "validation.leave.year");
        }
        if (type.Unit == LeaveUnits.Hour)
        {
            if (input.End != input.Start)
            {
                errors.Add("end", "validation.leave.hours_one_day");
            }
            if (input.Hours is not { } h || h <= 0 || h > 24 || decimal.Round(h * 4) != h * 4)
            {
                errors.Add("hours", "validation.leave.hours");
            }
        }
        else if (!type.HalfDays && (input.StartHalf || input.EndHalf))
        {
            errors.Add("startHalf", "validation.leave.half_days");
        }
        errors.ThrowIfAny();

        var calendar = await calendars.ForUserAsync(person.UserId, ct);
        var days = LeaveMath.Days(calendar, input.Start, input.End, input.StartHalf, input.EndHalf, type.YearStartMonth);
        if (type.Unit == LeaveUnits.Hour && days.Count == 1)
        {
            days = [days[0] with { Fraction = Math.Clamp(Math.Round(input.Hours!.Value / LeaveUnits.HoursPerDay, 2), 0.01m, 1m) }];
        }
        var total = type.Unit == LeaveUnits.Hour ? (days.Count == 1 ? input.Hours!.Value : 0m) : LeaveMath.Total(days);
        var problems = new List<string>();
        if (days.Count == 0)
        {
            problems.Add("validation.leave.no_working_days");
        }
        if (input.Start < today.AddDays(type.NoticeDays) && type.NoticeDays > 0)
        {
            problems.Add("validation.leave.notice");
        }
        if (type.MaxDaysPerRequest is { } max && total > max)
        {
            problems.Add("validation.leave.too_long");
        }
        if (await session.ScalarAsync<bool>(
            """
            select exists (select 1 from hr.leave_request where user_id = @user and status in ('pending', 'approved') and start_date <= @end and end_date >= @start
              and (@except::uuid is null or id <> @except))
            """, new { user = person.UserId, start = input.Start, end = input.End, except }, ct))
        {
            problems.Add("validation.leave.overlaps_own");
        }

        var years = new List<YearBalance>();
        var takenCurrent = 0m;
        foreach (var group in days.GroupBy(d => d.LeaveYear).OrderBy(g => g.Key))
        {
            var used = type.Unit == LeaveUnits.Hour ? total : group.Sum(d => d.Fraction);
            var (balance, pending, available) = await ledger.AvailableAsync(person, type, group.Key, today, group.Max(d => d.Date), takenCurrent, except, ct);
            if (group.Key == currentYear)
            {
                takenCurrent = used;
            }
            years.Add(new YearBalance(group.Key, used, balance, pending, available, available - used));
            if (!LeaveMath.Allowed(available, used, type.NegativeAllowed, type.NegativeMax))
            {
                problems.Add("validation.leave.balance");
            }
        }
        var documentRequired = type.DocumentAfterDays is { } after && total > after;
        return new Plan(person, type, days, total, years, documentRequired, problems.Distinct().ToList());
    }

    public async Task<LeavePreview> PreviewAsync(LeaveRequestInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var person = await SubjectAsync(input.OnBehalfOf, ct);
        var plan = await PlanAsync(input, person, null, ct);
        var overlap = await OverlapAsync(person, plan.Days.Select(d => d.Date).ToList(), null, ct);
        return new LeavePreview(plan.Type.Key, plan.Type.Unit, plan.Total, plan.Days.Select(d => d.Date).ToList(), plan.Years, plan.DocumentRequired, overlap,
            plan.Problems.Select(p => new LeaveProblem(p, p)).ToList());
    }

    /// <summary>Submits a request (US1). The same idempotency key from the same person returns the request already made.</summary>
    public async Task<LeaveRequestView> SubmitAsync(LeaveRequestInput input, string? idempotencyKey, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var me = context.RequireUser();
        var person = await SubjectAsync(input.OnBehalfOf, ct);
        if (idempotencyKey is { Length: > 0 and <= 100 } k
            && await session.ScalarAsync<Guid?>("select id from hr.leave_request where user_id = @user and idempotency_key = @k", new { user = person.UserId, k }, ct) is { } done)
        {
            return await GetAsync(done, ct);
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('hr.leave:' || @user::text))", new { user = person.UserId }, ct);
        var plan = await PlanAsync(input, person, null, ct);
        var errors = new ValidationErrors();
        foreach (var problem in plan.Problems)
        {
            errors.Add(problem == "validation.leave.balance" ? "end" : "start", problem);
        }
        var comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim();
        if (comment?.Length > MaxCommentLength)
        {
            errors.Add("comment", "validation.leave.comment");
        }
        if (input.ReplacementUserId is { } replacement && (replacement == person.UserId || await ledger.PersonAsync(replacement, ct) is not { Active: true }))
        {
            errors.Add("replacementUserId", "validation.leave.replacement");
        }
        if (plan.DocumentRequired && input.DocumentId is null)
        {
            errors.Add("documentId", "validation.leave.document_required");
        }
        if (input.DocumentId is { } doc && await documents.ProblemAsync(doc, ct) is { } docProblem)
        {
            errors.Add("documentId", docProblem);
        }
        errors.ThrowIfAny();

        var id = Ids.New();
        var now = clock.UtcNow;
        var number = await NextNumberAsync(ct);
        await session.ExecAsync(
            """
            insert into hr.leave_request (tenant_id, id, number, user_id, filed_by, leave_type_id, org_unit_id, start_date, end_date, start_half, end_half, days, status,
              comment, replacement_user_id, document_id, idempotency_key, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @number, @user, @me, @type, @unit, @start, @end, @startHalf, @endHalf, @days, 'pending', @comment, @replacement,
              @doc, @key, @now, @now)
            """, new
            {
                id, number, user = person.UserId, me, type = plan.Type.Id, unit = person.PrimaryUnit, start = input.Start, end = input.End,
                startHalf = input.StartHalf, endHalf = plan.Type.Unit == LeaveUnits.Hour ? false : input.EndHalf, days = plan.Total, comment,
                replacement = input.ReplacementUserId, doc = input.DocumentId, key = idempotencyKey is { Length: > 0 and <= 100 } ? idempotencyKey : null, now,
            }, ct);
        await InsertDaysAsync(id, plan.Days, ct);
        if (input.DocumentId is { } attached)
        {
            await documents.LinkAsync(attached, id, ct);
        }
        var instance = await engine.StartAsync(await RouteAsync(ct), id.ToString("N"), person.PrimaryUnit, me, [person.UserId], person.UserId, ct: ct);
        await session.ExecAsync("update hr.leave_request set instance_id = @instance where id = @id", new { id, instance = instance.Id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveSubmitted, "leave_request", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { number, type = plan.Type.Key, start = input.Start, end = input.End, days = plan.Total, person = person.UserId }), ct);
        if (person.UserId != me)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveOnBehalf, "leave_request", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
                Changes: new { number, person = person.UserId, filedBy = me }), ct);
        }
        if (input.ReplacementUserId is { } named)
        {
            await notifications.SendAsync(new NotificationRequest([named], NotificationCategories.RequestUpdate, ReplacementAsked, Link(id),
                DedupeKey: $"hr.leave:{id:N}:replacement:{named:N}"), ct);
        }
        return await GetAsync(id, ct);
    }

    private async Task<string> NextNumberAsync(CancellationToken ct)
    {
        var n = await session.ScalarAsync<int>(
            """
            insert into hr.leave_counter (tenant_id, next_number) values (platform.current_tenant_id(), 2)
            on conflict (tenant_id) do update set next_number = hr.leave_counter.next_number + 1
            returning next_number - 1
            """, null, ct);
        return $"LV-{n:000000}";
    }

    private async Task InsertDaysAsync(Guid id, IReadOnlyList<LeaveDay> days, CancellationToken ct)
    {
        if (days.Count == 0)
        {
            return;
        }
        await session.ExecAsync(
            """
            insert into hr.leave_request_day (tenant_id, request_id, date, fraction, leave_year)
            select platform.current_tenant_id(), @id, d, f, y from unnest(@dates, @fractions, @years) as x(d, f, y)
            """, new { id, dates = days.Select(d => d.Date).ToArray(), fractions = days.Select(d => d.Fraction).ToArray(), years = days.Select(d => d.LeaveYear).ToArray() }, ct);
    }

    /// <summary>One line manager step; with no manager, or when the manager filed the request, HR officers of the unit decide.</summary>
    private async Task<string> RouteAsync(CancellationToken ct)
    {
        if (await engine.GetDefinitionAsync(ProcessKey, null, ct) is null)
        {
            await engine.PublishRouteAsync(ProcessKey, RouteSources.LeaveRoute,
                new RouteSpec("leave approval", [new RouteStep("manager", "Line manager", new CandidateRule(Permission: Permissions.HrManage, LineManager: true))]), ct);
        }
        return ProcessKey;
    }

    // ---- Reading ----

    private async Task<IReadOnlyDictionary<Guid, PersonRef>> PeopleRefsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var found = await users.GetManyAsync(ids.Distinct().ToList(), ct);
        return found.ToDictionary(u => u.Key, u => new PersonRef(u.Key, u.Value.DisplayName));
    }

    private enum Access { None, Person, Replacement, Approver, Hr }

    private async Task<Access> AccessAsync(LeaveRow row, CancellationToken ct)
    {
        var me = context.RequireUser();
        if (row.UserId == me || row.FiledBy == me)
        {
            return Access.Person;
        }
        if (await ledger.PersonAsync(row.UserId, ct) is { } person && await ledger.HrCoversAsync(person, ct))
        {
            return Access.Hr;
        }
        if (await DecidingTaskAsync(row, ct) is not null || await reporting.IsManagerOfAsync(me, row.UserId, ct))
        {
            return Access.Approver;
        }
        return row.Replacement == me ? Access.Replacement : Access.None;
    }

    /// <summary>The open approval task of the request or its change that the caller may complete, if any.</summary>
    internal async Task<UserTaskInfo?> DecidingTaskAsync(LeaveRow row, CancellationToken ct)
    {
        foreach (var instance in new[] { row.InstanceId, row.ChangeInstanceId }.OfType<Guid>())
        {
            foreach (var task in (await engine.InstanceTasksAsync(instance, ct)).Where(t => t.Status == "open"))
            {
                if (await engine.MyTaskAsync(task.Id, ct) is { } mine)
                {
                    return mine;
                }
            }
        }
        return null;
    }

    public async Task<LeaveRequestView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var access = await AccessAsync(row, ct);
        if (access == Access.None)
        {
            throw AppException.NotFound();
        }
        var view = await ViewAsync(row, access, ct);
        if (access is Access.Person or Access.Approver or Access.Hr && await ledger.PersonAsync(row.UserId, ct) is { } person)
        {
            var dates = await session.QueryAsync("select date from hr.leave_request_day where request_id = @id order by date", new { id },
                r => DateOnly.FromDateTime(r.GetDateTime(0)), ct);
            view = view with { Overlap = await OverlapAsync(person, dates, row.Id, ct) };
        }
        return view;
    }

    /// <summary>The supporting document, for the person, their approver and HR officers; never for the replacement.</summary>
    public async Task<(Stream Content, string ContentType, string FileName)> DocumentAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        if (await AccessAsync(row, ct) is Access.None or Access.Replacement || row.DocumentId is null)
        {
            throw AppException.NotFound();
        }
        return await documents.OpenAsync(id, ct);
    }

    private async Task<LeaveRequestView> ViewAsync(LeaveRow row, Access access, CancellationToken ct)
    {
        var type = await config.TypeAsync(row.TypeId, ct);
        var refs = await PeopleRefsAsync(new[] { row.UserId, row.FiledBy }.Concat(row.Replacement is { } r ? [r] : []), ct);
        var today = await ledger.TodayAsync(ct);
        var me = context.RequireUser();
        var mayChange = access is Access.Person or Access.Hr && row.ChangeKind is null;
        var rights = new LeaveRequestRights(
            Cancel: mayChange && (row.Status == LeaveStatuses.Pending || (row.Status == LeaveStatuses.Approved && (row.Start > today || access == Access.Hr))),
            Shorten: mayChange && row.Status == LeaveStatuses.Approved && row.End > row.Start && row.End >= today,
            AnswerReplacement: row.Replacement == me && row.ReplacementAnswer is null && row.Status is LeaveStatuses.Pending or LeaveStatuses.Approved,
            NameReplacement: access == Access.Person && row.Status is LeaveStatuses.Pending or LeaveStatuses.Approved
                && (row.Replacement is null || row.ReplacementAnswer == "declined"),
            Decide: await DecidingTaskAsync(row, ct) is not null);
        // Colleagues named as the replacement see the dates, not the type, of a sensitive leave.
        var showType = access != Access.Replacement || type is { Sensitive: false };
        return new LeaveRequestView(row.Id, row.Number, refs.GetValueOrDefault(row.UserId) ?? new PersonRef(row.UserId, string.Empty),
            refs.GetValueOrDefault(row.FiledBy) ?? new PersonRef(row.FiledBy, string.Empty), showType ? type!.Key : string.Empty,
            showType ? type!.Names : new Dictionary<string, string>(), type!.Unit, row.Start, row.End, row.StartHalf, row.EndHalf, row.Days, row.Status,
            access == Access.Replacement ? null : row.Comment, row.Replacement is { } rep ? refs.GetValueOrDefault(rep) ?? new PersonRef(rep, string.Empty) : null,
            row.ReplacementAnswer, row.DocumentId is not null && access != Access.Replacement, row.ChangeKind, row.ChangeEnd, row.CreatedAt, row.DecidedAt, rights);
    }

    public async Task<MyLeaveView> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var me = context.RequireUser();
        var balances = await MyBalancesAsync(ct);
        var person = await ledger.PersonAsync(me, ct);
        var types = (await config.TypesAsync(ct)).Where(t => t.Active && (person is null || t.Eligible(person.MembershipType))).Select(t => t.View()).ToList();
        var rows = await session.QueryAsync($"select {Columns} from hr.leave_request where user_id = @me order by start_date desc, created_at desc limit 100", new { me }, Read, ct);
        var requests = new List<LeaveRequestView>();
        foreach (var row in rows)
        {
            requests.Add(await ViewAsync(row, Access.Person, ct));
        }
        var asks = new List<LeaveRequestView>();
        foreach (var row in await OpenReplacementAsksAsync(me, ct))
        {
            asks.Add(await ViewAsync(row, Access.Replacement, ct));
        }
        return new MyLeaveView(balances, types, requests, asks);
    }

    private async Task<List<LeaveRow>> OpenReplacementAsksAsync(Guid userId, CancellationToken ct) =>
        await session.QueryAsync(
            $"""
            select {Columns} from hr.leave_request
            where replacement_user_id = @userId and replacement_answer is null and status in ('pending', 'approved') and end_date >= @today
            order by start_date limit 50
            """, new { userId, today = await ledger.TodayAsync(ct) }, Read, ct);

    internal async Task<IReadOnlyList<PersonalTask>> ReplacementTasksAsync(Guid userId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            return [];
        }
        var tasks = new List<PersonalTask>();
        foreach (var row in await OpenReplacementAsksAsync(userId, ct))
        {
            var name = (await users.GetAsync(row.UserId, ct))?.DisplayName ?? string.Empty;
            tasks.Add(new PersonalTask("leave_replacement", row.Id, new Dictionary<string, string> { ["en"] = $"{row.Number} {name}", ["ar"] = $"{row.Number} {name}" },
                Link(row.Id), row.CreatedAt, null));
        }
        return tasks;
    }

    public async Task<IReadOnlyList<LeaveBalance>> MyBalancesAsync(CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            return [];
        }
        var me = context.RequireUser();
        var person = await ledger.PersonAsync(me, ct);
        var today = await ledger.TodayAsync(ct);
        var list = new List<LeaveBalance>();
        foreach (var type in (await config.TypesAsync(ct)).Where(t => t.Active && (person is null || t.Eligible(person.MembershipType))))
        {
            var year = LeaveMath.LeaveYear(today, type.YearStartMonth);
            var balance = await ledger.BalanceAsync(me, type.Id, year, ct);
            var pending = await ledger.PendingAsync(me, type.Id, year, null, ct);
            list.Add(new LeaveBalance(type.Key, type.Names, year, balance, pending, balance - pending, type.Unit));
        }
        return list;
    }

    // ---- Overlap and team availability ----

    /// <summary>The person's colleagues: members of their primary unit, people with the same manager, and their own reports.</summary>
    private async Task<IReadOnlySet<Guid>> ColleaguesAsync(LeavePerson person, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        if (person.PrimaryUnit is { } unit)
        {
            set.UnionWith(await hierarchy.GetMemberUserIdsAsync([unit], ct));
        }
        if (await reporting.ManagerOfAsync(person.UserId, ct) is { } manager)
        {
            set.UnionWith(await reporting.DirectReportsAsync(manager, ct));
        }
        set.UnionWith(await reporting.DirectReportsAsync(person.UserId, ct));
        set.Remove(person.UserId);
        return set;
    }

    private async Task<IReadOnlyList<OverlapPerson>> OverlapAsync(LeavePerson person, IReadOnlyList<DateOnly> dates, Guid? except, CancellationToken ct)
    {
        if (dates.Count == 0)
        {
            return [];
        }
        var colleagues = await ColleaguesAsync(person, ct);
        if (colleagues.Count == 0)
        {
            return [];
        }
        var absences = await AbsencesAsync(colleagues.ToList(), dates.Min(), dates.Max(), includePending: true, ct);
        var wanted = dates.ToHashSet();
        var names = await PeopleRefsAsync(absences.Select(a => a.UserId), ct);
        return absences.Where(a => wanted.Contains(a.Date)).GroupBy(a => a.UserId)
            .Select(g => new OverlapPerson(g.Key, names.GetValueOrDefault(g.Key)?.DisplayName ?? string.Empty, g.Select(a => a.Date).Distinct().Order().ToList(),
                g.Any(a => a.Status == LeaveStatuses.Approved) ? LeaveStatuses.Approved : LeaveStatuses.Pending, g.Select(a => a.TypeKey).FirstOrDefault(t => t is not null)))
            .OrderBy(o => o.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// Leave days of people between two dates. The type is shown when it is not sensitive, or to the person, anyone in
    /// their manager chain, and HR officers covering them (section 6).
    /// </summary>
    private async Task<IReadOnlyList<LeaveAbsence>> AbsencesAsync(IReadOnlyList<Guid> userIds, DateOnly from, DateOnly to, bool includePending, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select r.user_id, d.date, d.fraction, r.status, t.key, t.sensitive
            from hr.leave_request r join hr.leave_request_day d on d.request_id = r.id join hr.leave_type t on t.id = r.leave_type_id
            where r.user_id = any(@userIds) and d.date between @from and @to and (r.status = 'approved' or (@pending and r.status = 'pending'))
            order by d.date, r.user_id
            """, new { userIds = userIds.ToArray(), from, to, pending = includePending },
            r => (User: r.GetGuid(0), Date: DateOnly.FromDateTime(r.GetDateTime(1)), Fraction: r.GetDecimal(2), Status: r.GetString(3), Key: r.GetString(4),
                Sensitive: r.GetBoolean(5)), ct);
        var me = context.RequireUser();
        var seeTypes = new Dictionary<Guid, bool>();
        var people = rows.Any(r => r.Sensitive) ? await ledger.PeopleAsync(rows.Where(r => r.Sensitive).Select(r => r.User).Distinct().ToList(), ct) : null;
        var list = new List<LeaveAbsence>();
        foreach (var r in rows)
        {
            var visible = !r.Sensitive;
            if (r.Sensitive)
            {
                if (!seeTypes.TryGetValue(r.User, out visible))
                {
                    visible = r.User == me || await reporting.IsManagerOfAsync(me, r.User, ct)
                        || (people?.GetValueOrDefault(r.User) is { } p && await ledger.HrCoversAsync(p, ct));
                    seeTypes[r.User] = visible;
                }
            }
            list.Add(new LeaveAbsence(r.User, r.Date, r.Fraction, r.Status, visible ? r.Key : null));
        }
        return list;
    }

    public async Task<IReadOnlyList<LeaveAbsence>> AvailabilityAsync(IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct) || userIds.Count == 0 || to < from || to.DayNumber - from.DayNumber >= 366)
        {
            return [];
        }
        return await AbsencesAsync(userIds.ToList(), from, to, includePending: false, ct);
    }

    /// <summary>
    /// The team view (FR-009): by default the viewer's colleagues and reports; for a unit, its members when the viewer
    /// belongs to it, or its whole subtree for an HR officer covering it.
    /// </summary>
    public async Task<TeamAvailabilityView> TeamAsync(DateOnly from, DateOnly to, Guid? orgUnitId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var me = context.RequireUser();
        if (to < from || to.DayNumber - from.DayNumber >= MaxTeamDays)
        {
            throw AppException.Validation("to", "validation.leave.team_range");
        }
        HashSet<Guid> people;
        if (orgUnitId is { } unit)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                throw AppException.NotFound();
            }
            var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
            if (coverage.Covers(unit))
            {
                people = [.. await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync([unit], ct), ct)];
            }
            else if ((await hierarchy.GetActiveMembershipsAsync(me, ct)).Any(m => m.OrgUnitId == unit))
            {
                people = [.. await hierarchy.GetMemberUserIdsAsync([unit], ct)];
            }
            else
            {
                throw AppException.NotFound();
            }
        }
        else
        {
            var person = await ledger.PersonAsync(me, ct) ?? throw AppException.Denied();
            people = [.. await ColleaguesAsync(person, ct), .. await reporting.DirectReportsAsync(me, ct), me];
        }
        var ids = people.Take(MaxTeamPeople).ToList();
        var names = await PeopleRefsAsync(ids, ct);
        var team = ids.Where(names.ContainsKey).Select(id => new TeamPerson(id, names[id].DisplayName))
            .OrderBy(p => p.UserId == me ? 0 : 1).ThenBy(p => p.DisplayName, StringComparer.CurrentCulture).ToList();
        return new TeamAvailabilityView(from, to, team, await AbsencesAsync(team.Select(p => p.UserId).ToList(), from, to, includePending: true, ct));
    }

    // ---- Cancel, shorten and replacement ----

    private async Task<(LeaveRow Row, LeaveTypeRow Type, Access Access)> ChangeableAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        var access = await AccessAsync(row, ct);
        if (access == Access.None)
        {
            throw AppException.NotFound();
        }
        if (access is not (Access.Person or Access.Hr))
        {
            throw AppException.Denied();
        }
        if (row.ChangeKind is not null)
        {
            throw AppException.Conflict("conflict.leave.change_pending");
        }
        return (row, (await config.TypeAsync(row.TypeId, ct))!, access);
    }

    /// <summary>
    /// Withdraws a pending request, or cancels approved leave (US1 scenario 7): at once, or after approval when the type
    /// says so. Leave that started can be cancelled only by HR; the person shortens it instead.
    /// </summary>
    public async Task<LeaveRequestView> CancelAsync(Guid id, CancellationToken ct)
    {
        var (row, type, access) = await ChangeableAsync(id, ct);
        var now = clock.UtcNow;
        if (row.Status == LeaveStatuses.Pending)
        {
            if (row.InstanceId is { } instance)
            {
                await engine.WithdrawAsync(instance, ct);
            }
            await session.ExecAsync("update hr.leave_request set status = 'withdrawn', updated_at = @now where id = @id", new { id, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveCancelled, "leave_request", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { row.Number, from = LeaveStatuses.Pending, to = LeaveStatuses.Withdrawn }), ct);
            return await GetAsync(id, ct);
        }
        if (row.Status != LeaveStatuses.Approved)
        {
            throw AppException.Conflict("conflict.leave.not_open");
        }
        if (row.Start <= await ledger.TodayAsync(ct) && access != Access.Hr)
        {
            throw AppException.Conflict("conflict.leave.started");
        }
        if (type.CancelNeedsApproval && access != Access.Hr)
        {
            await StartChangeAsync(row, "cancel", null, ct);
            return await GetAsync(id, ct);
        }
        await ApplyCancelAsync(row, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Ends approved leave earlier (US1 scenario 7); the days after the new end go back to the balance.</summary>
    public async Task<LeaveRequestView> ShortenAsync(Guid id, ShortenRequest request, CancellationToken ct)
    {
        var (row, type, access) = await ChangeableAsync(id, ct);
        if (row.Status != LeaveStatuses.Approved)
        {
            throw AppException.Conflict("conflict.leave.not_open");
        }
        if (request.End < row.Start || request.End > row.End || (request.End == row.End && (request.EndHalf == row.EndHalf || !request.EndHalf))
            || (request.EndHalf && (!type.HalfDays || type.Unit == LeaveUnits.Hour)))
        {
            throw AppException.Validation("end", "validation.leave.shorten");
        }
        if (type.CancelNeedsApproval && access != Access.Hr)
        {
            await StartChangeAsync(row, "shorten", request.End, ct, request.EndHalf);
            return await GetAsync(id, ct);
        }
        await ApplyShortenAsync(row, type, request.End, request.EndHalf, ct);
        return await GetAsync(id, ct);
    }

    private async Task StartChangeAsync(LeaveRow row, string kind, DateOnly? end, CancellationToken ct, bool endHalf = false)
    {
        var n = row.ChangeCount + 1;
        var instance = await engine.StartAsync(await RouteAsync(ct), $"{row.Id:N}:c{n}", row.OrgUnitId, context.RequireUser(), [row.UserId], row.UserId, ct: ct);
        await session.ExecAsync(
            """
            update hr.leave_request set change_kind = @kind, change_end = @end, change_end_half = @endHalf, change_instance_id = @instance, change_count = @n,
              updated_at = @now
            where id = @id
            """, new { id = row.Id, kind, end, endHalf, instance = instance.Id, n, now = clock.UtcNow }, ct);
    }

    private async Task ApplyCancelAsync(LeaveRow row, CancellationToken ct)
    {
        var perYear = await session.QueryAsync(
            "select leave_year, sum(fraction) from hr.leave_request_day where request_id = @id group by leave_year", new { id = row.Id },
            r => (Year: r.GetInt32(0), Days: r.GetDecimal(1)), ct);
        var type = await config.TypeAsync(row.TypeId, ct);
        if (type!.Unit == LeaveUnits.Hour)
        {
            perYear = perYear.Select(p => (p.Year, row.Days)).ToList();
        }
        await ledger.PostRequestAsync(row.UserId, row.TypeId, row.Id, "cancel", perYear, ct);
        await session.ExecAsync("update hr.leave_request set status = 'cancelled', change_kind = null, change_end = null, change_end_half = false, change_instance_id = null, updated_at = @now where id = @id",
            new { id = row.Id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveCancelled, "leave_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Number, from = LeaveStatuses.Approved, to = LeaveStatuses.Cancelled, days = row.Days }), ct);
        await outbox.EnqueueAsync(new LeaveCancelled(row.Id, row.UserId, row.Start, row.End), ct);
    }

    private async Task ApplyShortenAsync(LeaveRow row, LeaveTypeRow type, DateOnly end, bool endHalf, CancellationToken ct)
    {
        var calendar = await calendars.ForUserAsync(row.UserId, ct);
        var keep = LeaveMath.Days(calendar, row.Start, end, row.StartHalf, endHalf, type.YearStartMonth);
        var before = await session.QueryAsync("select date, fraction, leave_year from hr.leave_request_day where request_id = @id", new { id = row.Id },
            r => new LeaveDay(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetDecimal(1), r.GetInt32(2)), ct);
        var kept = keep.ToDictionary(d => d.Date);
        var returned = before.Select(d => (d.LeaveYear, Days: d.Fraction - (kept.TryGetValue(d.Date, out var k) ? Math.Min(k.Fraction, d.Fraction) : 0m)))
            .Where(d => d.Days > 0).GroupBy(d => d.LeaveYear).Select(g => (g.Key, g.Sum(d => d.Days))).ToList();
        var newDays = before.Sum(d => d.Fraction) - returned.Sum(r => r.Item2);
        await ledger.PostRequestAsync(row.UserId, row.TypeId, row.Id, "cancel", returned, ct);
        await session.ExecAsync("delete from hr.leave_request_day where request_id = @id", new { id = row.Id }, ct);
        await InsertDaysAsync(row.Id, before.Where(d => kept.ContainsKey(d.Date)).Select(d => d with { Fraction = Math.Min(kept[d.Date].Fraction, d.Fraction) }).ToList(), ct);
        await session.ExecAsync(
            """
            update hr.leave_request set end_date = @end, end_half = @endHalf, days = @days, change_kind = null, change_end = null, change_end_half = false, change_instance_id = null, updated_at = @now
            where id = @id
            """, new { id = row.Id, end, endHalf, days = newDays, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrLeaveShortened, "leave_request", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Number, previousEnd = row.End, end, daysBefore = row.Days, days = newDays }), ct);
        var returnedFrom = endHalf ? end : end.AddDays(1);
        await outbox.EnqueueAsync(new LeaveCancelled(row.Id, row.UserId, returnedFrom, row.End), ct);
    }

    /// <summary>The replacement accepts or declines (US1 scenario 5); a decline tells the requester, who can name someone else.</summary>
    public async Task<LeaveRequestView> AnswerReplacementAsync(Guid id, ReplacementAnswer answer, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var me = context.RequireUser();
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (row.Replacement != me)
        {
            throw AppException.NotFound();
        }
        if (row.ReplacementAnswer is not null || row.Status is not (LeaveStatuses.Pending or LeaveStatuses.Approved))
        {
            throw AppException.Conflict("conflict.leave.replacement_answered");
        }
        var value = answer.Accept ? "accepted" : "declined";
        await session.ExecAsync("update hr.leave_request set replacement_answer = @value, replacement_answered_at = @now, updated_at = @now where id = @id",
            new { id, value, now = clock.UtcNow }, ct);
        if (!answer.Accept)
        {
            await notifications.SendAsync(new NotificationRequest(new[] { row.UserId, row.FiledBy }.Distinct().ToArray(), NotificationCategories.RequestUpdate,
                ReplacementDeclined, Link(id), DedupeKey: $"hr.leave:{id:N}:declined:{me:N}"), ct);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>The requester names a replacement, or another one after a decline.</summary>
    public async Task<LeaveRequestView> NameReplacementAsync(Guid id, ReplacementChange change, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrLeave, ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        if (await AccessAsync(row, ct) != Access.Person)
        {
            throw AppException.NotFound();
        }
        if (row.Status is not (LeaveStatuses.Pending or LeaveStatuses.Approved) || (row.Replacement is not null && row.ReplacementAnswer != "declined"))
        {
            throw AppException.Conflict("conflict.leave.replacement_answered");
        }
        if (change.UserId is { } named && (named == row.UserId || named == row.Replacement || await ledger.PersonAsync(named, ct) is not { Active: true }))
        {
            throw AppException.Validation("userId", "validation.leave.replacement");
        }
        await session.ExecAsync(
            "update hr.leave_request set replacement_user_id = @named, replacement_answer = null, replacement_answered_at = null, updated_at = @now where id = @id",
            new { id, named = change.UserId, now = clock.UtcNow }, ct);
        if (change.UserId is { } who)
        {
            await notifications.SendAsync(new NotificationRequest([who], NotificationCategories.RequestUpdate, ReplacementAsked, Link(id),
                DedupeKey: $"hr.leave:{id:N}:replacement:{who:N}"), ct);
        }
        return await GetAsync(id, ct);
    }

    // ---- Decisions (from the task inbox) ----

    internal async Task DecidedAsync(LeaveRow row, bool change, ProcessInstanceInfo instance, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var approved = instance.Outcome == InstanceOutcomes.Approved;
        var recipients = new[] { row.UserId, row.FiledBy }.Distinct().ToArray();
        if (change)
        {
            if (approved && row.ChangeKind == "cancel")
            {
                await ApplyCancelAsync(row, ct);
            }
            else if (approved && row.ChangeKind == "shorten" && row.ChangeEnd is { } end)
            {
                await ApplyShortenAsync(row, (await config.TypeAsync(row.TypeId, ct))!, end, row.ChangeEndHalf, ct);
            }
            else
            {
                await session.ExecAsync("update hr.leave_request set change_kind = null, change_end = null, change_end_half = false, change_instance_id = null, updated_at = @now where id = @id",
                    new { id = row.Id, now }, ct);
            }
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.RequestUpdate,
                approved ? ChangeApprovedTemplate : ChangeRejectedTemplate, Link(row.Id), DedupeKey: $"hr.leave:{row.Id:N}:change:{row.ChangeCount}"), ct);
            return;
        }
        if (approved)
        {
            var perYear = await session.QueryAsync("select leave_year, sum(fraction) from hr.leave_request_day where request_id = @id group by leave_year",
                new { id = row.Id }, r => (Year: r.GetInt32(0), Days: r.GetDecimal(1)), ct);
            var type = await config.TypeAsync(row.TypeId, ct);
            if (type!.Unit == LeaveUnits.Hour)
            {
                perYear = perYear.Select(p => (p.Year, row.Days)).ToList();
            }
            await ledger.PostRequestAsync(row.UserId, row.TypeId, row.Id, "request", perYear, ct);
        }
        await session.ExecAsync("update hr.leave_request set status = @status, decided_by = @decider, decided_at = @now, updated_at = @now where id = @id",
            new { id = row.Id, status = approved ? LeaveStatuses.Approved : LeaveStatuses.Rejected, decider, now }, ct);
        await audit.WriteAsync(new AuditEntry(approved ? AuditActions.HrLeaveApproved : AuditActions.HrLeaveRejected, "leave_request", row.Id.ToString(),
            TargetOrgUnitId: row.OrgUnitId, Changes: new { row.Number, days = row.Days, decider, outcome = instance.Outcome }), ct);
        if (approved)
        {
            await outbox.EnqueueAsync(new LeaveApproved(row.Id, row.UserId, row.Start, row.End), ct);
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.RequestUpdate,
            approved ? LeaveApprovedTemplate : LeaveRejectedTemplate, Link(row.Id), DedupeKey: $"hr.leave:{row.Id:N}:decided"), ct);
    }
}
