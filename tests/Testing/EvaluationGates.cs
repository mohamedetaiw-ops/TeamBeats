namespace TeamBeats.Testing;

/// <summary>
/// For tests about the agent lifecycle and runtime rather than evaluation: marks the queued release gate run of a
/// version as passed, so publishing goes on as it would after a clean gate. The gate itself, with real runs, overrides
/// and refusals, is tested in EvaluationGateTests.
/// </summary>
public static class EvaluationGates
{
    private const string PassedResult =
        """{"verdict":"passed","suites":[],"criticalFailures":[],"blockingFailures":[],"warnings":[],"incomplete":[],"latencyP95":{},"tokensPerSuccess":{},"baselineRunId":null,"regressions":[],"metrics":[]}""";

    public static async Task PassGateAsync(this TestDatabase db, Guid tenantId, Guid agentId, int version)
    {
        var changed = await db.TenantScalarAsync<long>(tenantId,
            """
            with run as (
              update evaluation.run set status = 'completed', verdict = 'passed', result = @result::jsonb, started_at = now(), finished_at = now()
              where agent_id = @agent and agent_version = @version and status in ('queued', 'running') returning id),
            cases as (update evaluation.case_result set status = 'skipped' where run_id in (select id from run) and status in ('pending', 'running') returning 1)
            select count(*) from run
            """, ("result", PassedResult), ("agent", agentId), ("version", version));
        if (changed == 0)
        {
            throw new InvalidOperationException($"No gate run is waiting for version {version} of agent {agentId}.");
        }
    }
}
