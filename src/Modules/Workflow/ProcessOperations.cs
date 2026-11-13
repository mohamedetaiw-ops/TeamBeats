using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

public sealed record InstanceSearch(string? Process, int? Version, string? State, string? BusinessKey, Guid? StartedBy, Guid? OrgUnitId, DateTimeOffset? From,
    DateTimeOffset? To, bool? WithIncident, int? Limit);

public sealed record InstanceSummary(Guid Id, string Process, int Version, string BusinessKey, Guid? OrgUnitId, Guid? StartedBy, string Status, string? Outcome,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, int OpenIncidents);

public sealed record ActiveElement(string ElementId, string Name, string Status);

public sealed record InstanceDetail(InstanceSummary Instance, JsonObject Variables, IReadOnlyList<ActiveElement> Active, IReadOnlyList<UserTaskInfo> Tasks,
    IReadOnlyList<IncidentInfo> Incidents, IReadOnlyList<HistoryEntry> History);

public sealed record OperatorReason(string? Reason);

public sealed record VariablesChange(JsonElement? Variables, string? Reason);

public sealed record IncidentResolution(string? Resolution, string? Reason);

public sealed record ElementMonitoring(string ElementId, string Name, int Active, int Incidents, int Finished, double? AverageSeconds, double? P90Seconds);

public sealed record ProcessMonitoring(string Process, int Version, DateTimeOffset From, DateTimeOffset To, int Active, int Completed, int Cancelled, int WithIncidents,
    double? AverageSeconds, double? P90Seconds, IReadOnlyList<ElementMonitoring> Elements, IReadOnlyList<string> Slowest);

/// <summary>
/// The operator view and monitoring of spec 024 (FR-013, FR-014, US5) for process model instances. Operators
/// (<c>process.operate</c> at the instance's unit) search instances, read their variables and history, change variables
/// on an incident, resolve incidents and cancel instances, each with a reason and an audit event. Analysts
/// (<c>process.analyze</c> at the model's owner unit) see monitoring numbers only, which carry no variables or people.
/// Approval routes of requests are operated from their requests (spec 016), not here.
/// </summary>
public sealed class ProcessOperations(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, IClock clock,
    ProcessEngine engine)
{
    public const int MaxResults = 100;
    public const int MaxScan = 1000;
    public const int ReasonMax = 500;
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaxPeriod = TimeSpan.FromDays(366);

    private static readonly HashSet<string> States = new(StringComparer.Ordinal) { "active", "completed", "cancelled" };

    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private async Task<bool> MayOperateAsync(Guid? unit, Dictionary<Guid, bool>? cache, CancellationToken ct)
    {
        var key = unit ?? Guid.Empty;
        if (cache is not null && cache.TryGetValue(key, out var known))
        {
            return known;
        }
        var allowed = (await authz.CheckAsync(Permissions.ProcessOperate, Target(unit), ct)).Allowed;
        cache?.Add(key, allowed);
        return allowed;
    }

    private const string SummaryColumns =
        """
        i.id, i.definition_key, i.definition_version, i.business_key, i.org_unit_id, i.started_by, i.status, i.outcome, i.started_at, i.ended_at,
        (select count(*)::int from workflow.incident n where n.instance_id = i.id and n.status = 'open') as open_incidents
        """;

    private static InstanceSummary ReadSummary(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1)[ProcessEngine.ModelPrefix.Length..], r.GetInt32(2),
        r.GetString(3), r.Get<Guid?>("org_unit_id"), r.Get<Guid?>("started_by"), r.GetString(6), r.Get<string?>("outcome"), r.GetFieldValue<DateTimeOffset>(8),
        r.Get<DateTimeOffset?>("ended_at"), r.GetInt32(10));

    private async Task DemandAsync(CancellationToken ct)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
    }

    /// <summary>Instances of process models matching the search, newest first, only those at units where the caller operates (US5 scenario 1).</summary>
    public async Task<IReadOnlyList<InstanceSummary>> SearchAsync(InstanceSearch search, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var errors = new ValidationErrors();
        if (search.State is not null && !States.Contains(search.State))
        {
            errors.Add("state", "validation.process.instance_state");
        }
        if (search.Limit is < 1 or > MaxResults)
        {
            errors.Add("limit", "validation.process.limit");
        }
        if (search.From > search.To)
        {
            errors.Add("from", "validation.process.period");
        }
        errors.ThrowIfAny();
        var rows = await session.QueryAsync(
            $"""
            select {SummaryColumns} from workflow.process_instance i
            where i.definition_key like 'process:%'
              and (@process::text is null or i.definition_key = 'process:' || @process)
              and (@version::int is null or i.definition_version = @version)
              and (@state::text is null or i.status = @state)
              and (@businessKey::text is null or i.business_key = @businessKey)
              and (@startedBy::uuid is null or i.started_by = @startedBy)
              and (@unit::uuid is null or i.org_unit_id = @unit)
              and (@from::timestamptz is null or i.started_at >= @from)
              and (@to::timestamptz is null or i.started_at < @to)
              and (not @withIncident or exists (select 1 from workflow.incident n where n.instance_id = i.id and n.status = 'open'))
            order by i.started_at desc, i.id
            limit {MaxScan}
            """, new
            {
                process = search.Process, version = search.Version, state = search.State, businessKey = search.BusinessKey, startedBy = search.StartedBy,
                unit = search.OrgUnitId, from = search.From, to = search.To, withIncident = search.WithIncident ?? false,
            }, ReadSummary, ct);
        var cache = new Dictionary<Guid, bool>();
        var result = new List<InstanceSummary>();
        foreach (var row in rows)
        {
            if (await MayOperateAsync(row.OrgUnitId, cache, ct))
            {
                result.Add(row);
                if (result.Count == (search.Limit ?? 50))
                {
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>The instance when the caller operates at its unit; otherwise, or for a route instance, not found.</summary>
    private async Task<InstanceSummary> InstanceAsync(Guid instanceId, CancellationToken ct)
    {
        await DemandAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync($"select {SummaryColumns} from workflow.process_instance i where i.id = @instanceId and i.definition_key like 'process:%'",
            new { instanceId }, ReadSummary, ct);
        if (row is null || !await MayOperateAsync(row.OrgUnitId, null, ct))
        {
            throw AppException.NotFound();
        }
        return row;
    }

    public async Task<InstanceDetail> GetAsync(Guid instanceId, CancellationToken ct = default)
    {
        var summary = await InstanceAsync(instanceId, ct);
        var row = await session.QuerySingleOrDefaultAsync("select variables::text, state::text from workflow.process_instance where id = @instanceId",
            new { instanceId }, r => (Variables: r.GetString(0), State: r.Get<string?>("state")), ct);
        var graph = await engine.GraphAsync(ProcessEngine.ModelPrefix + summary.Process, summary.Version, ct);
        var active = new List<ActiveElement>();
        if (row.State is not null)
        {
            var state = JsonSerializer.Deserialize<Graph.InstanceState>(row.State, Graph.ProcessGraph.Json)!;
            foreach (var token in state.Tokens.Where(t => t.Status != "listening"))
            {
                var name = graph is not null && graph.Has(token.ElementId) ? graph.Element(token.ElementId).Name(Locale) : token.ElementId;
                active.Add(new ActiveElement(token.ElementId, name, token.Status));
            }
        }
        return new InstanceDetail(summary, JsonNode.Parse(row.Variables) as JsonObject ?? [], active, await engine.InstanceTasksAsync(instanceId, ct),
            await engine.IncidentsAsync(instanceId, ct), await engine.HistoryAsync(instanceId, ct));
    }

    private string Locale => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

    private static string Reason(string? reason)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > ReasonMax)
        {
            throw AppException.Validation("reason", "validation.workflow.reason");
        }
        return reason;
    }

    public async Task<InstanceDetail> ChangeVariablesAsync(Guid instanceId, VariablesChange request, CancellationToken ct = default)
    {
        await InstanceAsync(instanceId, ct);
        var reason = Reason(request.Reason);
        if (request.Variables is not { ValueKind: JsonValueKind.Object } v || JsonNode.Parse(v.GetRawText()) is not JsonObject changes || changes.Count == 0
            || changes.Any(p => p.Key.Length is 0 or > 100))
        {
            throw AppException.Validation("variables", "validation.workflow.variables");
        }
        await engine.ChangeVariablesAsync(instanceId, changes, reason, ct);
        return await GetAsync(instanceId, ct);
    }

    public async Task<InstanceDetail> CancelAsync(Guid instanceId, OperatorReason request, CancellationToken ct = default)
    {
        await InstanceAsync(instanceId, ct);
        await engine.CancelByOperatorAsync(instanceId, Reason(request.Reason), ct);
        return await GetAsync(instanceId, ct);
    }

    /// <summary>Retries, skips or cancels at an incident (US5 scenario 2); a second operator on a resolved incident is refused (scenario 5).</summary>
    public async Task<IncidentInfo> ResolveIncidentAsync(Guid incidentId, IncidentResolution request, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var instanceId = await session.QuerySingleOrDefaultAsync("select instance_id from workflow.incident where id = @incidentId", new { incidentId },
            r => (Guid?)r.GetGuid(0), ct) ?? throw AppException.NotFound();
        await InstanceAsync(instanceId, ct);
        return await engine.ResolveIncidentAsync(incidentId, request.Resolution ?? "", request.Reason, ct);
    }

    /// <summary>
    /// Monitoring of one version (US5 scenarios 3 and 4): instance counts, per element the tokens waiting now, open incidents
    /// and how long finished user and service tasks took in the period, and the slowest elements by 90th percentile.
    /// </summary>
    public async Task<ProcessMonitoring> MonitoringAsync(string key, int? version, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        await DemandAsync(ct);
        var model = await session.QuerySingleOrDefaultAsync("select owner_org_unit_id from workflow.process_model where key = @key", new { key },
            r => (Found: true, Owner: r.Get<Guid?>("owner_org_unit_id")), ct);
        if (!model.Found)
        {
            throw AppException.NotFound();
        }
        var target = Target(model.Owner);
        if (!(await authz.CheckAsync(Permissions.ProcessAnalyze, target, ct)).Allowed && !(await authz.CheckAsync(Permissions.ProcessOperate, target, ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        var definition = ProcessEngine.ModelPrefix + key;
        var v = version ?? await session.QuerySingleOrDefaultAsync("select max(version) from workflow.process_definition where key = @definition",
            new { definition }, r => r.IsDBNull(0) ? (int?)null : r.GetInt32(0), ct) ?? throw AppException.NotFound();
        var graph = await engine.GraphAsync(definition, v, ct) ?? throw AppException.NotFound();
        var end = to ?? clock.UtcNow;
        var start = from ?? end - DefaultPeriod;
        if (start > end || end - start > MaxPeriod)
        {
            throw AppException.Validation("from", "validation.process.period");
        }
        var args = new { definition, v, start, end };
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where status = 'active')::int,
              count(*) filter (where status = 'completed' and ended_at >= @start and ended_at < @end)::int,
              count(*) filter (where status = 'cancelled' and ended_at >= @start and ended_at < @end)::int,
              count(*) filter (where status = 'active' and exists (select 1 from workflow.incident n where n.instance_id = i.id and n.status = 'open'))::int,
              avg(extract(epoch from ended_at - started_at)) filter (where status = 'completed' and ended_at >= @start and ended_at < @end),
              percentile_cont(0.9) within group (order by extract(epoch from ended_at - started_at)) filter (where status = 'completed' and ended_at >= @start and ended_at < @end)
            from workflow.process_instance i where definition_key = @definition and definition_version = @v
            """, args, r => (Active: r.GetInt32(0), Completed: r.GetInt32(1), Cancelled: r.GetInt32(2), Incidents: r.GetInt32(3),
                Avg: r.IsDBNull(4) ? (double?)null : Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture),
                P90: r.IsDBNull(5) ? (double?)null : r.GetDouble(5)), ct);
        var waiting = (await session.QueryAsync(
            """
            select t->>'elementId', count(*)::int from workflow.process_instance i, jsonb_array_elements(i.state->'tokens') t
            where i.definition_key = @definition and i.definition_version = @v and i.status = 'active' and coalesce(t->>'status', 'waiting') <> 'listening'
            group by 1
            """, args, r => (Element: r.GetString(0), Count: r.GetInt32(1)), ct)).ToDictionary(x => x.Element, x => x.Count);
        var incidents = (await session.QueryAsync(
            """
            select n.element_id, count(*)::int from workflow.incident n join workflow.process_instance i on i.id = n.instance_id
            where i.definition_key = @definition and i.definition_version = @v and n.status = 'open' group by 1
            """, args, r => (Element: r.GetString(0), Count: r.GetInt32(1)), ct)).ToDictionary(x => x.Element, x => x.Count);
        var durations = (await session.QueryAsync(
            """
            select element_id, count(*)::int, avg(seconds), percentile_cont(0.9) within group (order by seconds) from (
              select u.element_id, extract(epoch from u.completed_at - u.created_at)::float8 as seconds
              from workflow.user_task u join workflow.process_instance i on i.id = u.instance_id
              where i.definition_key = @definition and i.definition_version = @v and u.status = 'completed' and u.completed_at >= @start and u.completed_at < @end
              union all
              select s.element_id, extract(epoch from s.ended_at - s.started_at)::float8
              from workflow.service_task s join workflow.process_instance i on i.id = s.instance_id
              where i.definition_key = @definition and i.definition_version = @v and s.status = 'succeeded' and s.ended_at >= @start and s.ended_at < @end
            ) d group by element_id
            """, args, r => (Element: r.GetString(0), Count: r.GetInt32(1), Avg: r.GetDouble(2), P90: r.GetDouble(3)), ct)).ToDictionary(x => x.Element);
        var elements = graph.Elements
            .Where(e => waiting.ContainsKey(e.Id) || incidents.ContainsKey(e.Id) || durations.ContainsKey(e.Id) || e.Type is Graph.ElementTypes.UserTask or Graph.ElementTypes.ServiceTask or Graph.ElementTypes.BusinessRuleTask)
            .Select(e => new ElementMonitoring(e.Id, e.Name(Locale), waiting.GetValueOrDefault(e.Id), incidents.GetValueOrDefault(e.Id),
                durations.TryGetValue(e.Id, out var d) ? d.Count : 0, durations.TryGetValue(e.Id, out var a) ? Math.Round(a.Avg, 1) : null,
                durations.TryGetValue(e.Id, out var p) ? Math.Round(p.P90, 1) : null))
            .ToList();
        var slowest = elements.Where(e => e.P90Seconds is not null).OrderByDescending(e => e.P90Seconds).ThenBy(e => e.ElementId, StringComparer.Ordinal)
            .Take(5).Select(e => e.ElementId).ToList();
        return new ProcessMonitoring(key, v, start, end, counts.Active, counts.Completed, counts.Cancelled, counts.Incidents,
            counts.Avg is { } avg ? Math.Round(avg, 1) : null, counts.P90 is { } p90 ? Math.Round(p90, 1) : null, elements, slowest);
    }
}
