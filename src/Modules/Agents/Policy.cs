using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// The tenant agent policy (spec 027 US8 scenario 1, FR-018), R1 settings: the autonomy cap, the tools agents may hold
/// (null means every registered tool), stricter approval rules per tool, the highest classification agents may use,
/// scheduled agents and persistent memory (both off until R2 builds them) and run limits (tool calls, steps, duration of
/// one run and runs per agent per day, FR-015). Who may design and publish is
/// set with roles (spec 004); the external AI client settings come with spec 035.
/// </summary>
public sealed record AgentPolicy(
    string MaxAutonomy,
    string MaxClassification,
    List<string>? AllowedTools,
    Dictionary<string, string>? ToolRules,
    bool ScheduledAgents,
    bool PersistentMemory,
    int MaxToolCalls,
    int MaxSteps,
    int MaxRunSeconds = AgentPolicy.DefaultRunSeconds,
    int MaxDailyRunsPerAgent = AgentPolicy.DefaultDailyRuns)
{
    /// <summary>The platform default, used until the tenant saves a policy.</summary>
    public static readonly AgentPolicy Default = new(AgentAutonomy.HighestBuilt, "internal", null, [], false, false, 20, 40);

    public const int PlatformMaxToolCalls = 50;
    public const int PlatformMaxSteps = 100;

    /// <summary>Interactive runs stop after five minutes of work (027 FR-015); L4 runs with longer limits come in R2.</summary>
    public const int DefaultRunSeconds = 300;
    public const int PlatformMaxRunSeconds = 300;
    public const int MinRunSeconds = 30;

    /// <summary>Runs each agent may start in a UTC day. The spec names no figure; this default is recorded in the R1 notes.</summary>
    public const int DefaultDailyRuns = 1000;
    public const int PlatformMaxDailyRuns = 100000;

    public bool Allows(string tool) => AllowedTools is null || AllowedTools.Contains(tool);

    /// <summary>The rule that applies to a tool: the tenant's when it is stricter than the tool's default, never looser.</summary>
    public string RuleFor(ToolDefinition tool) =>
        ToolRules?.GetValueOrDefault(tool.Name) is { } rule && ToolApprovalRules.Strictness(rule) > ToolApprovalRules.Strictness(tool.ApprovalRule)
            ? rule
            : tool.ApprovalRule;
}

public sealed record AgentPolicyView(int Version, AgentPolicy Policy, DateTimeOffset? UpdatedAt);

public sealed record AgentPolicyRequest(int? Version, AgentPolicy? Policy);

/// <summary>
/// Reads and changes the tenant agent policy (spec 027 T011). Anyone who designs, publishes or oversees agents may read
/// it, since the editor shows its limits; only the AI Policy Administrator changes it, with optimistic versioning.
/// </summary>
public sealed class AgentPolicyService(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IAuditWriter audit, IClock clock, ToolRegistry tools)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The policy in force, for the registry and the runtime; no permission check.</summary>
    public async Task<AgentPolicyView> CurrentAsync(CancellationToken ct)
    {
        // Every use of the registry follows a policy read, so the tenant's own tools are loaded here (spec 027 FR-006).
        await tools.LoadAsync(ct);
        var rows = await session.QueryAsync("select version, policy::text, updated_at from agents.agent_policy", null,
            r => new AgentPolicyView(r.GetInt32(0), JsonSerializer.Deserialize<AgentPolicy>(r.GetString(1), Json) ?? AgentPolicy.Default,
                r.GetFieldValue<DateTimeOffset>(2)), ct);
        return rows.Count == 0 ? new AgentPolicyView(0, AgentPolicy.Default, null) : rows[0];
    }

    public async Task<AgentPolicyView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct) && !await authz.HoldsAnywhereAsync(Permissions.AgentDesign, ct) &&
            !await authz.HoldsAnywhereAsync(Permissions.AgentView, ct))
        {
            throw AppException.Denied();
        }
        return await CurrentAsync(ct);
    }

    public async Task<AgentPolicyView> ChangeAsync(AgentPolicyRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        var by = context.RequireUser();
        if (request.Policy is not { } policy)
        {
            throw AppException.Validation("policy", "validation.agent_policy.required");
        }
        var current = await CurrentAsync(ct);
        policy = Validate(policy, tools);
        if (request.Version != current.Version)
        {
            throw AppException.Conflict("conflict.agent_policy.version");
        }
        var now = clock.UtcNow;
        var version = current.Version + 1;
        await session.ExecAsync(
            """
            insert into agents.agent_policy (tenant_id, version, policy, updated_at, updated_by)
            values (platform.current_tenant_id(), @version, @policy::jsonb, @now, @by)
            on conflict (tenant_id) do update set version = excluded.version, policy = excluded.policy, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, new { version, policy = JsonSerializer.Serialize(policy, Json), now, by }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentPolicyChanged, "agent_policy", version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Changes: new { from = current.Policy, to = policy }), ct);
        return new AgentPolicyView(version, policy, now);
    }

    /// <summary>Checks a policy and returns it normalized (sorted, without duplicates).</summary>
    public static AgentPolicy Validate(AgentPolicy p, ToolRegistry tools)
    {
        var errors = new ValidationErrors();
        var autonomy = AgentAutonomy.Rank(p.MaxAutonomy);
        if (autonomy < 0 || autonomy > AgentAutonomy.Rank(AgentAutonomy.HighestBuilt))
        {
            errors.Add("maxAutonomy", "validation.agent_policy.autonomy");
        }
        if (Classifications.Rank(p.MaxClassification) < 0)
        {
            errors.Add("maxClassification", "validation.agent_policy.classification");
        }
        if (p.AllowedTools is { } allowed && (allowed.Count > 200 || allowed.Any(t => tools.Find(t) is null)))
        {
            errors.Add("allowedTools", "validation.agent_policy.tools");
        }
        foreach (var (name, rule) in p.ToolRules ?? [])
        {
            if (tools.Find(name) is not { } tool || !ToolApprovalRules.Ordered.Contains(rule) ||
                ToolApprovalRules.Strictness(rule) < ToolApprovalRules.Strictness(tool.Definition.ApprovalRule))
            {
                errors.Add($"toolRules.{name}", "validation.agent_policy.rule");
            }
        }
        if (p.ScheduledAgents || p.PersistentMemory)
        {
            // Scheduled agents and persistent memory come in R2 (spec 020 FR-014, FR-015); they cannot be turned on yet.
            errors.Add(p.ScheduledAgents ? "scheduledAgents" : "persistentMemory", "validation.agent_policy.not_built");
        }
        if (p.MaxToolCalls is < 1 or > AgentPolicy.PlatformMaxToolCalls)
        {
            errors.Add("maxToolCalls", "validation.agent_policy.max_tool_calls");
        }
        if (p.MaxSteps < p.MaxToolCalls || p.MaxSteps > AgentPolicy.PlatformMaxSteps)
        {
            errors.Add("maxSteps", "validation.agent_policy.max_steps");
        }
        if (p.MaxRunSeconds is < AgentPolicy.MinRunSeconds or > AgentPolicy.PlatformMaxRunSeconds)
        {
            errors.Add("maxRunSeconds", "validation.agent_policy.max_run_seconds");
        }
        if (p.MaxDailyRunsPerAgent is < 1 or > AgentPolicy.PlatformMaxDailyRuns)
        {
            errors.Add("maxDailyRunsPerAgent", "validation.agent_policy.max_daily_runs");
        }
        errors.ThrowIfAny();
        return p with
        {
            AllowedTools = p.AllowedTools?.Distinct().Order(StringComparer.Ordinal).ToList(),
            ToolRules = p.ToolRules is null ? [] : new Dictionary<string, string>(p.ToolRules.OrderBy(k => k.Key, StringComparer.Ordinal)),
        };
    }
}
