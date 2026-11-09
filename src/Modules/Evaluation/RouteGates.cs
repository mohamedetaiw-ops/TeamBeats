using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>Results of a route gate, as spec 013 reads them.</summary>
public static class RouteGateResults
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Incomplete = "incomplete";
    public const string FailedCritical = "failed_critical";
}

/// <summary>
/// The release gate of a proposed model route change (US5 scenario 4, spec 013 FR-019): every published agent on a
/// changed use case runs its version in force against the proposed routes, and once every run has finished the gate
/// decides: a critical failure in any agent rejects the change, blocking failures or anything not tested leave it for
/// the AI Policy Administrator to apply with a reason, and otherwise it applies.
/// </summary>
public sealed class RouteGates(IDbSession session, IOutbox outbox, IAuditWriter audit, IClock clock, ITenantContext context, ILogger<RouteGates> logger)
{
    /// <summary>What a gate cannot test in R1: the assistant has no suites of its own yet.</summary>
    public const string AssistantNotTested = "assistant/no_suites";

    internal async Task<bool> ExistsAsync(Guid routeChangeId, CancellationToken ct) =>
        await session.ScalarAsync<long>("select count(*) from evaluation.route_gate where route_change_id = @routeChangeId", new { routeChangeId }, ct) > 0;

    internal async Task OpenAsync(Guid routeChangeId, IReadOnlyList<string> useCases, IReadOnlyList<string> untested, int runs, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into evaluation.route_gate (tenant_id, route_change_id, use_cases, status, failures, runs, created_at)
            values (platform.current_tenant_id(), @routeChangeId, @useCases, 'running', @failures, @runs, @now)
            """, new { routeChangeId, useCases = useCases.ToArray(), failures = Jsonb.From(untested), runs, now = clock.UtcNow }, ct);
        logger.LogInformation("eval.route_gate_started tenant {Tenant} route change {RouteChange} runs {Runs}", context.TenantId, routeChangeId, runs);
        if (runs == 0)
        {
            await DecideAsync(routeChangeId, ct);
        }
    }

    /// <summary>Decides the route gate of the run's route change once none of its runs is still going.</summary>
    internal async Task TryDecideAsync(Guid runId, CancellationToken ct)
    {
        var route = await session.ScalarAsync<Guid?>("select route_change_id from evaluation.run where id = @runId and trigger = 'route'", new { runId }, ct);
        if (route is { } id && await session.ScalarAsync<long>(
                "select count(*) from evaluation.run where route_change_id = @id and status in ('queued', 'running')", new { id }, ct) == 0)
        {
            await DecideAsync(id, ct);
        }
    }

    private async Task DecideAsync(Guid routeChangeId, CancellationToken ct)
    {
        var gate = await session.QueryAsync("select status, failures::text from evaluation.route_gate where route_change_id = @routeChangeId for update",
            new { routeChangeId }, r => (Status: r.GetString(0), Failures: r.GetString(1)), ct);
        if (gate.Count == 0 || gate[0].Status != "running")
        {
            return;
        }
        var incomplete = JsonSerializer.Deserialize<List<string>>(gate[0].Failures) ?? [];
        var critical = new List<string>();
        var blocking = new List<string>();
        var runs = await session.QueryAsync("select agent_id, status, result::text from evaluation.run where route_change_id = @routeChangeId", new { routeChangeId },
            r => (Agent: r.GetGuid(0), Status: r.GetString(1), Result: r.Get<string?>("result")), ct);
        foreach (var run in runs)
        {
            if (run.Status != RunStatuses.Completed || run.Result is null)
            {
                incomplete.Add($"{run.Agent:N}/{RunStatuses.Cancelled}");
                continue;
            }
            var result = JsonSerializer.Deserialize<RunResult>(run.Result, EvalJson.Options)!;
            critical.AddRange(result.CriticalFailures.Select(f => $"{run.Agent:N}/{f}"));
            blocking.AddRange(result.BlockingFailures.Select(f => $"{run.Agent:N}/{f}"));
            incomplete.AddRange(result.Incomplete.Select(f => $"{run.Agent:N}/{f}"));
        }
        var verdict = critical.Count > 0 ? RouteGateResults.FailedCritical
            : blocking.Count > 0 ? RouteGateResults.Failed
            : incomplete.Count > 0 ? RouteGateResults.Incomplete
            : RouteGateResults.Passed;
        var failures = critical.Concat(blocking).Concat(incomplete).ToList();
        await session.ExecAsync(
            "update evaluation.route_gate set status = 'decided', result = @verdict, failures = @failures, decided_at = @now where route_change_id = @routeChangeId",
            new { routeChangeId, verdict, failures = Jsonb.From(failures), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalGateDecided, "ai_route_change", routeChangeId.ToString(),
            Changes: new { verdict, runs = runs.Count, critical, blocking, incomplete }), ct);
        await outbox.EnqueueAsync(new EvaluationRouteGateDecided(routeChangeId, verdict, failures), ct);
        logger.LogInformation("eval.route_gate_decided tenant {Tenant} route change {RouteChange} verdict {Verdict}", context.TenantId, routeChangeId, verdict);
    }
}

/// <summary>
/// Starts the route gate when spec 013 holds a route change (FR-019): one run per published agent when the agent use
/// case changes, on behalf of the person who proposed it. An agent with nothing to run is listed as not tested.
/// </summary>
public sealed class RouteGateStarter(IServiceScopeFactory scopes, ITenantContext context, ILogger<RouteGateStarter> logger) : IMessageHandler<AiModelRouteChangeProposed>
{
    public async Task HandleAsync(AiModelRouteChangeProposed message, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(message.ProposedBy is null ? ActorType.System : ActorType.User, message.ProposedBy);
        var sp = scope.ServiceProvider;
        var gates = sp.GetRequiredService<RouteGates>();
        if (await gates.ExistsAsync(message.RouteChangeId, ct))
        {
            return;
        }
        var untested = new List<string>();
        if (message.UseCases.Contains(AiUseCases.Assistant))
        {
            untested.Add(RouteGates.AssistantNotTested);
        }
        var runs = 0;
        if (message.UseCases.Contains(AiUseCases.Agent))
        {
            var service = sp.GetRequiredService<RunService>();
            foreach (var agent in await sp.GetRequiredService<IAgentCatalog>().PublishedAsync(ct))
            {
                if (message.ProposedBy is null || agent.CurrentVersion is not { } version)
                {
                    untested.Add($"{agent.Id:N}/not_run");
                    continue;
                }
                try
                {
                    await service.CreateAsync(agent, version, RunTriggers.Route, ct, message.RouteChangeId);
                    runs++;
                }
                catch (AppException e) when (e.Status == 400)
                {
                    logger.LogWarning("eval.route_gate_agent_skipped tenant {Tenant} agent {Agent} reason {Reason}", tenant, agent.Id, e.DetailKey);
                    untested.Add($"{agent.Id:N}/not_run");
                }
            }
        }
        await gates.OpenAsync(message.RouteChangeId, message.UseCases, untested, runs, ct);
        await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
    }
}
