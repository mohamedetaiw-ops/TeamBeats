using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

/// <summary>
/// The R1 engine core of spec 024: immutable route versions, instances on the version they started with, user tasks in
/// sequence with an outcome gateway after each, and history. Moves happen in the caller's transaction, with the task row
/// locked, so a second completion of the same task is refused and never creates a second next step (FR-012).
/// </summary>
public sealed class ProcessEngine(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuditWriter audit, IOutbox outbox, IClock clock, EngineJobs jobs)
    : IProcessEngine
{
    public const int CommentMax = 2000;

    private static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal)
    {
        RouteSources.ServiceRoute, RouteSources.ContentRoute, RouteSources.PolicyRoute, RouteSources.DocumentRoute,
    };

    private Guid? Actor() => context.ActorType is ActorType.User or ActorType.System ? context.ActorId : null;

    // ---- Definitions ----

    public async Task<ProcessDefinitionInfo> PublishRouteAsync(string key, string source, RouteSpec route, CancellationToken ct = default)
    {
        context.RequireTenant();
        var errors = RouteCompiler.Validate(route);
        if (!RouteCompiler.IsValidKey(key))
        {
            errors.Add("key", "validation.workflow.key");
        }
        if (!Sources.Contains(source))
        {
            errors.Add("source", "validation.workflow.source");
        }
        errors.ThrowIfAny();
        var compiled = RouteCompiler.Compile(route);

        // One publisher per key at a time, so versions never collide.
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':workflow:' || @key, 0))", new { key }, ct);
        var latest = await LatestAsync(key, ct);
        if (latest is not null && CompiledRoute.FromJson(latest.Value.Compiled).ToJson() == compiled.ToJson() && latest.Value.Info.Source == source)
        {
            // Saving the same route again does not create a new version.
            return latest.Value.Info;
        }
        var version = (latest?.Info.Version ?? 0) + 1;
        var bpmn = RouteCompiler.ToBpmn(key, compiled);
        var publishedAt = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into workflow.process_definition (tenant_id, key, version, name, source, bpmn_xml, compiled, published_at, published_by)
            values (platform.current_tenant_id(), @key, @version, @name, @source, @bpmn, @compiled, @publishedAt, @by)
            """, new { key, version, name = compiled.Name, source, bpmn, compiled = new Jsonb(compiled.ToJson()), publishedAt, by = Actor() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.WorkflowDefinitionPublished, "process_definition", $"{key}:{version}",
            Changes: new { key, version, source, steps = compiled.Steps.Count }), ct);
        return new ProcessDefinitionInfo(key, version, compiled.Name, source, publishedAt);
    }

    private async Task<(ProcessDefinitionInfo Info, string Compiled)?> LatestAsync(string key, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select key, version, name, source, published_at, compiled::text from workflow.process_definition where key = @key order by version desc limit 1",
            new { key }, r => (Info: ReadDefinition(r), Compiled: r.GetString(5)), ct);
        return row.Info is null ? null : row;
    }

    private static ProcessDefinitionInfo ReadDefinition(NpgsqlDataReader r) =>
        new(r.GetString(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));

    public async Task<ProcessDefinitionInfo?> GetDefinitionAsync(string key, int? version = null, CancellationToken ct = default) =>
        await session.QuerySingleOrDefaultAsync(
            """
            select key, version, name, source, published_at from workflow.process_definition
            where key = @key and (@version::int is null or version = @version) order by version desc limit 1
            """, new { key, version }, ReadDefinition, ct);

    public Task<string?> GetBpmnAsync(string key, int version, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync("select bpmn_xml from workflow.process_definition where key = @key and version = @version",
            new { key, version }, r => r.GetString(0), ct);

    private async Task<CompiledRoute> RouteAsync(string key, int version, CancellationToken ct)
    {
        var json = await session.QuerySingleOrDefaultAsync("select compiled::text from workflow.process_definition where key = @key and version = @version",
            new { key, version }, r => r.GetString(0), ct) ?? throw new InvalidOperationException($"Definition {key}:{version} is missing.");
        return CompiledRoute.FromJson(json);
    }

    // ---- Instances ----

    private const string InstanceColumns = "id, definition_key, definition_version, business_key, org_unit_id, started_by, status, outcome, started_at, ended_at";

    private static ProcessInstanceInfo ReadInstance(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.Get<Guid?>("org_unit_id"), r.Get<Guid?>("started_by"), r.GetString(6),
        r.Get<string?>("outcome"), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("ended_at"));

    public async Task<ProcessInstanceInfo> StartAsync(string key, string businessKey, Guid? orgUnitId, Guid? startedBy, CancellationToken ct = default)
    {
        context.RequireTenant();
        if (string.IsNullOrWhiteSpace(businessKey) || businessKey.Length > 200)
        {
            throw AppException.Validation("businessKey", "validation.workflow.business_key");
        }
        var definition = await GetDefinitionAsync(key, null, ct) ?? throw AppException.NotFound();
        var route = await RouteAsync(definition.Key, definition.Version, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        var created = await session.ScalarAsync<Guid?>(
            """
            insert into workflow.process_instance (tenant_id, id, definition_key, definition_version, business_key, org_unit_id, started_by, status, started_at)
            values (platform.current_tenant_id(), @id, @key, @version, @businessKey, @orgUnitId, @startedBy, 'active', @now)
            on conflict (tenant_id, definition_key, business_key) do nothing
            returning id
            """, new { id, key = definition.Key, version = definition.Version, businessKey, orgUnitId, startedBy, now }, ct);
        if (created is null)
        {
            // Started before for this business object: hand back that instance (at most one per business key).
            return (await FindInstanceAsync(key, businessKey, ct))!;
        }
        await HistoryAsync(id, "start", "started", startedBy, null, ct);
        var instance = new ProcessInstanceInfo(id, definition.Key, definition.Version, businessKey, orgUnitId, startedBy, "active", null, now, null);
        await CreateTaskAsync(instance, route, 0, ct);
        return instance;
    }

    public Task<ProcessInstanceInfo?> GetInstanceAsync(Guid instanceId, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync($"select {InstanceColumns} from workflow.process_instance where id = @instanceId", new { instanceId }, ReadInstance, ct);

    public Task<ProcessInstanceInfo?> FindInstanceAsync(string key, string businessKey, CancellationToken ct = default) =>
        session.QuerySingleOrDefaultAsync($"select {InstanceColumns} from workflow.process_instance where definition_key = @key and business_key = @businessKey",
            new { key, businessKey }, ReadInstance, ct);

    public async Task<ProcessInstanceInfo> WithdrawAsync(Guid instanceId, CancellationToken ct = default)
    {
        var instance = await session.QuerySingleOrDefaultAsync(
            $"select {InstanceColumns} from workflow.process_instance where id = @instanceId for update", new { instanceId }, ReadInstance, ct)
            ?? throw AppException.NotFound();
        // Only the person who started it withdraws as a user; modules acting as the system may also end it.
        if (context.ActorType == ActorType.User && context.ActorId != instance.StartedBy)
        {
            throw AppException.Denied();
        }
        if (instance.Status != "active")
        {
            throw AppException.Conflict("conflict.workflow.instance_closed");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update workflow.user_task set status = 'cancelled', completed_at = @now where instance_id = @instanceId and status = 'open'", new { instanceId, now }, ct);
        await jobs.CloseInstanceJobsAsync(instanceId, ct);
        await session.ExecAsync("update workflow.process_instance set status = 'withdrawn', outcome = @outcome, ended_at = @now where id = @instanceId",
            new { instanceId, outcome = InstanceOutcomes.Withdrawn, now }, ct);
        await HistoryAsync(instanceId, "end_withdrawn", "withdrawn", Actor(), null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.WorkflowInstanceWithdrawn, "process_instance", instanceId.ToString(), TargetOrgUnitId: instance.OrgUnitId,
            Changes: new { instance.DefinitionKey, instance.BusinessKey }), ct);
        await outbox.EnqueueAsync(new ProcessInstanceWithdrawn(instanceId, instance.DefinitionKey, instance.BusinessKey), ct);
        return instance with { Status = "withdrawn", Outcome = InstanceOutcomes.Withdrawn, EndedAt = now };
    }

    // ---- Tasks ----

    private const string TaskColumns =
        """
        t.id, t.instance_id, i.definition_key, i.business_key, t.element_id, t.step_index, t.status, t.outcome, t.comment, t.completed_by,
        t.on_behalf_of, t.due_at, t.created_at, t.completed_at, t.candidates::text, t.excluded_user_ids, i.org_unit_id, i.definition_version
        """;

    private sealed record TaskRow(UserTaskInfo Info, CandidateSet Candidates, Guid[] Excluded, Guid? OrgUnitId, int DefinitionVersion);

    private sealed record CandidateSet(string Name, IReadOnlyList<Guid> Users, string? Permission);

    private static TaskRow ReadTask(NpgsqlDataReader r)
    {
        var candidates = System.Text.Json.JsonSerializer.Deserialize<CandidateSet>(r.GetString(14), CompiledRoute.Json)!;
        var info = new UserTaskInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), candidates.Name, r.GetInt32(5), r.GetString(6),
            r.Get<string?>("outcome"), r.Get<string?>("comment"), r.Get<Guid?>("completed_by"), r.Get<Guid?>("on_behalf_of"), r.Get<DateTimeOffset?>("due_at"),
            r.GetFieldValue<DateTimeOffset>(12), r.Get<DateTimeOffset?>("completed_at"));
        return new TaskRow(info, candidates, r.GetFieldValue<Guid[]>(15), r.Get<Guid?>("org_unit_id"), r.GetInt32(17));
    }

    private async Task CreateTaskAsync(ProcessInstanceInfo instance, CompiledRoute route, int stepIndex, CancellationToken ct)
    {
        var step = route.Steps[stepIndex];
        var id = Ids.New();
        Guid[] excluded = route.ExcludeStarter && instance.StartedBy is { } starter ? [starter] : [];
        var candidates = new CandidateSet(step.Name, step.Users, step.Permission);
        var now = clock.UtcNow;
        DateTimeOffset? dueAt = step.DueWorkingDays is { } days ? await jobs.DueAtAsync(instance.OrgUnitId, now, days, ct) : null;
        await session.ExecAsync(
            """
            insert into workflow.user_task (tenant_id, id, instance_id, element_id, step_index, candidates, excluded_user_ids, status, due_at, created_at)
            values (platform.current_tenant_id(), @id, @instanceId, @elementId, @stepIndex, @candidates, @excluded, 'open', @dueAt, @now)
            """, new { id, instanceId = instance.Id, elementId = step.Id, stepIndex, candidates = Jsonb.From(candidates), excluded, dueAt, now }, ct);
        if (dueAt is { } due)
        {
            await jobs.ScheduleTaskDueAsync(instance.Id, id, due, ct);
        }
        await HistoryAsync(instance.Id, step.Id, "task_created", null, null, ct);
        await outbox.EnqueueAsync(new UserTaskCreated(id, instance.Id, instance.DefinitionKey, instance.BusinessKey, step.Id, dueAt), ct);
    }

    public async Task<UserTaskInfo?> GetTaskAsync(Guid taskId, CancellationToken ct = default) =>
        (await session.QuerySingleOrDefaultAsync(
            $"select {TaskColumns} from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id where t.id = @taskId",
            new { taskId }, ReadTask, ct))?.Info;

    public async Task<IReadOnlyList<UserTaskInfo>> InstanceTasksAsync(Guid instanceId, CancellationToken ct = default) =>
        (await session.QueryAsync(
            $"select {TaskColumns} from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id where t.instance_id = @instanceId order by t.created_at, t.id",
            new { instanceId }, ReadTask, ct)).Select(t => t.Info).ToList();

    /// <summary>
    /// Whether the user may complete the task: never the excluded starter; otherwise a named user, or a holder of the
    /// step's permission at the instance's org unit through central authorization. Checks are for the current user only.
    /// </summary>
    private async Task<bool> IsCandidateAsync(TaskRow task, Guid userId, Dictionary<(string, Guid?), bool> cache, CancellationToken ct)
    {
        if (task.Excluded.Contains(userId))
        {
            return false;
        }
        if (task.Candidates.Users.Contains(userId))
        {
            return true;
        }
        if (task.Candidates.Permission is not { } permission)
        {
            return false;
        }
        if (!cache.TryGetValue((permission, task.OrgUnitId), out var allowed))
        {
            var target = task.OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
            allowed = (await authz.CheckAsync(permission, target, ct)).Allowed;
            cache[(permission, task.OrgUnitId)] = allowed;
        }
        return allowed;
    }

    public async Task<IReadOnlyList<UserTaskInfo>> MyOpenTasksAsync(int limit = 100, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        var open = await session.QueryAsync(
            $"""
            select {TaskColumns} from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id
            where t.status = 'open' order by t.created_at, t.id limit 1000
            """, null, ReadTask, ct);
        var cache = new Dictionary<(string, Guid?), bool>();
        var mine = new List<UserTaskInfo>();
        foreach (var task in open)
        {
            if (await IsCandidateAsync(task, user, cache, ct))
            {
                mine.Add(task.Info);
                if (mine.Count >= Math.Clamp(limit, 1, 500))
                {
                    break;
                }
            }
        }
        return mine;
    }

    public async Task<UserTaskInfo> CompleteTaskAsync(Guid taskId, string outcome, string? comment, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        var errors = new ValidationErrors();
        if (!TaskOutcomes.All.Contains(outcome ?? string.Empty))
        {
            errors.Add("outcome", "validation.workflow.outcome");
        }
        comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (comment is { Length: > CommentMax })
        {
            errors.Add("comment", "validation.workflow.comment");
        }
        errors.ThrowIfAny();
        ArgumentNullException.ThrowIfNull(outcome);

        // Lock the task and its instance, so two completions of one task run one after the other.
        var task = await session.QuerySingleOrDefaultAsync(
            $"""
            select {TaskColumns} from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id
            where t.id = @taskId for update of t, i
            """, new { taskId }, ReadTask, ct) ?? throw AppException.NotFound();
        if (!await IsCandidateAsync(task, user, [], ct))
        {
            throw AppException.Denied();
        }
        if (task.Info.Status != "open")
        {
            throw AppException.Conflict("conflict.workflow.task_closed");
        }

        var now = clock.UtcNow;
        await session.ExecAsync(
            "update workflow.user_task set status = 'completed', outcome = @outcome, comment = @comment, completed_by = @user, completed_at = @now where id = @taskId",
            new { taskId, outcome, comment, user, now }, ct);
        await jobs.CloseTaskJobsAsync(taskId, ct);
        await HistoryAsync(task.Info.InstanceId, task.Info.ElementId, "task_completed:" + outcome, user, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.WorkflowTaskCompleted, "user_task", taskId.ToString(), TargetOrgUnitId: task.OrgUnitId,
            Changes: new { task.Info.DefinitionKey, task.Info.BusinessKey, element = task.Info.ElementId, outcome }), ct);
        await outbox.EnqueueAsync(new UserTaskCompleted(taskId, task.Info.InstanceId, task.Info.DefinitionKey, task.Info.BusinessKey, task.Info.ElementId, outcome, user), ct);

        var instance = (await GetInstanceAsync(task.Info.InstanceId, ct))!;
        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        var next = task.Info.StepIndex + 1;
        if (outcome == TaskOutcomes.Approve && next < route.Steps.Count)
        {
            await CreateTaskAsync(instance, route, next, ct);
        }
        else
        {
            var end = outcome switch
            {
                TaskOutcomes.Approve => InstanceOutcomes.Approved,
                TaskOutcomes.Reject => InstanceOutcomes.Rejected,
                _ => InstanceOutcomes.Returned,
            };
            await session.ExecAsync("update workflow.process_instance set status = 'completed', outcome = @end, ended_at = @now where id = @id",
                new { id = instance.Id, end, now }, ct);
            await HistoryAsync(instance.Id, "end_" + end, "completed", null, null, ct);
            await outbox.EnqueueAsync(new ProcessInstanceCompleted(instance.Id, instance.DefinitionKey, instance.BusinessKey, end), ct);
        }
        return task.Info with { Status = "completed", Outcome = outcome, Comment = comment, CompletedBy = user, CompletedAt = now };
    }

    // ---- History ----

    private Task HistoryAsync(Guid instanceId, string elementId, string @event, Guid? actor, Guid? onBehalfOf, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into workflow.instance_history (tenant_id, instance_id, element_id, event, actor_id, on_behalf_of, at)
            values (platform.current_tenant_id(), @instanceId, @elementId, @event, @actor, @onBehalfOf, @at)
            """, new { instanceId, elementId, @event, actor, onBehalfOf, at = clock.UtcNow }, ct);

    public async Task<IReadOnlyList<HistoryEntry>> HistoryAsync(Guid instanceId, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select element_id, event, actor_id, on_behalf_of, at from workflow.instance_history where instance_id = @instanceId order by id",
            new { instanceId }, r => new HistoryEntry(r.GetString(0), r.GetString(1), r.Get<Guid?>("actor_id"), r.Get<Guid?>("on_behalf_of"),
                r.GetFieldValue<DateTimeOffset>(4)), ct);
}
