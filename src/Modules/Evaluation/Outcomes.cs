using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A baseline: the task the agent does, named per locale, and the minutes it takes a person without AI.</summary>
public sealed record OutcomeBaselineRequest(Dictionary<string, string>? TaskNames, double? Minutes);

public sealed record OutcomeBaselineView(IReadOnlyDictionary<string, string> TaskNames, double Minutes, DateTimeOffset UpdatedAt);

/// <summary>
/// One agent's business outcomes in the period. The rates and the time saved are null when fewer people than the minimum
/// group size ran it (<see cref="Withheld"/>), and the time saved also without a baseline.
/// </summary>
public sealed record AgentOutcomeView(Guid AgentId, IReadOnlyDictionary<string, string>? AgentName, OutcomeBaselineView? Baseline, bool Withheld, long? Runs,
    double? CompletionRate, double? EscalationRate, double? ResolvedWithoutHelpRate, double? HoursSaved);

public sealed record OutcomesView(bool Available, int Days, int MinGroupSize, IReadOnlyList<AgentOutcomeView> Agents, bool CanEdit);

/// <summary>The rules of business outcome scoring (spec 034 FR-027), kept pure so each is tested on its own.</summary>
public static class OutcomeRules
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;
    public const double MaxMinutes = 480;
    public const int MaxTaskName = 120;

    /// <summary>
    /// The outcomes of a group of runs, or withheld when fewer distinct people than the minimum group size ran them. Time
    /// saved counts each run resolved without human help at the baseline's minutes.
    /// </summary>
    public static (bool Withheld, long? Runs, double? Completion, double? Escalation, double? Resolved, double? Hours) Score(AgentOutcomeFigure? figure, double? minutes,
        int minGroupSize)
    {
        if (figure is null || figure.Runs == 0 || figure.People < minGroupSize)
        {
            return (true, null, null, null, null, null);
        }
        double Rate(long n) => Math.Round(n / (double)figure.Runs, 4);
        return (false, figure.Runs, Rate(figure.Completed), Rate(figure.Escalated), Rate(figure.ResolvedWithoutHelp),
            minutes is { } m ? Math.Round(figure.ResolvedWithoutHelp * m / 60, 1) : null);
    }
}

/// <summary>
/// Business outcome scoring (spec 034 US9 scenario 1, FR-027): per agent the viewer may read the evaluation of, the
/// completion rate, escalation rate, resolution without human help and estimated time saved against the tenant's
/// baseline, aggregated with the minimum group size of spec 021 and never per employee. The AI Policy Administrator sets
/// the baselines.
/// </summary>
public sealed class OutcomeService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, IAgentCatalog agents,
    IAgentOutcomeFigures figures, IAnalyticsPrivacy privacy, EvalAccess access, IAuditWriter audit, IClock clock)
{
    private async Task<bool> MayEditAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<OutcomesView> GetAsync(int? days, CancellationToken ct)
    {
        context.RequireUser();
        var period = days ?? OutcomeRules.DefaultDays;
        if (period is < 1 or > OutcomeRules.MaxDays)
        {
            throw AppException.Validation("days", "validation.eval.outcome_days");
        }
        var min = await privacy.MinGroupSizeAsync(ct);
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsBusinessOutcomes, ct))
        {
            return new OutcomesView(false, period, min, [], false);
        }
        var readable = new List<AgentFacts>();
        foreach (var agent in await agents.ListAsync(ct))
        {
            if (await access.MayReadAsync(agent, ct))
            {
                readable.Add(agent);
            }
        }
        var to = clock.UtcNow;
        var counted = (await figures.ForAgentsAsync([.. readable.Select(a => a.Id)], to.AddDays(-period), to, ct)).ToDictionary(f => f.AgentId);
        var baselines = await BaselinesAsync(ct);
        var rows = readable.Select(agent =>
        {
            var baseline = baselines.GetValueOrDefault(agent.Id);
            var (withheld, runs, completion, escalation, resolved, hours) = OutcomeRules.Score(counted.GetValueOrDefault(agent.Id), baseline?.Minutes, min);
            return new AgentOutcomeView(agent.Id, agent.Name, baseline, withheld, runs, completion, escalation, resolved, hours);
        }).ToList();
        return new OutcomesView(true, period, min, rows, await MayEditAsync(ct));
    }

    private async Task<Dictionary<Guid, OutcomeBaselineView>> BaselinesAsync(CancellationToken ct) =>
        (await session.QueryAsync("select agent_id, task_names::text, minutes, updated_at from evaluation.outcome_baseline", null,
            r => (Agent: r.GetGuid(0), View: new OutcomeBaselineView(JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1))!, (double)r.GetDecimal(2),
                r.GetFieldValue<DateTimeOffset>(3))), ct)).ToDictionary(r => r.Agent, r => r.View);

    /// <summary>Sets an agent's baseline, or removes it when the minutes are left out.</summary>
    public async Task<OutcomesView> SetBaselineAsync(Guid agentId, OutcomeBaselineRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AiEvalsBusinessOutcomes, ct);
        var agent = await agents.GetAsync(agentId, ct) ?? throw AppException.NotFound();
        var now = clock.UtcNow;
        if (request.Minutes is null)
        {
            await session.ExecAsync("delete from evaluation.outcome_baseline where agent_id = @agentId", new { agentId }, ct);
        }
        else
        {
            var errors = new ValidationErrors();
            var minutes = Math.Round(request.Minutes.Value, 1);
            if (double.IsNaN(minutes) || minutes <= 0 || minutes > OutcomeRules.MaxMinutes)
            {
                errors.Add("minutes", "validation.eval.outcome_minutes");
            }
            var names = (request.TaskNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim());
            if (names.Count == 0 || names.Any(n => !CaseRules.Locales.Contains(n.Key) || n.Value.Length > OutcomeRules.MaxTaskName))
            {
                errors.Add("taskNames", "validation.eval.outcome_task");
            }
            errors.ThrowIfAny();
            await session.ExecAsync(
                """
                insert into evaluation.outcome_baseline (tenant_id, agent_id, task_names, minutes, updated_by, updated_at)
                values (platform.current_tenant_id(), @agentId, @names, @minutes, @user, @now)
                on conflict (tenant_id, agent_id) do update set task_names = excluded.task_names, minutes = excluded.minutes, updated_by = excluded.updated_by,
                  updated_at = excluded.updated_at
                """, new { agentId, names = Jsonb.From(names), minutes = (decimal)minutes, user = context.RequireUser(), now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalOutcomeBaselineChanged, "eval_outcome_baseline", agentId.ToString(), TargetOrgUnitId: agent.OrgUnitId,
            Changes: new { minutes = request.Minutes, taskNames = request.TaskNames }), ct);
        return await GetAsync(null, ct);
    }
}
