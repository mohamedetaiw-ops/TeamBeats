using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>Used until spec 034 is wired in: reports <c>not_built</c> without blocking, and the review page says so.</summary>
public sealed class NoReleaseGate : IAgentReleaseGate
{
    public Task RequestAsync(Guid agentId, int version, CancellationToken ct) => Task.CompletedTask;

    public Task<AgentGateResult> ResultAsync(Guid agentId, int version, CancellationToken ct) => Task.FromResult(new AgentGateResult(GateStatuses.NotBuilt, false, []));
}

public sealed record SubmitRequest(int? Revision);

public sealed record DecisionRequest(int? Version, string? Reason);

public sealed record RollbackRequest(int? Version);

public sealed record ReasonRequest(string? Reason);

public sealed record KillSwitchRequest(bool? Active, string? Confirmation, string? Reason);

public sealed record KillSwitchView(bool Active, string? Reason, DateTimeOffset? ChangedAt);

public sealed record FieldChange(string Field, JsonElement? From, JsonElement? To);

/// <summary>The review page (020 US2 scenario 1): the version, what changed since the published one, risk class and gate result.</summary>
public sealed record AgentReview(AgentSummary Agent, AgentVersionView Version, AgentDefinition Definition, int? PublishedVersion, IReadOnlyList<FieldChange> Changes,
    AgentGateResult Gate, bool CanApprove, string? CannotApproveReason);

/// <summary>
/// The agent lifecycle (spec 020 T009, T011; spec 027 T010, T011): submit for review with an inbox task for publishers,
/// review against the published version, approve and publish with separation of duties, reject, roll back with a
/// policy re-check, suspend and resume, retire, the tenant kill switch, and suspending published agents that break a
/// new agent policy.
/// </summary>
public sealed class AgentLifecycle(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IAuditWriter audit, IClock clock, ToolRegistry tools, AgentPolicyService policies, AgentRegistry registry, IAgentReleaseGate gate,
    ITenantSettingsReader tenant, INotificationSender notifications)
{
    public const int MaxReason = 500;

    /// <summary>The notice an owner gets when a policy change suspends their agent (027 FR-018).</summary>
    public const string AutoSuspendedTemplate = "agents.auto_suspended";
    private static readonly JsonSerializerOptions Json = AgentPolicyService.Json;

    internal static AgentDefinition Definition(string json) => JsonSerializer.Deserialize<AgentDefinition>(json, Json)!;

    private async Task<Guid> UserAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        return context.RequireUser();
    }

    private async Task<string?> VersionDefinitionAsync(Guid id, int number, CancellationToken ct) =>
        await session.ScalarAsync<string>("select definition::text from agents.agent_version where agent_id = @id and number = @number", new { id, number }, ct);

    private async Task<(string State, Guid LastEditor, string Risk)?> VersionRowAsync(Guid id, int number, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select state, last_editor, risk_class from agents.agent_version where agent_id = @id and number = @number",
            new { id, number }, r => (r.GetString(0), r.GetGuid(1), r.GetString(2)), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    private async Task DemandOnAgentAsync(string permission, AgentDetail agent, CancellationToken ct) =>
        await authz.DemandAsync(permission, AuthzTarget.OrgUnit(agent.Agent.OrgUnitId), ct);

    /// <summary>Submits the draft for review (US1): checked again, locked, and offered to publishers in their inbox.</summary>
    public async Task<AgentDetail> SubmitAsync(Guid id, SubmitRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        if (!agent.CanEdit)
        {
            throw AppException.Denied();
        }
        if (agent.Latest.State != AgentVersionStates.Draft)
        {
            throw AppException.Conflict("conflict.agent.not_draft");
        }
        if (request.Revision != agent.Latest.Revision)
        {
            throw AppException.Conflict("conflict.agent.revision");
        }
        AgentDefinitions.Validate(agent.Definition, tools, (await policies.CurrentAsync(ct)).Policy);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update agents.agent_version set state = 'in_review', submitted_by = @user, submitted_at = @now, updated_at = @now
            where agent_id = @id and number = @number and state = 'draft';
            update agents.agent set status = case when current_version is null then 'in_review' else status end, updated_at = @now where id = @id;
            """, new { id, number = agent.Latest.Number, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentVersionSubmitted, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId,
            Changes: new { version = agent.Latest.Number, riskClass = agent.Latest.RiskClass }), ct);
        // The release gate of spec 034 starts on the submitted version at once (020 US5 scenario 3).
        await gate.RequestAsync(id, agent.Latest.Number, ct);
        return await registry.GetAsync(id, ct);
    }

    /// <summary>The version in review with its changes against the published version (US2 scenario 1).</summary>
    public async Task<AgentReview> ReviewAsync(Guid id, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        var canPublish = (await authz.CheckAsync(Permissions.AgentPublish, AuthzTarget.OrgUnit(agent.Agent.OrgUnitId), ct)).Allowed;
        if (!canPublish && !agent.CanEdit)
        {
            throw AppException.Denied();
        }
        var version = agent.Versions.FirstOrDefault(v => v.State == AgentVersionStates.InReview) ?? throw AppException.NotFound();
        var definition = Definition((await VersionDefinitionAsync(id, version.Number, ct))!);
        var published = agent.Agent.CurrentVersion is { } current ? Definition((await VersionDefinitionAsync(id, current, ct))!) : null;
        var result = await gate.ResultAsync(id, version.Number, ct);
        var reason = !canPublish ? "agent.publish_permission"
            : RequiresOtherApprover(version.RiskClass) && version.LastEditor == user ? "agent.last_editor"
            : result.Blocking ? GateReason(result) : null;
        return new AgentReview(agent.Agent, version, definition, agent.Agent.CurrentVersion, Diff(published, definition), result, reason is null, reason);
    }

    /// <summary>Why the gate keeps a version from being published, as the review page and the publish refusal say it.</summary>
    internal static string GateReason(AgentGateResult result) => result.Status switch
    {
        GateStatuses.Running => "agent.gate_running",
        GateStatuses.NotRun or GateStatuses.Stale or GateStatuses.Cancelled => "agent.gate_not_run",
        _ => "agent.gate_failed",
    };

    internal static bool RequiresOtherApprover(string risk) => risk is AgentRiskClasses.Medium or AgentRiskClasses.High;

    internal static List<FieldChange> Diff(AgentDefinition? before, AgentDefinition after)
    {
        var from = before is null ? default : JsonSerializer.SerializeToElement(before, Json);
        var to = JsonSerializer.SerializeToElement(after, Json);
        var changes = new List<FieldChange>();
        foreach (var field in to.EnumerateObject())
        {
            JsonElement? old = before is not null && from.TryGetProperty(field.Name, out var o) ? o : null;
            if (old is null || !JsonElement.DeepEquals(old.Value, field.Value))
            {
                changes.Add(new FieldChange(field.Name, old, field.Value));
            }
        }
        return changes;
    }

    /// <summary>
    /// Approves and publishes a version in review (US2): needs <c>agent.publish</c> on the agent's unit, a different
    /// person than the last editor for Medium and High risk, a gate result that does not block, and the current policy.
    /// New runs use it at once; runs waiting on a card finish on their own version.
    /// </summary>
    public async Task<AgentDetail> PublishAsync(Guid id, DecisionRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        await DemandOnAgentAsync(Permissions.AgentPublish, agent, ct);
        if (request.Version is not { } number || await VersionRowAsync(id, number, ct) is not { } version || version.State != AgentVersionStates.InReview)
        {
            throw AppException.Conflict("conflict.agent.not_in_review");
        }
        if (agent.Agent.Status is AgentStatuses.Retired)
        {
            throw AppException.Conflict("conflict.agent.unavailable");
        }
        if (RequiresOtherApprover(version.Risk) && version.LastEditor == user)
        {
            throw AppException.Conflict("conflict.agent.last_editor");
        }
        var result = await gate.ResultAsync(id, number, ct);
        if (result.Blocking)
        {
            throw AppException.Conflict(result.Status switch
            {
                GateStatuses.Running => "conflict.agent.gate_running",
                GateStatuses.NotRun or GateStatuses.Stale or GateStatuses.Cancelled => "conflict.agent.gate_not_run",
                _ => "conflict.agent.gate_failed",
            });
        }
        var definition = Definition((await VersionDefinitionAsync(id, number, ct))!);
        AgentDefinitions.Validate(definition, tools, (await policies.CurrentAsync(ct)).Policy);
        var risk = AgentDefinitions.RiskClass(definition, tools);
        if (agent.Agent.CurrentVersion is null)
        {
            await DemandPublishedLimitAsync(ct);
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update agents.agent_version set state = 'published', approved_by = @user, published_at = @now, risk_class = @risk, updated_at = @now
            where agent_id = @id and number = @number;
            update agents.agent set status = case when status = 'suspended' then 'suspended' else 'published' end, current_version = @number,
              risk_class = @risk, updated_at = @now where id = @id;
            """, new { id, number, user, risk, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentVersionApproved, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId,
            Changes: new { version = number, riskClass = risk, gate = result.Status }), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentVersionPublished, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId,
            Changes: new { version = number, from = agent.Agent.CurrentVersion, riskClass = risk }), ct);
        return await registry.GetAsync(id, ct);
    }

    /// <summary>
    /// The package limit on published agents (020 FR-019, <c>ai.agents.max_published</c>): an agent counts from its first
    /// publication until it is retired, so a new version, a rollback or a resume never needs a free place.
    /// </summary>
    private async Task DemandPublishedLimitAsync(CancellationToken ct)
    {
        if (await entitlements.GetLimitAsync(Capabilities.AiAgentsMaxPublished, ct) is not { } max)
        {
            return;
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('agents.published:' || @t::text))", new { t = context.RequireTenant() }, ct);
        var count = await session.ScalarAsync<long>("select count(*) from agents.agent where current_version is not null and status <> 'retired'", null, ct);
        if (count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.agent.published_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.AiAgentsMaxPublished, ["limit"] = max });
        }
    }

    /// <summary>Rejects the version in review with a reason; the designer can edit it again as the next draft.</summary>
    public async Task<AgentDetail> RejectAsync(Guid id, DecisionRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        await DemandOnAgentAsync(Permissions.AgentPublish, agent, ct);
        var reason = Reason(request.Reason, required: true);
        if (request.Version is not { } number || await VersionRowAsync(id, number, ct) is not { State: AgentVersionStates.InReview })
        {
            throw AppException.Conflict("conflict.agent.not_in_review");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update agents.agent_version set state = 'rejected', rejected_by = @user, rejection_reason = @reason, updated_at = @now where agent_id = @id and number = @number;
            update agents.agent set status = case when status = 'in_review' then 'draft' else status end, updated_at = @now where id = @id;
            """, new { id, number, user, reason, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentVersionRejected, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId,
            Changes: new { version = number, reason }), ct);
        return await registry.GetAsync(id, ct);
    }

    /// <summary>
    /// Makes an earlier published version current again (US3 scenario 1, 027 US7 scenario 5): checked against the current
    /// policy, without a new review and without waiting for evaluation, so it stays an emergency control.
    /// </summary>
    public async Task<AgentDetail> RollbackAsync(Guid id, RollbackRequest request, CancellationToken ct)
    {
        await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        await DemandOnAgentAsync(Permissions.AgentPublish, agent, ct);
        if (request.Version is not { } number || number == agent.Agent.CurrentVersion ||
            agent.Versions.FirstOrDefault(v => v.Number == number) is not { PublishedAt: not null })
        {
            throw AppException.Validation("version", "validation.agent.rollback_version");
        }
        var definition = Definition((await VersionDefinitionAsync(id, number, ct))!);
        try
        {
            AgentDefinitions.Validate(definition, tools, (await policies.CurrentAsync(ct)).Policy);
        }
        catch (AppException e) when (e.Errors is not null)
        {
            // The older version breaks today's policy: rollback is blocked with the reason.
            var errors = new Dictionary<string, string[]> { ["version"] = ["validation.agent.rollback_policy"] };
            foreach (var (field, keys) in e.Errors.Where(x => x.Key != "version"))
            {
                errors[field] = keys;
            }
            throw AppException.Validation(errors);
        }
        var risk = AgentDefinitions.RiskClass(definition, tools);
        var now = clock.UtcNow;
        await session.ExecAsync("update agents.agent set current_version = @number, risk_class = @risk, updated_at = @now where id = @id",
            new { id, number, risk, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentRolledBack, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId,
            Changes: new { from = agent.Agent.CurrentVersion, to = number }), ct);
        // Rollback never waits for evaluation; when the restored version has no current gate result, the gate runs right after (020 FR-009).
        if ((await gate.ResultAsync(id, number, ct)).Status is GateStatuses.NotRun or GateStatuses.Stale or GateStatuses.Cancelled)
        {
            await gate.RequestAsync(id, number, ct);
        }
        return await registry.GetAsync(id, ct);
    }

    /// <summary>Suspends an agent (US3 scenario 2, 027 US7 scenario 1): new runs refused, waiting actions cancelled.</summary>
    public async Task<AgentDetail> SuspendAsync(Guid id, ReasonRequest request, CancellationToken ct)
    {
        await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        await DemandOnAgentAsync(Permissions.AgentSuspend, agent, ct);
        var reason = Reason(request.Reason, required: true);
        if (agent.Agent.Status is AgentStatuses.Suspended or AgentStatuses.Retired)
        {
            throw AppException.Conflict("conflict.agent.unavailable");
        }
        await SuspendRowAsync(id, agent.Agent.OrgUnitId, reason, ct);
        return await registry.GetAsync(id, ct);
    }

    private async Task SuspendRowAsync(Guid id, Guid unit, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("update agents.agent set status = 'suspended', suspended_reason = @reason, updated_at = @now where id = @id", new { id, reason, now }, ct);
        await StopWaitingAsync("agent_id = @id", new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentSuspended, "agent", id.ToString(), TargetOrgUnitId: unit, Changes: new { reason }), ct);
    }

    /// <summary>Waiting runs stop and their pending actions are cancelled.</summary>
    private async Task StopWaitingAsync(string where, object args, CancellationToken ct) =>
        await session.ExecAsync(
            $"""
            delete from agents.run_checkpoint c using agents.run r where r.id = c.run_id and r.status = 'waiting' and r.{where};
            update agents.run set status = 'stopped', outcome = 'suspended', reason = 'agent.suspended', finished_at = @now, updated_at = @now
            where status in ('waiting', 'running') and {where};
            """, args, ct);

    public async Task<AgentDetail> ResumeAsync(Guid id, CancellationToken ct)
    {
        await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        await DemandOnAgentAsync(Permissions.AgentSuspend, agent, ct);
        if (agent.Agent.Status != AgentStatuses.Suspended)
        {
            throw AppException.Conflict("conflict.agent.not_suspended");
        }
        if (agent.Agent.CurrentVersion is { } current)
        {
            // A version that breaks today's policy cannot come back.
            AgentDefinitions.Validate(Definition((await VersionDefinitionAsync(id, current, ct))!), tools, (await policies.CurrentAsync(ct)).Policy);
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update agents.agent set status = case when current_version is null then 'draft' else 'published' end, suspended_reason = null, updated_at = @now where id = @id
            """, new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentResumed, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId), ct);
        return await registry.GetAsync(id, ct);
    }

    /// <summary>Retires an agent (US3 scenario 3): never usable again; its versions, records and audit stay.</summary>
    public async Task<AgentDetail> RetireAsync(Guid id, ReasonRequest request, CancellationToken ct)
    {
        await UserAsync(ct);
        var agent = await registry.GetAsync(id, ct);
        await DemandOnAgentAsync(Permissions.AgentPublish, agent, ct);
        var reason = Reason(request.Reason, required: false);
        if (agent.Agent.Status == AgentStatuses.Retired)
        {
            throw AppException.Conflict("conflict.agent.unavailable");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update agents.agent set status = 'retired', updated_at = @now where id = @id", new { id, now }, ct);
        await StopWaitingAsync("agent_id = @id", new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentRetired, "agent", id.ToString(), TargetOrgUnitId: agent.Agent.OrgUnitId, Changes: new { reason }), ct);
        return await registry.GetAsync(id, ct);
    }

    private static string Reason(string? reason, bool required)
    {
        var text = reason?.Trim() ?? string.Empty;
        if ((required && text.Length == 0) || text.Length > MaxReason)
        {
            throw AppException.Validation("reason", "validation.agent.reason");
        }
        return text;
    }

    public async Task<KillSwitchView> KillSwitchAsync(CancellationToken ct)
    {
        await UserAsync(ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.AgentSuspend, ct) && !await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct))
        {
            throw AppException.Denied();
        }
        var rows = await session.QueryAsync("select active, reason, changed_at from agents.kill_switch", null,
            r => new KillSwitchView(r.GetBoolean(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)), ct);
        return rows.Count == 0 ? new KillSwitchView(false, null, null) : rows[0];
    }

    /// <summary>
    /// The tenant kill switch (027 US7 scenario 2): every agent in the tenant stops at its next step and waiting actions
    /// are cancelled, while the rest of the intranet keeps working. Turning it on needs the tenant name typed and a reason.
    /// </summary>
    public async Task<KillSwitchView> ChangeKillSwitchAsync(KillSwitchRequest request, CancellationToken ct)
    {
        var user = await UserAsync(ct);
        await authz.DemandAsync(Permissions.AgentSuspend, AuthzTarget.Tenant, ct);
        if (request.Active is not { } active)
        {
            throw AppException.Validation("active", "validation.agent.kill_switch");
        }
        var reason = Reason(request.Reason, required: active);
        if (active)
        {
            var name = (await tenant.GetBrandingAsync(ct)).DisplayName;
            if (string.IsNullOrWhiteSpace(request.Confirmation) || !string.Equals(request.Confirmation.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw AppException.Validation("confirmation", "validation.agent.kill_switch_confirmation");
            }
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.kill_switch (tenant_id, active, reason, changed_by, changed_at) values (platform.current_tenant_id(), @active, @reason, @user, @now)
            on conflict (tenant_id) do update set active = excluded.active, reason = excluded.reason, changed_by = excluded.changed_by, changed_at = excluded.changed_at
            """, new { active, reason, user, now }, ct);
        if (active)
        {
            await StopWaitingAsync("tenant_id = platform.current_tenant_id()", new { now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentKillSwitch, "agent_kill_switch", null, Changes: new { active, reason }), ct);
        return new KillSwitchView(active, reason, now);
    }

    /// <summary>
    /// After a policy change (027 FR-018): published agents whose version in use breaks the new policy are suspended,
    /// with the policy as the reason. Returns the agents suspended.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> SuspendBreakingAsync(AgentPolicy policy, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select a.id, a.org_unit_id, v.definition::text, a.owner_user_id from agents.agent a
            join agents.agent_version v on v.agent_id = a.id and v.number = a.current_version
            where a.status = 'published'
            """, null, r => (Id: r.GetGuid(0), Unit: r.GetGuid(1), Definition: r.GetString(2), Owner: r.GetGuid(3)), ct);
        var suspended = new List<Guid>();
        foreach (var row in rows)
        {
            try
            {
                AgentDefinitions.Validate(Definition(row.Definition), tools, policy);
            }
            catch (AppException)
            {
                await SuspendRowAsync(row.Id, row.Unit, "policy_changed", ct);
                await notifications.SendAsync(new NotificationRequest([row.Owner], NotificationCategories.System, AutoSuspendedTemplate,
                    $"/admin/agents/{row.Id}", DedupeKey: $"agents.auto_suspended:{row.Id:N}"), ct);
                suspended.Add(row.Id);
            }
        }
        return suspended;
    }
}

/// <summary>
/// Versions waiting for review, as inbox tasks for the people who may approve them (020 US1). Kept apart from the
/// lifecycle so the inbox does not depend on the tool registry, whose tools read the inbox.
/// </summary>
public sealed class AgentReviewTasks(IDbSession session, IEntitlementService entitlements, IAuthorizationService authz) : IPersonalTaskSource
{
    public const string TaskSource = "agent_review";

    public static string ReviewLink(Guid agentId) => $"/admin/agents/{agentId}/review";

    /// <summary>Versions waiting for review that the person may approve, as inbox tasks (US1, plan "submit with inbox task").</summary>
    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAgentsBasic, ct))
        {
            return [];
        }
        var coverage = await authz.CoverageAsync(Permissions.AgentPublish, ct);
        if (!coverage.Any)
        {
            return [];
        }
        var rows = await session.QueryAsync(
            """
            select a.id, a.org_unit_id, v.definition::text, v.risk_class, v.last_editor, coalesce(v.submitted_at, v.updated_at)
            from agents.agent a join agents.agent_version v on v.agent_id = a.id and v.state = 'in_review'
            where a.status <> 'retired' and (@all or a.org_unit_id = any(@units))
            order by 6 limit 50
            """, new { all = coverage.WholeTenant, units = coverage.Units.ToArray() },
            r => (Id: r.GetGuid(0), Definition: r.GetString(2), Risk: r.GetString(3), Editor: r.GetGuid(4), At: r.GetFieldValue<DateTimeOffset>(5)), ct);
        return rows.Where(r => !(AgentLifecycle.RequiresOtherApprover(r.Risk) && r.Editor == userId))
            .Select(r => new PersonalTask(TaskSource, r.Id, (IReadOnlyDictionary<string, string>?)AgentLifecycle.Definition(r.Definition).Name ?? new Dictionary<string, string>(), ReviewLink(r.Id),
                r.At, null))
            .ToList();
    }
}
