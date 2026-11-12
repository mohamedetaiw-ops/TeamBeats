using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

/// <summary>
/// The engine core of spec 024: immutable route versions, instances on the version they started with, user tasks in
/// sequence with an outcome gateway after each, service tasks that call connector actions (R2 part of FR-010) with
/// incidents an operator resolves (FR-013), and history. Moves happen in the caller's transaction, with the task row
/// locked, so a second completion of the same task is refused and never creates a second next step (FR-012).
/// </summary>
public sealed class ProcessEngine(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuthorizationProvisioning grants, IDelegations delegations,
    IReportingLine reportingLine, IAuditWriter audit, IOutbox outbox, IClock clock, EngineJobs jobs, IConnectorRuntime connectors, IConnectorActions actions)
    : IProcessEngine
{
    public const int CommentMax = 2000;
    public const int ReasonMax = 2000;
    public const int MaxVariablesBytes = 256 * 1024;

    /// <summary>Correlation of a connector call made by a service task: <c>wf:{instance}:{element}</c>.</summary>
    public const string CorrelationPrefix = "wf:";

    private static readonly IReadOnlySet<string> Sources = new HashSet<string>(StringComparer.Ordinal)
    {
        RouteSources.ServiceRoute, RouteSources.ContentRoute, RouteSources.PolicyRoute, RouteSources.DocumentRoute, RouteSources.LeaveRoute, RouteSources.JourneyRoute, RouteSources.AttendanceRoute, RouteSources.LetterRoute, RouteSources.DataChangeRoute, RouteSources.ExpenseRoute,
        RouteSources.BookingRoute,
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
        await CheckActionsAsync(route, errors, ct);
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

    /// <summary>
    /// Checks each connector step against its action (spec 017 FR-008, FR-009): it exists, declares its inputs, every
    /// mapped input is declared, every required input is mapped, and every business error with a path is declared.
    /// </summary>
    private async Task CheckActionsAsync(RouteSpec route, ValidationErrors errors, CancellationToken ct)
    {
        for (var i = 0; i < route.Steps.Count; i++)
        {
            if (route.Steps[i].Action is not { } step)
            {
                continue;
            }
            var field = $"steps[{i}].action";
            var action = await actions.FindAsync(step.ConnectionId, step.Action, ct);
            if (action is null)
            {
                errors.Add($"{field}.action", "validation.workflow.action_unknown");
                continue;
            }
            if (action.Inputs is not { } inputs)
            {
                errors.Add($"{field}.action", "validation.workflow.action_no_inputs");
                continue;
            }
            if (step.Inputs.Keys.Any(k => !inputs.Any(d => d.Name == k)))
            {
                errors.Add($"{field}.inputs", "validation.workflow.action_input_undeclared");
            }
            if (inputs.Any(d => d.Required && !step.Inputs.ContainsKey(d.Name)))
            {
                errors.Add($"{field}.inputs", "validation.workflow.action_input_missing");
            }
            if ((step.OnBusinessError ?? new Dictionary<string, string>()).Keys.Any(code => !action.BusinessErrors.Contains(code)))
            {
                errors.Add($"{field}.onBusinessError", "validation.workflow.action_error_unknown");
            }
        }
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

    public async Task<ProcessInstanceInfo> StartAsync(string key, string businessKey, Guid? orgUnitId, Guid? startedBy, IReadOnlyCollection<Guid>? alsoExcluded = null,
        Guid? requestedFor = null, IReadOnlyDictionary<string, JsonElement>? variables = null, CancellationToken ct = default)
    {
        context.RequireTenant();
        if (string.IsNullOrWhiteSpace(businessKey) || businessKey.Length > 200)
        {
            throw AppException.Validation("businessKey", "validation.workflow.business_key");
        }
        var variablesJson = JsonSerializer.Serialize(variables ?? new Dictionary<string, JsonElement>(), CompiledRoute.Json);
        if (System.Text.Encoding.UTF8.GetByteCount(variablesJson) > MaxVariablesBytes)
        {
            // Large content belongs in attachments (spec 024 edge cases).
            throw AppException.Validation("variables", "validation.workflow.variables_too_large");
        }
        var definition = await GetDefinitionAsync(key, null, ct) ?? throw AppException.NotFound();
        var route = await RouteAsync(definition.Key, definition.Version, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        var created = await session.ScalarAsync<Guid?>(
            """
            insert into workflow.process_instance (tenant_id, id, definition_key, definition_version, business_key, org_unit_id, started_by, excluded_user_ids, requested_for, status, variables, started_at)
            values (platform.current_tenant_id(), @id, @key, @version, @businessKey, @orgUnitId, @startedBy, @alsoExcluded, @requestedFor, 'active', @variables, @now)
            on conflict (tenant_id, definition_key, business_key) do nothing
            returning id
            """, new { id, key = definition.Key, version = definition.Version, businessKey, orgUnitId, startedBy, alsoExcluded = (alsoExcluded ?? []).Distinct().ToArray(), requestedFor,
                variables = new Jsonb(variablesJson), now }, ct);
        if (created is null)
        {
            // Started before for this business object: hand back that instance (at most one per business key).
            return (await FindInstanceAsync(key, businessKey, ct))!;
        }
        await HistoryAsync(id, "start", "started", startedBy, null, ct);
        var instance = new ProcessInstanceInfo(id, definition.Key, definition.Version, businessKey, orgUnitId, startedBy, "active", null, now, null);
        await EnterStepAsync(instance, route, 0, ct);
        return (await GetInstanceAsync(id, ct))!;
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
        // Only the person who started it withdraws as a user; modules acting as the system may also end it. An instance
        // the system started (a journey step, spec 022) is ended by the user the calling module authorized.
        if (context.ActorType == ActorType.User && instance.StartedBy is { } starter && context.ActorId != starter)
        {
            throw AppException.Denied();
        }
        if (instance.Status != "active")
        {
            throw AppException.Conflict("conflict.workflow.instance_closed");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update workflow.user_task set status = 'cancelled', completed_at = @now where instance_id = @instanceId and status = 'open'", new { instanceId, now }, ct);
        // A connector step still running or waiting on an incident stops here; a late answer finds it closed and does nothing.
        await session.ExecAsync("update workflow.service_task set status = 'cancelled', ended_at = @now where instance_id = @instanceId and status in ('running', 'incident')",
            new { instanceId, now }, ct);
        await session.ExecAsync(
            "update workflow.incident set status = 'resolved', resolution = 'cancelled', resolved_by = @by, resolved_at = @now where instance_id = @instanceId and status = 'open'",
            new { instanceId, now, by = Actor() }, ct);
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
        t.on_behalf_of, t.due_at, t.created_at, t.completed_at, t.candidates::text, t.excluded_user_ids, i.org_unit_id, i.definition_version,
        coalesce(i.requested_for, i.started_by) as subject_id
        """;

    private sealed record TaskRow(UserTaskInfo Info, CandidateSet Candidates, Guid[] Excluded, Guid? OrgUnitId, int DefinitionVersion, Guid? Subject);

    private sealed record CandidateSet(string Name, IReadOnlyList<Guid> Users, string? Permission, bool LineManager = false, bool Subject = false);

    private static TaskRow ReadTask(NpgsqlDataReader r)
    {
        var candidates = System.Text.Json.JsonSerializer.Deserialize<CandidateSet>(r.GetString(14), CompiledRoute.Json)!;
        var info = new UserTaskInfo(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4), candidates.Name, r.GetInt32(5), r.GetString(6),
            r.Get<string?>("outcome"), r.Get<string?>("comment"), r.Get<Guid?>("completed_by"), r.Get<Guid?>("on_behalf_of"), r.Get<DateTimeOffset?>("due_at"),
            r.GetFieldValue<DateTimeOffset>(12), r.Get<DateTimeOffset?>("completed_at"));
        return new TaskRow(info, candidates, r.GetFieldValue<Guid[]>(15), r.Get<Guid?>("org_unit_id"), r.GetInt32(17), r.Get<Guid?>("subject_id"));
    }

    private async Task CreateTaskAsync(ProcessInstanceInfo instance, CompiledRoute route, int stepIndex, CancellationToken ct)
    {
        var step = route.Steps[stepIndex];
        var id = Ids.New();
        var excluded = Array.Empty<Guid>();
        if (route.ExcludeStarter)
        {
            var others = await session.ScalarAsync<Guid[]>("select excluded_user_ids from workflow.process_instance where id = @id", new { id = instance.Id }, ct) ?? [];
            excluded = [.. others.Concat(instance.StartedBy is { } starter ? [starter] : []).Distinct()];
        }
        var candidates = new CandidateSet(step.Name, step.Users, step.Permission, step.LineManager, step.Subject);
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
    /// Whether the current user may complete the task: never an excluded person (the starter, or the person a request
    /// is for), directly or as a delegate; otherwise a named user or a holder of the step's permission at the instance's
    /// org unit through central authorization. A delegate may complete the task when the person who handed it over is
    /// a candidate, and then acts on their behalf. Returns the person acted for, or null when acting directly.
    /// </summary>
    private CandidateCheck Check(Guid user) => new(authz, grants, delegations, reportingLine, clock.UtcNow, user);

    private sealed class CandidateCheck(IAuthorizationService authz, IAuthorizationProvisioning grants, IDelegations delegations, IReportingLine reportingLine,
        DateTimeOffset now, Guid user)
    {
        private readonly Dictionary<(Guid, string, Guid?), bool> holds = [];
        private readonly Dictionary<Guid, Guid?> managers = [];
        private IReadOnlyList<Guid>? delegators;

        /// <summary>
        /// The subject's current manager when a line manager step can go to them, or null when it falls back to the
        /// step's permission: no subject, no manager, or a manager who may not approve this instance (an excluded
        /// person, such as a manager who filed the request for their report). Read at check time, so a manager change
        /// during approval moves an open step to the new manager (spec 016 edge cases).
        /// </summary>
        private async Task<Guid?> LineManagerAsync(TaskRow task, CancellationToken ct)
        {
            if (task.Subject is not { } subject)
            {
                return null;
            }
            if (!managers.TryGetValue(subject, out var manager))
            {
                manager = await reportingLine.ManagerOfAsync(subject, ct);
                managers[subject] = manager;
            }
            return manager is { } m && !task.Excluded.Contains(m) ? m : null;
        }

        private async Task<bool> IsCandidateAsync(TaskRow task, Guid person, CancellationToken ct)
        {
            if (task.Excluded.Contains(person))
            {
                return false;
            }
            if (task.Candidates.Users.Contains(person) || (task.Candidates.Subject && task.Subject == person))
            {
                return true;
            }
            if (task.Candidates.LineManager && await LineManagerAsync(task, ct) is { } manager)
            {
                // The reporting line grants the approval (spec 016: a line manager holds workflow.approve for their reports).
                return person == manager;
            }
            if (task.Candidates.Permission is not { } permission)
            {
                return false;
            }
            if (!holds.TryGetValue((person, permission, task.OrgUnitId), out var allowed))
            {
                var target = task.OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
                allowed = person == user
                    ? (await authz.CheckAsync(permission, target, ct)).Allowed
                    : await grants.UserHoldsAsync(person, permission, target, ct);
                holds[(person, permission, task.OrgUnitId)] = allowed;
            }
            return allowed;
        }

        public async Task<(bool Allowed, Guid? OnBehalfOf)> CheckAsync(TaskRow task, CancellationToken ct)
        {
            if (task.Excluded.Contains(user))
            {
                return (false, null);
            }
            if (await IsCandidateAsync(task, user, ct))
            {
                return (true, null);
            }
            delegators ??= await delegations.DelegatorsOfAsync(user, now, ct);
            foreach (var delegator in delegators)
            {
                if (await IsCandidateAsync(task, delegator, ct))
                {
                    return (true, delegator);
                }
            }
            return (false, null);
        }
    }

    public async Task<UserTaskInfo?> MyTaskAsync(Guid taskId, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        var task = await session.QuerySingleOrDefaultAsync(
            $"select {TaskColumns} from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id where t.id = @taskId",
            new { taskId }, ReadTask, ct);
        if (task is null)
        {
            return null;
        }
        var (allowed, onBehalfOf) = await Check(user).CheckAsync(task, ct);
        return !allowed ? null : task.Info.Status == "open" ? task.Info with { OnBehalfOf = onBehalfOf } : task.Info;
    }

    public async Task<IReadOnlyList<UserTaskInfo>> MyOpenTasksAsync(int limit = 100, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        var open = await session.QueryAsync(
            $"""
            select {TaskColumns} from workflow.user_task t join workflow.process_instance i on i.id = t.instance_id
            where t.status = 'open' order by t.created_at, t.id limit 1000
            """, null, ReadTask, ct);
        var check = Check(user);
        var mine = new List<UserTaskInfo>();
        foreach (var task in open)
        {
            if (await check.CheckAsync(task, ct) is (true, var onBehalfOf))
            {
                mine.Add(task.Info with { OnBehalfOf = onBehalfOf });
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
        var (allowed, onBehalfOf) = await Check(user).CheckAsync(task, ct);
        if (!allowed)
        {
            throw AppException.Denied();
        }
        if (task.Info.Status != "open")
        {
            throw AppException.Conflict("conflict.workflow.task_closed");
        }

        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update workflow.user_task set status = 'completed', outcome = @outcome, comment = @comment, completed_by = @user, on_behalf_of = @onBehalfOf, completed_at = @now
            where id = @taskId
            """, new { taskId, outcome, comment, user, onBehalfOf, now }, ct);
        await jobs.CloseTaskJobsAsync(taskId, ct);
        await HistoryAsync(task.Info.InstanceId, task.Info.ElementId, "task_completed:" + outcome, user, onBehalfOf, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.WorkflowTaskCompleted, "user_task", taskId.ToString(), TargetOrgUnitId: task.OrgUnitId,
            Changes: new { task.Info.DefinitionKey, task.Info.BusinessKey, element = task.Info.ElementId, outcome, onBehalfOf }), ct);
        await outbox.EnqueueAsync(new UserTaskCompleted(taskId, task.Info.InstanceId, task.Info.DefinitionKey, task.Info.BusinessKey, task.Info.ElementId, outcome, user), ct);

        var instance = (await GetInstanceAsync(task.Info.InstanceId, ct))!;
        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        if (outcome == TaskOutcomes.Approve)
        {
            await EnterStepAsync(instance, route, task.Info.StepIndex + 1, ct);
        }
        else
        {
            await EndAsync(instance, outcome == TaskOutcomes.Reject ? InstanceOutcomes.Rejected : InstanceOutcomes.Returned, ct);
        }
        return task.Info with { Status = "completed", Outcome = outcome, Comment = comment, CompletedBy = user, OnBehalfOf = onBehalfOf, CompletedAt = now };
    }

    // ---- Steps, service tasks and incidents ----

    /// <summary>Moves the instance to step <paramref name="index"/>: a user task, a service task, or the approved end after the last step.</summary>
    private async Task EnterStepAsync(ProcessInstanceInfo instance, CompiledRoute route, int index, CancellationToken ct)
    {
        if (index >= route.Steps.Count)
        {
            await EndAsync(instance, InstanceOutcomes.Approved, ct);
            return;
        }
        if (route.Steps[index].IsServiceTask)
        {
            await StartServiceTaskAsync(instance, route, index, ct);
            return;
        }
        await CreateTaskAsync(instance, route, index, ct);
    }

    private async Task EndAsync(ProcessInstanceInfo instance, string end, CancellationToken ct)
    {
        await session.ExecAsync("update workflow.process_instance set status = @status, outcome = @end, ended_at = @now where id = @id",
            new { id = instance.Id, end, status = end == InstanceOutcomes.Cancelled ? "cancelled" : "completed", now = clock.UtcNow }, ct);
        await HistoryAsync(instance.Id, "end_" + end, "completed", null, null, ct);
        await outbox.EnqueueAsync(new ProcessInstanceCompleted(instance.Id, instance.DefinitionKey, instance.BusinessKey, end), ct);
    }

    private sealed record ServiceTaskRow(Guid Id, Guid InstanceId, string ElementId, int StepIndex, string Key, string Status);

    /// <summary>
    /// Starts a connector step as a durable connector job (spec 017 FR-005). Only the mapped variables are sent, under a key
    /// fixed for this instance and step, so a retry, a duplicate delivery or a replay never acts twice where the system
    /// supports it. The answer comes back as <see cref="ConnectorActionCompleted"/>.
    /// </summary>
    private async Task StartServiceTaskAsync(ProcessInstanceInfo instance, CompiledRoute route, int index, CancellationToken ct)
    {
        var step = route.Steps[index];
        var key = $"{CorrelationPrefix}{instance.Id:N}:{step.Id}";
        var id = Ids.New();
        var inserted = await session.ExecAsync(
            """
            insert into workflow.service_task (tenant_id, id, instance_id, element_id, step_index, idempotency_key, status, started_at)
            values (platform.current_tenant_id(), @id, @instanceId, @element, @index, @key, 'running', @now)
            on conflict do nothing
            """, new { id, instanceId = instance.Id, element = step.Id, index, key, now = clock.UtcNow }, ct);
        if (inserted == 0)
        {
            return; // Entered before: the step runs once.
        }
        await HistoryAsync(instance.Id, step.Id, "service_task_started", null, null, ct);
        await CallAsync(new ServiceTaskRow(id, instance.Id, step.Id, index, key, "running"), step, ct);
    }

    private async Task CallAsync(ServiceTaskRow task, CompiledStep step, CancellationToken ct)
    {
        var action = step.Action!;
        var variables = await session.ScalarAsync<string>("select variables::text from workflow.process_instance where id = @id", new { id = task.InstanceId }, ct) ?? "{}";
        using var doc = JsonDocument.Parse(variables);
        var input = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, source) in action.Inputs)
        {
            if (doc.RootElement.TryGetProperty(source, out var value) && value.ValueKind != JsonValueKind.Null)
            {
                input[name] = value.Clone();
            }
        }
        var result = await connectors.EnqueueAsync(new ConnectorCall(action.ConnectionId, action.Action, JsonSerializer.SerializeToElement(input, CompiledRoute.Json),
            task.Key, $"{CorrelationPrefix}{task.InstanceId:D}:{task.ElementId}"), ct);
        if (result.RunId is { } run)
        {
            await session.ExecAsync("update workflow.service_task set run_id = @run where id = @id", new { id = task.Id, run }, ct);
        }
        if (result.Outcome is ConnectorOutcomes.Queued or ConnectorOutcomes.InProgress)
        {
            return;
        }
        // The key had already finished (a repeated step), or the call was refused before it was queued.
        await OnOutcomeAsync(task, step, result.Outcome, result.ExternalReference, result.ErrorClass, result.ErrorCode, ct);
    }

    /// <summary>
    /// The end of a connector job a service task started (spec 024 FR-010). Duplicate deliveries do nothing: only a
    /// running service task moves.
    /// </summary>
    public async Task OnConnectorCompletedAsync(ConnectorActionCompleted done, CancellationToken ct = default)
    {
        if (done.CorrelationId is not { } correlation || !correlation.StartsWith(CorrelationPrefix, StringComparison.Ordinal))
        {
            return;
        }
        var parts = correlation[CorrelationPrefix.Length..].Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var instanceId))
        {
            return;
        }
        var task = await session.QuerySingleOrDefaultAsync(
            """
            select s.id, s.instance_id, s.element_id, s.step_index, s.idempotency_key, s.status
            from workflow.service_task s join workflow.process_instance i on i.id = s.instance_id
            where s.instance_id = @instanceId and s.element_id = @element for update of s, i
            """, new { instanceId, element = parts[1] },
            r => new ServiceTaskRow(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5)), ct);
        if (task is null || task.Status != "running")
        {
            return;
        }
        var instance = (await GetInstanceAsync(task.InstanceId, ct))!;
        if (instance.Status != "active")
        {
            return;
        }
        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        await OnOutcomeAsync(task, route.Steps[task.StepIndex], done.Outcome, done.ExternalReference, done.ErrorClass, done.ErrorCode, ct);
    }

    private async Task OnOutcomeAsync(ServiceTaskRow task, CompiledStep step, string outcome, string? reference, string? errorClass, string? errorCode,
        CancellationToken ct)
    {
        var instance = (await GetInstanceAsync(task.InstanceId, ct))!;
        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        var now = clock.UtcNow;
        if (outcome == ConnectorOutcomes.Succeeded)
        {
            await session.ExecAsync(
                "update workflow.service_task set status = 'succeeded', external_reference = @reference, error_class = null, error_code = null, ended_at = @now where id = @id",
                new { id = task.Id, reference = reference is { Length: > 500 } ? reference[..500] : reference, now }, ct);
            await HistoryAsync(task.InstanceId, task.ElementId, "service_task_completed", null, null, ct);
            await outbox.EnqueueAsync(new ServiceTaskCompleted(task.InstanceId, instance.DefinitionKey, instance.BusinessKey, task.ElementId, reference), ct);
            await EnterStepAsync(instance, route, task.StepIndex + 1, ct);
            return;
        }
        if (outcome == ConnectorOutcomes.BusinessError && errorCode is not null && step.Action!.OnBusinessError?.GetValueOrDefault(errorCode) is { } path)
        {
            // A declared business error with a path: not retried and not an incident (spec 024 US4 scenario 1).
            await session.ExecAsync(
                "update workflow.service_task set status = 'business_error', error_class = @errorClass, error_code = @errorCode, ended_at = @now where id = @id",
                new { id = task.Id, errorClass = ConnectorErrorClassBusiness, errorCode, now }, ct);
            await HistoryAsync(task.InstanceId, task.ElementId, "service_task_business_error:" + errorCode, null, null, ct);
            if (path == BusinessErrorPaths.Continue)
            {
                await EnterStepAsync(instance, route, task.StepIndex + 1, ct);
            }
            else
            {
                await EndAsync(instance, InstanceOutcomes.Rejected, ct);
            }
            return;
        }
        var cls = outcome switch
        {
            ConnectorOutcomes.Unknown => OutcomeUnknown,
            ConnectorOutcomes.BusinessError => ConnectorErrorClassBusiness,
            _ => errorClass ?? "unexpected",
        };
        await RaiseIncidentAsync(instance, task, cls, outcome == ConnectorOutcomes.BusinessError ? errorCode : null, ct);
    }

    private const string OutcomeUnknown = "outcome_unknown";
    private const string ConnectorErrorClassBusiness = "business_error";

    private async Task RaiseIncidentAsync(ProcessInstanceInfo instance, ServiceTaskRow task, string errorClass, string? errorCode, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("update workflow.service_task set status = 'incident', error_class = @errorClass, error_code = @errorCode where id = @id",
            new { id = task.Id, errorClass, errorCode }, ct);
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into workflow.incident (tenant_id, id, instance_id, service_task_id, element_id, error_class, error_code, status, raised_at)
            values (platform.current_tenant_id(), @id, @instanceId, @taskId, @element, @errorClass, @errorCode, 'open', @now)
            """, new { id, instanceId = task.InstanceId, taskId = task.Id, element = task.ElementId, errorClass, errorCode, now }, ct);
        await HistoryAsync(task.InstanceId, task.ElementId, "incident_raised", null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessIncidentRaised, "process_instance", task.InstanceId.ToString(), TargetOrgUnitId: instance.OrgUnitId,
            Changes: new { incident = id, element = task.ElementId, errorClass, errorCode }), ct);
        await outbox.EnqueueAsync(new ProcessIncidentRaised(id, task.InstanceId, instance.DefinitionKey, instance.BusinessKey, task.ElementId, errorClass), ct);
    }

    public async Task<IncidentInfo> ResolveIncidentAsync(Guid incidentId, string resolution, string? reason, CancellationToken ct = default)
    {
        context.RequireTenant();
        var errors = new ValidationErrors();
        if (resolution is not (IncidentResolutions.Retried or IncidentResolutions.Skipped or IncidentResolutions.Cancelled))
        {
            errors.Add("resolution", "validation.workflow.resolution");
        }
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonMax)
        {
            errors.Add("reason", "validation.workflow.reason");
        }
        errors.ThrowIfAny();
        var row = await session.QuerySingleOrDefaultAsync(
            """
            select n.id, n.status, s.id, s.instance_id, s.element_id, s.step_index, s.idempotency_key, s.status
            from workflow.incident n join workflow.service_task s on s.id = n.service_task_id join workflow.process_instance i on i.id = n.instance_id
            where n.id = @incidentId for update of n, s, i
            """, new { incidentId },
            r => (Status: r.GetString(1), Task: new ServiceTaskRow(r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.GetString(7))), ct);
        if (row.Task is null)
        {
            throw AppException.NotFound();
        }
        if (row.Status != "open")
        {
            // Two operators on one incident: the second is refused (spec 024 US4 scenario 5).
            throw AppException.Conflict("conflict.workflow.incident_resolved");
        }
        var instance = (await GetInstanceAsync(row.Task.InstanceId, ct))!;
        var now = clock.UtcNow;
        var by = Actor();
        await session.ExecAsync(
            "update workflow.incident set status = 'resolved', resolution = @resolution, reason = @reason, resolved_by = @by, resolved_at = @now where id = @incidentId",
            new { incidentId, resolution, reason, by, now }, ct);
        await HistoryAsync(instance.Id, row.Task.ElementId, "incident_" + resolution, by, null, ct);
        var action = resolution switch
        {
            IncidentResolutions.Retried => AuditActions.ProcessIncidentRetried,
            IncidentResolutions.Skipped => AuditActions.ProcessIncidentSkipped,
            _ => AuditActions.ProcessIncidentCancelled,
        };
        await audit.WriteAsync(new AuditEntry(action, "process_instance", instance.Id.ToString(), TargetOrgUnitId: instance.OrgUnitId,
            Changes: new { incident = incidentId, element = row.Task.ElementId, reason }), ct);
        await outbox.EnqueueAsync(new ProcessIncidentResolved(incidentId, instance.Id, instance.DefinitionKey, instance.BusinessKey, row.Task.ElementId, resolution), ct);

        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        switch (resolution)
        {
            case IncidentResolutions.Retried:
                await session.ExecAsync("update workflow.service_task set status = 'running', error_class = null, error_code = null where id = @id", new { id = row.Task.Id }, ct);
                await CallAsync(row.Task with { Status = "running" }, route.Steps[row.Task.StepIndex], ct);
                break;
            case IncidentResolutions.Skipped:
                await session.ExecAsync("update workflow.service_task set status = 'skipped', ended_at = @now where id = @id", new { id = row.Task.Id, now }, ct);
                await EnterStepAsync(instance, route, row.Task.StepIndex + 1, ct);
                break;
            default:
                await session.ExecAsync("update workflow.service_task set status = 'cancelled', ended_at = @now where id = @id", new { id = row.Task.Id, now }, ct);
                await session.ExecAsync("update workflow.user_task set status = 'cancelled', completed_at = @now where instance_id = @id and status = 'open'",
                    new { id = instance.Id, now }, ct);
                await jobs.CloseInstanceJobsAsync(instance.Id, ct);
                await EndAsync(instance, InstanceOutcomes.Cancelled, ct);
                break;
        }
        return (await IncidentsAsync(instance.Id, ct)).First(i => i.Id == incidentId);
    }

    public async Task<IReadOnlyList<ServiceTaskInfo>> ServiceTasksAsync(Guid instanceId, CancellationToken ct = default)
    {
        var instance = await GetInstanceAsync(instanceId, ct);
        if (instance is null)
        {
            return [];
        }
        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        return await session.QueryAsync(
            """
            select instance_id, element_id, step_index, status, external_reference, error_class, error_code, started_at, ended_at
            from workflow.service_task where instance_id = @instanceId order by started_at, id
            """, new { instanceId },
            r => new ServiceTaskInfo(r.GetGuid(0), r.GetString(1), route.Steps[r.GetInt32(2)].Name, r.GetString(3), r.Get<string?>("external_reference"),
                r.Get<string?>("error_class"), r.Get<string?>("error_code"), r.GetFieldValue<DateTimeOffset>(7), r.Get<DateTimeOffset?>("ended_at")), ct);
    }

    public async Task<IReadOnlyList<IncidentInfo>> IncidentsAsync(Guid instanceId, CancellationToken ct = default)
    {
        var instance = await GetInstanceAsync(instanceId, ct);
        if (instance is null)
        {
            return [];
        }
        var route = await RouteAsync(instance.DefinitionKey, instance.DefinitionVersion, ct);
        return await session.QueryAsync(
            """
            select id, element_id, error_class, error_code, status, resolution, reason, resolved_by, raised_at, resolved_at
            from workflow.incident where instance_id = @instanceId order by raised_at desc, id
            """, new { instanceId },
            r => new IncidentInfo(r.GetGuid(0), instanceId, instance.DefinitionKey, instance.BusinessKey, r.GetString(1),
                route.Steps.FirstOrDefault(x => x.Id == r.GetString(1))?.Name ?? r.GetString(1), r.GetString(2), r.Get<string?>("error_code"), r.GetString(4),
                r.Get<string?>("resolution"), r.Get<string?>("reason"), r.Get<Guid?>("resolved_by"), r.GetFieldValue<DateTimeOffset>(8), r.Get<DateTimeOffset?>("resolved_at")), ct);
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
