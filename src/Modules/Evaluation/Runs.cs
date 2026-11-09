using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record RunStartRequest(Guid? AgentId, int? Version);

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}

public sealed record RunSummaryView(Guid Id, Guid AgentId, int AgentVersion, string Trigger, string Status, int Total, int Done, int Errors, string? Verdict,
    Guid? RequestedBy, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);

public sealed record RunDetailView(RunSummaryView Run, IReadOnlyList<RunSuite> Suites, RunResult? Result, bool CanCancel);

public sealed record CaseResultView(int N, string Suite, Guid? DatasetId, int? DatasetVersion, int CaseN, string Locale, string Input, CaseExpectations Expect,
    Guid PersonaId, string Severity, string Status, Guid? AgentRunId, string? Outcome, string? Answer, IReadOnlyDictionary<string, CaseMetric>? Metrics,
    IReadOnlyList<JudgeVerdict>? Verdicts, string? Error, string? Model, int? LatencyMs, int? Tokens, bool Failed);

/// <summary>
/// Evaluation runs (US3 scenario 7): a person with <c>eval.design</c> on the agent's unit starts a run of a version
/// against every suite the agent has, the inherited platform suites and its own. The cases and the exact suite and
/// dataset versions are fixed when it starts; the run then goes on in the background on the workers, shows progress,
/// and can be cancelled. Results follow the viewer's scope like the suites.
/// </summary>
public sealed class RunService(IDbSession session, ITenantContext context, EvalAccess access, SuiteService suites, DatasetService datasets,
    EvalSettingsService settings, PersonaService personas, RouteGates routes, CalibrationService calibration, IOutbox outbox, IClock clock, ILogger<RunService> logger)
{
    public const int MaxCases = 5000;

    public async Task<RunDetailView> StartAsync(RunStartRequest request, CancellationToken ct)
    {
        if (request.AgentId is not { } agentId)
        {
            throw AppException.Validation("agentId", "validation.eval.run_agent");
        }
        var agent = await access.DesignableAgentAsync(agentId, ct);
        var version = request.Version ?? agent.LatestVersion;
        if (version < 1 || version > agent.LatestVersion)
        {
            throw AppException.Validation("version", "validation.eval.run_version");
        }
        if (agent.Status is "retired" or "suspended")
        {
            throw AppException.Conflict("conflict.eval.agent_unavailable");
        }
        if (await ActiveAsync(agentId, version, ct))
        {
            throw AppException.Conflict("conflict.eval.run_active");
        }
        var id = await CreateAsync(agent, version, RunTriggers.Manual, ct);
        return await GetAsync(id, ct);
    }

    internal async Task<bool> ActiveAsync(Guid agentId, int version, CancellationToken ct) =>
        await session.ScalarAsync<long>("select count(*) from evaluation.run where agent_id = @agentId and agent_version = @version and status in ('queued', 'running')",
            new { agentId, version }, ct) > 0;

    /// <summary>Fixes the cases and versions of a run and queues it. The caller has checked who may start it.</summary>
    internal async Task<Guid> CreateAsync(AgentFacts agent, int version, string trigger, CancellationToken ct, Guid? routeChangeId = null)
    {
        var agentId = agent.Id;
        var user = context.RequireUser();
        var (suiteList, cases) = await PlanAsync(agent, ct);
        if (cases.Count == 0)
        {
            throw AppException.Validation("agentId", "validation.eval.run_no_cases");
        }
        if (cases.Count > MaxCases)
        {
            throw AppException.Validation("agentId", "validation.eval.run_too_many_cases");
        }

        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into evaluation.run (tenant_id, id, agent_id, agent_version, org_unit_id, trigger, status, suites, total, requested_by, created_at, route_change_id)
            values (platform.current_tenant_id(), @id, @agentId, @version, @unit, @trigger, 'queued', @suites, @total, @user, @now, @routeChangeId)
            """, new { id, agentId, version, unit = agent.OrgUnitId, trigger, suites = Jsonb.From(suiteList), total = cases.Count, user, now, routeChangeId }, ct);
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            await session.ExecAsync(
                """
                insert into evaluation.case_result (tenant_id, run_id, n, suite_key, dataset_id, dataset_version, case_n, locale, input, expectations, persona_id, severity,
                  classification, status)
                values (platform.current_tenant_id(), @id, @n, @suite, @dataset, @datasetVersion, @caseN, @locale, @input, @expect, @persona, @severity, @classification, 'pending')
                """, new
                {
                    id, n = i + 1, suite = c.Suite, dataset = c.DatasetId, datasetVersion = c.DatasetVersion, caseN = c.CaseN, locale = c.Locale, input = c.Input,
                    expect = Jsonb.From(c.Expect), persona = c.PersonaId, severity = c.Severity, classification = c.Classification,
                }, ct);
        }
        await outbox.EnqueueAsync(new EvaluationRunRequested(id), ct);
        logger.LogInformation("eval.run_started tenant {Tenant} run {Run} agent {Agent} version {Version} cases {Cases} trigger {Trigger}", context.TenantId, id, agentId,
            version, cases.Count, trigger);
        return id;
    }

    /// <summary>What a run of the agent would test now, to tell whether an earlier result is stale (section 6).</summary>
    internal async Task<IReadOnlyList<string>> SignatureAsync(AgentFacts agent, CancellationToken ct)
    {
        var (list, _) = await PlanAsync(agent, ct, withCases: false);
        return Signature(list);
    }

    internal static IReadOnlyList<string> Signature(IEnumerable<RunSuite> suites) =>
        suites.Select(s => $"{s.Key}@{s.Version}:{string.Join(",", s.Datasets.Select(d => $"{d.Id:N}@{d.Version}"))}").Order(StringComparer.Ordinal).ToList();

    internal sealed record PlannedCase(string Suite, Guid? DatasetId, int? DatasetVersion, int CaseN, string Locale, string Input, CaseExpectations Expect, Guid PersonaId,
        string Severity, string Classification);

    /// <summary>What the run tests: the inherited platform suites and the agent's own suite, with their effective rules and cases in the agent's languages.</summary>
    private async Task<(List<RunSuite> Suites, List<PlannedCase> Cases)> PlanAsync(AgentFacts agent, CancellationToken ct, bool withCases = true)
    {
        var languages = agent.Languages.Count > 0 ? agent.Languages : ["en"];
        var tenant = await settings.TenantRulesAsync(ct);
        var demoted = withCases ? await calibration.DemotedAsync(ct) : new HashSet<string>();
        var list = new List<RunSuite>();
        var cases = new List<PlannedCase>();
        Guid? fallback = null;
        async Task<Guid> PersonaAsync(Guid? persona) => persona ?? (fallback ??= (await personas.DefaultAsync(agent.OrgUnitId, ct)).Id);
        if (!withCases)
        {
            foreach (var suite in PlatformSuites.For(agent.TemplateId))
            {
                list.Add(new RunSuite(suite.Key, "platform", suite.Version, suite.Metrics, [], []));
            }
            var mine = await suites.CurrentAsync(agent.Id, ct);
            if (mine.Id is not null && mine.Metrics.Count > 0)
            {
                var uses = new List<DatasetUse>();
                foreach (var datasetId in mine.Datasets)
                {
                    if (await session.ScalarAsync<int?>("select current_version from evaluation.dataset where id = @datasetId and agent_id = @agent",
                        new { datasetId, agent = agent.Id }, ct) is > 0 and var v)
                    {
                        uses.Add(new DatasetUse(datasetId, v));
                    }
                }
                list.Add(new RunSuite(AgentSuiteKey, "agent", mine.Version, mine.Metrics, [], uses));
            }
            return (list, cases);
        }

        foreach (var suite in PlatformSuites.For(agent.TemplateId))
        {
            var rules = ThresholdRules.Effective(suite.Metrics.Select(m => m.Key),
                [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Platform, ThresholdRules.Rules(suite.Metrics)), (EvalLevels.Tenant, tenant)]);
            list.Add(new RunSuite(suite.Key, "platform", suite.Version, suite.Metrics, CalibrationService.Demote(rules, demoted), []));
            for (var i = 0; i < suite.Cases.Count; i++)
            {
                var c = suite.Cases[i];
                if (languages.Contains(c.Locale))
                {
                    cases.Add(new PlannedCase(suite.Key, null, null, i + 1, c.Locale, c.Input, c.Expect, await PersonaAsync(null), c.Severity, "public"));
                }
            }
        }

        var own = await suites.CurrentAsync(agent.Id, ct);
        if (own.Id is not null && own.Metrics.Count > 0)
        {
            var rules = ThresholdRules.Effective(own.Metrics.Select(m => m.Key),
                [(EvalLevels.Platform, ThresholdRules.PlatformRules), (EvalLevels.Tenant, tenant), (EvalLevels.Agent, ThresholdRules.Rules(own.Metrics))]);
            var uses = new List<DatasetUse>();
            foreach (var datasetId in own.Datasets)
            {
                var current = await session.ScalarAsync<int?>("select current_version from evaluation.dataset where id = @datasetId and agent_id = @agent",
                    new { datasetId, agent = agent.Id }, ct);
                if (current is not > 0)
                {
                    continue;
                }
                uses.Add(new DatasetUse(datasetId, current.Value));
                foreach (var c in await datasets.CasesAsync(datasetId, current.Value, ct))
                {
                    if (languages.Contains(c.Locale))
                    {
                        cases.Add(new PlannedCase(AgentSuiteKey, datasetId, current.Value, c.N, c.Locale, c.Input, c.Expect, await PersonaAsync(c.PersonaId), c.Severity,
                            c.Classification));
                    }
                }
            }
            list.Add(new RunSuite(AgentSuiteKey, "agent", own.Version, own.Metrics, CalibrationService.Demote(rules, demoted), uses));
        }
        return (list, cases);
    }

    /// <summary>The key of an agent's own suite in a run.</summary>
    public const string AgentSuiteKey = "agent";

    private const string SummarySql =
        "select id, agent_id, agent_version, trigger, status, total, done, errors, verdict, requested_by, created_at, started_at, finished_at, suites::text, result::text from evaluation.run";

    private static (RunSummaryView Run, string Suites, string? Result) ReadSummary(Npgsql.NpgsqlDataReader r) =>
        (new RunSummaryView(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.Get<string?>("verdict"),
            r.Get<Guid?>("requested_by"), r.GetFieldValue<DateTimeOffset>(10), r.Get<DateTimeOffset?>("started_at"), r.Get<DateTimeOffset?>("finished_at")),
            r.GetString(13), r.Get<string?>("result"));

    public async Task<IReadOnlyList<RunSummaryView>> ListAsync(Guid agentId, CancellationToken ct)
    {
        _ = await access.ReadableAgentAsync(agentId, ct);
        var rows = await session.QueryAsync($"{SummarySql} where agent_id = @agentId order by created_at desc, id limit 50", new { agentId }, ReadSummary, ct);
        return rows.Select(r => r.Run).ToList();
    }

    private async Task<(RunSummaryView Run, string Suites, string? Result, AgentFacts Agent)> LoadAsync(Guid id, CancellationToken ct)
    {
        var rows = await session.QueryAsync($"{SummarySql} where id = @id", new { id }, ReadSummary, ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var agent = await access.ReadableAgentAsync(rows[0].Run.AgentId, ct);
        return (rows[0].Run, rows[0].Suites, rows[0].Result, agent);
    }

    public async Task<RunDetailView> GetAsync(Guid id, CancellationToken ct)
    {
        var (run, suiteText, result, agent) = await LoadAsync(id, ct);
        return new RunDetailView(run, JsonSerializer.Deserialize<List<RunSuite>>(suiteText, EvalJson.Options)!,
            result is null ? null : JsonSerializer.Deserialize<RunResult>(result, EvalJson.Options),
            run.Status is RunStatuses.Queued or RunStatuses.Running && await access.CanDesignAsync(agent, ct));
    }

    public async Task<IReadOnlyList<CaseResultView>> CasesAsync(Guid id, CancellationToken ct)
    {
        _ = await LoadAsync(id, ct);
        return await session.QueryAsync(
            """
            select n, suite_key, dataset_id, dataset_version, case_n, locale, input, expectations::text, persona_id, severity, status, agent_run_id, outcome, answer,
              metrics::text, verdicts::text, error, model, latency_ms, tokens
            from evaluation.case_result where run_id = @id order by n
            """, new { id },
            r =>
            {
                var metrics = r.Get<string?>("metrics") is { } m ? JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(m, EvalJson.Options) : null;
                var status = r.GetString(10);
                return new CaseResultView(r.GetInt32(0), r.GetString(1), r.Get<Guid?>("dataset_id"), r.Get<int?>("dataset_version"), r.GetInt32(4), r.GetString(5), r.GetString(6),
                    JsonSerializer.Deserialize<CaseExpectations>(r.GetString(7), EvalJson.Options)!, r.GetGuid(8), r.GetString(9), status, r.Get<Guid?>("agent_run_id"),
                    r.Get<string?>("outcome"), r.Get<string?>("answer"), metrics,
                    r.Get<string?>("verdicts") is { } v ? JsonSerializer.Deserialize<List<JudgeVerdict>>(v, EvalJson.Options) : null,
                    r.Get<string?>("error"), r.Get<string?>("model"), r.Get<int?>("latency_ms"), r.Get<int?>("tokens"),
                    status == "error" || (metrics is not null && GateService.Fails(metrics)));
            }, ct);
    }

    /// <summary>Stops a queued or running run (US3 scenario 7). Cases already scored stay; the rest are skipped.</summary>
    public async Task<RunDetailView> CancelAsync(Guid id, CancellationToken ct)
    {
        var (run, _, _, agent) = await LoadAsync(id, ct);
        if (!await access.CanDesignAsync(agent, ct))
        {
            throw AppException.Denied();
        }
        var now = clock.UtcNow;
        var user = context.RequireUser();
        var changed = await session.ExecAsync(
            "update evaluation.run set status = 'cancelled', cancelled_by = @user, finished_at = @now where id = @id and status in ('queued', 'running')",
            new { id, user, now }, ct);
        if (changed == 0)
        {
            throw AppException.Conflict("conflict.eval.run_finished");
        }
        await session.ExecAsync("update evaluation.case_result set status = 'skipped' where run_id = @id and status = 'pending'", new { id }, ct);
        // A cancelled run of a route gate counts as not tested, so the route gate can still decide (incomplete).
        await routes.TryDecideAsync(id, ct);
        logger.LogInformation("eval.run_cancelled tenant {Tenant} run {Run} agent {Agent} done {Done} of {Total}", context.TenantId, id, run.AgentId, run.Done, run.Total);
        return await GetAsync(id, ct);
    }
}

/// <summary>A case claimed for running, with the identity of its persona.</summary>
internal sealed record CaseWork(Guid RunId, int N, Guid AgentId, int AgentVersion, string Suite, string Locale, string Input, CaseExpectations Expect, Guid PersonaUserId,
    string Classification, IReadOnlyList<string> Metrics, Guid? RouteChangeId = null);

/// <summary>The run's bookkeeping, each call in its own short transaction so a cancel is never blocked by a running case.</summary>
public sealed class RunStore(IDbSession session, IOutbox outbox, IClock clock, ITenantContext context, IAuditWriter audit, RouteGates routes, ILogger<RunStore> logger)
{
    /// <summary>Marks the run started; false when it is finished or cancelled.</summary>
    internal async Task<bool> BeginAsync(Guid runId, CancellationToken ct)
    {
        await session.ExecAsync("update evaluation.run set status = 'running', started_at = @now where id = @runId and status = 'queued'", new { runId, now = clock.UtcNow }, ct);
        return await session.ScalarAsync<string>("select status from evaluation.run where id = @runId", new { runId }, ct) == RunStatuses.Running;
    }

    /// <summary>Claims the next pending case, or null when the run is done, cancelled or another worker has the rest.</summary>
    internal async Task<CaseWork?> ClaimAsync(Guid runId, CancellationToken ct)
    {
        var run = await session.QueryAsync("select agent_id, agent_version, status, suites::text, route_change_id from evaluation.run where id = @runId", new { runId },
            r => (Agent: r.GetGuid(0), Version: r.GetInt32(1), Status: r.GetString(2), Suites: r.GetString(3), Route: r.Get<Guid?>("route_change_id")), ct);
        if (run.Count == 0 || run[0].Status != RunStatuses.Running)
        {
            return null;
        }
        var claimed = await session.QueryAsync(
            """
            update evaluation.case_result c set status = 'running'
            where (c.tenant_id, c.run_id, c.n) = (select tenant_id, run_id, n from evaluation.case_result where run_id = @runId and status = 'pending' order by n limit 1 for update skip locked)
            returning c.n, c.suite_key, c.locale, c.input, c.expectations::text, c.persona_id, c.classification
            """, new { runId },
            r => (N: r.GetInt32(0), Suite: r.GetString(1), Locale: r.GetString(2), Input: r.GetString(3), Expect: r.GetString(4), Persona: r.GetGuid(5), Classification: r.GetString(6)), ct);
        if (claimed.Count == 0)
        {
            return null;
        }
        var c = claimed[0];
        var user = await session.ScalarAsync<Guid?>("select user_id from evaluation.persona where id = @persona", new { persona = c.Persona }, ct)
            ?? throw new InvalidOperationException("The persona of an evaluation case is missing.");
        var suites = JsonSerializer.Deserialize<List<RunSuite>>(run[0].Suites, EvalJson.Options)!;
        var metrics = suites.FirstOrDefault(s => s.Key == c.Suite)?.Metrics.Select(m => m.Key).ToList() ?? [];
        return new CaseWork(runId, c.N, run[0].Agent, run[0].Version, c.Suite, c.Locale, c.Input, JsonSerializer.Deserialize<CaseExpectations>(c.Expect, EvalJson.Options)!,
            user, c.Classification, metrics, run[0].Route);
    }

    internal async Task ErrorAsync(Guid runId, int n, string error, CancellationToken ct)
    {
        var changed = await session.ExecAsync(
            "update evaluation.case_result set status = 'error', error = @error, finished_at = @now where run_id = @runId and n = @n and status in ('pending', 'running')",
            new { runId, n, error, now = clock.UtcNow }, ct);
        if (changed > 0)
        {
            await session.ExecAsync("update evaluation.run set done = done + 1, errors = errors + 1 where id = @runId", new { runId }, ct);
        }
    }

    internal async Task<bool> HasPendingAsync(Guid runId, CancellationToken ct) =>
        await session.ScalarAsync<long>("select count(*) from evaluation.case_result where run_id = @runId and status = 'pending'", new { runId }, ct) > 0;

    /// <summary>Scores a run whose cases have all finished and publishes <see cref="EvaluationRunCompleted"/> (US4).</summary>
    internal async Task FinishAsync(Guid runId, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select agent_id, agent_version, status, suites::text, trigger, org_unit_id from evaluation.run where id = @runId for update",
            new { runId }, r => (Agent: r.GetGuid(0), Version: r.GetInt32(1), Status: r.GetString(2), Suites: r.GetString(3), Trigger: r.GetString(4), Unit: r.GetGuid(5)), ct);
        if (rows.Count == 0 || rows[0].Status != RunStatuses.Running)
        {
            return;
        }
        var (agent, version, _, suiteText, trigger, unit) = rows[0];
        // A case left running by a worker that stopped mid-case cannot be trusted, so it counts as not run.
        var interrupted = await session.ExecAsync(
            "update evaluation.case_result set status = 'error', error = 'eval.case_interrupted', finished_at = @now where run_id = @runId and status = 'running'",
            new { runId, now = clock.UtcNow }, ct);
        if (interrupted > 0)
        {
            await session.ExecAsync("update evaluation.run set done = done + @interrupted, errors = errors + @interrupted where id = @runId", new { runId, interrupted }, ct);
        }
        var suites = JsonSerializer.Deserialize<List<RunSuite>>(suiteText, EvalJson.Options)!;
        var cases = await session.QueryAsync(
            "select suite_key, locale, status, metrics::text, latency_ms, tokens from evaluation.case_result where run_id = @runId order by n", new { runId },
            r =>
            {
                var metrics = r.Get<string?>("metrics") is { } m ? JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(m, EvalJson.Options)! : [];
                var success = metrics.TryGetValue("task_success", out var t) ? t.Value == 1 : r.GetString(2) == "done";
                return new ScoredCase(r.GetString(0), r.GetString(1), r.GetString(2), metrics, r.Get<int?>("latency_ms"), r.Get<int?>("tokens"), success);
            }, ct);
        var locales = cases.Select(c => c.Locale).Distinct().Order().ToList();
        var (baselineId, baseline, repeats) = await BaselineAsync(agent, version, ct);
        var result = RunScorer.Score(suites, cases, locales, baseline, repeats, baselineId);
        await session.ExecAsync(
            "update evaluation.run set status = 'completed', verdict = @verdict, result = @result, finished_at = @now where id = @runId",
            new { runId, verdict = result.Verdict, result = Jsonb.From(result), now = clock.UtcNow }, ct);
        await outbox.EnqueueAsync(new EvaluationRunCompleted(runId, agent, version, RunStatuses.Completed, result.Verdict), ct);
        if (trigger == RunTriggers.Gate)
        {
            // A gate decision is evidence (FR-030): audited with the suite and dataset versions it used, and published for the Agent Hub.
            await audit.WriteAsync(new AuditEntry(AuditActions.EvalGateDecided, "eval_run", runId.ToString(), TargetOrgUnitId: unit,
                Changes: new { agentId = agent, version, verdict = result.Verdict, critical = result.CriticalFailures, blocking = result.BlockingFailures,
                    incomplete = result.Incomplete, suites = RunService.Signature(suites) }), ct);
            await outbox.EnqueueAsync(new EvaluationGateDecided(runId, agent, version, result.Verdict), ct);
        }
        if (trigger == RunTriggers.Route)
        {
            await routes.TryDecideAsync(runId, ct);
        }
        if (result.Verdict == EvalVerdicts.Passed)
        {
            // A later version that passes closes the owner's follow-up of an earlier override (US5 scenario 3).
            await session.ExecAsync(
                "update evaluation.gate_override set follow_up_done_at = @now where agent_id = @agent and agent_version < @version and follow_up_done_at is null",
                new { agent, version, now = clock.UtcNow }, ct);
        }
        logger.LogInformation("eval.run_completed tenant {Tenant} run {Run} agent {Agent} version {Version} verdict {Verdict}", context.TenantId, runId, agent, version,
            result.Verdict);
    }

    /// <summary>
    /// The baseline (FR-016): the latest completed run of another version, preferring the agent's current published
    /// version, with up to two earlier runs of that same version for the noise margin.
    /// </summary>
    private async Task<(Guid? Id, RunResult? Result, IReadOnlyList<RunResult> Repeats)> BaselineAsync(Guid agent, int version, CancellationToken ct)
    {
        var runs = await session.QueryAsync(
            """
            select id, agent_version, result::text from evaluation.run
            where agent_id = @agent and agent_version <> @version and status = 'completed' and result is not null and trigger <> 'route'
            order by finished_at desc limit 20
            """, new { agent, version }, r => (Id: r.GetGuid(0), Version: r.GetInt32(1), Result: r.GetString(2)), ct);
        if (runs.Count == 0)
        {
            return (null, null, []);
        }
        var baseVersion = runs.Where(r => r.Version < version).Select(r => r.Version).DefaultIfEmpty(runs[0].Version).Max();
        var same = runs.Where(r => r.Version == baseVersion).ToList();
        var parsed = same.Select(r => JsonSerializer.Deserialize<RunResult>(r.Result, EvalJson.Options)!).ToList();
        return (same[0].Id, parsed[0], parsed.Skip(1).Take(2).ToList());
    }
}

/// <summary>Runs one case as its persona and scores it (US3): the agent on the real runtime, then the judge, then the deterministic and trajectory evaluators.</summary>
public sealed class CaseExecutor(IDbSession session, IAgentEvaluationRuntime runtime, EvaluationJudge judge, RunStore store, IClock clock)
{
    internal async Task RunAsync(CaseWork work, CancellationToken ct)
    {
        EvalRunRecord record;
        try
        {
            record = await runtime.RunCaseAsync(work.AgentId, work.AgentVersion, work.Input, work.Locale, work.RouteChangeId, ct);
        }
        catch (AppException e) when (e.Status is 404 or 409)
        {
            await store.ErrorAsync(work.RunId, work.N, e.DetailKey ?? e.Code, ct);
            return;
        }
        var classification = Enum.Parse<DataClassification>(work.Classification, ignoreCase: true);
        var verdicts = await judge.JudgeAsync(work.Metrics, work.Locale, work.Input, work.Expect, record, classification, ct);
        var metrics = CaseEvaluators.Evaluate(work.Metrics, work.Expect, work.Locale, record, verdicts);
        await session.ExecAsync(
            """
            update evaluation.case_result set status = 'done', agent_run_id = @runId, outcome = @outcome, answer = @answer, metrics = @metrics, verdicts = @verdicts,
              model = @model, latency_ms = @latency, tokens = @tokens, finished_at = @now
            where run_id = @run and n = @n and status = 'running'
            """, new
            {
                run = work.RunId, work.N, runId = record.RunId, outcome = record.Outcome ?? record.Status, answer = record.Answer, metrics = Jsonb.From(metrics),
                verdicts = Jsonb.From(verdicts.Values.Where(v => v is not null).ToList()), model = record.Model, latency = record.DurationMs,
                tokens = record.InputTokens + record.OutputTokens, now = clock.UtcNow,
            }, ct);
        await session.ExecAsync("update evaluation.run set done = done + 1 where id = @run", new { run = work.RunId }, ct);
    }
}

/// <summary>
/// The evaluation worker (spec 034 plan, US3 scenario 7): takes a batch of cases of a run at a time, each in its own
/// scope and transaction, as the case's synthetic persona identity, then queues the next batch, so one tenant's large
/// run takes turns with other work instead of holding a worker. When no case is left it scores the run.
/// </summary>
public sealed class EvaluationRunner(IServiceScopeFactory scopes, ITenantContext context, IOutbox outbox, ILogger<EvaluationRunner> logger)
    : IMessageHandler<EvaluationRunRequested>
{
    public const int BatchSize = 10;

    private async Task<T> InScopeAsync<T>(Guid tenant, Guid? user, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(user is null ? ActorType.System : ActorType.User, user);
        var result = await action(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync();
        return result;
    }

    public async Task HandleAsync(EvaluationRunRequested message, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var runId = message.RunId;
        if (!await InScopeAsync(tenant, null, sp => sp.GetRequiredService<RunStore>().BeginAsync(runId, ct)))
        {
            return;
        }
        for (var i = 0; i < BatchSize; i++)
        {
            var work = await InScopeAsync(tenant, null, sp => sp.GetRequiredService<RunStore>().ClaimAsync(runId, ct));
            if (work is null)
            {
                break;
            }
            try
            {
                await InScopeAsync(tenant, work.PersonaUserId, async sp =>
                {
                    await sp.GetRequiredService<CaseExecutor>().RunAsync(work, ct);
                    return true;
                });
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Evaluation case {N} of run {Run} failed", work.N, runId);
                await InScopeAsync(tenant, null, async sp =>
                {
                    await sp.GetRequiredService<RunStore>().ErrorAsync(runId, work.N, "eval.case_failed", ct);
                    return true;
                });
            }
        }
        if (await InScopeAsync(tenant, null, sp => sp.GetRequiredService<RunStore>().HasPendingAsync(runId, ct)))
        {
            await outbox.EnqueueAsync(new EvaluationRunRequested(runId), ct);
            return;
        }
        await InScopeAsync(tenant, null, async sp =>
        {
            await sp.GetRequiredService<RunStore>().FinishAsync(runId, ct);
            return true;
        });
    }
}
