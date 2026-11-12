using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public sealed record RuleRequest(Guid? OrgUnitId, ContentAudience? Audience, IReadOnlyList<Guid>? People, int? DueDays, int? RecurrenceMonths, int? GraceDays);

public sealed record RulePerson(Guid Id, string DisplayName);

public sealed record RuleView(
    Guid Id,
    Guid CourseId,
    Guid? OrgUnitId,
    ContentAudience Audience,
    IReadOnlyList<RulePerson> People,
    int DueDays,
    int? RecurrenceMonths,
    int GraceDays,
    string Status,
    DateTimeOffset CreatedAt,
    int Covered);

public static class RuleStatuses
{
    public const string Active = "active";
    public const string Ended = "ended";
}

/// <summary>
/// Mandatory training (spec 028 US2, FR-005 to FR-008, FR-012, FR-017). A rule gives a published course to an audience of
/// units, groups, roles or named people, only within the scope of the administrator who made it. Each person it covers
/// gets one open assignment due a number of days after they join it, moved to their next working day; several rules
/// collapse to the earliest date. People who leave the audience lose the assignments only rules gave them; completions
/// stay. Completions under a recurring rule expire and a new assignment follows. Reminders go 7 days and 1 day before the
/// due date; once overdue the learner and their manager are told, at most once a week.
/// </summary>
public sealed class AssignmentRules(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAudiences audiences,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IUserDirectory users,
    IReportingLine reportingLine,
    IWorkingCalendar calendars,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    Courses courses,
    Player player)
    : IMessageHandler<OrganizationMembershipChanged>, IMessageHandler<TenantMembershipDisabled>, IMessageHandler<CoursePublished>
{
    public const int DefaultDueDays = 30;
    public const int MaxDueDays = 365;
    public const int MaxRecurrenceMonths = 60;
    public const int MaxGraceDays = 30;
    public const int MaxPeople = 500;
    public const int MaxRulesPerCourse = 50;
    public static readonly IReadOnlyList<int> ReminderDays = [7, 1];
    public static readonly TimeSpan OverdueRepeat = TimeSpan.FromDays(7);
    public static readonly TimeSpan ReevaluateAfter = TimeSpan.FromHours(1);

    public const string AssignedTemplate = "learning.course.assigned";
    public const string ReminderTemplate = "learning.course.reminder";
    public const string OverdueTemplate = "learning.course.overdue";
    public const string TeamOverdueTemplate = "learning.team.overdue";
    public const string RetrainingTemplate = "learning.course.retraining";

    internal sealed record RuleRow(Guid Id, Guid CourseId, Guid? OrgUnitId, string Audience, Guid[] People, int DueDays, int? RecurrenceMonths, int GraceDays, string Status,
        DateTimeOffset CreatedAt)
    {
        public ContentAudience AudienceValue => LearningJson.Read<ContentAudience>(Audience);
    }

    private const string RuleColumns = "id, course_id, org_unit_id, audience::text, people, due_days, recurrence_months, grace_days, status, created_at";

    private static RuleRow ReadRule(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<Guid?>("org_unit_id"), r.GetString(3), r.GetFieldValue<Guid[]>(4),
        r.GetInt32(5), r.Get<int?>("recurrence_months"), r.GetInt32(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9));

    // ---- Managing rules ----

    private async Task<Courses.CourseRow> ManagedCourseAsync(Guid courseId, bool forUpdate, CancellationToken ct)
    {
        var row = await courses.ManagedAsync(courseId, forUpdate, ct);
        await entitlements.DemandAsync(Capabilities.LearningMandatory, ct);
        return row;
    }

    public async Task<IReadOnlyList<RuleView>> ListAsync(Guid courseId, CancellationToken ct)
    {
        await ManagedCourseAsync(courseId, false, ct);
        var rows = await session.QueryAsync($"select {RuleColumns} from learning.assignment_rule where course_id = @courseId order by created_at desc, id",
            new { courseId }, ReadRule, ct);
        var result = new List<RuleView>();
        foreach (var row in rows)
        {
            result.Add(await ViewAsync(row, ct));
        }
        return result;
    }

    private async Task<RuleView> ViewAsync(RuleRow row, CancellationToken ct)
    {
        var covered = await session.ScalarAsync<int>("select count(*)::int from learning.rule_target where rule_id = @id", new { id = row.Id }, ct);
        var people = new List<RulePerson>();
        foreach (var id in row.People)
        {
            people.Add(new RulePerson(id, (await users.GetAsync(id, ct))?.DisplayName ?? ""));
        }
        return new RuleView(row.Id, row.CourseId, row.OrgUnitId, row.AudienceValue, people, row.DueDays, row.RecurrenceMonths, row.GraceDays, row.Status, row.CreatedAt, covered);
    }

    /// <summary>Creates a rule and gives the course to everyone it covers now (US2 scenario 1). Only people in the rule's scope can be targeted (scenario 4).</summary>
    public async Task<RuleView> CreateAsync(Guid courseId, RuleRequest request, CancellationToken ct)
    {
        var course = await ManagedCourseAsync(courseId, true, ct);
        if (course.Status != CourseStatuses.Published)
        {
            throw AppException.Conflict("conflict.learning.rule_course_unpublished");
        }
        if (!(await authz.CheckAsync(Permissions.LearningManage, request.OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied("authz.denied.learning_scope");
        }
        var active = await session.ScalarAsync<int>("select count(*)::int from learning.assignment_rule where course_id = @courseId and status = 'active'", new { courseId }, ct);
        if (active >= MaxRulesPerCourse)
        {
            throw AppException.Conflict("conflict.learning.rule_count");
        }
        var errors = new ValidationErrors();
        var audience = request.Audience ?? ContentAudience.Everyone;
        audiences.Validate(audience, "audience", errors);
        var people = (request.People ?? []).Distinct().ToArray();
        if (people.Length > MaxPeople)
        {
            errors.Add("people", "validation.learning.rule_people");
        }
        var dueDays = request.DueDays ?? DefaultDueDays;
        if (dueDays is < 1 or > MaxDueDays)
        {
            errors.Add("dueDays", "validation.learning.rule_due");
        }
        if (request.RecurrenceMonths is { } months && months is < 1 or > MaxRecurrenceMonths)
        {
            errors.Add("recurrenceMonths", "validation.learning.rule_recurrence");
        }
        var grace = request.GraceDays ?? 0;
        if (grace is < 0 or > MaxGraceDays)
        {
            errors.Add("graceDays", "validation.learning.rule_grace");
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.learning.unit");
        }
        errors.ThrowIfAny();

        // Scope (US2 scenario 4): named units must sit inside the rule's unit and named people must belong to it.
        if (request.OrgUnitId is { } scope)
        {
            var subtree = await hierarchy.GetSubtreesAsync([scope], ct);
            var units = (audience.AnyOf ?? []).SelectMany(c => c.OrgUnits ?? []).Select(t => t.Id);
            if (units.Any(id => !subtree.Contains(id)))
            {
                errors.Add("audience", "validation.learning.rule_scope");
            }
            var inScope = await hierarchy.GetMemberUserIdsAsync(subtree, ct);
            if (people.Any(p => !inScope.Contains(p)))
            {
                errors.Add("people", "validation.learning.rule_scope");
            }
        }
        if (people.Length > 0)
        {
            var known = await memberships.GetManyAsync(people, ct);
            if (people.Any(p => !known.TryGetValue(p, out var m) || m.Status != MembershipStatus.Active))
            {
                errors.Add("people", "validation.learning.rule_person");
            }
        }
        errors.ThrowIfAny();

        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into learning.assignment_rule (tenant_id, id, course_id, org_unit_id, audience, people, due_days, recurrence_months, grace_days, created_by, created_at)
            values (@tenant, @id, @courseId, @unit, @audience, @people, @dueDays, @months, @grace, @user, @now)
            """, new
            {
                tenant = context.RequireTenant(), id, courseId, unit = request.OrgUnitId, audience = LearningJson.Jsonb(audience), people, dueDays, months = request.RecurrenceMonths,
                grace, user = context.RequireUser(), now,
            }, ct);
        var rule = (await RuleAsync(id, ct))!;
        var given = await ApplyAsync(rule, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningAssignmentCreated, "assignment_rule", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { course = courseId, audience, people = people.Length, dueDays, recurrenceMonths = request.RecurrenceMonths, graceDays = grace, added = given }), ct);
        return await ViewAsync(rule, ct);
    }

    /// <summary>Ends a rule: the open assignments only it gave are withdrawn, completions stay.</summary>
    public async Task<RuleView> EndAsync(Guid courseId, Guid ruleId, CancellationToken ct)
    {
        await ManagedCourseAsync(courseId, true, ct);
        var rule = await RuleAsync(ruleId, ct);
        if (rule is null || rule.CourseId != courseId)
        {
            throw AppException.NotFound();
        }
        if (!(await authz.CheckAsync(Permissions.LearningManage, rule.OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied("authz.denied.learning_scope");
        }
        if (rule.Status == RuleStatuses.Active)
        {
            var withdrawn = await EndRuleAsync(rule, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.LearningAssignmentWithdrawn, "assignment_rule", ruleId.ToString(), TargetOrgUnitId: rule.OrgUnitId,
                Changes: new { course = courseId, withdrawn }), ct);
        }
        return await ViewAsync((await RuleAsync(ruleId, ct))!, ct);
    }

    private async Task<int> EndRuleAsync(RuleRow rule, CancellationToken ct)
    {
        var covered = await session.QueryAsync("delete from learning.rule_target where rule_id = @id returning learner_id", new { id = rule.Id }, r => r.GetGuid(0), ct);
        await session.ExecAsync("update learning.assignment_rule set status = 'ended', ended_by = @user, ended_at = @now where id = @id",
            new { id = rule.Id, user = context.ActorId, now = clock.UtcNow }, ct);
        var withdrawn = 0;
        foreach (var learner in covered)
        {
            withdrawn += await ReleaseAsync(rule.CourseId, learner, ct) ? 1 : 0;
        }
        return withdrawn;
    }

    private Task<RuleRow?> RuleAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {RuleColumns} from learning.assignment_rule where id = @id", new { id }, ReadRule, ct);

    // ---- Who a rule covers ----

    /// <summary>Active members the rule covers: its audience and named people, inside its scope.</summary>
    private async Task<HashSet<Guid>> TargetsAsync(RuleRow rule, CancellationToken ct)
    {
        var audience = rule.AudienceValue;
        var targets = audience.IsEveryone && rule.People.Length > 0 ? new HashSet<Guid>() : (await audiences.MembersAsync(audience, ct)).ToHashSet();
        if (rule.People.Length > 0)
        {
            var known = await memberships.GetManyAsync(rule.People, ct);
            targets.UnionWith(rule.People.Where(p => known.TryGetValue(p, out var m) && m.Status == MembershipStatus.Active));
        }
        if (rule.OrgUnitId is { } unit)
        {
            targets.IntersectWith(await hierarchy.GetMemberUserIdsAsync(await hierarchy.GetSubtreesAsync([unit], ct), ct));
        }
        return targets;
    }

    private async Task<bool> CoversAsync(RuleRow rule, Guid user, CancellationToken ct)
    {
        if (await memberships.GetAsync(user, ct) is not { Status: MembershipStatus.Active })
        {
            return false;
        }
        if (rule.OrgUnitId is { } unit)
        {
            var subtree = await hierarchy.GetSubtreesAsync([unit], ct);
            if (!(await hierarchy.GetActiveMembershipsAsync(user, ct)).Any(m => subtree.Contains(m.OrgUnitId)))
            {
                return false;
            }
        }
        var audience = rule.AudienceValue;
        return rule.People.Contains(user) || (!(audience.IsEveryone && rule.People.Length > 0) && await audiences.IncludesAsync(audience, user, ct));
    }

    /// <summary>
    /// Brings the rule's targets up to date, for everyone or for one person: newcomers get the course with their own due
    /// date, people no longer covered lose what only rules gave them. Returns how many people were added or removed.
    /// </summary>
    private async Task<int> ApplyAsync(RuleRow rule, Guid? onlyUser, CancellationToken ct)
    {
        var course = await courses.RowAsync(rule.CourseId, false, ct);
        if (course is not { Status: CourseStatuses.Published })
        {
            return 0;
        }
        var existing = (await session.QueryAsync("select learner_id from learning.rule_target where rule_id = @id and (@user::uuid is null or learner_id = @user)",
            new { id = rule.Id, user = onlyUser }, r => r.GetGuid(0), ct)).ToHashSet();
        HashSet<Guid> targets;
        if (onlyUser is { } one)
        {
            targets = await CoversAsync(rule, one, ct) ? [one] : [];
        }
        else
        {
            targets = await TargetsAsync(rule, ct);
        }
        var now = clock.UtcNow;
        var changed = 0;
        foreach (var learner in targets.Where(t => !existing.Contains(t)))
        {
            var calendar = await calendars.ForUserAsync(learner, ct);
            var due = NextWorkingDay(calendar, calendar.Today(now).AddDays(rule.DueDays));
            await session.ExecAsync(
                "insert into learning.rule_target (tenant_id, rule_id, learner_id, due_on, added_at) values (@tenant, @rule, @learner, @due, @now) on conflict do nothing",
                new { tenant = context.RequireTenant(), rule = rule.Id, learner, due, now }, ct);
            await GiveAsync(course, learner, due, rule.GraceDays, AssignmentSources.Rule, ct);
            changed++;
        }
        foreach (var learner in existing.Where(e => !targets.Contains(e)))
        {
            await session.ExecAsync("delete from learning.rule_target where rule_id = @rule and learner_id = @learner", new { rule = rule.Id, learner }, ct);
            await ReleaseAsync(rule.CourseId, learner, ct);
            changed++;
        }
        if (onlyUser is null)
        {
            await session.ExecAsync("update learning.assignment_rule set evaluated_at = @now where id = @id", new { id = rule.Id, now }, ct);
        }
        return changed;
    }

    /// <summary>The date, or the first working day after it on the learner's calendar (edge case "due on a weekend or holiday").</summary>
    internal static DateOnly NextWorkingDay(WorkingCalendarSnapshot calendar, DateOnly day)
    {
        for (var i = 0; i < 31; i++)
        {
            if (calendar.IsWorkingDay(day.AddDays(i)))
            {
                return day.AddDays(i);
            }
        }
        return day;
    }

    /// <summary>
    /// Gives the course: an open assignment takes the earlier due date (FR-017); a completion that still counts is left as
    /// it is unless <paramref name="again"/> asks for the course to be taken again; otherwise a new assignment starts and
    /// the learner is told.
    /// </summary>
    internal async Task GiveAsync(Courses.CourseRow course, Guid learner, DateOnly due, int grace, string source, CancellationToken ct, bool again = false)
    {
        var current = await player.CurrentAssignmentAsync(course.Id, learner, true, ct);
        if (current is { Status: AssignmentStatuses.Open })
        {
            await session.ExecAsync("update learning.assignment set due_on = @due, grace_days = @grace, reminders_sent = '{}' where id = @id and (due_on is null or due_on > @due)",
                new { id = current.Id, due, grace }, ct);
            return;
        }
        if (!again && Player.StateOf(current, course.RetrainingVersion) == LearnerStates.Completed)
        {
            return;
        }
        var created = await player.AssignAsync(course.Id, learner, source, due, ct);
        await session.ExecAsync("update learning.assignment set grace_days = @grace where id = @id", new { id = created.Id, grace }, ct);
        await NotifyAsync([learner], course, AssignedTemplate, NotificationCategories.RequestUpdate, $"{created.Id:N}:assigned", ct, due);
    }

    /// <summary>Withdraws an open assignment a rule gave once no active rule covers the learner. Completions stay.</summary>
    private async Task<bool> ReleaseAsync(Guid courseId, Guid learner, CancellationToken ct)
    {
        var still = await session.ScalarAsync<bool>(
            """
            select exists (select 1 from learning.rule_target t join learning.assignment_rule r on r.id = t.rule_id
              where r.course_id = @courseId and r.status = 'active' and t.learner_id = @learner)
            """, new { courseId, learner }, ct);
        if (still)
        {
            return false;
        }
        return await session.ExecAsync(
            "update learning.assignment set status = 'withdrawn', ended_at = @now where course_id = @courseId and learner_id = @learner and status = 'open' and source = 'rule'",
            new { courseId, learner, now = clock.UtcNow }, ct) > 0;
    }

    private async Task NotifyAsync(IReadOnlyCollection<Guid> recipients, Courses.CourseRow course, string template, string category, string dedupe, CancellationToken ct,
        DateOnly? due = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        var definition = await courses.VersionAsync(course.Id, course.PublishedVersion!.Value, ct);
        var parameters = new Dictionary<string, string> { ["course"] = definition is null ? "" : await courses.TitleAsync(definition, ct) };
        if (due is { } d)
        {
            parameters["due"] = d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        foreach (var (key, value) in extra ?? new Dictionary<string, string>())
        {
            parameters[key] = value;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, category, template, Courses.CoursePath(course.Id), parameters, DedupeKey: $"learning:{dedupe}"), ct);
    }

    // ---- Events ----

    /// <summary>New members get the course with their own due date; leavers lose open assignments only rules gave them (US2 scenarios 1 and 3).</summary>
    public Task HandleAsync(OrganizationMembershipChanged message, CancellationToken ct) => MemberChangedAsync(message.UserId, ct);

    public Task HandleAsync(TenantMembershipDisabled message, CancellationToken ct) => MemberChangedAsync(message.UserId, ct);

    private async Task MemberChangedAsync(Guid user, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.LearningMandatory, ct))
        {
            return;
        }
        var rules = await session.QueryAsync($"select {RuleColumns} from learning.assignment_rule where status = 'active' order by created_at, id", null, ReadRule, ct);
        foreach (var rule in rules)
        {
            await ApplyAsync(rule, user, ct);
        }
    }

    /// <summary>
    /// A version that requires retraining gives the course again to everyone whose last completion is older (FR-012), due
    /// as the rule that covers them says, or in 30 days, keeping how they had it.
    /// </summary>
    public async Task HandleAsync(CoursePublished message, CancellationToken ct)
    {
        if (!message.RequiresRetraining || message.Version <= 1 || await courses.RowAsync(message.CourseId, false, ct) is not { Status: CourseStatuses.Published } course)
        {
            return;
        }
        var learners = await session.QueryAsync(
            """
            select distinct on (a.learner_id) a.learner_id, a.source, a.status, a.completed_version from learning.assignment a
            where a.course_id = @course and a.status in ('open', 'completed')
            order by a.learner_id, (a.status = 'open') desc, a.completed_at desc nulls last
            """, new { course = message.CourseId }, r => (Learner: r.GetGuid(0), Source: r.GetString(1), Status: r.GetString(2), Version: r.Get<int?>("completed_version")), ct);
        var now = clock.UtcNow;
        foreach (var l in learners.Where(l => l.Status == AssignmentStatuses.Completed && l.Version < message.Version))
        {
            var rule = await session.QuerySingleOrDefaultAsync(
                """
                select r.due_days, r.grace_days from learning.rule_target t join learning.assignment_rule r on r.id = t.rule_id
                where r.course_id = @course and r.status = 'active' and t.learner_id = @learner order by r.due_days limit 1
                """, new { course = message.CourseId, learner = l.Learner }, r => (Due: r.GetInt32(0), Grace: r.GetInt32(1)), ct);
            var calendar = await calendars.ForUserAsync(l.Learner, ct);
            var due = NextWorkingDay(calendar, calendar.Today(now).AddDays(rule == default ? DefaultDueDays : rule.Due));
            var created = await player.AssignAsync(message.CourseId, l.Learner, l.Source, due, ct);
            await session.ExecAsync("update learning.assignment set grace_days = @grace where id = @id", new { id = created.Id, grace = rule == default ? 0 : rule.Grace }, ct);
            await NotifyAsync([l.Learner], course, RetrainingTemplate, NotificationCategories.RequestUpdate, $"{created.Id:N}:retraining", ct, due);
        }
    }

    // ---- Recurrence on completion ----

    /// <summary>When a completion expires: the shortest recurrence of the active rules that cover the learner, or never.</summary>
    internal static async Task<DateOnly?> ExpiryAsync(IDbSession session, IWorkingCalendar calendars, Guid courseId, Guid learner, DateTimeOffset completedAt, CancellationToken ct)
    {
        var months = await session.ScalarAsync<int?>(
            """
            select min(r.recurrence_months) from learning.rule_target t join learning.assignment_rule r on r.id = t.rule_id
            where r.course_id = @courseId and r.status = 'active' and t.learner_id = @learner
            """, new { courseId, learner }, ct);
        if (months is not { } m)
        {
            return null;
        }
        var calendar = await calendars.ForUserAsync(learner, ct);
        return calendar.Today(completedAt).AddMonths(m);
    }

    // ---- The job ----

    /// <summary>
    /// For one tenant, as the system: brings rules up to date at least hourly (groups and roles change without an event),
    /// sends reminders, tells overdue learners and their managers, and expires recurring completions with a new assignment.
    /// Returns how many assignments it touched.
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.LearningMandatory, ct))
        {
            return 0;
        }
        var moved = 0;
        var stale = await session.QueryAsync(
            $"select {RuleColumns} from learning.assignment_rule where status = 'active' and (evaluated_at is null or evaluated_at < @before) order by evaluated_at nulls first limit 20",
            new { before = now - ReevaluateAfter }, ReadRule, ct);
        foreach (var rule in stale)
        {
            moved += await ApplyAsync(rule, null, ct);
        }

        var today = (await calendars.TenantDefaultAsync(ct)).Today(now);
        var open = await session.QueryAsync(
            """
            select a.id, a.course_id, a.learner_id, a.due_on, a.grace_days, a.reminders_sent, a.overdue_notified_at from learning.assignment a
            join learning.course c on c.id = a.course_id
            where a.status = 'open' and a.due_on is not null and c.status = 'published' and a.due_on <= @horizon
              and (a.due_on + a.grace_days >= @today and not (a.reminders_sent @> @all)
                or a.due_on + a.grace_days < @today and (a.overdue_notified_at is null or a.overdue_notified_at <= @repeat))
            order by a.due_on, a.id limit @limit
            """, new { today, horizon = today.AddDays(ReminderDays.Max()), all = ReminderDays.ToArray(), repeat = now - OverdueRepeat, limit },
            r => (Id: r.GetGuid(0), Course: r.GetGuid(1), Learner: r.GetGuid(2), Due: r.GetFieldValue<DateOnly>(3), Grace: r.GetInt32(4), Sent: r.GetFieldValue<int[]>(5),
                Notified: r.Get<DateTimeOffset?>("overdue_notified_at")), ct);
        var rows = new Dictionary<Guid, Courses.CourseRow?>();
        foreach (var a in open)
        {
            if (!rows.TryGetValue(a.Course, out var course))
            {
                rows[a.Course] = course = await courses.RowAsync(a.Course, false, ct);
            }
            if (course is null)
            {
                continue;
            }
            if (a.Due.AddDays(a.Grace) < today)
            {
                await session.ExecAsync("update learning.assignment set overdue_notified_at = @now where id = @id", new { id = a.Id, now }, ct);
                var week = now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                await NotifyAsync([a.Learner], course, OverdueTemplate, NotificationCategories.AttestationDue, $"{a.Id:N}:overdue:{week}", ct, a.Due);
                if (await reportingLine.ManagerOfAsync(a.Learner, ct) is { } manager)
                {
                    var name = (await users.GetAsync(a.Learner, ct))?.DisplayName ?? "";
                    await NotifyAsync([manager], course, TeamOverdueTemplate, NotificationCategories.AttestationDue, $"{a.Id:N}:team_overdue:{week}", ct, a.Due,
                        new Dictionary<string, string> { ["person"] = name });
                }
                if (a.Notified is null)
                {
                    await outbox.EnqueueAsync(new CourseOverdue(a.Id, a.Course, a.Learner, a.Due), ct);
                }
                moved++;
                continue;
            }
            var due = ReminderDays.Where(d => a.Due.AddDays(-d) <= today && !a.Sent.Contains(d)).ToArray();
            if (due.Length == 0 || today > a.Due)
            {
                continue;
            }
            var sent = ReminderDays.Where(d => a.Due.AddDays(-d) <= today).ToArray();
            await session.ExecAsync("update learning.assignment set reminders_sent = @sent where id = @id", new { id = a.Id, sent }, ct);
            await NotifyAsync([a.Learner], course, ReminderTemplate, NotificationCategories.AttestationDue, $"{a.Id:N}:reminder:{due.Min()}", ct, a.Due);
            moved++;
        }

        // Recurrence (FR-008): an expired completion makes way for a new assignment while a rule still covers the learner.
        var expired = await session.QueryAsync(
            """
            update learning.assignment set status = 'expired', ended_at = @now
            where id in (select id from learning.assignment where status = 'completed' and expires_on <= @today order by expires_on limit @limit)
            returning course_id, learner_id
            """, new { today, now, limit }, r => (Course: r.GetGuid(0), Learner: r.GetGuid(1)), ct);
        foreach (var e in expired)
        {
            moved++;
            var rule = await session.QuerySingleOrDefaultAsync(
                """
                select r.due_days, r.grace_days from learning.rule_target t join learning.assignment_rule r on r.id = t.rule_id
                where r.course_id = @course and r.status = 'active' and t.learner_id = @learner order by r.due_days limit 1
                """, new { course = e.Course, learner = e.Learner }, r => (Due: r.GetInt32(0), Grace: r.GetInt32(1)), ct);
            if (rule == default || await courses.RowAsync(e.Course, false, ct) is not { Status: CourseStatuses.Published } course)
            {
                continue;
            }
            var calendar = await calendars.ForUserAsync(e.Learner, ct);
            var due = NextWorkingDay(calendar, calendar.Today(now).AddDays(rule.Due));
            if (await player.CurrentAssignmentAsync(e.Course, e.Learner, false, ct) is { Status: AssignmentStatuses.Open })
            {
                continue;
            }
            var created = await player.AssignAsync(e.Course, e.Learner, AssignmentSources.Rule, due, ct);
            await session.ExecAsync("update learning.assignment set grace_days = @grace where id = @id", new { id = created.Id, grace = rule.Grace }, ct);
            await NotifyAsync([e.Learner], course, AssignedTemplate, NotificationCategories.RequestUpdate, $"{created.Id:N}:assigned", ct, due);
        }
        return moved;
    }
}
