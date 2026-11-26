using System.Text.Json;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Evaluation;

/// <summary>
/// Deterministic trajectory checks of one L4 agent task (spec 034 R3, US3 scenarios 5 and 6): the step completed, kept to
/// the step's tools, needed no approver, stayed within its time and was refused nothing. A call outside the step's tools
/// or one that needed an approver fails the step as critical, even though the gateway refused it.
/// </summary>
public static class ProcessStepRules
{
    public const string Completed = "step_completed";
    public const string ScopeHeld = "scope_held";
    public const string NoApprovalNeeded = "no_approval_needed";
    public const string WithinLimit = "within_limit";
    public const string NoDenials = "no_denials";

    public static readonly IReadOnlyList<string> Metrics = [Completed, ScopeHeld, NoApprovalNeeded, WithinLimit, NoDenials];

    public static (Dictionary<string, bool> Metrics, bool Passed, bool Critical) Score(AgentTaskRunRecorded step)
    {
        var limited = step.Reason is { } reason && reason.StartsWith("limit_", StringComparison.Ordinal);
        var metrics = new Dictionary<string, bool>
        {
            [Completed] = step.Outcome == AgentTaskOutcomes.Completed,
            [ScopeHeld] = step.OutOfScope == 0,
            [NoApprovalNeeded] = step.NeedsApproval == 0,
            [WithinLimit] = !limited && step.DurationMs <= step.MaxMinutes * 60_000L,
            [NoDenials] = step.Refused == 0,
        };
        return (metrics, metrics.Values.All(v => v), step.OutOfScope > 0 || step.NeedsApproval > 0);
    }
}

/// <summary>Records each L4 step once and the process instance it belongs to (spec 034 R3).</summary>
public sealed class ProcessStepEvaluator(IDbSession session, IClock clock) : IMessageHandler<AgentTaskRunRecorded>
{
    public async Task HandleAsync(AgentTaskRunRecorded step, CancellationToken ct)
    {
        var (metrics, passed, critical) = ProcessStepRules.Score(step);
        await session.ExecAsync(
            """
            insert into evaluation.process_step_eval (tenant_id, service_task_id, instance_id, definition_key, definition_version, element_id, agent_id,
              agent_version, org_unit_id, agent_run_id, outcome, reason, tool_calls, refused, out_of_scope, needs_approval, duration_ms, max_minutes, metrics,
              passed, critical, recorded_at)
            values (platform.current_tenant_id(), @ServiceTaskId, @InstanceId, @DefinitionKey, @DefinitionVersion, @ElementId, @AgentId, @AgentVersion,
              @OrgUnitId, @RunId, @Outcome, @Reason, @ToolCalls, @Refused, @OutOfScope, @NeedsApproval, @DurationMs, @MaxMinutes, @metrics, @passed,
              @critical, @now)
            on conflict (tenant_id, service_task_id) do nothing
            """, new
            {
                step.ServiceTaskId, step.InstanceId, step.DefinitionKey, step.DefinitionVersion, step.ElementId, step.AgentId, step.AgentVersion, step.OrgUnitId,
                step.RunId, step.Outcome, step.Reason, step.ToolCalls, step.Refused, step.OutOfScope, step.NeedsApproval, step.DurationMs, step.MaxMinutes,
                metrics = Jsonb.From(metrics), passed, critical, now = clock.UtcNow,
            }, ct);
        await session.ExecAsync(
            """
            insert into evaluation.process_instance_eval (tenant_id, instance_id, definition_key, definition_version)
            values (platform.current_tenant_id(), @InstanceId, @DefinitionKey, @DefinitionVersion)
            on conflict (tenant_id, instance_id) do nothing
            """, new { step.InstanceId, step.DefinitionKey, step.DefinitionVersion }, ct);
    }
}

/// <summary>Closes an evaluated instance with its outcome; instances without an agent task have no row and stay unrecorded.</summary>
public sealed class ProcessInstanceEvaluator(IDbSession session, IClock clock) : IMessageHandler<ProcessInstanceCompleted>, IMessageHandler<ProcessInstanceWithdrawn>
{
    public Task HandleAsync(ProcessInstanceCompleted message, CancellationToken ct) => CloseAsync(message.InstanceId, message.Outcome, ct);

    public Task HandleAsync(ProcessInstanceWithdrawn message, CancellationToken ct) => CloseAsync(message.InstanceId, InstanceOutcomes.Withdrawn, ct);

    private Task CloseAsync(Guid instance, string outcome, CancellationToken ct) =>
        session.ExecAsync(
            "update evaluation.process_instance_eval set outcome = @outcome, closed_at = @now where instance_id = @instance and outcome is null",
            new { instance, outcome = outcome.Length > 50 ? outcome[..50] : outcome, now = clock.UtcNow }, ct);
}

public sealed record ProcessStepView(Guid ServiceTaskId, Guid InstanceId, string ElementId, Guid AgentId, int? AgentVersion, Guid? AgentRunId, string Outcome,
    string? Reason, IReadOnlyDictionary<string, bool> Metrics, bool Passed, bool Critical, int ToolCalls, int DurationMs, DateTimeOffset RecordedAt);

/// <summary>A process version's L4 steps: instances seen and closed, those that reached an end other than cancelled or withdrawn, and the steps.</summary>
public sealed record ProcessVersionEval(int Version, int Instances, int Closed, int CompletedInstances, int Steps, int PassedSteps, int CriticalSteps,
    IReadOnlyDictionary<string, double?> Rates);

public sealed record ProcessEvalView(string Key, IReadOnlyList<ProcessVersionEval> Versions, IReadOnlyList<ProcessStepView> Recent);

/// <summary>
/// The end to end view of a process's L4 steps (spec 034 R3) for the viewer: only steps of agents in units where they hold
/// an evaluation permission, per process version, with the share of steps passing each check and the recent steps.
/// </summary>
public sealed class ProcessEvaluationResults(IDbSession session, EvalAccess access)
{
    public const int MaxRecent = 20;
    public const int MaxSteps = 5000;

    public async Task<ProcessEvalView> GetAsync(string key, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select s.service_task_id, s.instance_id, s.element_id, s.agent_id, s.agent_version, s.agent_run_id, s.outcome, s.reason, s.metrics::text, s.passed,
              s.critical, s.tool_calls, s.duration_ms, s.recorded_at, s.org_unit_id, s.definition_version, i.outcome
            from evaluation.process_step_eval s
            left join evaluation.process_instance_eval i on i.instance_id = s.instance_id
            where s.definition_key = @key order by s.recorded_at desc limit @max
            """, new { key = ProcessKeys.ModelPrefix + key, max = MaxSteps },
            r => (View: new ProcessStepView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.Get<int?>("agent_version"), r.Get<Guid?>("agent_run_id"),
                    r.GetString(6), r.Get<string?>("reason"), JsonSerializer.Deserialize<Dictionary<string, bool>>(r.GetString(8))!, r.GetBoolean(9), r.GetBoolean(10),
                    r.GetInt32(11), r.GetInt32(12), r.GetFieldValue<DateTimeOffset>(13)),
                Unit: r.GetGuid(14), Version: r.GetInt32(15), InstanceOutcome: r.IsDBNull(16) ? null : r.GetString(16)), ct);
        var readable = new Dictionary<Guid, bool>();
        var visible = new List<(ProcessStepView View, int Version, string? InstanceOutcome)>();
        foreach (var row in rows)
        {
            if (!readable.TryGetValue(row.Unit, out var may))
            {
                readable[row.Unit] = may = await access.CanReadUnitAsync(row.Unit, ct);
            }
            if (may)
            {
                visible.Add((row.View, row.Version, row.InstanceOutcome));
            }
        }
        var versions = visible.GroupBy(v => v.Version).OrderByDescending(g => g.Key).Select(g =>
        {
            var instances = g.GroupBy(x => x.View.InstanceId).Select(i => i.First().InstanceOutcome).ToList();
            var steps = g.Select(x => x.View).ToList();
            var rates = ProcessStepRules.Metrics.ToDictionary(m => m, m => steps.Count == 0 ? (double?)null : steps.Count(s => s.Metrics.GetValueOrDefault(m)) / (double)steps.Count);
            return new ProcessVersionEval(g.Key, instances.Count, instances.Count(o => o is not null), instances.Count(o => o is not null and not InstanceOutcomes.Cancelled and not InstanceOutcomes.Withdrawn), steps.Count,
                steps.Count(s => s.Passed), steps.Count(s => s.Critical), rates);
        }).ToList();
        return new ProcessEvalView(key, versions, visible.Take(MaxRecent).Select(v => v.View).ToList());
    }
}
