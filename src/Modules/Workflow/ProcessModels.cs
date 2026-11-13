using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Modules.Workflow.Graph;
using TeamBeats.Localization;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

public sealed record ProcessModelRequest(string? BpmnXml, Guid? OwnerOrgUnitId);

public sealed record ProcessModelSummary(string Key, Guid? OwnerOrgUnitId, IReadOnlyDictionary<string, string> Names, int Problems, int? LatestVersion, DateTimeOffset UpdatedAt);

public sealed record ProcessVersionView(int Version, DateTimeOffset PublishedAt, Guid? PublishedBy, long ActiveInstances);

/// <summary>A problem of a draft on its element, with its text in the caller's language.</summary>
public sealed record ProblemView(string ElementId, string Key, string Message);

public sealed record ProcessModelView(string Key, Guid? OwnerOrgUnitId, IReadOnlyDictionary<string, string> Names, string BpmnXml, IReadOnlyList<ProblemView> Problems,
    DateTimeOffset UpdatedAt, IReadOnlyList<ProcessVersionView> Versions);

public sealed record StartProcessRequest(string? BusinessKey, Guid? OrgUnitId, JsonElement? Variables);

/// <summary>
/// Process models of spec 024 R2 (FR-001 to FR-003): a draft per key that process owners edit and validate, published as
/// the next immutable version of <c>process:&lt;key&gt;</c>. Owners act with <c>process.design</c> at the model's owner
/// unit (or the tenant when it has none); everything here needs the <c>workflow.bpmn</c> capability.
/// </summary>
public sealed class ProcessModels(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, IAuditWriter audit,
    IClock clock, ProcessEngine engine, IConnectorActions actions, ILocalizer localizer, RequestLocale locale, ProcessSandbox sandbox)
{
    /// <summary>Model keys stay short enough to be an analytics key (spec 021) once published.</summary>
    public const int MaxKeyLength = 64;

    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private async Task<bool> MayDesignAsync(Guid? unit, CancellationToken ct) => (await authz.CheckAsync(Permissions.WorkflowDesign, Target(unit), ct)).Allowed;

    private sealed record ModelRow(string Key, Guid? Owner, string Names, string Xml, string Problems, DateTimeOffset UpdatedAt);

    private Task<ModelRow?> RowAsync(string key, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select key, owner_org_unit_id, names::text, bpmn_xml, problems::text, updated_at from workflow.process_model where key = @key{(forUpdate ? " for update" : "")}",
            new { key }, r => new ModelRow(r.GetString(0), r.Get<Guid?>("owner_org_unit_id"), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)), ct);

    private static Dictionary<string, string> Names(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    private static List<ModelProblem> Problems(string json) => JsonSerializer.Deserialize<List<ModelProblem>>(json, ProcessGraph.Json) ?? [];

    public async Task<IReadOnlyList<ProcessModelSummary>> ListAsync(CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var rows = await session.QueryAsync(
            """
            select m.key, m.owner_org_unit_id, m.names::text, jsonb_array_length(m.problems), m.updated_at,
              (select max(version) from workflow.process_definition d where d.key = 'process:' || m.key)
            from workflow.process_model m order by m.key
            """, null, r => (Summary: new ProcessModelSummary(r.GetString(0), r.Get<Guid?>("owner_org_unit_id"), Names(r.GetString(2)), r.GetInt32(3),
                r.Get<int?>("max"), r.GetFieldValue<DateTimeOffset>(4)), Owner: r.Get<Guid?>("owner_org_unit_id")), ct);
        var allowed = new Dictionary<Guid, bool>();
        var result = new List<ProcessModelSummary>();
        foreach (var (summary, owner) in rows)
        {
            var key = owner ?? Guid.Empty;
            if (!allowed.TryGetValue(key, out var may))
            {
                may = await MayDesignAsync(owner, ct) || (await authz.CheckAsync(Permissions.ProcessOperate, Target(owner), ct)).Allowed
                    || (await authz.CheckAsync(Permissions.ProcessAnalyze, Target(owner), ct)).Allowed;
                allowed[key] = may;
            }
            if (may)
            {
                result.Add(summary);
            }
        }
        return result;
    }

    public async Task<ProcessModelView> GetAsync(string key, CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var row = await RowAsync(key, false, ct) ?? throw AppException.NotFound();
        if (!await MayDesignAsync(row.Owner, ct) && !(await authz.CheckAsync(Permissions.ProcessOperate, Target(row.Owner), ct)).Allowed
            && !(await authz.CheckAsync(Permissions.ProcessAnalyze, Target(row.Owner), ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, ct);
    }

    /// <summary>
    /// Runs the saved draft in the sandbox (US1 scenario 4) as its owner: nothing is created or sent. A draft the reader
    /// cannot run is answered with its problems and no steps.
    /// </summary>
    public async Task<ProcessTestResult> TestRunAsync(string key, ProcessTestRequest request, CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var row = await RowAsync(key, false, ct) ?? throw AppException.NotFound();
        if (!await MayDesignAsync(row.Owner, ct))
        {
            throw AppException.NotFound();
        }
        var (graph, problems) = BpmnReader.Read(key, row.Xml);
        if (graph is null || problems.Count > 0)
        {
            return new ProcessTestResult("invalid", [], [],
                problems.Select(p => new ProblemView(p.ElementId, p.Key, localizer.Get(p.Key, locale.Locale))).ToList());
        }
        return await sandbox.RunAsync(graph, request, locale.Locale, ct);
    }

    private async Task<ProcessModelView> ViewAsync(ModelRow row, CancellationToken ct)
    {
        var versions = await session.QueryAsync(
            """
            select d.version, d.published_at, d.published_by,
              (select count(*) from workflow.process_instance i where i.definition_key = d.key and i.definition_version = d.version and i.status = 'active')
            from workflow.process_definition d where d.key = @key order by d.version desc
            """, new { key = ProcessEngine.ModelPrefix + row.Key },
            r => new ProcessVersionView(r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1), r.Get<Guid?>("published_by"), r.GetInt64(3)), ct);
        return new ProcessModelView(row.Key, row.Owner, Names(row.Names), row.Xml, Problems(row.Problems).Select(p => new ProblemView(p.ElementId, p.Key, localizer.Get(p.Key, locale.Locale))).ToList(),
            row.UpdatedAt, versions);
    }

    /// <summary>Saves the draft, valid or not, and returns it with the problems the validator found (FR-002).</summary>
    public async Task<ProcessModelView> SaveAsync(string key, ProcessModelRequest request, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        if (!BpmnReader.IsValidKey(key) || key.Contains(':') || key.Length > MaxKeyLength)
        {
            throw AppException.Validation("key", "validation.process.key");
        }
        if (string.IsNullOrWhiteSpace(request.BpmnXml))
        {
            throw AppException.Validation("bpmnXml", "validation.process.xml");
        }
        if (request.BpmnXml.Length > BpmnReader.MaxBytes)
        {
            throw AppException.Validation("bpmnXml", "validation.process.too_large");
        }
        if (!await MayDesignAsync(request.OwnerOrgUnitId, ct))
        {
            throw AppException.Denied();
        }
        var existing = await RowAsync(key, true, ct);
        if (existing is not null && existing.Owner != request.OwnerOrgUnitId && !await MayDesignAsync(existing.Owner, ct))
        {
            throw AppException.Denied();
        }
        var (graph, read) = BpmnReader.Read(key, request.BpmnXml);
        var problems = read.ToList();
        if (graph is not null)
        {
            problems.AddRange(await PublishProblemsAsync(graph, ct));
        }
        var names = graph?.Names ?? (existing is null ? new Dictionary<string, string>() : Names(existing.Names));
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into workflow.process_model (tenant_id, key, owner_org_unit_id, names, bpmn_xml, problems, created_at, created_by, updated_at, updated_by)
            values (platform.current_tenant_id(), @key, @owner, @names, @xml, @problems, @now, @user, @now, @user)
            on conflict (tenant_id, key) do update set owner_org_unit_id = excluded.owner_org_unit_id, names = excluded.names, bpmn_xml = excluded.bpmn_xml,
              problems = excluded.problems, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { key, owner = request.OwnerOrgUnitId, names = Jsonb.From(names), xml = request.BpmnXml, problems = new Jsonb(JsonSerializer.Serialize(problems, ProcessGraph.Json)),
                now, user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessModelSaved, "process_model", key, TargetOrgUnitId: request.OwnerOrgUnitId,
            Changes: new { key, owner = request.OwnerOrgUnitId, problems = problems.Count }), ct);
        return await ViewAsync((await RowAsync(key, false, ct))!, ct);
    }

    /// <summary>
    /// Publishes the draft as the next version (FR-003). Refused while the validator or the publish checks find problems:
    /// call activity targets that are not published, permissions that do not exist, and connector actions that are not
    /// there. Publishing the same model again returns the current version.
    /// </summary>
    public async Task<ProcessModelView> PublishAsync(string key, CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var row = await RowAsync(key, true, ct) ?? throw AppException.NotFound();
        if (!await MayDesignAsync(row.Owner, ct))
        {
            throw AppException.Denied();
        }
        var (graph, problems) = BpmnReader.Read(key, row.Xml);
        var found = problems.ToList();
        if (graph is not null)
        {
            found.AddRange(await PublishProblemsAsync(graph, ct));
        }
        if (found.Count > 0)
        {
            // Each problem names its element (FR-002), so the modeler marks it: elements.<id>, or bpmnXml for the whole model.
            throw AppException.Validation(found.GroupBy(p => p.ElementId.Length == 0 ? "bpmnXml" : "elements." + p.ElementId)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Key).Distinct().ToArray()));
        }
        await engine.PublishGraphAsync(ProcessEngine.ModelPrefix + key, graph!, row.Xml, row.Owner, ct);
        await session.ExecAsync("update workflow.process_model set problems = '[]'::jsonb where key = @key", new { key }, ct);
        return await ViewAsync((await RowAsync(key, false, ct))!, ct);
    }

    private async Task<List<ModelProblem>> PublishProblemsAsync(ProcessGraph graph, CancellationToken ct)
    {
        var problems = new List<ModelProblem>();
        foreach (var element in graph.Elements)
        {
            if (element.CalledKey is { } called && called != graph.Key && await engine.GetDefinitionAsync(ProcessEngine.ModelPrefix + called, null, ct) is null)
            {
                problems.Add(new ModelProblem(element.Id, "validation.process.called_missing"));
            }
            if (element.Assignment?.Permission is { } permission && !Permissions.All.Contains(permission))
            {
                problems.Add(new ModelProblem(element.Id, "validation.process.assignment_permission"));
            }
            if (element.Call is { ConnectionId: { } connection, Action: { } action } call)
            {
                var found = await actions.FindAsync(connection, action, ct);
                if (found is null || (found.Inputs is { } inputs && call.Inputs.Keys.Any(k => !inputs.Any(d => d.Name == k))))
                {
                    problems.Add(new ModelProblem(element.Id, "validation.process.service_binding"));
                }
            }
        }
        return problems;
    }

    public async Task<string> BpmnAsync(string key, int version, CancellationToken ct = default)
    {
        await GetAsync(key, ct);
        return await engine.GetBpmnAsync(ProcessEngine.ModelPrefix + key, version, ct) ?? throw AppException.NotFound();
    }

    /// <summary>
    /// Starts an instance of the latest version at its none start event, as a process operator at the owner unit. Other
    /// modules start instances through <see cref="IProcessEngine.StartAsync"/> with their own checks.
    /// </summary>
    public async Task<ProcessInstanceInfo> StartAsync(string key, StartProcessRequest request, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var row = await RowAsync(key, false, ct) ?? throw AppException.NotFound();
        await authz.DemandAsync(Permissions.ProcessOperate, Target(request.OrgUnitId ?? row.Owner), ct);
        if (request.OrgUnitId is not null && row.Owner is not null)
        {
            await authz.DemandAsync(Permissions.ProcessOperate, Target(row.Owner), ct);
        }
        var definition = await engine.GetDefinitionAsync(ProcessEngine.ModelPrefix + key, null, ct) ?? throw AppException.Conflict("conflict.process.not_published");
        if (await engine.HasNoneStartAsync(definition, ct) is false)
        {
            throw AppException.Conflict("conflict.process.no_none_start");
        }
        var variables = new Dictionary<string, JsonElement>();
        if (request.Variables is { ValueKind: JsonValueKind.Object } given)
        {
            foreach (var p in given.EnumerateObject())
            {
                variables[p.Name] = p.Value.Clone();
            }
        }
        else if (request.Variables is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined })
        {
            throw AppException.Validation("variables", "validation.workflow.variables");
        }
        if (JsonSerializer.Serialize(variables).Length > ProcessEngine.MaxVariablesBytes)
        {
            throw AppException.Validation("variables", "validation.workflow.variables_too_large");
        }
        var businessKey = string.IsNullOrWhiteSpace(request.BusinessKey) ? Ids.New().ToString("N") : request.BusinessKey.Trim();
        var instance = await engine.StartAsync(definition.Key, businessKey, request.OrgUnitId ?? row.Owner, user, null, null, variables, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessInstanceStarted, "process_instance", instance.Id.ToString(), TargetOrgUnitId: instance.OrgUnitId,
            Changes: new { key, instance.DefinitionVersion, instance.BusinessKey }), ct);
        return instance;
    }

    /// <summary>The variables of an instance, for tests and the operator view.</summary>
    internal static JsonObject Parse(string json) => JsonNode.Parse(json) as JsonObject ?? [];
}

/// <summary>Tasks of process models in the spec 016 inbox. The engine checks the candidates; there is no module object to move on.</summary>
public sealed class ProcessModelTasks(ProcessEngine engine) : ITaskSource
{
    public bool Handles(string definitionKey) => definitionKey.StartsWith(ProcessEngine.ModelPrefix, StringComparison.Ordinal);

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        var names = await engine.StepNamesAsync(task, ct);
        return new TaskSubject("process", names, "/tasks?task=" + task.Id, null, null);
    }

    public Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) => Task.FromResult(true);

    public Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default) => Task.CompletedTask;
}
