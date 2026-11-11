using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

public static class JourneyStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Active, Completed, Cancelled];
}

public static class JourneyStepStatuses
{
    public const string Scheduled = "scheduled";
    public const string Open = "open";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
}

/// <summary>How a step ended: done, or closed as not needed (the task was rejected or returned).</summary>
public static class JourneyStepOutcomes
{
    public const string Done = "done";
    public const string Skipped = "skipped";
}

public sealed record JourneyStartRequest(string? TemplateKey, Guid UserId, DateOnly? AnchorDate, Guid? BuddyUserId);

public sealed record JourneyAnchorRequest(DateOnly? AnchorDate);

public sealed record JourneyBuddyRequest(Guid? BuddyUserId);

/// <summary>Cancels a journey. <paramref name="NoShow"/> on onboarding also starts offboarding when a template exists (edge cases).</summary>
public sealed record JourneyCancelRequest(string? Reason, bool NoShow = false);

public sealed record JourneySummary(
    Guid Id,
    string TemplateKey,
    IReadOnlyDictionary<string, string> TemplateNames,
    string Kind,
    PersonRef Person,
    PersonRef? Buddy,
    Guid? OrgUnitId,
    DateOnly AnchorDate,
    string Status,
    int Steps,
    int Done,
    int Open,
    int Overdue,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record JourneyStepView(
    string Key,
    int Position,
    IReadOnlyDictionary<string, string> Names,
    int OffsetDays,
    string Assignee,
    DateOnly ScheduledOn,
    string Status,
    string? Outcome,
    PersonRef? CompletedBy,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? DueAt,
    bool Overdue,
    Guid? MyTaskId);

public sealed record JourneyView(
    JourneySummary Journey,
    string StartedBy,
    string? CancelReason,
    Guid? PreviousJourneyId,
    IReadOnlyList<JourneyStepView> Steps,
    bool CanManage);

internal sealed record JourneyRow(
    Guid Id, Guid TemplateId, string TemplateKey, int TemplateVersion, string Kind, Guid UserId, Guid? BuddyUserId, Guid? OrgUnitId, DateOnly AnchorDate,
    string Status, string StartedByEvent, string? CancelReason, Guid? PreviousJourneyId, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

internal sealed record JourneyStepRow(
    Guid JourneyId, string Key, int Position, Dictionary<string, string> Names, int OffsetDays, string Assignee, DateOnly ScheduledOn, string Status,
    Guid? InstanceId, Guid? TaskId, DateTimeOffset? DueAt, string? Outcome, Guid? CompletedBy, DateTimeOffset? CompletedAt);

/// <summary>
/// Journeys (spec 022 US4, US5, FR-013, FR-014): one per template and person while active, started from HR, directory
/// and service events or by an HR officer. Each step runs as a one step process on spec 024, started on its scheduled
/// date; a later event for the same person moves the anchor and reschedules every step not yet open. HR officers see
/// and manage journeys of the units they cover; the person, the buddy and anyone with a task in it may read it. Needs
/// <c>hr.journeys</c> in the plan.
/// </summary>
public sealed class JourneyService(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IProcessEngine engine,
    IUserDirectory users,
    ITenantMemberships memberships,
    LeaveLedger people,
    JourneyTemplates templates)
{
    public const int MaxReasonLength = 500;
    public const int MaxList = 500;

    public static string Link(Guid id) => $"/hr/journeys/{id}";

    internal static string BusinessKey(Guid journeyId, string step) => $"journey:{journeyId:N}:{step}";

    internal static (Guid Journey, string Step)? ParseBusinessKey(string businessKey)
    {
        var parts = businessKey.Split(':');
        return parts.Length == 3 && parts[0] == "journey" && Guid.TryParseExact(parts[1], "N", out var id) ? (id, parts[2]) : null;
    }

    private const string JourneyColumns =
        """
        j.id, j.template_id, t.key, j.template_version, j.kind, j.user_id, j.buddy_user_id, j.org_unit_id, j.anchor_date, j.status, j.started_by_event,
        j.cancel_reason, j.previous_journey_id, j.created_at, j.completed_at
        """;

    private static JourneyRow ReadJourney(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetGuid(5), r.Get<Guid?>("buddy_user_id"), r.Get<Guid?>("org_unit_id"),
        DateOnly.FromDateTime(r.GetDateTime(8)), r.GetString(9), r.GetString(10), r.Get<string?>("cancel_reason"), r.Get<Guid?>("previous_journey_id"),
        r.GetFieldValue<DateTimeOffset>(13), r.Get<DateTimeOffset?>("completed_at"));

    private const string StepColumns =
        "journey_id, key, position, names::text, offset_days, assignee, scheduled_on, status, instance_id, task_id, due_at, outcome, completed_by, completed_at";

    private static JourneyStepRow ReadStep(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetInt32(2), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3)) ?? [], r.GetInt32(4), r.GetString(5),
        DateOnly.FromDateTime(r.GetDateTime(6)), r.GetString(7), r.Get<Guid?>("instance_id"), r.Get<Guid?>("task_id"), r.Get<DateTimeOffset?>("due_at"),
        r.Get<string?>("outcome"), r.Get<Guid?>("completed_by"), r.Get<DateTimeOffset?>("completed_at"));

    internal async Task<JourneyRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        (await session.QueryAsync(
            $"select {JourneyColumns} from hr.journey j join hr.journey_template t on t.id = j.template_id where j.id = @id{(forUpdate ? " for update of j" : "")}",
            new { id }, ReadJourney, ct)).SingleOrDefault();

    internal Task<List<JourneyStepRow>> StepsAsync(Guid journeyId, CancellationToken ct) =>
        session.QueryAsync($"select {StepColumns} from hr.journey_step where journey_id = @journeyId order by position", new { journeyId }, ReadStep, ct);

    internal async Task<JourneyStepRow?> StepByInstanceAsync(Guid instanceId, bool forUpdate, CancellationToken ct) =>
        (await session.QueryAsync($"select {StepColumns} from hr.journey_step where instance_id = @instanceId{(forUpdate ? " for update" : "")}",
            new { instanceId }, ReadStep, ct)).SingleOrDefault();

    internal async Task<IReadOnlyCollection<Guid>> StepUsersAsync(Guid journeyId, string key, Guid person, CancellationToken ct) =>
        (await session.ScalarAsync<Guid[]>("select users from hr.journey_step where journey_id = @journeyId and key = @key", new { journeyId, key }, ct) ?? [])
            .Where(u => u != person).ToList();

    // ---- Starting and rescheduling ----

    /// <summary>
    /// Starts a journey of the template for the person, once per template and source event; when the person already has
    /// an active journey of the template, moves its anchor instead (US4 scenario 2). Returns the journey, or null when the
    /// person is not an active member of the tenant. With <paramref name="refuseActive"/>, an active journey is a conflict.
    /// </summary>
    internal async Task<Guid?> StartOrMoveAsync(JourneyTemplateRow template, Guid userId, DateOnly anchor, string startedBy, string sourceKey, Guid? buddy,
        bool refuseActive, CancellationToken ct)
    {
        if (await people.PersonAsync(userId, ct) is not { Active: true } person)
        {
            return null;
        }
        // One start per template and person at a time, so two events never race to two journeys.
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':journey:' || @key, 0))",
            new { key = $"{template.Id:N}:{userId:N}" }, ct);
        if (await session.ScalarAsync<Guid?>("select id from hr.journey where template_id = @template and source_key = @sourceKey",
                new { template = template.Id, sourceKey }, ct) is { } seen)
        {
            return seen;
        }
        if (await session.ScalarAsync<Guid?>("select id from hr.journey where template_id = @template and user_id = @userId and status = 'active'",
                new { template = template.Id, userId }, ct) is { } active)
        {
            if (refuseActive)
            {
                throw AppException.Conflict("conflict.journey.active");
            }
            await MoveAnchorAsync((await RowAsync(active, true, ct))!, anchor, person.PrimaryUnit, ct);
            return active;
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        var previous = await session.ScalarAsync<Guid?>(
            "select id from hr.journey where template_id = @template and user_id = @userId order by created_at desc limit 1", new { template = template.Id, userId }, ct);
        await session.ExecAsync(
            """
            insert into hr.journey (tenant_id, id, template_id, template_version, kind, user_id, buddy_user_id, org_unit_id, anchor_date, status, started_by_event,
              source_key, previous_journey_id, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @template, @version, @kind, @userId, @buddy, @unit, @anchor, 'active', @startedBy, @sourceKey, @previous, @now, @now)
            """, new
            {
                id, template = template.Id, version = template.Version, kind = template.Kind, userId, buddy, unit = person.PrimaryUnit, anchor, startedBy, sourceKey,
                previous, now,
            }, ct);
        var position = 0;
        foreach (var step in template.Steps)
        {
            await session.ExecAsync(
                """
                insert into hr.journey_step (tenant_id, journey_id, key, position, names, offset_days, assignee, users, permission, due_working_days, scheduled_on, status)
                values (platform.current_tenant_id(), @id, @key, @position, @names, @offset, @assignee, @users, @permission, @due, @scheduled, 'scheduled')
                """, new
                {
                    id, key = step.Key, position = position++, names = Jsonb.From(step.Names), offset = step.OffsetDays, assignee = step.Assignee,
                    users = (step.Users ?? []).ToArray(), permission = step.Permission, due = step.DueWorkingDays, scheduled = anchor.AddDays(step.OffsetDays),
                }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrJourneyStarted, "journey", id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { template = template.Key, template.Version, template.Kind, userId, anchor, startedBy, previous }), ct);
        await outbox.EnqueueAsync(new JourneyStarted(id, userId, template.Kind, anchor), ct);
        await OpenDueAsync(id, ct);
        return id;
    }

    /// <summary>Moves the anchor: every step not yet open is scheduled again from the new date.</summary>
    private async Task MoveAnchorAsync(JourneyRow journey, DateOnly anchor, Guid? unit, CancellationToken ct)
    {
        if (journey.AnchorDate == anchor && journey.OrgUnitId == unit)
        {
            return;
        }
        await session.ExecAsync("update hr.journey set anchor_date = @anchor, org_unit_id = @unit, updated_at = @now where id = @id",
            new { id = journey.Id, anchor, unit, now = clock.UtcNow }, ct);
        await session.ExecAsync(
            "update hr.journey_step set scheduled_on = @anchor + offset_days where journey_id = @id and status = 'scheduled'", new { id = journey.Id, anchor }, ct);
        if (journey.AnchorDate != anchor)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrJourneyRescheduled, "journey", journey.Id.ToString(), TargetOrgUnitId: unit,
                Changes: new { from = journey.AnchorDate, to = anchor }), ct);
        }
        await OpenDueAsync(journey.Id, ct);
    }

    /// <summary>
    /// Starts the engine process of every scheduled step whose date has come, in active journeys (all of them, or one).
    /// Run by the journey worker every two minutes and right after a start or a move.
    /// </summary>
    public async Task<int> OpenDueAsync(Guid? journeyId, CancellationToken ct)
    {
        var today = await people.TodayAsync(ct);
        var due = await session.QueryAsync(
            """
            select s.journey_id, s.key, s.assignee, t.key, j.template_version, j.user_id, j.buddy_user_id, j.org_unit_id
            from hr.journey_step s join hr.journey j on j.id = s.journey_id join hr.journey_template t on t.id = j.template_id
            where s.status = 'scheduled' and s.scheduled_on <= @today and j.status = 'active' and (@journeyId::uuid is null or j.id = @journeyId)
            order by s.scheduled_on, s.journey_id, s.position limit 500
            for update of s skip locked
            """, new { today, journeyId },
            r => (Journey: r.GetGuid(0), Step: r.GetString(1), Assignee: r.GetString(2), Template: r.GetString(3), Version: r.GetInt32(4), User: r.GetGuid(5),
                Buddy: r.Get<Guid?>("buddy_user_id"), Unit: r.Get<Guid?>("org_unit_id")), ct);
        foreach (var step in due)
        {
            // The person does their own steps; nobody else's step about them is theirs to complete (no self-approval).
            var personStep = step.Assignee == JourneyAssignees.Person;
            Guid? subject = step.Assignee == JourneyAssignees.Buddy ? step.Buddy : step.User;
            var instance = await engine.StartAsync(JourneyTemplates.ProcessKey(step.Template, step.Version, step.Step), BusinessKey(step.Journey, step.Step), step.Unit,
                null, personStep ? [] : [step.User], subject, ct: ct);
            var task = (await engine.InstanceTasksAsync(instance.Id, ct)).FirstOrDefault(t => t.Status == "open");
            await session.ExecAsync(
                "update hr.journey_step set status = 'open', instance_id = @instance, task_id = @task, due_at = @dueAt where journey_id = @journey and key = @key",
                new { instance = instance.Id, task = task?.Id, dueAt = task?.DueAt, journey = step.Journey, key = step.Step }, ct);
        }
        return due.Count;
    }

    /// <summary>A step's task was completed: the step is done (or skipped), and the journey completes when no step is left.</summary>
    internal async Task StepDecidedAsync(JourneyStepRow step, UserTaskInfo done, CancellationToken ct)
    {
        if (await RowAsync(step.JourneyId, true, ct) is not { Status: JourneyStatuses.Active } journey)
        {
            return;
        }
        var now = clock.UtcNow;
        var outcome = done.Outcome == TaskOutcomes.Approve ? JourneyStepOutcomes.Done : JourneyStepOutcomes.Skipped;
        var by = done.CompletedBy ?? context.ActorId;
        await session.ExecAsync(
            "update hr.journey_step set status = 'done', outcome = @outcome, completed_by = @by, completed_at = @now where journey_id = @journey and key = @key",
            new { outcome, by, now, journey = journey.Id, key = step.Key }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrJourneyStepCompleted, "journey", journey.Id.ToString(), TargetOrgUnitId: journey.OrgUnitId,
            Changes: new { step = step.Key, outcome, userId = journey.UserId, onBehalfOf = done.OnBehalfOf }), ct);
        if (!await session.ScalarAsync<bool>("select exists (select 1 from hr.journey_step where journey_id = @id and status in ('scheduled', 'open'))",
                new { id = journey.Id }, ct))
        {
            await session.ExecAsync("update hr.journey set status = 'completed', completed_at = @now, updated_at = @now where id = @id", new { id = journey.Id, now }, ct);
            await outbox.EnqueueAsync(new JourneyCompleted(journey.Id, journey.UserId, journey.Kind), ct);
        }
    }

    // ---- HR actions ----

    private async Task<OrgCoverage> CoverageAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    /// <summary>The journey for an HR officer covering its unit; not found for anyone else, so a sibling unit's officer learns nothing.</summary>
    private async Task<JourneyRow> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        var row = await RowAsync(id, forUpdate, ct);
        return row is not null && (coverage.WholeTenant || coverage.Covers(row.OrgUnitId)) ? row : throw AppException.NotFound();
    }

    public async Task<JourneyView> StartAsync(JourneyStartRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        var errors = new ValidationErrors();
        var template = request.TemplateKey is { } key ? await templates.RowByKeyAsync(key, ct) : null;
        if (template is not { Active: true })
        {
            errors.Add("templateKey", "validation.journey.template_unknown");
        }
        if (request.AnchorDate is null)
        {
            errors.Add("anchorDate", "validation.journey.anchor_date");
        }
        var person = await people.PersonAsync(request.UserId, ct);
        if (person is not { Active: true })
        {
            errors.Add("userId", "validation.journey.person");
        }
        else if (!(coverage.WholeTenant || person.PrimaryUnit is { } u && coverage.Covers(u)))
        {
            // Not found rather than denied for a person outside the officer's units.
            throw AppException.NotFound();
        }
        else if (template is { OrgUnitId: { } unit } && !person.Path.Contains(unit))
        {
            errors.Add("templateKey", "validation.journey.template_scope");
        }
        await ValidateBuddyAsync(request.BuddyUserId, request.UserId, errors, ct);
        errors.ThrowIfAny();
        var id = await StartOrMoveAsync(template!, request.UserId, request.AnchorDate!.Value, JourneyStarts.Manual, $"manual:{Ids.New():N}", request.BuddyUserId,
            refuseActive: true, ct);
        return await ViewAsync((await RowAsync(id!.Value, false, ct))!, true, ct);
    }

    private async Task ValidateBuddyAsync(Guid? buddy, Guid person, ValidationErrors errors, CancellationToken ct)
    {
        if (buddy is { } b && (b == person || await memberships.GetAsync(b, ct) is not { Status: MembershipStatus.Active }))
        {
            errors.Add("buddyUserId", "validation.journey.buddy");
        }
    }

    public async Task<JourneyView> MoveAnchorAsync(Guid id, JourneyAnchorRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (request.AnchorDate is not { } anchor)
        {
            throw AppException.Validation("anchorDate", "validation.journey.anchor_date");
        }
        if (row.Status != JourneyStatuses.Active)
        {
            throw AppException.Conflict("conflict.journey.closed");
        }
        await MoveAnchorAsync(row, anchor, row.OrgUnitId, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    /// <summary>Names or clears the buddy. Steps already open keep who they went to; later buddy steps go to the new buddy.</summary>
    public async Task<JourneyView> SetBuddyAsync(Guid id, JourneyBuddyRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (row.Status != JourneyStatuses.Active)
        {
            throw AppException.Conflict("conflict.journey.closed");
        }
        var errors = new ValidationErrors();
        await ValidateBuddyAsync(request.BuddyUserId, row.UserId, errors, ct);
        errors.ThrowIfAny();
        await session.ExecAsync("update hr.journey set buddy_user_id = @buddy, updated_at = @now where id = @id", new { id, buddy = request.BuddyUserId, now = clock.UtcNow }, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    /// <summary>
    /// Cancels an active journey: open steps are withdrawn and the rest cancelled (audited). A no-show on onboarding also
    /// starts the nearest offboarding template anchored today, so access is removed (edge cases).
    /// </summary>
    public async Task<JourneyView> CancelAsync(Guid id, JourneyCancelRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > MaxReasonLength || reason.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            throw AppException.Validation("reason", "validation.journey.reason");
        }
        if (row.Status != JourneyStatuses.Active)
        {
            throw AppException.Conflict("conflict.journey.closed");
        }
        foreach (var step in (await StepsAsync(id, ct)).Where(s => s.Status == JourneyStepStatuses.Open && s.InstanceId is not null))
        {
            if (await engine.GetInstanceAsync(step.InstanceId!.Value, ct) is { Status: "active" })
            {
                await engine.WithdrawAsync(step.InstanceId.Value, ct);
            }
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update hr.journey_step set status = 'cancelled' where journey_id = @id and status in ('scheduled', 'open')", new { id }, ct);
        await session.ExecAsync("update hr.journey set status = 'cancelled', cancel_reason = @reason, updated_at = @now where id = @id", new { id, reason, now }, ct);
        Guid? offboarding = null;
        if (request.NoShow && row.Kind == JourneyKinds.Onboarding && await people.PersonAsync(row.UserId, ct) is { } person
            && await templates.OffboardingForAsync(person.Path, ct) is { } template)
        {
            offboarding = await StartOrMoveAsync(template, row.UserId, await people.TodayAsync(ct), "no_show", $"no_show:{id:N}", null, refuseActive: false, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrJourneyCancelled, "journey", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { reason, noShow = request.NoShow, userId = row.UserId, offboarding }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, true, ct);
    }

    // ---- Reading ----

    /// <summary>The dashboard (US4 scenarios 6 and 7): journeys of the units the HR officer covers, with progress and overdue steps.</summary>
    public async Task<IReadOnlyList<JourneySummary>> ListAsync(string? status, bool overdueOnly, Guid? userId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        if (status is not null && !JourneyStatuses.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.journey.status");
        }
        var rows = await SummariesAsync(status, userId, null, ct);
        var shown = rows.Where(r => coverage.WholeTenant || coverage.Covers(r.Row.OrgUnitId)).Where(r => !overdueOnly || r.Overdue > 0).Take(MaxList).ToList();
        var refs = await RefsAsync(shown.SelectMany(r => new[] { r.Row.UserId, r.Row.BuddyUserId ?? Guid.Empty }), ct);
        return shown.Select(r => Summary(r, refs)).ToList();
    }

    /// <summary>The signed-in person's own journeys.</summary>
    public async Task<IReadOnlyList<JourneySummary>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var me = context.RequireUser();
        var rows = await SummariesAsync(null, me, null, ct);
        var refs = await RefsAsync(rows.SelectMany(r => new[] { r.Row.UserId, r.Row.BuddyUserId ?? Guid.Empty }), ct);
        return rows.Select(r => Summary(r, refs)).ToList();
    }

    public async Task<JourneyView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var me = context.RequireUser();
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        var manage = coverage.WholeTenant || coverage.Covers(row.OrgUnitId);
        if (!manage && row.UserId != me && row.BuddyUserId != me && !await HasTaskAsync(id, ct))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, manage, ct);
    }

    private async Task<bool> HasTaskAsync(Guid id, CancellationToken ct)
    {
        foreach (var step in (await StepsAsync(id, ct)).Where(s => s.TaskId is not null))
        {
            if (await engine.MyTaskAsync(step.TaskId!.Value, ct) is not null)
            {
                return true;
            }
        }
        return false;
    }

    private sealed record SummaryRow(JourneyRow Row, Dictionary<string, string> TemplateNames, int Steps, int Done, int Open, int Overdue);

    private Task<List<SummaryRow>> SummariesAsync(string? status, Guid? userId, Guid? id, CancellationToken ct) =>
        session.QueryAsync(
            $"""
            select {JourneyColumns}, t.names::text,
              count(s.key)::int, (count(s.key) filter (where s.status = 'done'))::int, (count(s.key) filter (where s.status = 'open'))::int,
              (count(s.key) filter (where s.status = 'open' and s.due_at < @now))::int
            from hr.journey j join hr.journey_template t on t.id = j.template_id left join hr.journey_step s on s.journey_id = j.id
            where (@status::text is null or j.status = @status) and (@userId::uuid is null or j.user_id = @userId) and (@id::uuid is null or j.id = @id)
            group by j.tenant_id, j.id, t.tenant_id, t.id
            order by j.status = 'active' desc, j.anchor_date desc, j.id
            limit 2000
            """, new { status, userId, id, now = clock.UtcNow },
            r => new SummaryRow(ReadJourney(r), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(15)) ?? [], r.GetInt32(16), r.GetInt32(17),
                r.GetInt32(18), r.GetInt32(19)), ct);

    private static JourneySummary Summary(SummaryRow s, IReadOnlyDictionary<Guid, PersonRef> refs) => new(
        s.Row.Id, s.Row.TemplateKey, s.TemplateNames, s.Row.Kind, refs.GetValueOrDefault(s.Row.UserId) ?? new PersonRef(s.Row.UserId, string.Empty),
        s.Row.BuddyUserId is { } b ? refs.GetValueOrDefault(b) ?? new PersonRef(b, string.Empty) : null, s.Row.OrgUnitId, s.Row.AnchorDate, s.Row.Status,
        s.Steps, s.Done, s.Open, s.Overdue, s.Row.CreatedAt, s.Row.CompletedAt);

    private async Task<IReadOnlyDictionary<Guid, PersonRef>> RefsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<Guid, PersonRef>();
        }
        return (await users.GetManyAsync(wanted, ct)).ToDictionary(u => u.Key, u => new PersonRef(u.Key, u.Value.DisplayName));
    }

    private async Task<JourneyView> ViewAsync(JourneyRow row, bool manage, CancellationToken ct)
    {
        var summary = (await SummariesAsync(null, null, row.Id, ct)).Single();
        var steps = await StepsAsync(row.Id, ct);
        var refs = await RefsAsync(steps.Select(s => s.CompletedBy ?? Guid.Empty).Append(row.UserId).Append(row.BuddyUserId ?? Guid.Empty), ct);
        var now = clock.UtcNow;
        var views = new List<JourneyStepView>();
        foreach (var s in steps)
        {
            Guid? mine = null;
            if (s.Status == JourneyStepStatuses.Open && s.TaskId is { } taskId && context.ActorType == ActorType.User
                && await engine.MyTaskAsync(taskId, ct) is { Status: "open" })
            {
                mine = taskId;
            }
            views.Add(new JourneyStepView(s.Key, s.Position, s.Names, s.OffsetDays, s.Assignee, s.ScheduledOn, s.Status, s.Outcome,
                s.CompletedBy is { } by ? refs.GetValueOrDefault(by) : null, s.CompletedAt, s.DueAt, s.Status == JourneyStepStatuses.Open && s.DueAt < now, mine));
        }
        return new JourneyView(Summary(summary, refs), row.StartedByEvent, manage ? row.CancelReason : null, row.PreviousJourneyId, views, manage);
    }
}
