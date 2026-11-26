using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Modules.Workflow.Graph;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Workflow;

/// <summary>An agent task of a process version, as the approvers see it.</summary>
public sealed record AgentTaskView(string ElementId, IReadOnlyDictionary<string, string> Names, Guid AgentId, IReadOnlyList<string> Tools, int MaxMinutes);

/// <summary>
/// Whether a published version may run its agent tasks (spec 027 FR-024): it needs a process owner and a holder of
/// <c>ai.policy_manage</c>, two different people. <see cref="CanApproveAs"/> lists the roles the caller may still sign.
/// </summary>
public sealed record ProcessAutonomyView(string Key, int Version, IReadOnlyList<AgentTaskView> Tasks, Guid? OwnerBy, DateTimeOffset? OwnerAt, Guid? PolicyBy,
    DateTimeOffset? PolicyAt, bool Approved, IReadOnlyList<string> CanApproveAs);

public sealed record AutonomyApprovalRequest(string? As);

public static class AutonomyRoles
{
    public const string Owner = "owner";
    public const string Policy = "policy";
}

/// <summary>
/// The dual approval of process versions for autonomous agent steps (spec 027 US1 scenario 5, FR-024). A process owner
/// holds <c>workflow.design</c> at the owner unit; the policy approver holds <c>ai.policy_manage</c>. One person cannot
/// sign both. Every version needs both again, so a changed process never runs agents on an earlier approval.
/// </summary>
public sealed class ProcessAutonomy(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, IAuditWriter audit,
    IClock clock, ProcessEngine engine)
{
    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    /// <summary>The latest version, or the one asked for, of a process model; null when it has no agent task.</summary>
    public async Task<ProcessAutonomyView?> GetAsync(string key, int? version, CancellationToken ct = default)
    {
        var (definition, owner, graph) = await LoadAsync(key, version, ct);
        if (graph is null || !graph.UsesAgents)
        {
            return null;
        }
        var roles = await RolesAsync(owner, ct);
        if (roles.Count == 0)
        {
            throw AppException.NotFound();
        }
        return await ViewAsync(definition.Key, definition.Version, graph, roles, ct);
    }

    public async Task<ProcessAutonomyView> ApproveAsync(string key, int version, AutonomyApprovalRequest request, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        var (definition, owner, graph) = await LoadAsync(key, version, ct);
        var roles = await RolesAsync(owner, ct);
        if (roles.Count == 0)
        {
            throw AppException.NotFound();
        }
        if (graph is null || !graph.UsesAgents)
        {
            throw AppException.Conflict("conflict.process.no_agent_tasks");
        }
        var role = request.As;
        if (role is not (AutonomyRoles.Owner or AutonomyRoles.Policy))
        {
            throw AppException.Validation("as", "validation.process.autonomy_role");
        }
        if (!roles.Contains(role))
        {
            throw AppException.Denied();
        }
        await session.ExecAsync(
            """
            insert into workflow.autonomy_approval (tenant_id, definition_key, version) values (platform.current_tenant_id(), @key, @version)
            on conflict do nothing
            """, new { key = definition.Key, version }, ct);
        var row = await session.QuerySingleOrDefaultAsync(
            "select owner_by, policy_by from workflow.autonomy_approval where definition_key = @key and version = @version for update",
            new { key = definition.Key, version }, r => (Owner: r.Get<Guid?>("owner_by"), Policy: r.Get<Guid?>("policy_by")), ct);
        var (mine, other) = role == AutonomyRoles.Owner ? (row.Owner, row.Policy) : (row.Policy, row.Owner);
        if (mine is not null)
        {
            throw AppException.Conflict("conflict.process.autonomy_signed");
        }
        if (other == user)
        {
            // Two different people (FR-024): whoever signed one part cannot sign the other.
            throw AppException.Conflict("conflict.process.autonomy_same_person");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(role == AutonomyRoles.Owner
                ? "update workflow.autonomy_approval set owner_by = @user, owner_at = @now where definition_key = @key and version = @version"
                : "update workflow.autonomy_approval set policy_by = @user, policy_at = @now where definition_key = @key and version = @version",
            new { user, now, key = definition.Key, version }, ct);
        var approved = other is not null;
        await audit.WriteAsync(new AuditEntry(AuditActions.ProcessAutonomyApproved, "process_definition", $"{definition.Key}:{version}", TargetOrgUnitId: owner,
            Changes: new { key, version, role, approved, agents = graph.Elements.Where(e => e.Call?.Agent is not null).Select(e => e.Call!.Agent!.AgentId).Distinct() }), ct);
        return await ViewAsync(definition.Key, version, graph, (await RolesAsync(owner, ct)).Where(r => r != role).ToList(), ct);
    }

    /// <summary>Whether both people approved this version (used by the engine before an agent task runs).</summary>
    internal Task<bool> IsApprovedAsync(string definitionKey, int version, CancellationToken ct) => ApprovedAsync(session, definitionKey, version, ct);

    internal static async Task<bool> ApprovedAsync(IDbSession session, string definitionKey, int version, CancellationToken ct) =>
        await session.ScalarAsync<bool?>(
            "select owner_by is not null and policy_by is not null from workflow.autonomy_approval where definition_key = @definitionKey and version = @version",
            new { definitionKey, version }, ct) == true;

    private async Task<(ProcessDefinitionInfo Definition, Guid? Owner, ProcessGraph? Graph)> LoadAsync(string key, int? version, CancellationToken ct)
    {
        context.RequireUser();
        await entitlements.DemandAsync(Capabilities.WorkflowBpmn, ct);
        await entitlements.DemandAsync(Capabilities.AiAgentsAutonomous, ct);
        var full = ProcessEngine.ModelPrefix + key;
        var definition = await engine.GetDefinitionAsync(full, version, ct) ?? throw AppException.NotFound();
        var owner = await session.ScalarAsync<Guid?>("select owner_org_unit_id from workflow.process_definition where key = @full and version = @v",
            new { full, v = definition.Version }, ct);
        return (definition, owner, await engine.GraphAsync(full, definition.Version, ct));
    }

    /// <summary>The roles the caller holds for this process; empty means they may not see its approval.</summary>
    private async Task<List<string>> RolesAsync(Guid? owner, CancellationToken ct)
    {
        var roles = new List<string>();
        if ((await authz.CheckAsync(Permissions.WorkflowDesign, Target(owner), ct)).Allowed)
        {
            roles.Add(AutonomyRoles.Owner);
        }
        if ((await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed)
        {
            roles.Add(AutonomyRoles.Policy);
        }
        return roles;
    }

    private async Task<ProcessAutonomyView> ViewAsync(string definitionKey, int version, ProcessGraph graph, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select owner_by, owner_at, policy_by, policy_at from workflow.autonomy_approval where definition_key = @definitionKey and version = @version",
            new { definitionKey, version },
            r => (Owner: r.Get<Guid?>("owner_by"), OwnerAt: r.Get<DateTimeOffset?>("owner_at"), Policy: r.Get<Guid?>("policy_by"), PolicyAt: r.Get<DateTimeOffset?>("policy_at")), ct);
        var user = context.RequireUser();
        // A role the caller may still sign: not signed yet, and the other part is not theirs.
        var open = roles.Where(r => r == AutonomyRoles.Owner ? row.Owner is null && row.Policy != user : row.Policy is null && row.Owner != user).ToList();
        var tasks = graph.Elements.Where(e => e.Call?.Agent is not null)
            .Select(e => new AgentTaskView(e.Id, e.Names, e.Call!.Agent!.AgentId, e.Call.Agent.Tools, e.Call.Agent.MaxMinutes)).ToList();
        return new ProcessAutonomyView(definitionKey[ProcessEngine.ModelPrefix.Length..], version, tasks, row.Owner, row.OwnerAt, row.Policy, row.PolicyAt,
            row.Owner is not null && row.Policy is not null, open);
    }
}

/// <summary>Moves an agent task on when its L4 run ends (spec 027 FR-024).</summary>
public sealed class AgentStepFinished(ProcessEngine engine) : IMessageHandler<AgentTaskFinished>
{
    public Task HandleAsync(AgentTaskFinished message, CancellationToken ct) => engine.OnAgentTaskFinishedAsync(message, ct);
}
