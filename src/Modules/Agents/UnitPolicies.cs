using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// An org unit's agent policy (spec 027 US8 scenario 3, R2): only the rules it sets, each of which may only be as strict
/// as the policy above it or stricter. A null value inherits. The tenant policy holds the rules a unit cannot set at all
/// (external AI clients, approvers, scheduled risk classes).
/// </summary>
public sealed record UnitAgentPolicy(
    string? MaxAutonomy = null,
    string? MaxClassification = null,
    List<string>? AllowedTools = null,
    Dictionary<string, string>? ToolRules = null,
    bool? ScheduledAgents = null,
    bool? PersistentMemory = null,
    int? MaxToolCalls = null,
    int? MaxSteps = null,
    int? MaxRunSeconds = null,
    int? MaxDailyRunsPerAgent = null);

public sealed record UnitAgentPolicyView(Guid UnitId, int Version, UnitAgentPolicy Policy, AgentPolicy Inherited, AgentPolicy Effective, DateTimeOffset? UpdatedAt);

public sealed record UnitAgentPolicyRequest(int? Version, UnitAgentPolicy? Policy);

public static class UnitAgentPolicies
{
    /// <summary>The parent policy with a unit's rules applied; every rule takes the stricter of the two.</summary>
    public static AgentPolicy Narrow(AgentPolicy parent, UnitAgentPolicy unit)
    {
        var rules = new Dictionary<string, string>(parent.ToolRules ?? [], StringComparer.Ordinal);
        foreach (var (tool, rule) in unit.ToolRules ?? [])
        {
            if (!rules.TryGetValue(tool, out var current) || ToolApprovalRules.Strictness(rule) > ToolApprovalRules.Strictness(current))
            {
                rules[tool] = rule;
            }
        }
        List<string>? allowed = parent.AllowedTools;
        if (unit.AllowedTools is { } own)
        {
            allowed = (parent.AllowedTools is null ? own : own.Intersect(parent.AllowedTools, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        }
        static string Lower(string a, string? b, Func<string, int> rank) => b is not null && rank(b) >= 0 && rank(b) < rank(a) ? b : a;
        static int Min(int a, int? b) => b is { } v && v < a ? v : a;
        return parent with
        {
            MaxAutonomy = Lower(parent.MaxAutonomy, unit.MaxAutonomy, AgentAutonomy.Rank),
            MaxClassification = Lower(parent.MaxClassification, unit.MaxClassification, Classifications.Rank),
            AllowedTools = allowed,
            ToolRules = new Dictionary<string, string>(rules.OrderBy(k => k.Key, StringComparer.Ordinal)),
            ScheduledAgents = parent.ScheduledAgents && unit.ScheduledAgents != false,
            PersistentMemory = parent.PersistentMemory && unit.PersistentMemory != false,
            MaxToolCalls = Min(parent.MaxToolCalls, unit.MaxToolCalls),
            MaxSteps = Min(parent.MaxSteps, unit.MaxSteps),
            MaxRunSeconds = Min(parent.MaxRunSeconds, unit.MaxRunSeconds),
            MaxDailyRunsPerAgent = Min(parent.MaxDailyRunsPerAgent, unit.MaxDailyRunsPerAgent),
        };
    }

    /// <summary>
    /// Checks a unit's rules against the policy above it. A rule looser than the parent is refused on its field with the
    /// parent named: <c>validation.agent_policy.tenant_limit</c> when the tenant policy sets the limit, and
    /// <c>validation.agent_policy.unit_limit</c> when a parent unit's policy does.
    /// </summary>
    public static UnitAgentPolicy Validate(UnitAgentPolicy p, AgentPolicy tenant, AgentPolicy parent, ToolRegistry tools)
    {
        var errors = new ValidationErrors();
        void Above(string field, bool tenantSets) => errors.Add(field, tenantSets ? "validation.agent_policy.tenant_limit" : "validation.agent_policy.unit_limit");

        if (p.MaxAutonomy is { } autonomy)
        {
            if (AgentAutonomy.Rank(autonomy) < 0)
            {
                errors.Add("maxAutonomy", "validation.agent_policy.autonomy");
            }
            else if (AgentAutonomy.Rank(autonomy) > AgentAutonomy.Rank(parent.MaxAutonomy))
            {
                Above("maxAutonomy", tenant.MaxAutonomy == parent.MaxAutonomy);
            }
        }
        if (p.MaxClassification is { } classification)
        {
            if (Classifications.Rank(classification) < 0)
            {
                errors.Add("maxClassification", "validation.agent_policy.classification");
            }
            else if (Classifications.Rank(classification) > Classifications.Rank(parent.MaxClassification))
            {
                Above("maxClassification", tenant.MaxClassification == parent.MaxClassification);
            }
        }
        if (p.AllowedTools is { } allowed)
        {
            if (allowed.Count > 200 || allowed.Any(t => tools.Find(t) is null))
            {
                errors.Add("allowedTools", "validation.agent_policy.tools");
            }
            foreach (var tool in allowed.Where(t => tools.Find(t) is not null && !parent.Allows(t)).Distinct(StringComparer.Ordinal))
            {
                Above($"allowedTools.{tool}", !tenant.Allows(tool));
            }
        }
        foreach (var (name, rule) in p.ToolRules ?? [])
        {
            if (tools.Find(name) is not { } tool || !ToolApprovalRules.Ordered.Contains(rule))
            {
                errors.Add($"toolRules.{name}", "validation.agent_policy.rule");
                continue;
            }
            var inherited = parent.RuleFor(tool.Definition);
            if (ToolApprovalRules.Strictness(rule) < ToolApprovalRules.Strictness(inherited))
            {
                Above($"toolRules.{name}", tenant.RuleFor(tool.Definition) == inherited);
            }
        }
        if (p.ScheduledAgents == true && !parent.ScheduledAgents)
        {
            Above("scheduledAgents", !tenant.ScheduledAgents);
        }
        if (p.PersistentMemory == true && !parent.PersistentMemory)
        {
            Above("persistentMemory", !tenant.PersistentMemory);
        }
        void Limit(string field, int? value, int parentValue, int tenantValue, int min)
        {
            if (value is not { } v)
            {
                return;
            }
            if (v < min)
            {
                errors.Add(field, $"validation.agent_policy.{Snake(field)}");
            }
            else if (v > parentValue)
            {
                Above(field, tenantValue == parentValue);
            }
        }
        Limit("maxToolCalls", p.MaxToolCalls, parent.MaxToolCalls, tenant.MaxToolCalls, 1);
        Limit("maxSteps", p.MaxSteps, parent.MaxSteps, tenant.MaxSteps, 1);
        Limit("maxRunSeconds", p.MaxRunSeconds, parent.MaxRunSeconds, tenant.MaxRunSeconds, AgentPolicy.MinRunSeconds);
        Limit("maxDailyRunsPerAgent", p.MaxDailyRunsPerAgent, parent.MaxDailyRunsPerAgent, tenant.MaxDailyRunsPerAgent, 1);
        if (p.MaxSteps is { } steps && steps < (p.MaxToolCalls ?? parent.MaxToolCalls) && steps >= 1)
        {
            errors.Add("maxSteps", "validation.agent_policy.max_steps");
        }
        errors.ThrowIfAny();
        return p with
        {
            AllowedTools = p.AllowedTools?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            ToolRules = p.ToolRules is null ? null : new Dictionary<string, string>(p.ToolRules.OrderBy(k => k.Key, StringComparer.Ordinal)),
        };
    }

    private static string Snake(string field) => field switch
    {
        "maxToolCalls" => "max_tool_calls",
        "maxSteps" => "max_steps",
        "maxRunSeconds" => "max_run_seconds",
        _ => "max_daily_runs",
    };
}

/// <summary>
/// Org unit agent policies (spec 027 US8 scenario 3, FR-018): kept per unit, read and changed by whoever holds
/// <c>ai.policy_manage</c> at that unit, and applied to every agent the unit or a unit below it owns. The policy for an
/// agent is the tenant policy narrowed by each unit policy from the top of the hierarchy down to the agent's unit.
/// </summary>
public sealed class UnitAgentPolicyService(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IAuditWriter audit, IClock clock, IOrganizationHierarchy hierarchy, AgentPolicyService policies, ToolRegistry tools)
{
    private readonly Dictionary<Guid, AgentPolicy> _effective = [];

    private async Task<Dictionary<Guid, (int Version, UnitAgentPolicy Policy, DateTimeOffset UpdatedAt)>> RowsAsync(IReadOnlyCollection<Guid> units, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            "select org_unit_id, version, policy::text, updated_at from agents.unit_agent_policy where org_unit_id = any(@units)",
            new { units = units.ToArray() },
            r => (Unit: r.GetGuid(0), Version: r.GetInt32(1), Policy: JsonSerializer.Deserialize<UnitAgentPolicy>(r.GetString(2), AgentPolicyService.Json) ?? new UnitAgentPolicy(),
                At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        return rows.ToDictionary(r => r.Unit, r => (r.Version, r.Policy, r.At));
    }

    /// <summary>The policy in force for agents owned by <paramref name="unitId"/>; no permission check.</summary>
    public async Task<AgentPolicy> ForUnitAsync(Guid unitId, CancellationToken ct)
    {
        if (_effective.TryGetValue(unitId, out var cached))
        {
            return cached;
        }
        var (_, effective) = await ChainAsync(unitId, ct);
        return _effective[unitId] = effective;
    }

    /// <summary>The policy above a unit (tenant narrowed by its ancestors) and the policy for the unit itself.</summary>
    private async Task<(AgentPolicy Inherited, AgentPolicy Effective)> ChainAsync(Guid unitId, CancellationToken ct)
    {
        var tenant = (await policies.CurrentAsync(ct)).Policy;
        var chain = await hierarchy.GetAncestorsAndSelfAsync(unitId, ct);
        var rows = await RowsAsync(chain.ToList(), ct);
        var inherited = tenant;
        foreach (var unit in chain.Where(u => u != unitId))
        {
            if (rows.TryGetValue(unit, out var row))
            {
                inherited = UnitAgentPolicies.Narrow(inherited, row.Policy);
            }
        }
        var effective = rows.TryGetValue(unitId, out var own) ? UnitAgentPolicies.Narrow(inherited, own.Policy) : inherited;
        return (inherited, effective);
    }

    private async Task DemandAsync(Guid unitId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        context.RequireUser();
        if (!await hierarchy.UnitExistsAsync(unitId, ct))
        {
            throw AppException.NotFound();
        }
        // Not found rather than forbidden, so a unit outside the administrator's scope is not confirmed (spec 004).
        if (!(await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.OrgUnit(unitId), ct)).Allowed)
        {
            throw AppException.NotFound();
        }
    }

    public async Task<UnitAgentPolicyView> GetAsync(Guid unitId, CancellationToken ct)
    {
        await DemandAsync(unitId, ct);
        var rows = await RowsAsync([unitId], ct);
        var (inherited, effective) = await ChainAsync(unitId, ct);
        return rows.TryGetValue(unitId, out var row)
            ? new UnitAgentPolicyView(unitId, row.Version, row.Policy, inherited, effective, row.UpdatedAt)
            : new UnitAgentPolicyView(unitId, 0, new UnitAgentPolicy(), inherited, effective, null);
    }

    public async Task<UnitAgentPolicyView> ChangeAsync(Guid unitId, UnitAgentPolicyRequest request, CancellationToken ct)
    {
        await DemandAsync(unitId, ct);
        var by = context.RequireUser();
        if (request.Policy is not { } policy)
        {
            throw AppException.Validation("policy", "validation.agent_policy.required");
        }
        var current = await RowsAsync([unitId], ct);
        var version = current.TryGetValue(unitId, out var row) ? row.Version : 0;
        if (request.Version != version)
        {
            throw AppException.Conflict("conflict.agent_policy.version");
        }
        var tenant = (await policies.CurrentAsync(ct)).Policy;
        var (inherited, _) = await ChainAsync(unitId, ct);
        policy = UnitAgentPolicies.Validate(policy, tenant, inherited, tools);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.unit_agent_policy (tenant_id, org_unit_id, version, policy, updated_at, updated_by)
            values (platform.current_tenant_id(), @unitId, @version, @policy::jsonb, @now, @by)
            on conflict (tenant_id, org_unit_id) do update set version = excluded.version, policy = excluded.policy, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { unitId, version = version + 1, policy = JsonSerializer.Serialize(policy, AgentPolicyService.Json), now, by }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentPolicyChanged, "agent_unit_policy", unitId.ToString(), TargetOrgUnitId: unitId,
            Changes: new { version = version + 1, from = row.Policy, to = policy }), ct);
        _effective.Clear();
        return new UnitAgentPolicyView(unitId, version + 1, policy, inherited, UnitAgentPolicies.Narrow(inherited, policy), now);
    }

    /// <summary>Drops the unit's own rules, so its agents follow the policy above it again.</summary>
    public async Task DeleteAsync(Guid unitId, CancellationToken ct)
    {
        await DemandAsync(unitId, ct);
        var deleted = await session.ExecAsync("delete from agents.unit_agent_policy where org_unit_id = @unitId", new { unitId }, ct);
        if (deleted > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AgentPolicyChanged, "agent_unit_policy", unitId.ToString(), TargetOrgUnitId: unitId,
                Changes: new { removed = true }), ct);
        }
        _effective.Clear();
    }
}
