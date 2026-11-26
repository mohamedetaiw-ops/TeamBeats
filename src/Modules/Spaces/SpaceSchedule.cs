using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

/// <summary>A task's dates and estimate, changed together so that the start never falls after the due date. Version is the task's.</summary>
public sealed record ScheduleRequest(DateOnly? StartOn, DateOnly? DueOn, int? EstimateMinutes, int? Version);

public sealed record BlockerRequest(Guid? TaskId);

/// <summary>Another task as a link shows it. Conflict: this blocker is due after the blocked task starts.</summary>
public sealed record LinkedTask(Guid Id, string Title, string Status, DateOnly? StartOn, DateOnly? DueOn, bool Conflict);

/// <summary>
/// A task's place in the plan (spec 029 US8): its dates, estimate, what blocks it and what it blocks. Conflict is set
/// when any blocker is due after this task starts; FinishedBeforeBlocker when it was completed while a blocker was open.
/// </summary>
public sealed record TaskSchedule(Guid TaskId, DateOnly? StartOn, DateOnly? DueOn, int? EstimateMinutes, IReadOnlyList<LinkedTask> BlockedBy, IReadOnlyList<LinkedTask> Blocks,
    bool Conflict, bool FinishedBeforeBlocker, int Version, bool CanEdit);

public sealed record TimelineTask(Guid Id, string Title, string Status, DateOnly? StartOn, DateOnly? DueOn, Guid? MilestoneId, IReadOnlyList<PersonRef> Assignees, bool Overdue,
    bool Conflict, int Version);

public sealed record TimelineLink(Guid TaskId, Guid BlockerId, bool Conflict);

/// <summary>
/// The timeline of a space (US8 scenario 2): its dated tasks with their links and milestones, and the first and last
/// date shown. Undated lists the open tasks that have neither date, so nothing is lost from the plan.
/// </summary>
public sealed record TimelineView(DateOnly? From, DateOnly? To, IReadOnlyList<TimelineTask> Tasks, IReadOnlyList<TimelineLink> Links, IReadOnlyList<MilestoneView> Milestones,
    IReadOnlyList<TimelineTask> Undated, bool CanEdit);

/// <summary>
/// Start dates, estimates, "blocked by" links and the timeline (spec 029 R3 US8, FR-019, FR-020). Links stay inside one
/// space, a task has at most 20 blockers, and a link that would close a loop is refused. A blocker does not stop a task
/// from being completed: the task keeps a note that it finished first (the smallest safe default; the spec does not
/// make blockers a hard stop). Anyone who reads the space reads the plan; members and owners change it.
/// </summary>
public sealed class SpaceSchedule(IDbSession session, ITenantContext context, SpacesService spaces, SpaceMilestones milestones, IUserDirectory users,
    IWorkingCalendar calendars, IClock clock)
{
    public const int MaxBlockers = 20;
    public const int MaxTimelineTasks = 2000;
    public const int MaxEstimateMinutes = 600_000;

    private sealed record LinkRow(Guid TaskId, Guid BlockerId, Guid OtherId, string Title, string Status, DateOnly? StartOn, DateOnly? DueOn, bool Conflict);

    public async Task<TaskSchedule> GetAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        return await ScheduleAsync(standing, taskId, ct);
    }

    private async Task<TaskSchedule> ScheduleAsync(SpaceStanding standing, Guid taskId, CancellationToken ct)
    {
        var task = await session.QuerySingleOrDefaultAsync(
            "select start_on, due_on, estimate_minutes, finished_before_blocker, row_version from spaces.task where id = @taskId and space_id = @spaceId",
            new { taskId, spaceId = standing.Space.Id },
            r => (Start: r.Get<DateOnly?>("start_on"), Due: r.Get<DateOnly?>("due_on"), Estimate: r.Get<int?>("estimate_minutes"), Early: r.GetBoolean(3), Version: r.GetInt32(4)), ct);
        if (task == default)
        {
            throw AppException.NotFound();
        }
        // Both directions of the task's links, each with the other task and whether the blocker is due after the blocked task starts.
        var links = await session.QueryAsync(
            """
            select l.task_id, l.blocker_id, o.id, o.title, o.status, o.start_on, o.due_on,
              coalesce(b.due_on > t.start_on, false)
            from spaces.task_link l
            join spaces.task t on t.id = l.task_id
            join spaces.task b on b.id = l.blocker_id
            join spaces.task o on o.id = case when l.task_id = @taskId then l.blocker_id else l.task_id end
            where l.task_id = @taskId or l.blocker_id = @taskId
            order by o.due_on nulls last, o.title, o.id
            """, new { taskId },
            r => new LinkRow(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.Get<DateOnly?>("start_on"), r.Get<DateOnly?>("due_on"), r.GetBoolean(7)), ct);
        var blockedBy = links.Where(l => l.TaskId == taskId).Select(l => new LinkedTask(l.OtherId, l.Title, l.Status, l.StartOn, l.DueOn, l.Conflict)).ToList();
        var blocks = links.Where(l => l.BlockerId == taskId).Select(l => new LinkedTask(l.OtherId, l.Title, l.Status, l.StartOn, l.DueOn, l.Conflict)).ToList();
        return new TaskSchedule(taskId, task.Start, task.Due, task.Estimate, blockedBy, blocks, blockedBy.Any(b => b.Conflict), task.Early, task.Version, standing.Contributes);
    }

    private async Task<(SpaceStanding Standing, int Version)> ChangeableAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var standing = await spaces.ContributableAsync(spaceId, ct);
        var version = await session.ScalarAsync<int?>("select row_version from spaces.task where id = @taskId and space_id = @spaceId", new { taskId, spaceId }, ct)
            ?? throw AppException.NotFound();
        return (standing, version);
    }

    /// <summary>Sets the start, due date and estimate together: the dates form that is the keyboard way to move a bar (US8 scenario 5).</summary>
    public async Task<TaskSchedule> SetAsync(Guid spaceId, Guid taskId, ScheduleRequest request, CancellationToken ct)
    {
        var (standing, version) = await ChangeableAsync(spaceId, taskId, ct);
        if (request.Version != version)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.task_changed", null, new Dictionary<string, object?> { ["version"] = version });
        }
        var errors = new ValidationErrors();
        if (request is { StartOn: { } start, DueOn: { } due } && start > due)
        {
            errors.Add("startOn", "validation.spaces.task_dates");
        }
        if (request.EstimateMinutes is < 1 or > MaxEstimateMinutes)
        {
            errors.Add("estimateMinutes", "validation.spaces.task_estimate");
        }
        errors.ThrowIfAny();
        var milestone = await session.ScalarAsync<Guid?>("select milestone_id from spaces.task where id = @taskId", new { taskId }, ct);
        await session.ExecAsync(
            """
            update spaces.task set start_on = @start, due_on = @due, estimate_minutes = @estimate,
              reminded_for = case when due_on is distinct from @due then null else reminded_for end, updated_at = @now, row_version = row_version + 1
            where id = @taskId
            """, new { taskId, start = request.StartOn, due = request.DueOn, estimate = request.EstimateMinutes, now = clock.UtcNow }, ct);
        await milestones.RecountAsync(spaceId, milestone, ct);
        return await ScheduleAsync(standing, taskId, ct);
    }

    /// <summary>Says the task waits for another task of the same space (US8 scenario 1). A link that would close a loop is refused.</summary>
    public async Task<TaskSchedule> AddBlockerAsync(Guid spaceId, Guid taskId, BlockerRequest request, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        if (request.TaskId is not { } blocker || blocker == taskId
            || !await session.ScalarAsync<bool>("select exists(select 1 from spaces.task where id = @blocker and space_id = @spaceId)", new { blocker, spaceId }, ct))
        {
            throw AppException.Validation("taskId", "validation.spaces.task_blocker");
        }
        if (await session.ScalarAsync<int>("select count(*)::int from spaces.task_link where task_id = @taskId", new { taskId }, ct) >= MaxBlockers)
        {
            throw AppException.Validation("taskId", "validation.spaces.task_blocker_count");
        }
        // The new link closes a loop when the blocker already waits, directly or through other tasks, for this task.
        var loop = await session.ScalarAsync<bool>(
            """
            with recursive waits(id) as (
              select blocker_id from spaces.task_link where task_id = @blocker
              union
              select l.blocker_id from spaces.task_link l join waits w on l.task_id = w.id
            )
            select exists(select 1 from waits where id = @taskId) or @blocker = @taskId
            """, new { blocker, taskId }, ct);
        if (loop)
        {
            throw AppException.Conflict("conflict.spaces.task_loop");
        }
        await session.ExecAsync(
            """
            insert into spaces.task_link (tenant_id, space_id, task_id, blocker_id, created_by, created_at)
            values (platform.current_tenant_id(), @spaceId, @taskId, @blocker, @user, @now) on conflict do nothing
            """, new { spaceId, taskId, blocker, user = context.RequireUser(), now = clock.UtcNow }, ct);
        return await ScheduleAsync(standing, taskId, ct);
    }

    public async Task<TaskSchedule> RemoveBlockerAsync(Guid spaceId, Guid taskId, Guid blockerId, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        if (await session.ExecAsync("delete from spaces.task_link where task_id = @taskId and blocker_id = @blockerId", new { taskId, blockerId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        return await ScheduleAsync(standing, taskId, ct);
    }

    /// <summary>
    /// The plan of the space (US8 scenarios 2 and 3). A task with only a due date shows as a one-day bar on that date and
    /// one with only a start runs to its start; open tasks without dates come back as Undated.
    /// </summary>
    public async Task<TimelineView> TimelineAsync(Guid spaceId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        var rows = await session.QueryAsync(
            """
            select t.id, t.title, t.status, t.start_on, t.due_on, t.milestone_id, t.row_version,
              coalesce((select array_agg(a.user_id order by a.assigned_at, a.user_id) from spaces.task_assignee a where a.task_id = t.id), '{}')
            from spaces.task t
            where t.space_id = @spaceId and (t.start_on is not null or t.due_on is not null or t.status = 'open')
            order by coalesce(t.start_on, t.due_on) nulls last, t.due_on nulls last, t.position, t.id
            limit @limit
            """, new { spaceId, limit = MaxTimelineTasks },
            r => (Id: r.GetGuid(0), Title: r.GetString(1), Status: r.GetString(2), Start: r.Get<DateOnly?>("start_on"), Due: r.Get<DateOnly?>("due_on"),
                Milestone: r.Get<Guid?>("milestone_id"), Version: r.GetInt32(6), Assignees: r.GetFieldValue<Guid[]>(7)), ct);
        var links = await session.QueryAsync(
            """
            select l.task_id, l.blocker_id, coalesce(b.due_on > t.start_on, false)
            from spaces.task_link l join spaces.task t on t.id = l.task_id join spaces.task b on b.id = l.blocker_id
            where l.space_id = @spaceId
            order by l.created_at, l.task_id, l.blocker_id
            """, new { spaceId }, r => new TimelineLink(r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2)), ct);
        var names = await users.GetManyAsync(rows.SelectMany(r => r.Assignees).Distinct().ToList(), ct);
        var today = (await calendars.ForUserAsync(context.RequireUser(), ct)).Today(clock.UtcNow);
        var conflicted = links.Where(l => l.Conflict).Select(l => l.TaskId).ToHashSet();
        var tasks = rows.Select(r => new TimelineTask(r.Id, r.Title, r.Status, r.Start, r.Due, r.Milestone,
            r.Assignees.Select(a => new PersonRef(a, names.TryGetValue(a, out var u) ? u.DisplayName : "")).ToList(),
            r.Status == "open" && r.Due is { } due && due < today, conflicted.Contains(r.Id), r.Version)).ToList();
        var dated = tasks.Where(t => t.StartOn is not null || t.DueOn is not null).ToList();
        var shown = dated.Select(t => t.Id).ToHashSet();
        var marks = await milestones.MilestonesAsync(spaceId, ct);
        var dates = dated.SelectMany(t => new[] { t.StartOn, t.DueOn }).Concat(marks.Select(m => m.DueOn)).Where(d => d is not null).Select(d => d!.Value).ToList();
        return new TimelineView(dates.Count == 0 ? null : dates.Min(), dates.Count == 0 ? null : dates.Max(), dated,
            links.Where(l => shown.Contains(l.TaskId) && shown.Contains(l.BlockerId)).ToList(), marks,
            tasks.Where(t => !shown.Contains(t.Id)).ToList(), standing.Contributes);
    }
}
