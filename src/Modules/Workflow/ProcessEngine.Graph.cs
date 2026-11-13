using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Modules.Workflow.Feel;
using TeamBeats.Modules.Workflow.Graph;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Workflow;

/// <summary>Kinds of published definitions: an R1 route (a fixed sequence of steps) or an R2 graph from a process model.</summary>
public static class DefinitionKinds
{
    public const string Route = "route";
    public const string Graph = "graph";
}

/// <summary>
/// Graph instances of spec 024 R2 (FR-004 to FR-011). The <see cref="Executor"/> moves tokens in memory and returns
/// effects; this part applies them to the tables in the caller's transaction, with the instance row locked, and saves
/// the tokens with the instance. Signals to other instances (a child's end for its parent, a message, a child start) run
/// after the state is saved, so an instance never acts on stale tokens.
/// </summary>
public sealed partial class ProcessEngine
{
    /// <summary>Definitions published from process models are named <c>process:&lt;model key&gt;</c>.</summary>
    public const string ModelPrefix = "process:";

    /// <summary>Spec 024 section 12: call activities nest at most 10 deep.</summary>
    public const int MaxCallDepth = 10;

    /// <summary>A message that matched nothing waits this long by default (spec 024 FR-009).</summary>
    public static readonly TimeSpan MessageTimeToLive = TimeSpan.FromHours(1);

    private const string GraphCorrelationPrefix = "wfg:";

    private readonly Dictionary<(string, int), ProcessGraph?> graphs = [];
    private readonly Queue<Func<Task>> deferred = new();
    private bool draining;

    private sealed record GraphInstance(ProcessInstanceInfo Info, ProcessGraph Graph, InstanceState State, Guid? ParentInstanceId, Guid? ParentTokenId, int Depth,
        Guid? Subject, Guid[] Excluded);

    /// <summary>The executor's view of the world: the time, and the current version of each decision the graph names, read before it runs.</summary>
    private sealed class Context(DateTimeOffset now, IReadOnlyDictionary<string, Decisions.DecisionTable> tables) : IExecutionContext
    {
        public DateTimeOffset Now { get; } = now;

        public (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string decisionKey, IReadOnlyDictionary<string, object?> variables) =>
            tables.TryGetValue(decisionKey, out var table) ? table.Evaluate(variables, Now) : throw new DecisionException("decision_missing");
    }

    private async Task<Executor> ExecutorAsync(GraphInstance instance, CancellationToken ct)
    {
        var tables = new Dictionary<string, Decisions.DecisionTable>(StringComparer.Ordinal);
        foreach (var key in instance.Graph.Elements.Where(e => e.DecisionKey is not null).Select(e => e.DecisionKey!).Distinct())
        {
            if (await CurrentDecisionAsync(key, ct) is { } table)
            {
                tables[key] = table;
            }
        }
        return new Executor(instance.Graph, instance.State, new Context(clock.UtcNow, tables));
    }

    /// <summary>The current version of a decision table, or null when it is not published.</summary>
    internal async Task<Decisions.DecisionTable?> CurrentDecisionAsync(string key, CancellationToken ct)
    {
        var json = await session.QuerySingleOrDefaultAsync(
            "select compiled::text from workflow.decision_definition where key = @key order by version desc limit 1", new { key }, r => r.GetString(0), ct);
        return json is null ? null : Decisions.DecisionTable.FromJson(json);
    }

    /// <summary>The graph of a published version, or null for a route.</summary>
    internal async Task<ProcessGraph?> GraphAsync(string key, int version, CancellationToken ct)
    {
        if (graphs.TryGetValue((key, version), out var cached))
        {
            return cached;
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select kind, compiled::text from workflow.process_definition where key = @key and version = @version",
            new { key, version }, r => (Kind: r.GetString(0), Compiled: r.GetString(1)), ct);
        var graph = row.Kind == DefinitionKinds.Graph ? ProcessGraph.FromJson(row.Compiled) : null;
        graphs[(key, version)] = graph;
        return graph;
    }

    private async Task<GraphInstance?> LoadAsync(Guid instanceId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            $"""
            select {InstanceColumns}, state::text, variables::text, parent_instance_id, parent_token_id, depth, coalesce(requested_for, started_by) as subject, excluded_user_ids
            from workflow.process_instance where id = @instanceId for update
            """, new { instanceId }, r => (Info: ReadInstance(r), State: r.Get<string?>("state"), Variables: r.GetString(11), Parent: r.Get<Guid?>("parent_instance_id"),
                Token: r.Get<Guid?>("parent_token_id"), Depth: r.GetInt32(14), Subject: r.Get<Guid?>("subject"), Excluded: r.GetFieldValue<Guid[]>(16)), ct);
        if (row.Info is null || row.Info.Status != "active" || row.State is null)
        {
            return null;
        }
        var graph = await GraphAsync(row.Info.DefinitionKey, row.Info.DefinitionVersion, ct);
        if (graph is null)
        {
            return null;
        }
        var state = JsonSerializer.Deserialize<InstanceState>(row.State, ProcessGraph.Json)!;
        state.Variables = JsonNode.Parse(row.Variables) as JsonObject ?? [];
        return new GraphInstance(row.Info, graph, state, row.Parent, row.Token, row.Depth, row.Subject, row.Excluded);
    }

    // ---- Publishing ----

    /// <summary>Stores the graph as the next immutable version of <paramref name="key"/>, unless it equals the latest one.</summary>
    internal async Task<ProcessDefinitionInfo> PublishGraphAsync(string key, ProcessGraph graph, string bpmn, Guid? owner, CancellationToken ct)
    {
        context.RequireTenant();
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':workflow:' || @key, 0))", new { key }, ct);
        var compiled = graph.ToJson();
        var latest = await LatestAsync(key, ct);
        if (latest is not null && latest.Value.Compiled is { } previous && ProcessGraph.FromJson(previous).ToJson() == compiled)
        {
            return latest.Value.Info;
        }
        var version = (latest?.Info.Version ?? 0) + 1;
        var name = graph.Names.TryGetValue("en", out var en) ? en : graph.Names.Values.FirstOrDefault() ?? key;
        var publishedAt = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into workflow.process_definition (tenant_id, key, version, name, source, kind, bpmn_xml, compiled, names, owner_org_unit_id, published_at, published_by)
            values (platform.current_tenant_id(), @key, @version, @name, 'process_model', 'graph', @bpmn, @compiled, @names, @owner, @publishedAt, @by)
            """, new { key, version, name, bpmn, compiled = new Jsonb(compiled), names = Jsonb.From(graph.Names), owner, publishedAt, by = Actor() }, ct);
        await RegisterStartsAsync(key, graph, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.WorkflowDefinitionPublished, "process_definition", $"{key}:{version}", TargetOrgUnitId: owner,
            Changes: new { key, version, source = "process_model", elements = graph.Elements.Count }), ct);
        return new ProcessDefinitionInfo(key, version, name, "process_model", publishedAt);
    }

    internal async Task<bool> HasNoneStartAsync(ProcessDefinitionInfo definition, CancellationToken ct) =>
        await GraphAsync(definition.Key, definition.Version, ct) is { } graph
        && graph.InScope(null).Any(e => e.Type == ElementTypes.StartEvent && e.EventKind == EventKinds.None);

    /// <summary>The names per locale of the step a task is on.</summary>
    internal async Task<IReadOnlyDictionary<string, string>> StepNamesAsync(UserTaskInfo task, CancellationToken ct)
    {
        var instance = await GetInstanceAsync(task.InstanceId, ct);
        if (instance is not null && await GraphAsync(instance.DefinitionKey, instance.DefinitionVersion, ct) is { } graph && graph.Has(task.ElementId)
            && graph.Element(task.ElementId).Names is { Count: > 0 } names)
        {
            return names;
        }
        return new Dictionary<string, string> { ["en"] = task.Name };
    }

    // ---- Starting ----

    private async Task<ProcessInstanceInfo> StartGraphAsync(ProcessDefinitionInfo definition, ProcessGraph graph, string businessKey, Guid? orgUnitId, Guid? startedBy,
        IReadOnlyCollection<Guid>? alsoExcluded, Guid? requestedFor, JsonObject variables, (Guid Instance, Guid Token, int Depth)? parent, string? startElement,
        JsonObject? payload, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        if (await entitlements.GetLimitAsync(Capabilities.WorkflowMaxActiveInstances, ct) is { } limit)
        {
            var active = await session.ScalarAsync<long>(
                "select count(*) from workflow.process_instance where status = 'active' and state is not null", null, ct);
            if (active >= limit)
            {
                throw AppException.Conflict("conflict.workflow.instance_quota");
            }
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        var created = await session.ScalarAsync<Guid?>(
            """
            insert into workflow.process_instance (tenant_id, id, definition_key, definition_version, business_key, org_unit_id, started_by, excluded_user_ids, requested_for,
              status, variables, state, parent_instance_id, parent_token_id, depth, started_at)
            values (platform.current_tenant_id(), @id, @key, @version, @businessKey, @orgUnitId, @startedBy, @alsoExcluded, @requestedFor, 'active', @variables, '{}'::jsonb,
              @parentInstance, @parentToken, @depth, @now)
            on conflict (tenant_id, definition_key, business_key) do nothing
            returning id
            """, new
            {
                id, key = definition.Key, version = definition.Version, businessKey, orgUnitId, startedBy, alsoExcluded = (alsoExcluded ?? []).Distinct().ToArray(), requestedFor,
                variables = new Jsonb(variables.ToJsonString()), parentInstance = parent?.Instance, parentToken = parent?.Token, depth = parent is { } p ? p.Depth + 1 : 0, now,
            }, ct);
        if (created is null)
        {
            return (await FindInstanceAsync(definition.Key, businessKey, ct))!;
        }
        await HistoryAsync(id, startElement ?? "start", "started", startedBy, null, ct);
        var info = new ProcessInstanceInfo(id, definition.Key, definition.Version, businessKey, orgUnitId, startedBy, "active", null, now, null);
        var instance = new GraphInstance(info, graph, new InstanceState(), parent?.Instance, parent?.Token, parent is { } q ? q.Depth + 1 : 0, requestedFor ?? startedBy,
            (alsoExcluded ?? []).ToArray());
        var executor = await ExecutorAsync(instance, ct);
        await ApplyAsync(instance, executor.Start(variables, startElement, payload), ct);
        await DrainAsync(ct);
        return (await GetInstanceAsync(id, ct))!;
    }

    private static JsonObject ToObject(IReadOnlyDictionary<string, JsonElement>? variables)
    {
        var result = new JsonObject();
        foreach (var (key, value) in variables ?? new Dictionary<string, JsonElement>())
        {
            result[key] = JsonNode.Parse(value.GetRawText());
        }
        return result;
    }

    // ---- Effects ----

    private async Task ApplyAsync(GraphInstance instance, IReadOnlyList<Effect> effects, CancellationToken ct)
    {
        var finished = false;
        foreach (var effect in effects)
        {
            switch (effect)
            {
                case Graph.Record r:
                    await HistoryAsync(instance.Info.Id, r.ElementId, r.Event, null, null, ct, r.Detail);
                    break;
                case CreateUserTask t:
                    await CreateGraphTaskAsync(instance, t, ct);
                    break;
                case Wait w:
                    await WaitAsync(instance, w, ct);
                    break;
                case CancelWaits c:
                    await CancelWaitsAsync(instance.Info.Id, c.Token, ct);
                    break;
                case CallService s:
                    await CallGraphServiceAsync(instance, s, ct);
                    break;
                case SendMessage m:
                    var messageId = $"process:{instance.Info.Id:N}:{Ids.New():N}";
                    deferred.Enqueue(() => CorrelateAsync(m.Name, m.CorrelationKey, messageId, m.Payload, "process", null, ct));
                    break;
                case StartChild child:
                    StartChildLater(instance, child, ct);
                    break;
                case RaiseIncident i:
                    await RaiseGraphIncidentAsync(instance, i.Token, i.ElementId, i.ErrorClass, i.Code, null, ct);
                    break;
                case Escalated e:
                    if (instance.ParentInstanceId is { } escalateTo && instance.ParentTokenId is { } escalateToken)
                    {
                        deferred.Enqueue(() => SignalAsync(escalateTo, ex => ex.ThrowEscalation(escalateToken, e.Code), ct));
                    }
                    break;
                case Finished f:
                    finished = true;
                    await FinishGraphAsync(instance, f, ct);
                    break;
            }
        }
        if (!finished)
        {
            await session.ExecAsync("update workflow.process_instance set state = @state, variables = @variables where id = @id",
                new { id = instance.Info.Id, state = new Jsonb(StateJson(instance.State)), variables = new Jsonb(instance.State.Variables.ToJsonString()) }, ct);
        }
    }

    private static string StateJson(InstanceState state)
    {
        var copy = new InstanceState { Tokens = state.Tokens, Scopes = state.Scopes, Visits = state.Visits };
        return JsonSerializer.Serialize(copy, ProcessGraph.Json);
    }

    /// <summary>Runs signals queued while applying effects, in order, until none are left.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        if (draining)
        {
            return;
        }
        draining = true;
        try
        {
            while (deferred.TryDequeue(out var next))
            {
                await next();
            }
        }
        finally
        {
            draining = false;
        }
    }

    /// <summary>Loads the instance, lets the executor react, and applies the effects. An instance that ended does nothing.</summary>
    private async Task SignalAsync(Guid instanceId, Func<Executor, IReadOnlyList<Effect>> signal, CancellationToken ct)
    {
        var instance = await LoadAsync(instanceId, ct);
        if (instance is null)
        {
            return;
        }
        var executor = await ExecutorAsync(instance, ct);
        await ApplyAsync(instance, signal(executor), ct);
    }

    private async Task FinishGraphAsync(GraphInstance instance, Finished finished, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cancelled = finished.Outcome == "cancelled";
        await session.ExecAsync(
            """
            update workflow.process_instance set status = @status, outcome = @outcome, ended_at = @now, state = null, variables = @variables where id = @id
            """, new { id = instance.Info.Id, status = cancelled ? "cancelled" : "completed", outcome = finished.Outcome, now,
                variables = new Jsonb(instance.State.Variables.ToJsonString()) }, ct);
        await CloseGraphWaitsAsync(instance.Info.Id, null, ct);
        await HistoryAsync(instance.Info.Id, "end_" + finished.Outcome, "completed", null, null, ct);
        await EndedAnalyticsAsync(instance.Info, cancelled ? "cancelled" : "completed", now, ct);
        await outbox.EnqueueAsync(new ProcessInstanceCompleted(instance.Info.Id, instance.Info.DefinitionKey, instance.Info.BusinessKey, finished.Outcome), ct);
        if (instance.ParentInstanceId is not { } parent || instance.ParentTokenId is not { } token)
        {
            return;
        }
        var variables = (JsonObject)instance.State.Variables.DeepClone();
        switch (finished.Outcome)
        {
            case "error":
                deferred.Enqueue(() => SignalAsync(parent, ex => ex.ThrowError(token, finished.Code ?? ""), ct));
                break;
            case "cancelled":
                deferred.Enqueue(async () =>
                {
                    var p = await LoadAsync(parent, ct);
                    if (p is not null && p.State.Tokens.Any(t => t.Id == token))
                    {
                        var element = p.State.Tokens.First(t => t.Id == token).ElementId;
                        p.State.Tokens.First(t => t.Id == token).Status = "incident";
                        await RaiseGraphIncidentAsync(p, token, element, "child_cancelled", null, null, ct);
                        await ApplyAsync(p, [], ct);
                    }
                });
                break;
            default:
                deferred.Enqueue(() => SignalAsync(parent, ex => ex.Complete(token, variables), ct));
                break;
        }
    }

    // ---- User tasks ----

    private async Task CreateGraphTaskAsync(GraphInstance instance, CreateUserTask effect, CancellationToken ct)
    {
        var element = instance.Graph.Element(effect.ElementId);
        var assignment = element.Assignment ?? new Assignment();
        var users = new List<Guid>(assignment.Users ?? []);
        var lineManager = false;
        if (assignment.LineManager && instance.Subject is { } subject && await reportingLine.ManagerOfAsync(subject, ct) is not null)
        {
            lineManager = true;
        }
        if (assignment.ManagerLevel is { } level && instance.Subject is { } person)
        {
            Guid? current = person;
            for (var i = 0; i < level && current is { } c; i++)
            {
                current = await reportingLine.ManagerOfAsync(c, ct);
            }
            if (current is { } manager && manager != person)
            {
                users.Add(manager);
            }
        }
        if (assignment.AssigneeExpression is { } expression)
        {
            try
            {
                users.AddRange(UserIds(FeelEngine.Evaluate(expression, FeelEngine.Variables(effect.Variables), clock.UtcNow)));
            }
            catch (FeelEvaluationException ex)
            {
                MarkIncident(instance, effect.Token);
                await RaiseGraphIncidentAsync(instance, effect.Token, element.Id, "expression", ex.Code, null, ct);
                return;
            }
        }
        users = users.Where(u => !instance.Excluded.Contains(u)).Distinct().ToList();
        if (users.Count == 0 && assignment.Permission is null && !lineManager)
        {
            users = (assignment.FallbackUsers ?? []).Where(u => !instance.Excluded.Contains(u)).Distinct().ToList();
        }
        if (users.Count == 0 && assignment.Permission is null && !lineManager)
        {
            MarkIncident(instance, effect.Token);
            await RaiseGraphIncidentAsync(instance, effect.Token, element.Id, "no_assignee", null, null, ct);
            return;
        }
        var candidates = new CandidateSet(element.Name("en"), users, assignment.Permission, lineManager, false, element.Names);
        var id = Ids.New();
        var now = clock.UtcNow;
        DateTimeOffset? dueAt = element.DueWorkingDays is { } days ? await jobs.DueAtAsync(instance.Info.OrgUnitId, now, days, ct) : null;
        await session.ExecAsync(
            """
            insert into workflow.user_task (tenant_id, id, instance_id, element_id, step_index, candidates, excluded_user_ids, status, due_at, created_at, token_id)
            values (platform.current_tenant_id(), @id, @instanceId, @elementId, 0, @candidates, @excluded, 'open', @dueAt, @now, @token)
            """, new { id, instanceId = instance.Info.Id, elementId = element.Id, candidates = Jsonb.From(candidates), excluded = instance.Excluded, dueAt, now, token = effect.Token }, ct);
        if (dueAt is { } due)
        {
            await jobs.ScheduleTaskDueAsync(instance.Info.Id, id, due, ct);
        }
        await HistoryAsync(instance.Info.Id, element.Id, "task_created", null, null, ct);
        await outbox.EnqueueAsync(new UserTaskCreated(id, instance.Info.Id, instance.Info.DefinitionKey, instance.Info.BusinessKey, element.Id, dueAt), ct);
    }

    private static void MarkIncident(GraphInstance instance, Guid token)
    {
        if (instance.State.Tokens.FirstOrDefault(t => t.Id == token) is { } t)
        {
            t.Status = "incident";
        }
    }

    private static IEnumerable<Guid> UserIds(object? value) => value switch
    {
        string s when Guid.TryParse(s, out var id) => [id],
        IEnumerable<object?> list => list.OfType<string>().Select(s => Guid.TryParse(s, out var id) ? id : Guid.Empty).Where(g => g != Guid.Empty),
        _ => [],
    };

    private async Task CompleteGraphTaskAsync(TaskRow task, string outcome, string? comment, Guid user, CancellationToken ct)
    {
        if (task.TokenId is not { } token)
        {
            return;
        }
        var result = new JsonObject { ["outcome"] = outcome, ["completedBy"] = user.ToString() };
        if (comment is not null)
        {
            result["comment"] = comment;
        }
        await SignalAsync(task.Info.InstanceId, ex => ex.State.Tokens.Any(t => t.Id == token) ? ex.Complete(token, result) : [], ct);
        await DrainAsync(ct);
    }

    // ---- Timers and messages ----

    private async Task WaitAsync(GraphInstance instance, Wait wait, CancellationToken ct)
    {
        if (wait.Kind == EventKinds.Timer)
        {
            var now = clock.UtcNow;
            var due = await TimerDueAsync(instance.Info.OrgUnitId, wait.Timer!, now, ct);
            if (due is null)
            {
                MarkIncident(instance, wait.Token);
                await RaiseGraphIncidentAsync(instance, wait.Token, wait.EventElementId, "timer", null, null, ct);
                return;
            }
            await session.ExecAsync(
                """
                insert into workflow.engine_job (tenant_id, id, kind, instance_id, token_id, element_id, due_at, detail)
                values (platform.current_tenant_id(), @id, @kind, @instanceId, @token, @element, @dueAt, @detail)
                """, new { id = Ids.New(), kind = EngineJobKinds.Timer, instanceId = instance.Info.Id, token = wait.Token, element = wait.EventElementId,
                    dueAt = due.Value.Due, detail = new Jsonb(due.Value.Detail.ToJsonString()) }, ct);
            return;
        }
        await session.ExecAsync(
            """
            insert into workflow.message_subscription (tenant_id, id, name, correlation_key, instance_id, token_id, element_id)
            values (platform.current_tenant_id(), @id, @name, @key, @instanceId, @token, @element)
            """, new { id = Ids.New(), name = wait.MessageName, key = wait.CorrelationKey, instanceId = instance.Info.Id, token = wait.Token, element = wait.EventElementId }, ct);
        // A message that came first waits in the buffer: the oldest one that has not expired is taken now.
        var buffered = await session.QuerySingleOrDefaultAsync(
            """
            update workflow.message_buffer set correlated_at = @now, instance_id = @instanceId
            where id = (select id from workflow.message_buffer
                        where correlated_at is null and name = @name and correlation_key is not distinct from @key and expires_at > @now
                        order by received_at, id limit 1 for update skip locked)
            returning payload::text
            """, new { now = clock.UtcNow, instanceId = instance.Info.Id, name = wait.MessageName, key = wait.CorrelationKey }, r => r.GetString(0), ct);
        if (buffered is not null)
        {
            var payload = JsonNode.Parse(buffered) as JsonObject;
            var (instanceId, token, element) = (instance.Info.Id, wait.Token, wait.EventElementId);
            deferred.Enqueue(() => FireAsync(instanceId, token, element, payload, ct));
        }
    }

    /// <summary>When a timer is due: a date, a duration, the first of a cycle or a number of working days.</summary>
    private async Task<(DateTimeOffset Due, JsonObject Detail)?> TimerDueAsync(Guid? orgUnitId, TimerSpec timer, DateTimeOffset now, CancellationToken ct)
    {
        if (timer.WorkingDays is { } days)
        {
            return (await jobs.DueAtAsync(orgUnitId, now, days, ct), []);
        }
        if (timer.Date is { } date)
        {
            return DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? (at, []) : null;
        }
        if (timer.Duration is { } text)
        {
            return FeelEngine.ParseDuration(text) is { } d ? (now.AddMonths(d.Months).Add(d.Time), []) : null;
        }
        if (timer.Cycle is { } cycle && BpmnReader.ParseCycle(cycle) is var (times, every))
        {
            var detail = new JsonObject { ["cycle"] = cycle };
            if (times is { } n)
            {
                detail["left"] = n - 1;
            }
            return (now.AddMonths(every.Months).Add(every.Time), detail);
        }
        return null;
    }

    /// <summary>A timer job of a graph instance is due (spec 024 FR-008). A missed cycle fires once and the next one is scheduled from now.</summary>
    internal async Task OnTimerAsync(Guid instanceId, Guid token, string element, JsonObject? detail, CancellationToken ct)
    {
        var instance = await LoadAsync(instanceId, ct);
        if (instance is null || !instance.State.Tokens.Any(t => t.Id == token))
        {
            return;
        }
        var executor = await ExecutorAsync(instance, ct);
        await ApplyAsync(instance, executor.Fire(token, element, null), ct);
        var evt = instance.Graph.Element(element);
        var repeats = detail?["cycle"]?.GetValue<string>() is { } cycle && BpmnReader.ParseCycle(cycle) is var (_, every)
            && (detail["left"] is null || detail["left"]!.GetValue<int>() > 0);
        if (repeats && !evt.Interrupting && instance.State.Tokens.Any(t => t.Id == token))
        {
            var next = (JsonObject)detail!.DeepClone();
            if (next["left"] is { } left)
            {
                next["left"] = left.GetValue<int>() - 1;
            }
            var (_, period) = BpmnReader.ParseCycle(detail["cycle"]!.GetValue<string>())!.Value;
            await session.ExecAsync(
                """
                insert into workflow.engine_job (tenant_id, id, kind, instance_id, token_id, element_id, due_at, detail)
                values (platform.current_tenant_id(), @id, @kind, @instanceId, @token, @element, @dueAt, @detail)
                """, new { id = Ids.New(), kind = EngineJobKinds.Timer, instanceId, token, element, dueAt = clock.UtcNow.AddMonths(period.Months).Add(period.Time),
                    detail = new Jsonb(next.ToJsonString()) }, ct);
        }
        await DrainAsync(ct);
    }

    private async Task FireAsync(Guid instanceId, Guid token, string element, JsonObject? payload, CancellationToken ct)
    {
        var instance = await LoadAsync(instanceId, ct);
        if (instance is null || !instance.State.Tokens.Any(t => t.Id == token))
        {
            return;
        }
        var evt = instance.Graph.Element(element);
        var keep = !evt.Interrupting && (evt.Type == ElementTypes.BoundaryEvent || evt.Type == ElementTypes.StartEvent);
        if (!keep)
        {
            await session.ExecAsync("delete from workflow.message_subscription where token_id = @token and element_id = @element", new { token, element }, ct);
        }
        var executor = await ExecutorAsync(instance, ct);
        await ApplyAsync(instance, executor.Fire(token, element, payload), ct);
    }

    /// <summary>
    /// Correlates a message by name and key within the tenant (spec 024 FR-009), once per message ID. The oldest waiting
    /// token takes it; otherwise a message start event starts an instance; otherwise it waits in the buffer until it
    /// expires. Returns the instance it reached, or null when it waits or came before.
    /// </summary>
    public async Task<MessageResult> CorrelateAsync(string name, string? correlationKey, string messageId, JsonObject? payload, string source, TimeSpan? timeToLive,
        CancellationToken ct = default)
    {
        context.RequireTenant();
        var now = clock.UtcNow;
        var id = Ids.New();
        var inserted = await session.ScalarAsync<Guid?>(
            """
            insert into workflow.message_buffer (tenant_id, id, message_id, name, correlation_key, payload, source, received_at, expires_at)
            values (platform.current_tenant_id(), @id, @messageId, @name, @correlationKey, @payload, @source, @now, @expiresAt)
            on conflict (tenant_id, message_id) do nothing
            returning id
            """, new { id, messageId, name, correlationKey, payload = new Jsonb((payload ?? []).ToJsonString()), source, now, expiresAt = now + (timeToLive ?? MessageTimeToLive) }, ct);
        if (inserted is null)
        {
            return new MessageResult(MessageResults.Duplicate, null);
        }
        var waiting = await session.QuerySingleOrDefaultAsync(
            """
            select s.id, s.instance_id, s.token_id, s.element_id from workflow.message_subscription s
            join workflow.process_instance i on i.id = s.instance_id and i.status = 'active'
            where s.name = @name and s.correlation_key is not distinct from @correlationKey and s.instance_id is not null
            order by s.created_at, s.id limit 1
            """, new { name, correlationKey }, r => (Id: r.GetGuid(0), Instance: r.GetGuid(1), Token: r.GetGuid(2), Element: r.GetString(3)), ct);
        if (waiting.Element is not null)
        {
            await session.ExecAsync("update workflow.message_buffer set correlated_at = @now, instance_id = @instance where id = @id", new { id, now, instance = waiting.Instance }, ct);
            await FireAsync(waiting.Instance, waiting.Token, waiting.Element, payload, ct);
            await DrainAsync(ct);
            return new MessageResult(MessageResults.Correlated, waiting.Instance);
        }
        var start = await session.QuerySingleOrDefaultAsync(
            "select definition_key, element_id from workflow.message_subscription where name = @name and definition_key is not null order by created_at, id limit 1",
            new { name }, r => (Key: r.GetString(0), Element: r.GetString(1)), ct);
        if (start.Key is not null && await GetDefinitionAsync(start.Key, null, ct) is { } definition && await GraphAsync(definition.Key, definition.Version, ct) is { } graph)
        {
            var started = await StartGraphAsync(definition, graph, "message:" + messageId, null, null, null, null, [], null, start.Element, payload, ct);
            await session.ExecAsync("update workflow.message_buffer set correlated_at = @now, instance_id = @instance where id = @id", new { id, now, instance = started.Id }, ct);
            return new MessageResult(MessageResults.Started, started.Id);
        }
        return new MessageResult(MessageResults.Buffered, null);
    }

    /// <summary>
    /// Registers the start events of a newly published version: message starts wait for their message, timer starts get
    /// their first job. Those of the previous version end, so only the current version starts new instances.
    /// </summary>
    internal async Task RegisterStartsAsync(string definitionKey, ProcessGraph graph, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("delete from workflow.message_subscription where definition_key = @definitionKey", new { definitionKey }, ct);
        await session.ExecAsync("update workflow.engine_job set done_at = @now where kind = @kind and definition_key = @definitionKey and done_at is null",
            new { definitionKey, now, kind = EngineJobKinds.TimerStart }, ct);
        foreach (var start in graph.InScope(null).Where(e => e.Type == ElementTypes.StartEvent))
        {
            if (start.EventKind == EventKinds.Message)
            {
                await session.ExecAsync(
                    """
                    insert into workflow.message_subscription (tenant_id, id, name, element_id, definition_key)
                    values (platform.current_tenant_id(), @id, @name, @element, @definitionKey)
                    """, new { id = Ids.New(), name = start.MessageName, element = start.Id, definitionKey }, ct);
            }
            else if (start.EventKind == EventKinds.Timer && await TimerDueAsync(null, start.Timer!, now, ct) is var (due, detail))
            {
                await ScheduleTimerStartAsync(definitionKey, start.Id, due, detail, ct);
            }
        }
    }

    private Task ScheduleTimerStartAsync(string definitionKey, string element, DateTimeOffset due, JsonObject detail, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into workflow.engine_job (tenant_id, id, kind, definition_key, element_id, due_at, detail)
            values (platform.current_tenant_id(), @id, @kind, @definitionKey, @element, @due, @detail)
            """, new { id = Ids.New(), kind = EngineJobKinds.TimerStart, definitionKey, element, due, detail = new Jsonb(detail.ToJsonString()) }, ct);

    /// <summary>A timer start event is due: one instance of the latest version starts, and a cycle schedules its next start from now.</summary>
    internal async Task OnTimerStartAsync(Guid jobId, string definitionKey, string element, DateTimeOffset dueAt, JsonObject? detail, CancellationToken ct)
    {
        var definition = await GetDefinitionAsync(definitionKey, null, ct);
        var graph = definition is null ? null : await GraphAsync(definition.Key, definition.Version, ct);
        if (definition is null || graph is null || !graph.Has(element) || graph.Element(element).EventKind != EventKinds.Timer)
        {
            return;
        }
        await StartGraphAsync(definition, graph, $"timer:{jobId:N}", null, null, null, null, [], null, element, null, ct);
        if (detail?["cycle"]?.GetValue<string>() is { } cycle && BpmnReader.ParseCycle(cycle) is var (_, every)
            && (detail["left"] is null || detail["left"]!.GetValue<int>() > 0))
        {
            var next = (JsonObject)detail.DeepClone();
            if (next["left"] is { } left)
            {
                next["left"] = left.GetValue<int>() - 1;
            }
            await ScheduleTimerStartAsync(definitionKey, element, clock.UtcNow.AddMonths(every.Months).Add(every.Time), next, ct);
        }
    }

    // ---- Service tasks ----

    private async Task CallGraphServiceAsync(GraphInstance instance, CallService effect, CancellationToken ct)
    {
        var element = instance.Graph.Element(effect.ElementId);
        var call = element.Call!;
        var id = Ids.New();
        var key = $"{GraphCorrelationPrefix}{instance.Info.Id:N}:{id:N}";
        await session.ExecAsync(
            """
            insert into workflow.service_task (tenant_id, id, instance_id, element_id, step_index, idempotency_key, status, started_at, token_id)
            values (platform.current_tenant_id(), @id, @instanceId, @element, 0, @key, 'running', @now, @token)
            """, new { id, instanceId = instance.Info.Id, element = element.Id, key, now = clock.UtcNow, token = effect.Token }, ct);
        await HistoryAsync(instance.Info.Id, element.Id, "service_task_started", null, null, ct);
        // A webhook step posts its inputs to the webhook connection, signed and only to an allowlisted host (spec 017 US5).
        var (connectionId, actionName, input) = call.WebhookId is { } hook
            ? ((Guid?)hook, (string?)WebhookActions.Deliver, JsonSerializer.SerializeToElement(new { type = "process.step", occurredAt = clock.UtcNow, data = effect.Inputs }))
            : (call.ConnectionId, call.Action, JsonSerializer.SerializeToElement(effect.Inputs));
        if (connectionId is not { } connection || actionName is not { } action)
        {
            MarkIncident(instance, effect.Token);
            await session.ExecAsync("update workflow.service_task set status = 'incident', error_class = 'not_configured' where id = @id", new { id }, ct);
            await RaiseGraphIncidentAsync(instance, effect.Token, element.Id, "not_configured", null, id, ct);
            return;
        }
        var result = await connectors.EnqueueAsync(new ConnectorCall(connection, action, input, key,
            $"{GraphCorrelationPrefix}{instance.Info.Id:D}:{id:D}"), ct);
        if (result.RunId is { } run)
        {
            await session.ExecAsync("update workflow.service_task set run_id = @run where id = @id", new { id, run }, ct);
        }
        if (result.Outcome is ConnectorOutcomes.Queued or ConnectorOutcomes.InProgress)
        {
            return;
        }
        var (instanceId, outcome, reference, errorClass, errorCode, output) = (instance.Info.Id, result.Outcome, result.ExternalReference, result.ErrorClass, result.ErrorCode,
            result.Output);
        deferred.Enqueue(() => OnGraphServiceOutcomeAsync(instanceId, id, outcome, reference, errorClass, errorCode, output, ct));
    }

    private async Task OnGraphServiceOutcomeAsync(Guid instanceId, Guid serviceTaskId, string outcome, string? reference, string? errorClass, string? errorCode,
        JsonElement? output, CancellationToken ct)
    {
        var instance = await LoadAsync(instanceId, ct);
        var task = await session.QuerySingleOrDefaultAsync(
            "select token_id, element_id, status from workflow.service_task where id = @serviceTaskId for update",
            new { serviceTaskId }, r => (Token: r.GetGuid(0), Element: r.GetString(1), Status: r.GetString(2)), ct);
        if (instance is null || task.Status != "running" || !instance.State.Tokens.Any(t => t.Id == task.Token))
        {
            return;
        }
        var now = clock.UtcNow;
        var executor = await ExecutorAsync(instance, ct);
        if (outcome == ConnectorOutcomes.Succeeded)
        {
            await session.ExecAsync(
                "update workflow.service_task set status = 'succeeded', external_reference = @reference, ended_at = @now where id = @serviceTaskId",
                new { serviceTaskId, reference = reference is { Length: > 500 } ? reference[..500] : reference, now }, ct);
            var result = output is { ValueKind: JsonValueKind.Object } o ? JsonNode.Parse(o.GetRawText())!.AsObject() : [];
            if (reference is not null)
            {
                result["reference"] = reference;
            }
            await outbox.EnqueueAsync(new ServiceTaskCompleted(instanceId, instance.Info.DefinitionKey, instance.Info.BusinessKey, task.Element, reference), ct);
            await ApplyAsync(instance, executor.Complete(task.Token, result), ct);
            return;
        }
        if (outcome == ConnectorOutcomes.BusinessError && errorCode is not null && CatchesError(instance.Graph, task.Element, errorCode))
        {
            await session.ExecAsync(
                "update workflow.service_task set status = 'business_error', error_class = 'business_error', error_code = @errorCode, ended_at = @now where id = @serviceTaskId",
                new { serviceTaskId, errorCode, now }, ct);
            await HistoryAsync(instanceId, task.Element, "service_task_business_error:" + errorCode, null, null, ct);
            await ApplyAsync(instance, executor.ThrowError(task.Token, errorCode), ct);
            return;
        }
        var cls = outcome switch
        {
            ConnectorOutcomes.Unknown => OutcomeUnknown,
            ConnectorOutcomes.BusinessError => ConnectorErrorClassBusiness,
            _ => errorClass ?? "unexpected",
        };
        await session.ExecAsync("update workflow.service_task set status = 'incident', error_class = @cls, error_code = @errorCode where id = @serviceTaskId",
            new { serviceTaskId, cls, errorCode = outcome == ConnectorOutcomes.BusinessError ? errorCode : null }, ct);
        MarkIncident(instance, task.Token);
        await RaiseGraphIncidentAsync(instance, task.Token, task.Element, cls, outcome == ConnectorOutcomes.BusinessError ? errorCode : null, serviceTaskId, ct);
        await ApplyAsync(instance, [], ct);
    }

    /// <summary>
    /// Whether a business error of <paramref name="elementId"/> has a catch: an error boundary on it or on an enclosing
    /// sub-process, or an error event sub-process in an enclosing scope. Without one it is an incident, not an end.
    /// </summary>
    private static bool CatchesError(ProcessGraph graph, string elementId, string code)
    {
        bool Matches(GraphElement e) => e.EventKind == EventKinds.Error && (string.IsNullOrEmpty(e.Code) || e.Code == code);
        for (var current = elementId; current is not null; current = graph.Element(current).Scope)
        {
            if (graph.BoundariesOf(current).Any(Matches))
            {
                return true;
            }
            var scope = graph.Element(current).Scope;
            if (graph.InScope(scope).Where(e => e.Type == ElementTypes.SubProcess && e.TriggeredByEvent)
                .Any(sub => graph.InScope(sub.Id).Any(s => s.Type == ElementTypes.StartEvent && Matches(s))))
            {
                return true;
            }
        }
        return false;
    }

    // ---- Call activities ----

    private void StartChildLater(GraphInstance instance, StartChild child, CancellationToken ct)
    {
        var (parentId, token, depth, orgUnit, startedBy) = (instance.Info.Id, child.Token, instance.Depth, instance.Info.OrgUnitId, instance.Info.StartedBy);
        deferred.Enqueue(async () =>
        {
            var key = ModelPrefix + child.CalledKey;
            var definition = await GetDefinitionAsync(key, null, ct);
            var graph = definition is null ? null : await GraphAsync(definition.Key, definition.Version, ct);
            if (definition is null || graph is null || depth + 1 > MaxCallDepth)
            {
                var parent = await LoadAsync(parentId, ct);
                if (parent is not null && parent.State.Tokens.Any(t => t.Id == token))
                {
                    MarkIncident(parent, token);
                    await RaiseGraphIncidentAsync(parent, token, child.ElementId, definition is null || graph is null ? "called_missing" : "call_depth", null, null, ct);
                    await ApplyAsync(parent, [], ct);
                }
                return;
            }
            await StartGraphAsync(definition, graph, $"call:{parentId:N}:{token:N}", orgUnit, startedBy, null, null, child.Variables, (parentId, token, depth), null, null, ct);
        });
    }

    // ---- Waits and incidents ----

    /// <summary>Ends everything a token waits on: timers, message subscriptions, open tasks, running steps and child instances.</summary>
    private async Task CancelWaitsAsync(Guid instanceId, Guid token, CancellationToken ct)
    {
        await CloseGraphWaitsAsync(instanceId, token, ct);
        var children = await session.QueryAsync(
            "select id from workflow.process_instance where parent_instance_id = @instanceId and parent_token_id = @token and status = 'active'",
            new { instanceId, token }, r => r.GetGuid(0), ct);
        foreach (var child in children)
        {
            await CancelGraphInstanceAsync(child, ct);
        }
    }

    private async Task CloseGraphWaitsAsync(Guid instanceId, Guid? token, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var args = new { instanceId, token, now, by = Actor() };
        await session.ExecAsync("update workflow.engine_job set done_at = @now where instance_id = @instanceId and done_at is null and (@token::uuid is null or token_id = @token)",
            args, ct);
        await session.ExecAsync("delete from workflow.message_subscription where instance_id = @instanceId and (@token::uuid is null or token_id = @token)", args, ct);
        await session.ExecAsync(
            "update workflow.user_task set status = 'cancelled', completed_at = @now where instance_id = @instanceId and status = 'open' and (@token::uuid is null or token_id = @token)",
            args, ct);
        await session.ExecAsync(
            """
            update workflow.service_task set status = 'cancelled', ended_at = @now
            where instance_id = @instanceId and status in ('running', 'incident') and (@token::uuid is null or token_id = @token)
            """, args, ct);
        await session.ExecAsync(
            """
            update workflow.incident set status = 'resolved', resolution = 'cancelled', resolved_by = @by, resolved_at = @now
            where instance_id = @instanceId and status = 'open' and (@token::uuid is null or token_id = @token)
            """, args, ct);
    }

    /// <summary>Cancels a graph instance and its children (an operator cancel, a withdrawal, or a parent that moved on).</summary>
    /// <summary>Counts an ended process model instance for spec 021 (FR-014): process, version, how it ended and how long it ran, with no person.</summary>
    private Task EndedAnalyticsAsync(ProcessInstanceInfo info, string state, DateTimeOffset now, CancellationToken ct) =>
        info.DefinitionKey.StartsWith(ModelPrefix, StringComparison.Ordinal) && info.DefinitionKey.Length - ModelPrefix.Length <= ProcessModels.MaxKeyLength
            ? analytics.EmitAsync(AnalyticsEvents.ProcessInstanceEnded, new Dictionary<string, object?>
            {
                ["process"] = info.DefinitionKey[ModelPrefix.Length..],
                ["version"] = info.DefinitionVersion,
                ["state"] = state,
                ["seconds"] = Math.Round(Math.Max(0, (now - info.StartedAt).TotalSeconds)),
            }, ct)
            : Task.CompletedTask;

    /// <summary>An operator cancels an active process model instance with a reason (spec 024 FR-013); its children end too.</summary>
    public async Task<ProcessInstanceInfo> CancelByOperatorAsync(Guid instanceId, string reason, CancellationToken ct = default)
    {
        var instance = await LoadAsync(instanceId, ct) ?? throw AppException.Conflict("conflict.process.instance_not_active");
        await CancelGraphInstanceAsync(instanceId, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessInstanceCancelled, "process_instance", instanceId.ToString(), TargetOrgUnitId: instance.Info.OrgUnitId,
            Changes: new { reason }), ct);
        await DrainAsync(ct);
        return (await GetInstanceAsync(instanceId, ct))!;
    }

    /// <summary>
    /// An operator changes variables of an instance that has an open incident, so a retry can succeed (spec 024 FR-013,
    /// US5 scenario 2). A null value removes the variable. The history and audit name the variables, never their values.
    /// </summary>
    public async Task ChangeVariablesAsync(Guid instanceId, JsonObject changes, string reason, CancellationToken ct = default)
    {
        var instance = await LoadAsync(instanceId, ct) ?? throw AppException.Conflict("conflict.process.instance_not_active");
        var open = await session.QuerySingleOrDefaultAsync("select count(*) from workflow.incident where instance_id = @instanceId and status = 'open'",
            new { instanceId }, r => r.GetInt64(0), ct);
        if (open == 0)
        {
            throw AppException.Conflict("conflict.process.no_open_incident");
        }
        var names = new List<string>();
        foreach (var (name, value) in changes)
        {
            names.Add(name);
            if (value is null)
            {
                instance.State.Variables.Remove(name);
            }
            else
            {
                instance.State.Variables[name] = value.DeepClone();
            }
        }
        var json = instance.State.Variables.ToJsonString();
        if (json.Length > Executor.MaxInstanceBytes || instance.State.Variables.Any(p => (p.Value?.ToJsonString().Length ?? 0) > Executor.MaxVariableBytes))
        {
            throw AppException.Validation("variables", "validation.workflow.variables_too_large");
        }
        await session.ExecAsync("update workflow.process_instance set variables = @variables where id = @instanceId",
            new { instanceId, variables = new Jsonb(json) }, ct);
        var detail = new JsonObject { ["names"] = new JsonArray(names.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) };
        await HistoryAsync(instanceId, "", "variables_changed", Actor(), null, ct, detail);
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessVariablesChanged, "process_instance", instanceId.ToString(), TargetOrgUnitId: instance.Info.OrgUnitId,
            Changes: new { variables = names, reason }), ct);
    }

    private async Task CancelGraphInstanceAsync(Guid instanceId, CancellationToken ct)
    {
        var instance = await LoadAsync(instanceId, ct);
        if (instance is null)
        {
            return;
        }
        var executor = await ExecutorAsync(instance, ct);
        var effects = executor.Cancel();
        foreach (var effect in effects.OfType<CancelWaits>())
        {
            await CancelWaitsAsync(instanceId, effect.Token, ct);
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update workflow.process_instance set status = 'cancelled', outcome = @outcome, ended_at = @now, state = null where id = @instanceId",
            new { instanceId, outcome = InstanceOutcomes.Cancelled, now }, ct);
        await CloseGraphWaitsAsync(instanceId, null, ct);
        await HistoryAsync(instanceId, "end_cancelled", "cancelled", Actor(), null, ct);
        await EndedAnalyticsAsync(instance.Info, "cancelled", now, ct);
        await outbox.EnqueueAsync(new ProcessInstanceCompleted(instanceId, instance.Info.DefinitionKey, instance.Info.BusinessKey, InstanceOutcomes.Cancelled), ct);
    }

    private async Task RaiseGraphIncidentAsync(GraphInstance instance, Guid token, string elementId, string errorClass, string? errorCode, Guid? serviceTaskId,
        CancellationToken ct)
    {
        var id = Ids.New();
        var inserted = await session.ExecAsync(
            """
            insert into workflow.incident (tenant_id, id, instance_id, service_task_id, token_id, element_id, error_class, error_code, status, raised_at)
            values (platform.current_tenant_id(), @id, @instanceId, @serviceTaskId, @token, @elementId, @errorClass, @errorCode, 'open', @now)
            on conflict do nothing
            """, new { id, instanceId = instance.Info.Id, serviceTaskId, token, elementId, errorClass, errorCode, now = clock.UtcNow }, ct);
        if (inserted == 0)
        {
            return;
        }
        await HistoryAsync(instance.Info.Id, elementId, "incident_raised", null, null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessIncidentRaised, "process_instance", instance.Info.Id.ToString(), TargetOrgUnitId: instance.Info.OrgUnitId,
            Changes: new { incident = id, element = elementId, errorClass, errorCode }), ct);
        await outbox.EnqueueAsync(new ProcessIncidentRaised(id, instance.Info.Id, instance.Info.DefinitionKey, instance.Info.BusinessKey, elementId, errorClass), ct);
    }

    /// <summary>Applies an operator's resolution to the token of a graph incident: run the step again, move past it, or end the instance.</summary>
    private async Task ResolveGraphIncidentAsync(Guid instanceId, Guid token, Guid? serviceTaskId, string resolution, CancellationToken ct)
    {
        var now = clock.UtcNow;
        switch (resolution)
        {
            case IncidentResolutions.Retried:
                if (serviceTaskId is { } retried)
                {
                    await session.ExecAsync("update workflow.service_task set status = 'cancelled', ended_at = @now where id = @retried", new { retried, now }, ct);
                }
                await SignalAsync(instanceId, ex => ex.Retry(token), ct);
                break;
            case IncidentResolutions.Skipped:
                if (serviceTaskId is { } skipped)
                {
                    await session.ExecAsync("update workflow.service_task set status = 'skipped', ended_at = @now where id = @skipped", new { skipped, now }, ct);
                }
                await SignalAsync(instanceId, ex => ex.Skip(token), ct);
                break;
            default:
                await CancelGraphInstanceAsync(instanceId, ct);
                break;
        }
        await DrainAsync(ct);
    }
}

/// <summary>What happened to a message (spec 024 FR-009).</summary>
public static class MessageResults
{
    public const string Correlated = "correlated";
    public const string Started = "started";
    public const string Buffered = "buffered";
    public const string Duplicate = "duplicate";
}

public sealed record MessageResult(string Result, Guid? InstanceId);
