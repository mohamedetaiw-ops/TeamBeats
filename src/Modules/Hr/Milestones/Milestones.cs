using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Milestones;

public static class MilestoneKinds
{
    public const string Probation = "probation";
    public const string Contract = "contract";

    public static readonly IReadOnlyList<string> All = [Probation, Contract];

    public static string Field(string kind) => kind == Probation ? HrFields.ProbationEndDate : HrFields.ContractEndDate;
}

public static class MilestoneStatuses
{
    public const string Open = "open";
    public const string Reviewed = "reviewed";
    public const string Closed = "closed";

    public static readonly IReadOnlyList<string> All = [Open, Reviewed, Closed];
}

public static class MilestoneOutcomes
{
    public const string Confirm = "confirm";
    public const string Extend = "extend";
    public const string End = "end";

    public static readonly IReadOnlyList<string> All = [Confirm, Extend, End];
}

public sealed record MilestoneDateInput(DateOnly? DueDate);

public sealed record MilestoneReviewInput(string? Outcome, DateOnly? NewDate, string? Comment);

public sealed record MilestoneReviewView(Guid Id, DateOnly DueDate, string Outcome, DateOnly? NewDate, string Comment, PersonRef ReviewedBy, DateTimeOffset ReviewedAt,
    string WriteBack);

public sealed record MilestoneRights(bool Review, bool SetDate);

/// <summary>
/// One milestone as HR officers and the reviewer see it. <see cref="DaysLeft"/> counts from today in the tenant's time
/// zone; <see cref="ReviewOpen"/> is true while a probation review waits for its reviewer.
/// </summary>
public sealed record MilestoneView(
    Guid Id,
    PersonRef Person,
    string Kind,
    DateOnly DueDate,
    string Source,
    string Status,
    int DaysLeft,
    bool ReviewOpen,
    DateOnly? HireDate,
    string? JobTitle,
    IReadOnlyList<MilestoneReviewView> Reviews,
    MilestoneRights Rights);

internal sealed record MilestoneRow(Guid Id, Guid UserId, string Kind, DateOnly DueDate, string Source, string Status, DateOnly? AlertSentFor, DateOnly? ReviewOpenedFor,
    DateOnly? ReminderSentFor);

/// <summary>
/// Probation and contract milestones (spec 022 US9, FR-027, FR-028). Dates come from HR through spec 019 where the
/// connection maps them, or are set by an HR officer covering the person where HR has none. Thirty days before a
/// probation ends the line manager gets a review task in the inbox, with a reminder seven days before while it is still
/// open; a person without a line manager is reviewed by the HR officers covering them. Sixty days before a contract
/// ends the HR officers of the person's unit are told, once per date. A moved date reopens the milestone and sends
/// everything again. The outcome of a review goes to HR as a spec 019 data change of group <c>probation</c> when the
/// date came from HR and a connection takes changes; the comment never leaves TeamBeats.
/// </summary>
public sealed class Milestones(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IAuthorizationProvisioning roles,
    IEntitlementService entitlements,
    IAuditWriter audit,
    INotificationSender notifications,
    IUserDirectory users,
    IReportingLine reporting,
    IHrRecords hrRecords,
    IHrDataChange writeBack,
    LeaveLedger people)
{
    public const int ReviewDaysBefore = 30;
    public const int ReminderDaysBefore = 7;
    public const int ContractAlertDaysBefore = 60;
    public const int MaxCommentLength = 2000;
    public const string WriteBackGroup = "probation";

    public const string ContractAlertTemplate = "hr.milestone_contract_alert";
    public const string ReviewOpenedTemplate = "hr.milestone_review_opened";
    public const string ReviewReminderTemplate = "hr.milestone_review_reminder";
    public const string ReviewedTemplate = "hr.milestone_reviewed";

    public static string Link(Guid id) => $"/hr/milestones/{id}";

    private const string Columns = "id, user_id, kind, due_date, source, status, alert_sent_for, review_opened_for, reminder_sent_for";

    private static MilestoneRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetString(4), r.GetString(5),
        r.Get<DateOnly?>("alert_sent_for"), r.Get<DateOnly?>("review_opened_for"), r.Get<DateOnly?>("reminder_sent_for"));

    internal async Task<MilestoneRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.milestone where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    private async Task<MilestoneRow?> RowOfAsync(Guid userId, string kind, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {Columns} from hr.milestone where user_id = @userId and kind = @kind for update", new { userId, kind }, Read, ct);

    private static DateOnly? Date(IReadOnlyDictionary<string, string>? fields, string key) =>
        fields?.GetValueOrDefault(key) is { } v && DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    // ---- refreshing and the daily pass ----

    /// <summary>
    /// Brings the dates HR holds into the milestones, then sends what is due. Run by the daily job and after every HR sync.
    /// Returns how many milestones changed.
    /// </summary>
    public async Task<int> RefreshAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrMilestones, ct))
        {
            return 0;
        }
        var changed = 0;
        foreach (var record in await hrRecords.MatchedAsync(null, ct))
        {
            foreach (var kind in MilestoneKinds.All)
            {
                if (Date(record.Fields, MilestoneKinds.Field(kind)) is { } due && await PutAsync(record.UserId, kind, due, "hr", null, ct))
                {
                    changed++;
                }
            }
        }
        await RunDueAsync(ct);
        return changed;
    }

    /// <summary>Writes a date: a new or moved date reopens the milestone and clears what was sent for the old one.</summary>
    private async Task<bool> PutAsync(Guid userId, string kind, DateOnly due, string source, Guid? by, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await RowOfAsync(userId, kind, ct);
        if (row is null)
        {
            await session.ExecAsync(
                """
                insert into hr.milestone (tenant_id, id, user_id, kind, due_date, source, status, set_by, created_at, updated_at)
                values (platform.current_tenant_id(), @id, @userId, @kind, @due, @source, 'open', @by, @now, @now)
                """, new { id = Ids.New(), userId, kind, due, source, by, now }, ct);
            return true;
        }
        if (row.DueDate == due)
        {
            if (row.Source != source)
            {
                await session.ExecAsync("update hr.milestone set source = @source, set_by = @by, updated_at = @now where id = @id", new { id = row.Id, source, by, now }, ct);
            }
            return false;
        }
        await session.ExecAsync(
            """
            update hr.milestone set due_date = @due, source = @source, status = 'open', alert_sent_for = null, review_opened_for = null, reminder_sent_for = null,
              set_by = @by, updated_at = @now
            where id = @id
            """, new { id = row.Id, due, source, by, now }, ct);
        return true;
    }

    /// <summary>Sends contract alerts, opens probation reviews and sends reminders that are due today, each once per date.</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        var today = await people.TodayAsync(ct);
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.milestone where status = 'open' and due_date >= @today and due_date <= @horizon order by due_date for update",
            new { today, horizon = today.AddDays(ContractAlertDaysBefore) }, Read, ct);
        foreach (var row in rows)
        {
            var left = row.DueDate.DayNumber - today.DayNumber;
            if (await people.PersonAsync(row.UserId, ct) is not { Active: true } person)
            {
                continue;
            }
            var name = await NameAsync(row.UserId, ct);
            var parameters = new Dictionary<string, string> { ["name"] = name, ["date"] = row.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            var stamp = row.DueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (row.Kind == MilestoneKinds.Contract)
            {
                if (row.AlertSentFor != row.DueDate)
                {
                    await session.ExecAsync("update hr.milestone set alert_sent_for = due_date, updated_at = @now where id = @id", new { id = row.Id, now = clock.UtcNow }, ct);
                    await notifications.SendAsync(new NotificationRequest(await HrOfficersAsync(person, ct), NotificationCategories.RequestUpdate, ContractAlertTemplate,
                        Link(row.Id), parameters, DedupeKey: $"hr.milestone:{row.Id:N}:alert:{stamp}"), ct);
                }
                continue;
            }
            if (left > ReviewDaysBefore)
            {
                continue;
            }
            var reviewers = await ReviewersAsync(person, ct);
            if (row.ReviewOpenedFor != row.DueDate)
            {
                // A review opened inside the last week gets no separate reminder: the task itself is the reminder.
                await session.ExecAsync(
                    "update hr.milestone set review_opened_for = due_date, reminder_sent_for = case when @late then due_date else null end, updated_at = @now where id = @id",
                    new { id = row.Id, late = left <= ReminderDaysBefore, now = clock.UtcNow }, ct);
                await notifications.SendAsync(new NotificationRequest(reviewers, NotificationCategories.ApprovalWaiting, ReviewOpenedTemplate, Link(row.Id), parameters,
                    DedupeKey: $"hr.milestone:{row.Id:N}:review:{stamp}"), ct);
            }
            else if (left <= ReminderDaysBefore && row.ReminderSentFor != row.DueDate)
            {
                await session.ExecAsync("update hr.milestone set reminder_sent_for = due_date, updated_at = @now where id = @id", new { id = row.Id, now = clock.UtcNow }, ct);
                await notifications.SendAsync(new NotificationRequest(reviewers, NotificationCategories.ApprovalWaiting, ReviewReminderTemplate, Link(row.Id), parameters,
                    DedupeKey: $"hr.milestone:{row.Id:N}:reminder:{stamp}"), ct);
            }
        }
    }

    /// <summary>HR officers whose scope covers the person's unit, and those of the whole tenant; never the person.</summary>
    private async Task<IReadOnlyList<Guid>> HrOfficersAsync(LeavePerson person, CancellationToken ct)
    {
        var to = new HashSet<Guid>(await roles.TenantHoldersAsync(Permissions.HrManage, ct));
        if (person.PrimaryUnit is { } unit)
        {
            foreach (var holder in await roles.OrgHoldersAsync(Permissions.HrManage, ct))
            {
                if (holder.UnitId == unit || holder.Subtree && person.Path.Contains(holder.UnitId))
                {
                    to.Add(holder.UserId);
                }
            }
        }
        to.Remove(person.UserId);
        return to.ToList();
    }

    /// <summary>The line manager; without one, the HR officers covering the person (smallest safe default).</summary>
    private async Task<IReadOnlyList<Guid>> ReviewersAsync(LeavePerson person, CancellationToken ct) =>
        await reporting.ManagerOfAsync(person.UserId, ct) is { } manager && manager != person.UserId ? [manager] : await HrOfficersAsync(person, ct);

    private async Task<string> NameAsync(Guid userId, CancellationToken ct) => (await users.GetAsync(userId, ct))?.DisplayName ?? userId.ToString();

    // ---- who may see and act ----

    private async Task<bool> HrCoversAsync(LeavePerson person, CancellationToken ct) => person.UserId != context.RequireUser() && await people.HrCoversAsync(person, ct);

    /// <summary>Whether the caller reviews the person's probation: their line manager, or HR covering them when there is none.</summary>
    private async Task<bool> ReviewsAsync(LeavePerson person, CancellationToken ct)
    {
        var me = context.RequireUser();
        if (me == person.UserId)
        {
            return false;
        }
        return await reporting.ManagerOfAsync(person.UserId, ct) is { } manager && manager != person.UserId ? manager == me : await people.HrCoversAsync(person, ct);
    }

    private static bool ReviewWaits(MilestoneRow row) =>
        row.Kind == MilestoneKinds.Probation && row.Status == MilestoneStatuses.Open && row.ReviewOpenedFor == row.DueDate;

    // ---- HR officers ----

    public async Task<IReadOnlyList<MilestoneView>> ListAsync(string? kind, string? status, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrMilestones, ct);
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        if (kind is not null && !MilestoneKinds.All.Contains(kind))
        {
            throw AppException.Validation("kind", "validation.hr.milestone_kind");
        }
        if (status is not null && !MilestoneStatuses.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.hr.milestone_status");
        }
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.milestone where (@kind::text is null or kind = @kind) and (@status::text is null or status = @status) order by due_date, id limit 2000",
            new { kind, status }, Read, ct);
        var persons = await people.PeopleAsync(rows.Select(r => r.UserId).Distinct().ToList(), ct);
        var me = context.RequireUser();
        var list = new List<MilestoneView>();
        foreach (var row in rows)
        {
            if (row.UserId != me && persons.TryGetValue(row.UserId, out var person)
                && (person.PrimaryUnit is { } unit ? coverage.Covers(unit) : coverage.WholeTenant))
            {
                list.Add(await ViewAsync(row, person, false, ct));
            }
            if (list.Count == 500)
            {
                break;
            }
        }
        return list;
    }

    /// <summary>Sets or clears a date by hand for a person HR has no date for (US9 scenario 4).</summary>
    public async Task<MilestoneView?> SetDateAsync(Guid userId, string kind, MilestoneDateInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrMilestones, ct);
        if (!MilestoneKinds.All.Contains(kind))
        {
            throw AppException.NotFound();
        }
        if (await people.PersonAsync(userId, ct) is not { } person || !await HrCoversAsync(person, ct))
        {
            throw (await authz.CoverageAsync(Permissions.HrManage, ct)).Any ? AppException.NotFound() : AppException.Denied();
        }
        var existing = await RowOfAsync(userId, kind, ct);
        if (existing is { Source: "hr" })
        {
            throw AppException.Conflict("conflict.hr.milestone_from_hr");
        }
        var today = await people.TodayAsync(ct);
        if (input.DueDate is not { } due)
        {
            if (existing is not null)
            {
                await session.ExecAsync("delete from hr.milestone where id = @id", new { id = existing.Id }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.HrMilestoneSet, "milestone", existing.Id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
                    Changes: new { person = userId, kind, dueDate = (string?)null }), ct);
            }
            return null;
        }
        if (due < today || due > today.AddYears(10))
        {
            throw AppException.Validation("dueDate", "validation.hr.milestone_date");
        }
        await PutAsync(userId, kind, due, "manual", context.RequireUser(), ct);
        var row = (await RowOfAsync(userId, kind, ct))!;
        await audit.WriteAsync(new AuditEntry(AuditActions.HrMilestoneSet, "milestone", row.Id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { person = userId, kind, dueDate = due }), ct);
        await RunDueAsync(ct);
        return await ViewAsync((await RowAsync(row.Id, false, ct))!, person, false, ct);
    }

    // ---- one milestone ----

    public async Task<MilestoneView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrMilestones, ct);
        if (await RowAsync(id, false, ct) is not { } row || await people.PersonAsync(row.UserId, ct) is not { } person)
        {
            throw AppException.NotFound();
        }
        var reviews = row.Kind == MilestoneKinds.Probation && await ReviewsAsync(person, ct);
        if (!reviews && !await HrCoversAsync(person, ct))
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, person, reviews, ct);
    }

    public async Task<MilestoneView> ReviewAsync(Guid id, MilestoneReviewInput input, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrMilestones, ct);
        if (await RowAsync(id, true, ct) is not { } row || await people.PersonAsync(row.UserId, ct) is not { } person || row.Kind != MilestoneKinds.Probation
            || !await ReviewsAsync(person, ct))
        {
            throw AppException.NotFound();
        }
        if (!ReviewWaits(row))
        {
            throw AppException.Conflict("conflict.hr.milestone_no_review");
        }
        var errors = new ValidationErrors();
        var outcome = input.Outcome;
        if (outcome is null || !MilestoneOutcomes.All.Contains(outcome))
        {
            errors.Add("outcome", "validation.hr.milestone_outcome");
        }
        var comment = input.Comment?.Trim();
        if (string.IsNullOrEmpty(comment) || comment.Length > MaxCommentLength || comment.Any(c => char.IsControl(c) && c is not '\n' and not '\r'))
        {
            errors.Add("comment", "validation.hr.milestone_comment");
        }
        DateOnly? newDate = null;
        if (outcome == MilestoneOutcomes.Extend)
        {
            if (input.NewDate is not { } d || d <= row.DueDate || d > row.DueDate.AddYears(1))
            {
                errors.Add("newDate", "validation.hr.milestone_new_date");
            }
            newDate = input.NewDate;
        }
        errors.ThrowIfAny();

        var me = context.RequireUser();
        var now = clock.UtcNow;
        var reviewId = Ids.New();
        var state = "none";
        if (row.Source == "hr")
        {
            var values = new Dictionary<string, string> { ["outcome"] = outcome! };
            if (newDate is { } extended)
            {
                values[HrFields.ProbationEndDate] = extended.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            state = await writeBack.HandOffAsync(new HrDataChangeHandOffRequest(reviewId, row.UserId, WriteBackGroup, values), ct) switch
            {
                HrDataChangeHandOffStates.Unavailable => "none",
                HrDataChangeHandOffStates.Recorded => "recorded",
                HrDataChangeHandOffStates.Failed => "failed",
                _ => "hr",
            };
        }
        await session.ExecAsync(
            """
            insert into hr.milestone_review (tenant_id, id, milestone_id, user_id, due_date, outcome, new_date, comment, reviewed_by, reviewed_at, write_back)
            values (platform.current_tenant_id(), @reviewId, @id, @userId, @due, @outcome, @newDate, @comment, @me, @now, @state)
            """, new { reviewId, id = row.Id, userId = row.UserId, due = row.DueDate, outcome, newDate, comment, me, now, state }, ct);
        if (outcome == MilestoneOutcomes.Extend && row.Source == "manual")
        {
            // Nothing in HR will move the date, so the extension is the new date here.
            await PutAsync(row.UserId, row.Kind, newDate!.Value, "manual", me, ct);
        }
        else
        {
            // An extension of an HR date waits for HR to bring the new date, which reopens the milestone.
            await session.ExecAsync("update hr.milestone set status = @status, updated_at = @now where id = @id",
                new { id = row.Id, status = outcome == MilestoneOutcomes.Extend ? MilestoneStatuses.Reviewed : MilestoneStatuses.Closed, now }, ct);
        }
        // The outcome only: the comment stays in TeamBeats (section 13).
        await audit.WriteAsync(new AuditEntry(AuditActions.HrMilestoneReviewed, "milestone", row.Id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
            Changes: new { person = row.UserId, kind = row.Kind, dueDate = row.DueDate, outcome, newDate, writeBack = state }), ct);
        if (state == "recorded")
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrMilestoneWrittenBack, "milestone", row.Id.ToString(), TargetOrgUnitId: person.PrimaryUnit,
                Changes: new { person = row.UserId, kind = row.Kind, outcome }), ct);
        }
        var hr = (await HrOfficersAsync(person, ct)).Where(u => u != me).ToList();
        if (hr.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(hr, NotificationCategories.RequestUpdate, ReviewedTemplate, Link(row.Id),
                new Dictionary<string, string> { ["name"] = await NameAsync(row.UserId, ct), ["outcome"] = outcome! }, DedupeKey: $"hr.milestone:{reviewId:N}:reviewed"), ct);
        }
        if (outcome == MilestoneOutcomes.Extend && row.Source == "manual")
        {
            await RunDueAsync(ct);
        }
        return await ViewAsync((await RowAsync(row.Id, false, ct))!, person, true, ct);
    }

    /// <summary>HR answered the write-back of a review (spec 019).</summary>
    internal async Task HandOffChangedAsync(HrDataChangeHandOffChanged message, CancellationToken ct)
    {
        var review = await session.QuerySingleOrDefaultAsync(
            "select milestone_id, user_id, outcome from hr.milestone_review where id = @id and write_back = 'hr' for update", new { id = message.RequestId },
            r => (Milestone: r.GetGuid(0), User: r.GetGuid(1), Outcome: r.GetString(2)), ct);
        if (review.Outcome is null || message.State is not (HrDataChangeHandOffStates.Recorded or HrDataChangeHandOffStates.Failed))
        {
            return;
        }
        var recorded = message.State == HrDataChangeHandOffStates.Recorded;
        await session.ExecAsync("update hr.milestone_review set write_back = @state where id = @id", new { id = message.RequestId, state = recorded ? "recorded" : "failed" }, ct);
        if (recorded)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.HrMilestoneWrittenBack, "milestone", review.Milestone.ToString(),
                Changes: new { person = review.User, kind = MilestoneKinds.Probation, outcome = review.Outcome, reference = message.ExternalReference }), ct);
        }
    }

    // ---- the review in the task inbox ----

    /// <summary>Open probation reviews the signed-in person is the reviewer of (spec 016 personal tasks).</summary>
    internal async Task<IReadOnlyList<Workflow.Contracts.PersonalTask>> OpenReviewsForAsync(Guid userId, CancellationToken ct)
    {
        if (userId != context.RequireUser() || !await entitlements.IsEnabledAsync(Capabilities.HrMilestones, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync(
            $"select {Columns} from hr.milestone where kind = 'probation' and status = 'open' and review_opened_for = due_date order by due_date limit 500", null, Read, ct);
        if (rows.Count == 0)
        {
            return [];
        }
        var persons = await people.PeopleAsync(rows.Select(r => r.UserId).Distinct().ToList(), ct);
        var tasks = new List<Workflow.Contracts.PersonalTask>();
        foreach (var row in rows)
        {
            if (!persons.TryGetValue(row.UserId, out var person) || !person.Active || !await ReviewsAsync(person, ct))
            {
                continue;
            }
            var name = await NameAsync(row.UserId, ct);
            var due = new DateTimeOffset(row.DueDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            tasks.Add(new Workflow.Contracts.PersonalTask("probation_review", row.Id, new Dictionary<string, string> { ["en"] = name, ["ar"] = name }, Link(row.Id),
                due.AddDays(-ReviewDaysBefore), due));
        }
        return tasks;
    }

    private async Task<MilestoneView> ViewAsync(MilestoneRow row, LeavePerson person, bool reviews, CancellationToken ct)
    {
        var today = await people.TodayAsync(ct);
        var reviewRows = await session.QueryAsync(
            "select id, due_date, outcome, new_date, comment, reviewed_by, reviewed_at, write_back from hr.milestone_review where milestone_id = @id order by reviewed_at desc limit 20",
            new { id = row.Id }, r => (Id: r.GetGuid(0), Due: r.GetFieldValue<DateOnly>(1), Outcome: r.GetString(2), NewDate: r.Get<DateOnly?>("new_date"),
                Comment: r.GetString(4), By: r.GetGuid(5), At: r.GetFieldValue<DateTimeOffset>(6), WriteBack: r.GetString(7)), ct);
        var list = new List<MilestoneReviewView>();
        foreach (var r in reviewRows)
        {
            list.Add(new MilestoneReviewView(r.Id, r.Due, r.Outcome, r.NewDate, r.Comment, new PersonRef(r.By, await NameAsync(r.By, ct)), r.At, r.WriteBack));
        }
        var record = (await hrRecords.MatchedAsync([row.UserId], ct)).FirstOrDefault();
        var hrCovers = await HrCoversAsync(person, ct);
        var rights = new MilestoneRights(ReviewWaits(row) && (reviews || await ReviewsAsync(person, ct)), hrCovers && row.Source == "manual");
        return new MilestoneView(row.Id, new PersonRef(row.UserId, await NameAsync(row.UserId, ct)), row.Kind, row.DueDate, row.Source, row.Status,
            row.DueDate.DayNumber - today.DayNumber, ReviewWaits(row), person.HireDate, record?.Fields.GetValueOrDefault(HrFields.JobTitle), list, rights);
    }
}

/// <summary>
/// Probation reviews in the spec 016 task inbox, the job that brings HR's dates after each sync, and HR's answers to a
/// review's write-back.
/// </summary>
public sealed class MilestoneHandlers(Milestones milestones)
    : Workflow.Contracts.IPersonalTaskSource, Messaging.IMessageHandler<HrSyncCompleted>, Messaging.IMessageHandler<HrDataChangeHandOffChanged>
{
    public Task<IReadOnlyList<Workflow.Contracts.PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default) => milestones.OpenReviewsForAsync(userId, ct);

    public async Task HandleAsync(HrSyncCompleted message, CancellationToken ct) => await milestones.RefreshAsync(ct);

    public Task HandleAsync(HrDataChangeHandOffChanged message, CancellationToken ct) => milestones.HandOffChangedAsync(message, ct);
}
