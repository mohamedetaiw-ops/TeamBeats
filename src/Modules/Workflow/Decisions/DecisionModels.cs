using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Workflow.Feel;
using TeamBeats.Modules.Workflow.Graph;
using TeamBeats.Localization;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow.Decisions;

public sealed record DecisionModelRequest(string? DmnXml, Guid? OwnerOrgUnitId);

public sealed record DecisionSummary(string Key, Guid? OwnerOrgUnitId, IReadOnlyDictionary<string, string> Names, int Problems, int? LatestVersion, DateTimeOffset UpdatedAt);

public sealed record DecisionVersionView(int Version, DateTimeOffset PublishedAt, Guid? PublishedBy);

public sealed record DecisionModelView(string Key, Guid? OwnerOrgUnitId, IReadOnlyDictionary<string, string> Names, string DmnXml, IReadOnlyList<ProblemView> Problems,
    DateTimeOffset UpdatedAt, IReadOnlyList<DecisionVersionView> Versions);

public sealed record DecisionTestRequest(IReadOnlyList<JsonElement>? Rows);

/// <summary>The result of one test row: the output and matched rule numbers, or the reason it could not be decided.</summary>
public sealed record DecisionTestResult(JsonNode? Result, IReadOnlyList<int> Rules, string? Error);

/// <summary>
/// Decision tables of spec 024 FR-007: a draft per key that decision owners (<c>decision.design</c> at the owner unit)
/// edit, test with input rows before publishing, and publish as immutable versions. Business rule tasks evaluate the
/// current version when they are reached. Needs the <c>workflow.bpmn</c> capability.
/// </summary>
public sealed class DecisionModels(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, IAuditWriter audit,
    IClock clock, ILocalizer localizer, RequestLocale locale)
{
    public const int MaxTestRows = 100;

    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private async Task<bool> MayDesignAsync(Guid? unit, CancellationToken ct) => (await authz.CheckAsync(Permissions.DecisionDesign, Target(unit), ct)).Allowed;

    private sealed record Row(string Key, Guid? Owner, string Names, string Xml, string Problems, DateTimeOffset UpdatedAt);

    private Task<Row?> RowAsync(string key, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select key, owner_org_unit_id, names::text, dmn_xml, problems::text, updated_at from workflow.decision_model where key = @key{(forUpdate ? " for update" : "")}",
            new { key }, r => new Row(r.GetString(0), r.Get<Guid?>("owner_org_unit_id"), r.GetString(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5)), ct);

    private static Dictionary<string, string> Names(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    public async Task<IReadOnlyList<DecisionSummary>> ListAsync(CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var rows = await session.QueryAsync(
            """
            select m.key, m.owner_org_unit_id, m.names::text, jsonb_array_length(m.problems), m.updated_at,
              (select max(version) from workflow.decision_definition d where d.key = m.key)
            from workflow.decision_model m order by m.key
            """, null, r => new DecisionSummary(r.GetString(0), r.Get<Guid?>("owner_org_unit_id"), Names(r.GetString(2)), r.GetInt32(3), r.Get<int?>("max"),
                r.GetFieldValue<DateTimeOffset>(4)), ct);
        var result = new List<DecisionSummary>();
        foreach (var row in rows)
        {
            if (await MayDesignAsync(row.OwnerOrgUnitId, ct) || (await authz.CheckAsync(Permissions.WorkflowDesign, Target(row.OwnerOrgUnitId), ct)).Allowed)
            {
                result.Add(row);
            }
        }
        return result;
    }

    public async Task<DecisionModelView> GetAsync(string key, CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var row = await RowAsync(key, false, ct) ?? throw AppException.NotFound();
        if (!await MayDesignAsync(row.Owner, ct) && !(await authz.CheckAsync(Permissions.WorkflowDesign, Target(row.Owner), ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(row, ct);
    }

    private async Task<DecisionModelView> ViewAsync(Row row, CancellationToken ct)
    {
        var versions = await session.QueryAsync(
            "select version, published_at, published_by from workflow.decision_definition where key = @key order by version desc",
            new { key = row.Key }, r => new DecisionVersionView(r.GetInt32(0), r.GetFieldValue<DateTimeOffset>(1), r.Get<Guid?>("published_by")), ct);
        return new DecisionModelView(row.Key, row.Owner, Names(row.Names), row.Xml,
            (JsonSerializer.Deserialize<List<ModelProblem>>(row.Problems, ProcessGraph.Json) ?? []).Select(p => new ProblemView(p.ElementId, p.Key, localizer.Get(p.Key, locale.Locale))).ToList(),
            row.UpdatedAt, versions);
    }

    public async Task<DecisionModelView> SaveAsync(string key, DecisionModelRequest request, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        if (!BpmnReader.IsValidKey(key) || key.Contains(':'))
        {
            throw AppException.Validation("key", "validation.decision.key");
        }
        if (string.IsNullOrWhiteSpace(request.DmnXml))
        {
            throw AppException.Validation("dmnXml", "validation.decision.xml");
        }
        if (request.DmnXml.Length > DecisionTable.MaxBytes)
        {
            throw AppException.Validation("dmnXml", "validation.decision.too_large");
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
        var (table, problems) = DecisionTable.Read(key, request.DmnXml);
        var names = table?.Names ?? (existing is null ? new Dictionary<string, string>() : Names(existing.Names));
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into workflow.decision_model (tenant_id, key, owner_org_unit_id, names, dmn_xml, problems, created_at, created_by, updated_at, updated_by)
            values (platform.current_tenant_id(), @key, @owner, @names, @xml, @problems, @now, @user, @now, @user)
            on conflict (tenant_id, key) do update set owner_org_unit_id = excluded.owner_org_unit_id, names = excluded.names, dmn_xml = excluded.dmn_xml,
              problems = excluded.problems, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { key, owner = request.OwnerOrgUnitId, names = Jsonb.From(names), xml = request.DmnXml, problems = new Jsonb(JsonSerializer.Serialize(problems, ProcessGraph.Json)),
                now, user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DecisionModelSaved, "decision_model", key, TargetOrgUnitId: request.OwnerOrgUnitId,
            Changes: new { key, owner = request.OwnerOrgUnitId, problems = problems.Count }), ct);
        return await ViewAsync((await RowAsync(key, false, ct))!, ct);
    }

    /// <summary>Publishes the draft as the next version; refused with one error per element while it has problems. The same table again keeps its version.</summary>
    public async Task<DecisionModelView> PublishAsync(string key, CancellationToken ct = default)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        var row = await RowAsync(key, true, ct) ?? throw AppException.NotFound();
        if (!await MayDesignAsync(row.Owner, ct))
        {
            throw AppException.Denied();
        }
        var (table, problems) = DecisionTable.Read(key, row.Xml);
        if (problems.Count > 0)
        {
            throw AppException.Validation(problems.GroupBy(p => p.ElementId.Length == 0 ? "dmnXml" : "elements." + p.ElementId)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Key).Distinct().ToArray()));
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':decision:' || @key, 0))", new { key }, ct);
        var latest = await session.QuerySingleOrDefaultAsync(
            "select version, compiled::text from workflow.decision_definition where key = @key order by version desc limit 1",
            new { key }, r => (Version: r.GetInt32(0), Compiled: r.GetString(1)), ct);
        var compiled = table!.ToJson();
        if (latest.Compiled is null || DecisionTable.FromJson(latest.Compiled).ToJson() != compiled)
        {
            var version = latest.Version + 1;
            await session.ExecAsync(
                """
                insert into workflow.decision_definition (tenant_id, key, version, names, owner_org_unit_id, dmn_xml, compiled, published_at, published_by)
                values (platform.current_tenant_id(), @key, @version, @names, @owner, @xml, @compiled, @now, @by)
                """, new { key, version, names = Jsonb.From(table.Names), owner = row.Owner, xml = row.Xml, compiled = new Jsonb(compiled), now = clock.UtcNow,
                    by = context.ActorId }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.DecisionPublished, "decision_definition", $"{key}:{version}", TargetOrgUnitId: row.Owner,
                Changes: new { key, version, rules = table.Rules.Count, hitPolicy = table.HitPolicy }), ct);
        }
        return await ViewAsync((await RowAsync(key, false, ct))!, ct);
    }

    /// <summary>Evaluates input rows against the draft (FR-007 test before publish). Each row is a JSON object of variables.</summary>
    public async Task<IReadOnlyList<DecisionTestResult>> TestAsync(string key, DecisionTestRequest request, CancellationToken ct = default)
    {
        var view = await GetAsync(key, ct);
        var rows = request.Rows ?? [];
        if (rows.Count is 0 or > MaxTestRows || rows.Any(r => r.ValueKind != JsonValueKind.Object))
        {
            throw AppException.Validation("rows", "validation.decision.test_rows");
        }
        var (table, problems) = DecisionTable.Read(key, view.DmnXml);
        if (table is null)
        {
            throw AppException.Validation(problems.GroupBy(p => p.ElementId.Length == 0 ? "dmnXml" : "elements." + p.ElementId)
                .ToDictionary(g => g.Key, g => g.Select(p => p.Key).Distinct().ToArray()));
        }
        var results = new List<DecisionTestResult>();
        foreach (var row in rows)
        {
            try
            {
                var variables = FeelEngine.Variables(JsonNode.Parse(row.GetRawText()) as JsonObject);
                var (result, rules) = table.Evaluate(variables, clock.UtcNow);
                results.Add(new DecisionTestResult(result, rules, null));
            }
            catch (DecisionException ex)
            {
                results.Add(new DecisionTestResult(null, [], ex.Code));
            }
            catch (FeelEvaluationException ex)
            {
                results.Add(new DecisionTestResult(null, [], ex.Code));
            }
        }
        return results;
    }
}
