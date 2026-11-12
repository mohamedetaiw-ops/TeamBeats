using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

/// <summary>A line of an access removal checklist: still pending, removed, or not applicable to the person.</summary>
public static class AccessItemStatuses
{
    public const string Pending = "pending";
    public const string Removed = "removed";
    public const string NotApplicable = "not_applicable";

    public static readonly IReadOnlyList<string> Confirmed = [Removed, NotApplicable];
}

/// <summary>The structured reasons of an exit interview (spec 022 FR-018), fixed keys so they can be counted (spec 021 US3 scenario 3).</summary>
public static class ExitReasons
{
    public static readonly IReadOnlyList<string> All =
    [
        "compensation", "career_growth", "management", "work_life_balance", "workload", "culture", "role_fit", "relocation", "family", "studies",
        "health", "retirement", "other",
    ];

    public const int MaxChosen = 5;
}

public sealed record AccessItemView(Guid Id, string Name, string Kind, string Status, string? Note, PersonRef? ConfirmedBy, DateTimeOffset? ConfirmedAt, DateTimeOffset? EscalatedAt);

public sealed record AccessConfirmRequest(string? Status, string? Note);

public sealed record ExitInterviewRequest(bool Declined, IReadOnlyList<string>? Reasons, string? PrimaryReason, int? Rating, bool? WouldReturn, string? Comment);

/// <summary>An exit interview as the viewer may see it: <see cref="Comment"/> only for HR officers covering the person (FR-018).</summary>
public sealed record ExitInterviewView(bool Declined, IReadOnlyList<string> Reasons, string? PrimaryReason, int? Rating, bool? WouldReturn, string? Comment,
    PersonRef SubmittedBy, DateTimeOffset SubmittedAt);

public sealed record ExitReasonCount(string Reason, int Chosen, int Primary);

/// <summary>Exit interviews of one unit, or of the units merged into "other" (a null unit) below the minimum group size.</summary>
public sealed record ExitReasonGroup(Guid? OrgUnitId, int Interviews, IReadOnlyList<ExitReasonCount> Reasons);

/// <summary>
/// Counts of exit reasons in a period for the units the HR officer covers (spec 021 US3 scenario 3). A unit with fewer
/// than <see cref="MinimumGroupSize"/> interviews is merged into "other"; when that is still too few it is left out
/// and counted in <see cref="Hidden"/>. Free text is never part of it.
/// </summary>
public sealed record ExitReasonsView(DateOnly From, DateOnly To, int MinimumGroupSize, int Interviews, int Declined, IReadOnlyList<ExitReasonCount> Reasons,
    IReadOnlyList<ExitReasonGroup> Units, int Hidden);

internal sealed record OpenedStep(Guid Journey, string Key, string Type, Guid User, Guid? Unit, Guid Instance, DateTimeOffset? DueAt, string[] Systems, Guid[] Courses);

/// <summary>
/// The typed journey steps of T019: attestation (spec 025 FR-017), the access removal checklist with its escalation
/// (FR-017) and the exit interview (FR-018).
/// </summary>
public sealed partial class JourneyService
{
    public const int MaxNoteLength = 500;
    public const int MaxCommentLength = 2000;
    public const int MinimumGroupSize = 5;
    public const int DefaultAttestationWorkingDays = 5;
    public const string AccessEscalatedTemplate = "hr.access_removal_escalated";

    /// <summary>What a step needs when it opens: the person's attestations, or the checklist lines.</summary>
    private async Task OpenedAsync(OpenedStep step, CancellationToken ct)
    {
        if (step.Type == JourneyStepTypes.Attestation)
        {
            var due = step.DueAt ?? await EndOfWorkingDaysAsync(step.Unit, DefaultAttestationWorkingDays, ct);
            if (await obligations.RequireOnboardingAsync(step.User, BusinessKey(step.Journey, step.Key), due, ct) == 0)
            {
                // Nothing to attest to: the step is not needed.
                await engine.WithdrawAsync(step.Instance, ct);
                await CloseStepAsync((await RowAsync(step.Journey, true, ct))!, step.Key, JourneyStepOutcomes.Skipped, null, null, ct);
            }
        }
        else if (step.Type == JourneyStepTypes.Training)
        {
            var due = step.DueAt ?? await EndOfWorkingDaysAsync(step.Unit, DefaultAttestationWorkingDays, ct);
            var calendar = await calendars.ForUserAsync(step.User, ct);
            // The step's deadline is the end of a day; the course is due on the last day before it.
            var dueOn = calendar.Today(due.AddTicks(-1));
            if (await training.RequireAsync([step.User], step.Courses, "onboarding", BusinessKey(step.Journey, step.Key), dueOn, null, ct) == 0)
            {
                // Already completed, or nothing left to take (retired courses, no learning in the plan): the step is not needed.
                await engine.WithdrawAsync(step.Instance, ct);
                await CloseStepAsync((await RowAsync(step.Journey, true, ct))!, step.Key, JourneyStepOutcomes.Skipped, null, null, ct);
            }
        }
        else if (step.Type == JourneyStepTypes.AccessRemoval)
        {
            var lines = (await systems.ListAsync(ct)).Select(c => (Connection: (Guid?)c.ConnectionId, c.DisplayName))
                .Concat(step.Systems.Select(n => (Connection: (Guid?)null, DisplayName: n))).ToList();
            var now = clock.UtcNow;
            for (var i = 0; i < lines.Count; i++)
            {
                await session.ExecAsync(
                    """
                    insert into hr.journey_access_item (tenant_id, id, journey_id, step_key, position, connection_id, name, status, created_at)
                    values (platform.current_tenant_id(), @id, @journey, @key, @i, @connection, @name, 'pending', @now)
                    """, new { id = Ids.New(), journey = step.Journey, key = step.Key, i, connection = lines[i].Connection, name = lines[i].DisplayName, now }, ct);
            }
        }
    }

    private async Task<DateTimeOffset> EndOfWorkingDaysAsync(Guid? unit, int days, CancellationToken ct)
    {
        var calendar = unit is { } u ? await calendars.ForUnitAsync(u, ct) : await calendars.TenantDefaultAsync(ct);
        var last = calendar.AddWorkingDays(calendar.Today(clock.UtcNow), days);
        var midnight = last.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, calendar.Zone.GetUtcOffset(midnight));
    }

    private async Task<JourneyStepRow?> StepAsync(Guid journeyId, string key, bool forUpdate, CancellationToken ct) =>
        (await session.QueryAsync($"select {StepColumns} from hr.journey_step where journey_id = @journeyId and key = @key{(forUpdate ? " for update" : "")}",
            new { journeyId, key }, ReadStep, ct)).SingleOrDefault();

    /// <summary>
    /// The open step of the type whose task the current user holds, with its journey. Anyone else who may read the
    /// journey is denied; the rest get not found.
    /// </summary>
    private async Task<(JourneyRow Journey, JourneyStepRow Step, UserTaskInfo Task)> HeldStepAsync(Guid id, string key, string type, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var me = context.RequireUser();
        var journey = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        var step = await StepAsync(id, key, true, ct);
        var task = step is { TaskId: { } taskId } ? await engine.MyTaskAsync(taskId, ct) : null;
        if (step is null || step.Type != type || task is null)
        {
            var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
            var reads = coverage.WholeTenant || coverage.Covers(journey.OrgUnitId) || journey.UserId == me || journey.BuddyUserId == me;
            throw step is not null && step.Type == type && reads ? AppException.Denied() : AppException.NotFound();
        }
        if (journey.Status != JourneyStatuses.Active || step.Status != JourneyStepStatuses.Open || task.Status != "open")
        {
            throw AppException.Conflict("conflict.journey.step_closed");
        }
        return (journey, step, task);
    }

    // ---- Attestation ----

    /// <summary>
    /// Closes open attestation steps whose policies are all attested to (or retired), for one person after they attested
    /// or for everyone on the worker's tick. The person is recorded as having done it.
    /// </summary>
    public async Task<int> SettleAttestationsAsync(Guid? userId, CancellationToken ct)
    {
        var open = await session.QueryAsync(
            """
            select s.journey_id, s.key, s.instance_id, j.user_id from hr.journey_step s join hr.journey j on j.id = s.journey_id
            where s.status = 'open' and s.type = 'attestation' and j.status = 'active' and (@userId::uuid is null or j.user_id = @userId)
            order by s.opened_at limit 500
            """, new { userId }, r => (Journey: r.GetGuid(0), Key: r.GetString(1), Instance: r.Get<Guid?>("instance_id"), User: r.GetGuid(3)), ct);
        var settled = 0;
        foreach (var step in open)
        {
            if (await obligations.OpenCountAsync(BusinessKey(step.Journey, step.Key), ct) > 0
                || await RowAsync(step.Journey, true, ct) is not { Status: JourneyStatuses.Active } journey)
            {
                continue;
            }
            if (step.Instance is { } instance && await engine.GetInstanceAsync(instance, ct) is { Status: "active" })
            {
                await engine.WithdrawAsync(instance, ct);
            }
            await CloseStepAsync(journey, step.Key, JourneyStepOutcomes.Done, step.User, null, ct);
            settled++;
        }
        return settled;
    }

    /// <summary>
    /// Closes open training steps whose courses are all completed (or retired), for one person after they completed a
    /// course or for everyone on the worker's tick (spec 028 US4 scenario 1). The person is recorded as having done it.
    /// </summary>
    public async Task<int> SettleTrainingAsync(Guid? userId, CancellationToken ct)
    {
        var open = await session.QueryAsync(
            """
            select s.journey_id, s.key, s.instance_id, j.user_id from hr.journey_step s join hr.journey j on j.id = s.journey_id
            where s.status = 'open' and s.type = 'training' and j.status = 'active' and (@userId::uuid is null or j.user_id = @userId)
            order by s.opened_at limit 500
            """, new { userId }, r => (Journey: r.GetGuid(0), Key: r.GetString(1), Instance: r.Get<Guid?>("instance_id"), User: r.GetGuid(3)), ct);
        var settled = 0;
        foreach (var step in open)
        {
            if (await training.OpenCountAsync(BusinessKey(step.Journey, step.Key), ct) > 0
                || await RowAsync(step.Journey, true, ct) is not { Status: JourneyStatuses.Active } journey)
            {
                continue;
            }
            if (step.Instance is { } instance && await engine.GetInstanceAsync(instance, ct) is { Status: "active" })
            {
                await engine.WithdrawAsync(instance, ct);
            }
            await CloseStepAsync(journey, step.Key, JourneyStepOutcomes.Done, step.User, null, ct);
            settled++;
        }
        return settled;
    }

    // ---- Access removal ----

    internal async Task<IReadOnlyList<AccessItemView>> AccessItemsAsync(Guid journeyId, string key, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select id, name, connection_id is not null, status, note, confirmed_by, confirmed_at, escalated_at from hr.journey_access_item
            where journey_id = @journeyId and step_key = @key order by position, id
            """, new { journeyId, key },
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Connected: r.GetBoolean(2), Status: r.GetString(3), Note: r.Get<string?>("note"),
                By: r.Get<Guid?>("confirmed_by"), At: r.Get<DateTimeOffset?>("confirmed_at"), Escalated: r.Get<DateTimeOffset?>("escalated_at")), ct);
        var refs = await RefsAsync(rows.Select(r => r.By ?? Guid.Empty), ct);
        return rows.Select(r => new AccessItemView(r.Id, r.Name, r.Connected ? "connected" : "listed", r.Status, r.Note, r.By is { } b ? refs.GetValueOrDefault(b) : null,
            r.At, r.Escalated)).ToList();
    }

    /// <summary>
    /// Confirms one line of the checklist (US5 scenario 4) as removed or not applicable, by whoever holds the step's
    /// task. When no line is left, the task is completed and the step is done.
    /// </summary>
    public async Task<JourneyView> ConfirmAccessAsync(Guid id, string key, Guid itemId, AccessConfirmRequest request, CancellationToken ct)
    {
        var (journey, step, task) = await HeldStepAsync(id, key, JourneyStepTypes.AccessRemoval, ct);
        var errors = new ValidationErrors();
        if (request.Status is null || !AccessItemStatuses.Confirmed.Contains(request.Status))
        {
            errors.Add("status", "validation.journey.access_status");
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is not null && (note.Length > MaxNoteLength || note.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
        {
            errors.Add("note", "validation.journey.note");
        }
        errors.ThrowIfAny();
        var name = await session.ScalarAsync<string?>(
            """
            update hr.journey_access_item set status = @status, note = @note, confirmed_by = @me, confirmed_at = @now
            where id = @itemId and journey_id = @id and step_key = @key and status = 'pending'
            returning name
            """, new { status = request.Status, note, me = context.RequireUser(), now = clock.UtcNow, itemId, id, key }, ct);
        if (name is null)
        {
            throw await session.ScalarAsync<bool>("select exists (select 1 from hr.journey_access_item where id = @itemId and journey_id = @id and step_key = @key)",
                new { itemId, id, key }, ct) ? AppException.Conflict("conflict.journey.item_confirmed") : AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrAccessRemovalConfirmed, "journey", id.ToString(), TargetOrgUnitId: journey.OrgUnitId,
            Changes: new { step = key, item = itemId, system = name, request.Status, userId = journey.UserId, onBehalfOf = task.OnBehalfOf, note = note is not null }), ct);
        if (!await PendingAsync(id, key, ct))
        {
            var done = await engine.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null, ct);
            await CloseStepAsync(journey, step.Key, JourneyStepOutcomes.Done, done.CompletedBy ?? context.ActorId, done.OnBehalfOf, ct);
        }
        return await ViewAsync((await RowAsync(id, false, ct))!, await ManagesAsync(journey, ct), ct);
    }

    internal Task<bool> PendingAsync(Guid journeyId, string key, CancellationToken ct) =>
        session.ScalarAsync<bool>("select exists (select 1 from hr.journey_access_item where journey_id = @journeyId and step_key = @key and status = 'pending')",
            new { journeyId, key }, ct);

    private async Task<bool> ManagesAsync(JourneyRow journey, CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        return coverage.WholeTenant || coverage.Covers(journey.OrgUnitId);
    }

    /// <summary>
    /// Lines still pending once the first working day after the step opened has ended go to the IT and security
    /// officers (US5 scenario 4), once per line, audited. Run by the journey worker.
    /// </summary>
    public async Task<int> EscalateAccessAsync(CancellationToken ct)
    {
        var pending = await session.QueryAsync(
            """
            select i.id, i.journey_id, i.step_key, i.name, s.opened_at, j.org_unit_id, j.user_id
            from hr.journey_access_item i join hr.journey_step s on s.journey_id = i.journey_id and s.key = i.step_key join hr.journey j on j.id = i.journey_id
            where i.status = 'pending' and i.escalated_at is null and s.status = 'open' and j.status = 'active' and s.opened_at is not null
            order by s.opened_at limit 2000
            """, null,
            r => (Id: r.GetGuid(0), Journey: r.GetGuid(1), Key: r.GetString(2), Name: r.GetString(3), OpenedAt: r.GetFieldValue<DateTimeOffset>(4),
                Unit: r.Get<Guid?>("org_unit_id"), User: r.GetGuid(6)), ct);
        if (pending.Count == 0)
        {
            return 0;
        }
        var now = clock.UtcNow;
        List<Guid>? officers = null;
        var escalated = 0;
        foreach (var group in pending.GroupBy(p => (p.Journey, p.Key)))
        {
            var first = group.First();
            var calendar = first.Unit is { } u ? await calendars.ForUnitAsync(u, ct) : await calendars.TenantDefaultAsync(ct);
            var deadline = calendar.NextWorkingDayOnOrAfter(calendar.Today(first.OpenedAt).AddDays(1));
            if (calendar.Today(now) <= deadline)
            {
                continue;
            }
            officers ??= (await grants.TenantHoldersAsync(Permissions.SecurityManage, ct)).Concat(await grants.TenantHoldersAsync(Permissions.IntegrationOperate, ct))
                .Distinct().ToList();
            var ids = group.Select(g => g.Id).ToArray();
            await session.ExecAsync("update hr.journey_access_item set escalated_at = @now where id = any(@ids)", new { now, ids }, ct);
            var recipients = officers.Where(o => o != first.User).ToList();
            if (recipients.Count > 0)
            {
                await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.ApprovalEscalation, AccessEscalatedTemplate, Link(first.Journey),
                    new Dictionary<string, string> { ["count"] = ids.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    DedupeKey: $"journey_access:{first.Journey:N}:{first.Key}:{now:yyyyMMdd}"), ct);
            }
            await audit.WriteAsync(new AuditEntry(AuditActions.HrAccessRemovalEscalated, "journey", first.Journey.ToString(), TargetOrgUnitId: first.Unit,
                Changes: new { step = first.Key, systems = group.Select(g => g.Name).ToList(), userId = first.User, recipients = recipients.Count }), ct);
            escalated += ids.Length;
        }
        return escalated;
    }

    // ---- Exit interview ----

    /// <summary>
    /// Records the exit interview (US5 scenario 5) by whoever holds the step's task, the leaver or an HR interviewer,
    /// and completes the step. A declined interview records nothing but that, and the step is closed as not needed.
    /// </summary>
    public async Task<JourneyView> RecordExitInterviewAsync(Guid id, string key, ExitInterviewRequest request, CancellationToken ct)
    {
        var (journey, step, task) = await HeldStepAsync(id, key, JourneyStepTypes.ExitInterview, ct);
        var errors = new ValidationErrors();
        var reasons = (request.Reasons ?? []).Distinct(StringComparer.Ordinal).ToList();
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (request.Declined)
        {
            if (reasons.Count > 0 || request.PrimaryReason is not null || request.Rating is not null || request.WouldReturn is not null || comment is not null)
            {
                errors.Add("declined", "validation.journey.exit_declined");
            }
        }
        else
        {
            if (reasons.Count is 0 or > ExitReasons.MaxChosen || reasons.Any(r => !ExitReasons.All.Contains(r)))
            {
                errors.Add("reasons", "validation.journey.exit_reasons");
            }
            if (request.PrimaryReason is not null && !reasons.Contains(request.PrimaryReason))
            {
                errors.Add("primaryReason", "validation.journey.exit_primary");
            }
            if (request.Rating is < 1 or > 5)
            {
                errors.Add("rating", "validation.journey.exit_rating");
            }
            if (comment is not null && (comment.Length > MaxCommentLength || comment.Any(c => char.IsControl(c) && c is not '\n' and not '\r')))
            {
                errors.Add("comment", "validation.journey.exit_comment");
            }
        }
        errors.ThrowIfAny();
        var me = context.RequireUser();
        var inserted = await session.ExecAsync(
            """
            insert into hr.exit_interview (tenant_id, journey_id, step_key, user_id, org_unit_id, declined, reasons, primary_reason, rating, would_return, comment,
              submitted_by, submitted_at)
            values (platform.current_tenant_id(), @id, @key, @user, @unit, @declined, @reasons, @primary, @rating, @wouldReturn, @comment, @me, @now)
            on conflict do nothing
            """, new
            {
                id, key, user = journey.UserId, unit = journey.OrgUnitId, declined = request.Declined, reasons = reasons.ToArray(), primary = request.PrimaryReason,
                rating = request.Rating, wouldReturn = request.WouldReturn, comment, me, now = clock.UtcNow,
            }, ct);
        if (inserted == 0)
        {
            throw AppException.Conflict("conflict.journey.exit_recorded");
        }
        // The free text never goes to the audit log; it stays with HR in the record.
        await audit.WriteAsync(new AuditEntry(AuditActions.HrExitInterviewRecorded, "journey", id.ToString(), TargetOrgUnitId: journey.OrgUnitId,
            Changes: new { step = key, userId = journey.UserId, request.Declined, reasons, request.PrimaryReason, comment = comment is not null, onBehalfOf = task.OnBehalfOf }), ct);
        var done = await engine.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null, ct);
        await CloseStepAsync(journey, step.Key, request.Declined ? JourneyStepOutcomes.Skipped : JourneyStepOutcomes.Done, done.CompletedBy ?? me, done.OnBehalfOf, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, await ManagesAsync(journey, ct), ct);
    }

    internal Task<bool> ExitRecordedAsync(Guid journeyId, CancellationToken ct) =>
        session.ScalarAsync<bool>("select exists (select 1 from hr.exit_interview where journey_id = @journeyId)", new { journeyId }, ct);

    /// <summary>The interview for HR officers in full; for the leaver and whoever recorded it without the free text; for anyone else nothing.</summary>
    private async Task<ExitInterviewView?> ExitInterviewAsync(JourneyRow row, bool manage, CancellationToken ct)
    {
        if (row.Kind != JourneyKinds.Offboarding)
        {
            return null;
        }
        var record = (await session.QueryAsync(
            "select declined, reasons, primary_reason, rating, would_return, comment, submitted_by, submitted_at from hr.exit_interview where journey_id = @id",
            new { id = row.Id }, r => (Declined: r.GetBoolean(0), Reasons: r.GetFieldValue<string[]>(1), Primary: r.Get<string?>("primary_reason"), Rating: r.Get<int?>("rating"),
                WouldReturn: r.Get<bool?>("would_return"), Comment: r.Get<string?>("comment"), By: r.GetGuid(6), At: r.GetFieldValue<DateTimeOffset>(7)), ct)).SingleOrDefault();
        if (record.Reasons is null)
        {
            return null;
        }
        var me = context.ActorType == ActorType.User ? context.ActorId : null;
        if (!manage && me != row.UserId && me != record.By)
        {
            return null;
        }
        var refs = await RefsAsync([record.By], ct);
        return new ExitInterviewView(record.Declined, record.Reasons, record.Primary, record.Rating, record.WouldReturn, manage ? record.Comment : null,
            refs.GetValueOrDefault(record.By) ?? new PersonRef(record.By, string.Empty), record.At);
    }

    /// <summary>Exit reasons counted for the units the officer covers, with the minimum group size (spec 021 US3 scenario 3).</summary>
    public async Task<ExitReasonsView> ExitReasonsAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        var today = await people.TodayAsync(ct);
        var end = to ?? today;
        var start = from ?? end.AddMonths(-12);
        if (start > end || end.DayNumber - start.DayNumber > 3 * 366)
        {
            throw AppException.Validation("from", "validation.journey.period");
        }
        var rows = (await session.QueryAsync(
            """
            select org_unit_id, declined, reasons, primary_reason from hr.exit_interview
            where submitted_at >= @start and submitted_at < @end
            """, new { start = start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), end = end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) },
            r => (Unit: r.Get<Guid?>("org_unit_id"), Declined: r.GetBoolean(1), Reasons: r.GetFieldValue<string[]>(2), Primary: r.Get<string?>("primary_reason")), ct))
            .Where(r => coverage.WholeTenant || coverage.Covers(r.Unit)).ToList();
        var answered = rows.Where(r => !r.Declined).ToList();
        static List<ExitReasonCount> Count(IEnumerable<(Guid? Unit, bool Declined, string[] Reasons, string? Primary)> items)
        {
            var list = items.ToList();
            return ExitReasons.All.Select(reason => new ExitReasonCount(reason, list.Count(i => i.Reasons.Contains(reason)), list.Count(i => i.Primary == reason)))
                .Where(c => c.Chosen > 0).ToList();
        }
        if (answered.Count < MinimumGroupSize)
        {
            return new ExitReasonsView(start, end, MinimumGroupSize, rows.Count, rows.Count - answered.Count, [], [], answered.Count);
        }
        var groups = answered.GroupBy(r => r.Unit).ToList();
        var shown = groups.Where(g => g.Key is not null && g.Count() >= MinimumGroupSize).Select(g => new ExitReasonGroup(g.Key, g.Count(), Count(g))).ToList();
        var rest = groups.Where(g => g.Key is null || g.Count() < MinimumGroupSize).SelectMany(g => g).ToList();
        var hidden = 0;
        if (rest.Count >= MinimumGroupSize)
        {
            shown.Add(new ExitReasonGroup(null, rest.Count, Count(rest)));
        }
        else
        {
            hidden = rest.Count;
        }
        return new ExitReasonsView(start, end, MinimumGroupSize, rows.Count, rows.Count - answered.Count, Count(answered), shown, hidden);
    }
}
