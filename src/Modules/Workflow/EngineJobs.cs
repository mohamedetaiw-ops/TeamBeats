using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow;

/// <summary>Kinds of engine jobs. R1 has one: the reminder when a user task reaches its due time.</summary>
public static class EngineJobKinds
{
    public const string TaskDue = "task_due";

    /// <summary>A timer a token of a graph instance waits for (spec 024 FR-008).</summary>
    public const string Timer = "timer";

    /// <summary>A timer start event of a published process model: starts an instance of the latest version.</summary>
    public const string TimerStart = "timer_start";
}

/// <summary>
/// Working-day due times on user tasks (spec 024 FR-008, R1 part). A step due in N working days is due at the end of the
/// Nth working day after the day the task was created, in the time zone of the calendar of the instance's org unit.
/// </summary>
public static class DueTimes
{
    public static DateTimeOffset For(WorkingCalendarSnapshot calendar, DateTimeOffset createdAt, int workingDays)
    {
        var day = calendar.AddWorkingDays(calendar.Today(createdAt), workingDays);
        // The end of that day is the start of the next one in the calendar's time zone.
        var local = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var zone = calendar.Zone;
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}

/// <summary>
/// Engine jobs (FR-012): timers stored in <c>workflow.engine_job</c>, claimed with row locks so two workers never run the
/// same job, and handled idempotently so a duplicate run has no second effect. Runs in the current tenant only.
/// </summary>
public sealed class EngineJobs(IDbSession session, IWorkingCalendar calendars, IOutbox outbox, IClock clock, IServiceProvider services)
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    internal async Task<DateTimeOffset> DueAtAsync(Guid? orgUnitId, DateTimeOffset createdAt, int workingDays, CancellationToken ct)
    {
        var calendar = orgUnitId is { } unit ? await calendars.ForUnitAsync(unit, ct) : await calendars.TenantDefaultAsync(ct);
        return DueTimes.For(calendar, createdAt, workingDays);
    }

    internal Task ScheduleTaskDueAsync(Guid instanceId, Guid taskId, DateTimeOffset dueAt, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into workflow.engine_job (tenant_id, id, kind, instance_id, task_id, due_at)
            values (platform.current_tenant_id(), @id, @kind, @instanceId, @taskId, @dueAt)
            """, new { id = Ids.New(), kind = EngineJobKinds.TaskDue, instanceId, taskId, dueAt }, ct);

    /// <summary>Closes the pending jobs of a task that is no longer open.</summary>
    internal Task CloseTaskJobsAsync(Guid taskId, CancellationToken ct) =>
        session.ExecAsync("update workflow.engine_job set done_at = @now where task_id = @taskId and done_at is null", new { taskId, now = clock.UtcNow }, ct);

    internal Task CloseInstanceJobsAsync(Guid instanceId, CancellationToken ct) =>
        session.ExecAsync("update workflow.engine_job set done_at = @now where instance_id = @instanceId and done_at is null", new { instanceId, now = clock.UtcNow }, ct);

    /// <summary>
    /// Runs jobs due at <paramref name="now"/>, oldest first. Rows are locked for the caller's transaction and rows another
    /// worker holds are skipped. A job due during an outage runs once when workers return. Returns the number handled.
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var due = await session.QueryAsync(
            """
            select id from workflow.engine_job
            where done_at is null and due_at <= @now and (locked_until is null or locked_until < @now)
            order by due_at, id limit @limit for update skip locked
            """, new { now, limit = Math.Clamp(limit, 1, 1000) }, r => r.GetGuid(0), ct);
        var handled = 0;
        foreach (var id in due)
        {
            handled += await RunAsync(id, ct) ? 1 : 0;
        }
        return handled;
    }

    /// <summary>Runs one job. A job already done (a duplicate delivery) does nothing and returns false.</summary>
    public async Task<bool> RunAsync(Guid jobId, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var job = await session.QuerySingleOrDefaultAsync(
            """
            update workflow.engine_job set done_at = @now, attempts = attempts + 1
            where id = @jobId and done_at is null
            returning kind, instance_id, task_id, due_at, token_id, element_id, detail::text, definition_key
            """, new { jobId, now }, r => (Kind: r.GetString(0), InstanceId: r.Get<Guid?>("instance_id") ?? Guid.Empty, TaskId: r.Get<Guid?>("task_id"),
                DueAt: r.GetFieldValue<DateTimeOffset>(3), Token: r.Get<Guid?>("token_id"), Element: r.Get<string?>("element_id"), Detail: r.Get<string?>("detail"),
                DefinitionKey: r.Get<string?>("definition_key")), ct);
        if (job.Kind is null)
        {
            return false;
        }
        if (job.Kind == EngineJobKinds.Timer && job.Token is { } token && job.Element is { } element)
        {
            await services.GetRequiredService<ProcessEngine>().OnTimerAsync(job.InstanceId, token, element, job.Detail is null ? null : JsonNode.Parse(job.Detail) as JsonObject, ct);
            return true;
        }
        if (job.Kind == EngineJobKinds.TimerStart && job.DefinitionKey is { } key && job.Element is { } start)
        {
            await services.GetRequiredService<ProcessEngine>().OnTimerStartAsync(jobId, key, start, job.DueAt, job.Detail is null ? null : JsonNode.Parse(job.Detail) as JsonObject, ct);
            return true;
        }
        if (job.Kind == EngineJobKinds.TaskDue && job.TaskId is { } taskId)
        {
            var task = await session.QuerySingleOrDefaultAsync(
                """
                select t.element_id, i.definition_key, i.business_key from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id
                where t.id = @taskId and t.status = 'open' and t.due_at = @dueAt
                """, new { taskId, dueAt = job.DueAt }, r => (Element: r.GetString(0), Key: r.GetString(1), BusinessKey: r.GetString(2)), ct);
            if (task.Element is not null)
            {
                await session.ExecAsync(
                    """
                    insert into workflow.instance_history (tenant_id, instance_id, element_id, event, at)
                    values (platform.current_tenant_id(), @instanceId, @element, 'task_due', @now)
                    """, new { instanceId = job.InstanceId, element = task.Element, now }, ct);
                await outbox.EnqueueAsync(new UserTaskDue(taskId, job.InstanceId, task.Key, task.BusinessKey, job.DueAt), ct);
            }
        }
        return true;
    }

    /// <summary>
    /// Recalculates the due times of open tasks with a pending reminder after a calendar change, and moves the reminder.
    /// Every such task in the tenant is recalculated: the change may reach a unit through inheritance, and the result is
    /// the same when nothing changed for it. Returns the number of tasks that moved.
    /// </summary>
    public async Task<int> RecalculateAsync(CancellationToken ct = default)
    {
        var open = await session.QueryAsync(
            """
            select t.id, t.created_at, t.due_at, i.org_unit_id, i.definition_key, i.definition_version, t.step_index, j.id as job_id
            from workflow.user_task t
            join workflow.process_instance i on i.id = t.instance_id
            join workflow.engine_job j on j.task_id = t.id and j.kind = @kind and j.done_at is null
            where t.status = 'open' and t.due_at is not null
            for update of t, j
            """, new { kind = EngineJobKinds.TaskDue }, r => (
                TaskId: r.GetGuid(0), CreatedAt: r.GetFieldValue<DateTimeOffset>(1), DueAt: r.GetFieldValue<DateTimeOffset>(2), OrgUnitId: r.Get<Guid?>("org_unit_id"),
                Key: r.GetString(4), Version: r.GetInt32(5), Step: r.GetInt32(6), JobId: r.GetGuid(7)), ct);
        var routes = new Dictionary<(string, int), CompiledRoute>();
        var moved = 0;
        foreach (var task in open)
        {
            if (!routes.TryGetValue((task.Key, task.Version), out var route))
            {
                var json = await session.ScalarAsync<string>(
                    "select compiled::text from workflow.process_definition where key = @key and version = @version", new { key = task.Key, version = task.Version }, ct);
                route = CompiledRoute.FromJson(json!);
                routes[(task.Key, task.Version)] = route;
            }
            if (route.Steps[task.Step].DueWorkingDays is not { } days)
            {
                continue;
            }
            var dueAt = await DueAtAsync(task.OrgUnitId, task.CreatedAt, days, ct);
            if (dueAt == task.DueAt)
            {
                continue;
            }
            await session.ExecAsync("update workflow.user_task set due_at = @dueAt where id = @id", new { id = task.TaskId, dueAt }, ct);
            await session.ExecAsync("update workflow.engine_job set due_at = @dueAt, locked_until = null where id = @id", new { id = task.JobId, dueAt }, ct);
            moved++;
        }
        return moved;
    }

    /// <summary>Deletes jobs done more than 30 days ago (spec 024 data table).</summary>
    public Task PurgeAsync(DateTimeOffset now, CancellationToken ct = default) =>
        session.ExecAsync("delete from workflow.engine_job where done_at < @cutoff", new { cutoff = now - Retention }, ct);
}

/// <summary>Recalculates open due times when a working calendar changes (spec 022 section 12).</summary>
public sealed class CalendarChangeHandler(EngineJobs jobs) : IMessageHandler<WorkingCalendarChanged>
{
    public Task HandleAsync(WorkingCalendarChanged message, CancellationToken ct) => jobs.RecalculateAsync(ct);
}
