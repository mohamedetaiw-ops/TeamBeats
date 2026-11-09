using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record SettingsView(int Version, IReadOnlyList<SuiteMetric> Thresholds, IReadOnlyList<EffectiveRule> Platform, DateTimeOffset? UpdatedAt, bool CanEdit = false);

public sealed record SettingsRequest(int? Version, List<SuiteMetric>? Thresholds);

public sealed record PlatformSuiteView(string Key, bool Mandatory, string? Template, int Version, Dictionary<string, string> Names, IReadOnlyList<SuiteMetric> Metrics,
    int CaseCount, IReadOnlyList<string> Locales);

public sealed record DatasetSummary(Guid Id, Dictionary<string, string> Names, int Version, int CaseCount, string Classification, IReadOnlyList<string> Locales, DateTimeOffset UpdatedAt);

public sealed record AgentSuiteView(Guid? Id, int Version, IReadOnlyList<SuiteMetric> Metrics, IReadOnlyList<Guid> Datasets, DateTimeOffset? UpdatedAt);

/// <summary>
/// An agent's suites (US1 scenario 2): the inherited platform suites (read-only), its own suite, its datasets, and the
/// effective rule of every metric with the level each part comes from.
/// </summary>
public sealed record AgentSuitesView(Guid AgentId, IReadOnlyList<PlatformSuiteView> Inherited, AgentSuiteView Suite, IReadOnlyList<DatasetSummary> Datasets,
    IReadOnlyList<EffectiveRule> Rules, bool CanEdit, bool WeightsAllowed);

public sealed record SuiteRequest(int? Version, List<SuiteMetric>? Metrics, List<Guid>? Datasets);

/// <summary>Who may do what with an agent's evaluation (section 3): designers edit, viewers and owners read.</summary>
public sealed class EvalAccess(IAgentCatalog agents, IAuthorizationService authz, ITenantContext context)
{
    public async Task<AgentFacts> ReadableAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await agents.GetAsync(agentId, ct) ?? throw AppException.NotFound();
        return await MayReadAsync(agent, ct) ? agent : throw AppException.NotFound();
    }

    public async Task<bool> MayReadAsync(AgentFacts agent, CancellationToken ct) =>
        agent.OwnerUserId == context.RequireUser() || await CanDesignAsync(agent, ct)
        || (await authz.CheckAsync(Permissions.EvalView, AuthzTarget.OrgUnit(agent.OrgUnitId), ct)).Allowed;

    public async Task<AgentFacts> DesignableAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await ReadableAgentAsync(agentId, ct);
        if (!await CanDesignAsync(agent, ct))
        {
            throw AppException.Denied();
        }
        return agent;
    }

    public async Task<bool> CanDesignAsync(AgentFacts agent, CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.EvalDesign, AuthzTarget.OrgUnit(agent.OrgUnitId), ct)).Allowed;

    /// <summary>The assistant serves the whole tenant, so its runs are started by the AI Policy Administrator (spec 014 T008).</summary>
    public async Task<bool> CanRunAssistantAsync(CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct)).Allowed;

    /// <summary>Assistant runs are read with <c>eval.view</c> across the tenant, or by whoever may start them.</summary>
    public async Task<bool> CanReadAssistantAsync(CancellationToken ct) =>
        await CanRunAssistantAsync(ct) || (await authz.CheckAsync(Permissions.EvalView, AuthzTarget.Tenant, ct)).Allowed;
}

/// <summary>Tenant evaluation settings (US10 scenario 1 and 2): thresholds only stricter than the platform's.</summary>
public sealed class EvalSettingsService(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuditWriter audit, IClock clock)
{
    public async Task<IReadOnlyDictionary<string, LevelRule>> TenantRulesAsync(CancellationToken ct) =>
        ThresholdRules.Rules((await LoadAsync(ct)).Thresholds);

    private async Task<SettingsView> LoadAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync("select version, thresholds::text, updated_at from evaluation.settings", null,
            r => (Version: r.GetInt32(0), Thresholds: r.GetString(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        var platform = ThresholdRules.Effective(MetricCatalog.All.Select(m => m.Key), [(EvalLevels.Platform, ThresholdRules.PlatformRules)])
            .Where(r => r.Threshold is not null || r.Severity is not null).ToList();
        return rows.Count == 0
            ? new SettingsView(0, [], platform, null)
            : new SettingsView(rows[0].Version, JsonSerializer.Deserialize<List<SuiteMetric>>(rows[0].Thresholds, EvalJson.Options)!, platform, rows[0].At);
    }

    public async Task<SettingsView> GetAsync(CancellationToken ct)
    {
        var manage = (await authz.CheckAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct)).Allowed;
        if (!manage && !(await authz.CheckAsync(Permissions.EvalView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        return await LoadAsync(ct) with { CanEdit = manage };
    }

    public async Task<SettingsView> SaveAsync(SettingsRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct);
        var current = await LoadAsync(ct);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.eval.settings_version");
        }
        var thresholds = request.Thresholds ?? [];
        var errors = ThresholdRules.Check(thresholds, EvalLevels.Tenant, [(EvalLevels.Platform, ThresholdRules.PlatformRules)], weightsAllowed: true, field: "thresholds");
        if (thresholds.Any(t => t is not null && t.Threshold is null && t.Severity is null))
        {
            errors.Add("thresholds", "validation.eval.threshold");
        }
        errors.ThrowIfAny();
        var normalized = thresholds.Select(t => t with { Weight = 1 }).ToList();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into evaluation.settings (tenant_id, version, thresholds, updated_by, updated_at)
            values (platform.current_tenant_id(), @version, @thresholds, @user, @now)
            on conflict (tenant_id) do update set version = excluded.version, thresholds = excluded.thresholds, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { version = current.Version + 1, thresholds = Jsonb.From(normalized), user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalPolicyChanged, "eval_settings", null,
            Changes: new { level = EvalLevels.Tenant, from = current.Thresholds, to = normalized }), ct);
        return await LoadAsync(ct) with { CanEdit = true };
    }
}

/// <summary>
/// Agent suites (US1): every agent inherits the mandatory safety suites and its template's suite read-only, and owns one
/// suite of its own that designers edit. Each save is a new version; thresholds may only be stricter than the tenant's
/// and the platform's.
/// </summary>
public sealed class SuiteService(IDbSession session, ITenantContext context, EvalAccess access, IEntitlementService entitlements, EvalSettingsService settings,
    IAuditWriter audit, IClock clock)
{
    public async Task<AgentSuitesView> GetAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await access.ReadableAgentAsync(agentId, ct);
        return await ViewAsync(agent, ct);
    }

    private async Task<AgentSuitesView> ViewAsync(AgentFacts agent, CancellationToken ct)
    {
        var inherited = PlatformSuites.For(agent.TemplateId);
        var suite = await CurrentAsync(agent.Id, ct);
        var datasets = await DatasetService.SummariesAsync(session, agent.Id, ct);
        var tenant = await settings.TenantRulesAsync(ct);
        var keys = inherited.SelectMany(s => s.Metrics.Select(m => m.Key)).Concat(suite.Metrics.Select(m => m.Key)).Concat(tenant.Keys).Distinct().ToList();
        var rules = ThresholdRules.Effective(keys,
        [
            (EvalLevels.Platform, ThresholdRules.PlatformRules),
            (EvalLevels.Tenant, tenant),
            (EvalLevels.Agent, ThresholdRules.Rules(suite.Metrics)),
        ]);
        return new AgentSuitesView(agent.Id, inherited.Select(View).ToList(), suite, datasets, rules,
            await access.CanDesignAsync(agent, ct) && agent.Status != "retired", await entitlements.IsEnabledAsync(Capabilities.AiEvalsAdvanced, ct));
    }

    public static PlatformSuiteView View(PlatformSuite s) =>
        new(s.Key, s.Mandatory, s.Template, s.Version, s.Names, s.Metrics, s.Cases.Count, s.Cases.Select(c => c.Locale).Distinct().Order().ToList());

    internal async Task<AgentSuiteView> CurrentAsync(Guid agentId, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select s.id, s.current_version, v.metrics::text, v.datasets, s.updated_at from evaluation.suite s
            join evaluation.suite_version v on v.suite_id = s.id and v.number = s.current_version
            where s.agent_id = @agent
            """, new { agent = agentId },
            r => new AgentSuiteView(r.GetGuid(0), r.GetInt32(1), JsonSerializer.Deserialize<List<SuiteMetric>>(r.GetString(2), EvalJson.Options)!, r.GetFieldValue<Guid[]>(3),
                r.GetFieldValue<DateTimeOffset>(4)), ct);
        return rows.Count == 0 ? new AgentSuiteView(null, 0, [], [], null) : rows[0];
    }

    public async Task<AgentSuiteView> VersionAsync(Guid agentId, int number, CancellationToken ct)
    {
        _ = await access.ReadableAgentAsync(agentId, ct);
        var rows = await session.QueryAsync(
            """
            select s.id, v.number, v.metrics::text, v.datasets, v.created_at from evaluation.suite s
            join evaluation.suite_version v on v.suite_id = s.id and v.number = @number
            where s.agent_id = @agent
            """, new { agent = agentId, number },
            r => new AgentSuiteView(r.GetGuid(0), r.GetInt32(1), JsonSerializer.Deserialize<List<SuiteMetric>>(r.GetString(2), EvalJson.Options)!, r.GetFieldValue<Guid[]>(3),
                r.GetFieldValue<DateTimeOffset>(4)), ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }

    public async Task<AgentSuitesView> SaveAsync(Guid agentId, SuiteRequest request, CancellationToken ct)
    {
        var agent = await access.DesignableAgentAsync(agentId, ct);
        if (agent.Status == "retired")
        {
            throw AppException.Conflict("conflict.eval.agent_retired");
        }
        var current = await CurrentAsync(agentId, ct);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.eval.suite_version");
        }
        var advanced = await entitlements.IsEnabledAsync(Capabilities.AiEvalsAdvanced, ct);
        var tenant = await settings.TenantRulesAsync(ct);
        var errors = ThresholdRules.Check(request.Metrics, EvalLevels.Agent,
            [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, tenant)], weightsAllowed: advanced);
        var datasets = (request.Datasets ?? []).Distinct().ToArray();
        var own = (await DatasetService.SummariesAsync(session, agentId, ct)).Select(d => d.Id).ToHashSet();
        if (datasets.Length > 20 || datasets.Any(d => !own.Contains(d)))
        {
            errors.Add("datasets", "validation.eval.suite_datasets");
        }
        errors.ThrowIfAny();

        var metrics = request.Metrics!;
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var id = current.Id ?? Ids.New();
        var number = current.Version + 1;
        if (current.Id is null)
        {
            await session.ExecAsync(
                """
                insert into evaluation.suite (tenant_id, id, capability, agent_id, org_unit_id, current_version, created_by, created_at, updated_at)
                values (platform.current_tenant_id(), @id, 'agent', @agent, @unit, @number, @user, @now, @now)
                """, new { id, agent = agentId, unit = agent.OrgUnitId, number, user, now }, ct);
        }
        else
        {
            await session.ExecAsync("update evaluation.suite set current_version = @number, updated_at = @now where id = @id", new { id, number, now }, ct);
        }
        await session.ExecAsync(
            """
            insert into evaluation.suite_version (tenant_id, suite_id, number, metrics, datasets, created_by, created_at)
            values (platform.current_tenant_id(), @id, @number, @metrics, @datasets, @user, @now)
            """, new { id, number, metrics = Jsonb.From(metrics), datasets, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(current.Id is null ? AuditActions.EvalSuiteCreated : AuditActions.EvalSuiteChanged, "eval_suite", id.ToString(),
            TargetOrgUnitId: agent.OrgUnitId, Changes: new { agentId, version = number, capability = "agent", from = current.Metrics, to = metrics, datasets }), ct);
        return await ViewAsync(agent, ct);
    }
}
