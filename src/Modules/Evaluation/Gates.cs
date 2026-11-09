using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record GateOverrideRequest(string? Reason);

/// <summary>
/// The release gate for agent versions (US5, FR-014, FR-017, spec 020 FR-009). Submitting a version starts a gate
/// run of the inherited platform suites and the agent's own suite; the latest run of the version decides. A run that
/// is still going, was cancelled, or tested suites or datasets that have since changed blocks publishing until it is
/// run again. Critical failures never pass. A blocking failure or an incomplete result on a Low or Medium risk version
/// can be overridden, with a written reason, by someone with <c>agent.publish</c> on the agent's unit who did not make
/// the last change; High risk versions are never overridden.
/// </summary>
public sealed class GateService(IDbSession session, ITenantContext context, IAgentCatalog agents, IAuthorizationService authz, RunService runs, IAuditWriter audit,
    EvalAccess access, INotificationSender notifications, IOutbox outbox, IClock clock, ILogger<GateService> logger) : IAgentReleaseGate
{
    public const int MaxFailingCases = 20;
    public const string OverriddenTemplate = "evaluation.gate_overridden";

    public async Task RequestAsync(Guid agentId, int version, CancellationToken ct)
    {
        if (await agents.GetAsync(agentId, ct) is not { } agent || agent.Status is "retired" || await runs.ActiveAsync(agentId, version, ct))
        {
            return;
        }
        try
        {
            await runs.CreateAsync(agent, version, RunTriggers.Gate, ct);
        }
        catch (AppException e) when (e.Status == 400)
        {
            // Nothing to run in the agent's languages: the gate stays not run, which blocks, and the review page says so.
            logger.LogWarning("eval.gate_not_started tenant {Tenant} agent {Agent} version {Version} reason {Reason}", context.TenantId, agentId, version, e.DetailKey);
        }
    }

    private sealed record LatestRun(Guid Id, string Status, string Suites, string? Result);

    private async Task<LatestRun?> LatestAsync(Guid agentId, int version, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            "select id, status, suites::text, result::text from evaluation.run where agent_id = @agentId and agent_version = @version and trigger <> 'route' order by created_at desc, id limit 1",
            new { agentId, version }, r => new LatestRun(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Get<string?>("result")), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<AgentGateResult> ResultAsync(Guid agentId, int version, CancellationToken ct)
    {
        if (await agents.GetAsync(agentId, ct) is not { } agent)
        {
            return new AgentGateResult(GateStatuses.NotRun, true, []);
        }
        var run = await LatestAsync(agentId, version, ct);
        if (run is null)
        {
            return new AgentGateResult(GateStatuses.NotRun, true, []);
        }
        switch (run.Status)
        {
            case RunStatuses.Queued or RunStatuses.Running:
                return new AgentGateResult(GateStatuses.Running, true, [], run.Id);
            case RunStatuses.Cancelled:
                return new AgentGateResult(GateStatuses.Cancelled, true, [], run.Id);
        }
        var tested = RunService.Signature(JsonSerializer.Deserialize<List<RunSuite>>(run.Suites, EvalJson.Options)!);
        if (!tested.SequenceEqual(await runs.SignatureAsync(agent, ct), StringComparer.Ordinal))
        {
            return new AgentGateResult(GateStatuses.Stale, true, [], run.Id);
        }
        var result = JsonSerializer.Deserialize<RunResult>(run.Result!, EvalJson.Options)!;
        var failing = await FailingCasesAsync(run.Id, ct);
        var diff = await DiffAsync(agent, version, result, ct);
        if (result.Verdict == EvalVerdicts.Passed)
        {
            return new AgentGateResult(GateStatuses.Passed, false, failing, run.Id, false, result.CriticalFailures, result.BlockingFailures, result.Incomplete, diff);
        }
        if (result.CriticalFailures.Count > 0)
        {
            return new AgentGateResult(GateStatuses.FailedCritical, true, failing, run.Id, false, result.CriticalFailures, result.BlockingFailures, result.Incomplete, diff);
        }
        var reason = await session.ScalarAsync<string?>("select reason from evaluation.gate_override where run_id = @id", new { id = run.Id }, ct);
        if (reason is not null)
        {
            return new AgentGateResult(GateStatuses.Overridden, false, failing, run.Id, false, result.CriticalFailures, result.BlockingFailures, result.Incomplete, diff, reason);
        }
        var status = result.Verdict == EvalVerdicts.Incomplete ? GateStatuses.Incomplete : GateStatuses.Failed;
        return new AgentGateResult(status, true, failing, run.Id, await MayOverrideAsync(agent, version, ct), result.CriticalFailures, result.BlockingFailures,
            result.Incomplete, diff);
    }

    /// <summary>Whether the signed-in person may override a blocking failure of the version (FR-014).</summary>
    private async Task<bool> MayOverrideAsync(AgentFacts agent, int version, CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || context.ActorId is not { } user || await agents.VersionAsync(agent.Id, version, ct) is not { } facts)
        {
            return false;
        }
        return facts.RiskClass != AgentRiskClasses.High && facts.LastEditor != user
            && (await authz.CheckAsync(Permissions.AgentPublish, AuthzTarget.OrgUnit(agent.OrgUnitId), ct)).Allowed;
    }

    /// <summary>Cases that errored or failed a scored check, as "suite#case:locale", for the review page (US5 scenario 1).</summary>
    private async Task<IReadOnlyList<string>> FailingCasesAsync(Guid runId, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select suite_key, case_n, locale, status, metrics::text from evaluation.case_result where run_id = @runId order by n",
            new { runId }, r => (Suite: r.GetString(0), CaseN: r.GetInt32(1), Locale: r.GetString(2), Status: r.GetString(3), Metrics: r.Get<string?>("metrics")), ct);
        return rows.Where(r => r.Status == "error" || (r.Metrics is { } m && Fails(JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(m, EvalJson.Options)!)))
            .Take(MaxFailingCases).Select(r => $"{r.Suite}#{r.CaseN}:{r.Locale}").ToList();
    }

    /// <summary>A case fails a check when a scored metric is at its worst: 0 where higher is better, above 0 where lower is better.</summary>
    internal static bool Fails(IReadOnlyDictionary<string, CaseMetric> metrics) =>
        metrics.Any(m => m.Value.State == CaseMetricStates.Scored && m.Value.Value is { } v && MetricCatalog.Find(m.Key) is { Method: not EvalMethods.Measured } def
            && (def.HigherIsBetter ? v <= 0 : v > 0));

    /// <summary>Each metric per locale against the latest completed run of the published version, when there is one.</summary>
    private async Task<IReadOnlyList<GateMetricDiff>> DiffAsync(AgentFacts agent, int version, RunResult result, CancellationToken ct)
    {
        Dictionary<(string, string), double?> published = [];
        if (agent.CurrentVersion is { } current && current != version)
        {
            var text = await session.ScalarAsync<string?>(
                """
                select result::text from evaluation.run where agent_id = @agent and agent_version = @current and status = 'completed' and result is not null
                  and trigger <> 'route'
                order by finished_at desc limit 1
                """, new { agent = agent.Id, current }, ct);
            if (text is not null)
            {
                foreach (var m in JsonSerializer.Deserialize<RunResult>(text, EvalJson.Options)!.Metrics ?? [])
                {
                    foreach (var l in m.Locales)
                    {
                        published[(m.Key, l.Locale)] = l.Value;
                    }
                }
            }
        }
        return (result.Metrics ?? []).SelectMany(m => m.Locales.Select(l => new GateMetricDiff(m.Key, l.Locale, published.GetValueOrDefault((m.Key, l.Locale)), l.Value)))
            .ToList();
    }

    /// <summary>The gate of a version, read by someone who may see the agent's evaluation.</summary>
    public async Task<AgentGateResult> GetAsync(Guid agentId, int version, CancellationToken ct)
    {
        _ = await access.ReadableAgentAsync(agentId, ct);
        return await ResultAsync(agentId, version, ct);
    }

    /// <summary>Overrides a blocking failure or an incomplete result with a written reason (US5 scenario 3).</summary>
    public async Task<AgentGateResult> OverrideAsync(Guid runId, GateOverrideRequest request, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (reason is not { Length: >= 10 and <= 1000 })
        {
            throw AppException.Validation("reason", "validation.eval.override_reason");
        }
        var rows = await session.QueryAsync("select agent_id, agent_version from evaluation.run where id = @runId and agent_id is not null", new { runId },
            r => (Agent: r.GetGuid(0), Version: r.GetInt32(1)), ct);
        if (rows.Count == 0 || await agents.GetAsync(rows[0].Agent, ct) is not { } agent)
        {
            throw AppException.NotFound();
        }
        var version = rows[0].Version;
        if (!(await authz.CheckAsync(Permissions.AgentPublish, AuthzTarget.OrgUnit(agent.OrgUnitId), ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var user = context.RequireUser();
        var facts = await agents.VersionAsync(agent.Id, version, ct) ?? throw AppException.NotFound();
        if (facts.LastEditor == user)
        {
            throw AppException.Conflict("conflict.eval.override_last_editor");
        }
        var gate = await ResultAsync(agent.Id, version, ct);
        if (gate.RunId != runId || gate.Status is not (GateStatuses.Failed or GateStatuses.Incomplete) || facts.RiskClass == AgentRiskClasses.High
            || facts.State != "in_review")
        {
            throw AppException.Conflict("conflict.eval.override_not_allowed");
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into evaluation.gate_override (tenant_id, run_id, agent_id, agent_version, reason, overridden_by, overridden_at, owner_id)
            values (platform.current_tenant_id(), @runId, @agent, @version, @reason, @user, @now, @owner)
            """, new { runId, agent = agent.Id, version, reason, user, now, owner = agent.OwnerUserId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalGateOverridden, "eval_run", runId.ToString(), TargetOrgUnitId: agent.OrgUnitId,
            Changes: new { agentId = agent.Id, version, status = gate.Status, riskClass = facts.RiskClass, blocking = gate.BlockingFailures, incomplete = gate.Incomplete, reason }), ct);
        await outbox.EnqueueAsync(new EvaluationGateDecided(runId, agent.Id, version, GateStatuses.Overridden), ct);
        await notifications.SendAsync(new NotificationRequest([agent.OwnerUserId], NotificationCategories.System, OverriddenTemplate,
            $"/admin/agents/{agent.Id}", DedupeKey: $"{OverriddenTemplate}:{runId:N}"), ct);
        logger.LogInformation("eval.gate_overridden tenant {Tenant} run {Run} agent {Agent} version {Version}", context.TenantId, runId, agent.Id, version);
        return await ResultAsync(agent.Id, version, ct);
    }
}

public static class RunTriggers
{
    public const string Manual = "manual";
    public const string Gate = "gate";
    public const string Route = "route";
    public const string Retrieval = "retrieval";
}

/// <summary>
/// The follow-up task an agent owner gets when a gate failure of their agent was overridden (US5 scenario 3). It
/// stays in the inbox until a later version of the agent passes its gate.
/// </summary>
public sealed class GateFollowUpTasks(IDbSession session, IEntitlementService entitlements, IAgentCatalog agents) : IPersonalTaskSource
{
    public const string TaskSource = "eval_follow_up";

    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAgentsBasic, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync(
            "select run_id, agent_id, overridden_at from evaluation.gate_override where owner_id = @userId and follow_up_done_at is null order by overridden_at limit 50",
            new { userId }, r => (Run: r.GetGuid(0), Agent: r.GetGuid(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        var tasks = new List<PersonalTask>();
        foreach (var row in rows)
        {
            if (await agents.GetAsync(row.Agent, ct) is { Status: not "retired" } agent)
            {
                tasks.Add(new PersonalTask(TaskSource, row.Run, agent.Name ?? new Dictionary<string, string>(), $"/admin/agents/{row.Agent}", row.At, null));
            }
        }
        return tasks;
    }
}
