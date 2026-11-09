using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>
/// The release gate of a proposed retrieval configuration (US5 scenario 5, FR-017, spec 012 FR-008): the retrieval
/// cases of the published agents run against the change's shadow index, and the gate decides from that one run under
/// the platform and tenant rules for retrieval quality. A critical failure (a forbidden source retrieved) rejects the
/// change; blocking failures, too few cases or no run at all leave it for a search administrator to apply with a reason.
/// </summary>
public sealed class RetrievalGates(IDbSession session, IOutbox outbox, IAuditWriter audit, IClock clock, ITenantContext context, ILogger<RetrievalGates> logger)
{
    /// <summary>No published agent has a case that names expected or forbidden sources, so nothing could be measured.</summary>
    public const string NoCases = "retrieval/no_cases";

    /// <summary>The change had no proposer to start the run on behalf of.</summary>
    public const string NotRun = "retrieval/not_run";

    internal async Task<bool> ExistsAsync(Guid changeId, CancellationToken ct) =>
        await session.ScalarAsync<long>("select count(*) from evaluation.retrieval_gate where change_id = @changeId", new { changeId }, ct) > 0;

    internal async Task OpenAsync(Guid changeId, int version, Guid? runId, IReadOnlyList<string> untested, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into evaluation.retrieval_gate (tenant_id, change_id, config_version, status, failures, run_id, created_at)
            values (platform.current_tenant_id(), @changeId, @version, 'running', @failures, @runId, @now)
            """, new { changeId, version, failures = Jsonb.From(untested), runId, now = clock.UtcNow }, ct);
        logger.LogInformation("eval.retrieval_gate_started tenant {Tenant} change {Change} version {Version} run {Run}", context.TenantId, changeId, version, runId);
        if (runId is null)
        {
            await DecideAsync(changeId, ct);
        }
    }

    /// <summary>Decides the gate of the run's retrieval change once the run has finished or was cancelled.</summary>
    internal async Task TryDecideAsync(Guid runId, CancellationToken ct)
    {
        var change = await session.ScalarAsync<Guid?>(
            "select retrieval_change_id from evaluation.run where id = @runId and trigger = 'retrieval' and status not in ('queued', 'running')", new { runId }, ct);
        if (change is { } id)
        {
            await DecideAsync(id, ct);
        }
    }

    private async Task DecideAsync(Guid changeId, CancellationToken ct)
    {
        var gate = await session.QueryAsync("select status, failures::text, run_id from evaluation.retrieval_gate where change_id = @changeId for update",
            new { changeId }, r => (Status: r.GetString(0), Failures: r.GetString(1), Run: r.Get<Guid?>("run_id")), ct);
        if (gate.Count == 0 || gate[0].Status != "running")
        {
            return;
        }
        var incomplete = JsonSerializer.Deserialize<List<string>>(gate[0].Failures) ?? [];
        var critical = new List<string>();
        var blocking = new List<string>();
        if (gate[0].Run is { } runId)
        {
            var run = await session.QueryAsync("select status, result::text from evaluation.run where id = @runId", new { runId },
                r => (Status: r.GetString(0), Result: r.Get<string?>("result")), ct);
            if (run.Count == 0 || run[0].Status != RunStatuses.Completed || run[0].Result is null)
            {
                incomplete.Add($"{RunService.RetrievalSuiteKey}/{RunStatuses.Cancelled}");
            }
            else
            {
                var result = JsonSerializer.Deserialize<RunResult>(run[0].Result!, EvalJson.Options)!;
                critical.AddRange(result.CriticalFailures.Select(f => $"{RunService.RetrievalSuiteKey}/{f}"));
                blocking.AddRange(result.BlockingFailures.Select(f => $"{RunService.RetrievalSuiteKey}/{f}"));
                incomplete.AddRange(result.Incomplete.Select(f => $"{RunService.RetrievalSuiteKey}/{f}"));
            }
        }
        var verdict = critical.Count > 0 ? RouteGateResults.FailedCritical
            : blocking.Count > 0 ? RouteGateResults.Failed
            : incomplete.Count > 0 ? RouteGateResults.Incomplete
            : RouteGateResults.Passed;
        var failures = critical.Concat(blocking).Concat(incomplete).ToList();
        await session.ExecAsync(
            "update evaluation.retrieval_gate set status = 'decided', result = @verdict, failures = @failures, decided_at = @now where change_id = @changeId",
            new { changeId, verdict, failures = Jsonb.From(failures), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalGateDecided, "search_retrieval_change", changeId.ToString(),
            Changes: new { verdict, run = gate[0].Run, critical, blocking, incomplete }), ct);
        await outbox.EnqueueAsync(new EvaluationRetrievalGateDecided(changeId, verdict, failures), ct);
        logger.LogInformation("eval.retrieval_gate_decided tenant {Tenant} change {Change} verdict {Verdict}", context.TenantId, changeId, verdict);
    }
}

/// <summary>
/// Starts the retrieval gate when Search has built the shadow index of a proposed change, on behalf of the person who
/// proposed it. A change with no proposer, or no retrieval case to run, is decided at once as incomplete.
/// </summary>
public sealed class RetrievalGateStarter(IServiceScopeFactory scopes, ITenantContext context) : IMessageHandler<RetrievalConfigChangeProposed>
{
    public async Task HandleAsync(RetrievalConfigChangeProposed message, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(message.ProposedBy is null ? ActorType.System : ActorType.User, message.ProposedBy);
        var sp = scope.ServiceProvider;
        var gates = sp.GetRequiredService<RetrievalGates>();
        if (await gates.ExistsAsync(message.ChangeId, ct))
        {
            return;
        }
        Guid? runId = null;
        var untested = new List<string>();
        if (message.ProposedBy is null)
        {
            untested.Add(RetrievalGates.NotRun);
        }
        else
        {
            var agents = await sp.GetRequiredService<IAgentCatalog>().PublishedAsync(ct);
            runId = await sp.GetRequiredService<RunService>().CreateRetrievalAsync(message.ChangeId, agents, ct);
            if (runId is null)
            {
                untested.Add(RetrievalGates.NoCases);
            }
        }
        await gates.OpenAsync(message.ChangeId, message.Version, runId, untested, ct);
        await sp.GetRequiredService<IDbSession>().CommitAsync(ct);
    }
}
