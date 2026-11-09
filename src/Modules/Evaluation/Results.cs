using System.Text.Json;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A metric of one version in one locale: the pooled value the gate read.</summary>
public sealed record MetricValue(string Metric, string Locale, double? Value, string Status);

/// <summary>A version on the dashboard: its gate and the headline metrics of its latest completed run (US6 scenario 1).</summary>
public sealed record VersionResult(int Version, bool Current, AgentGateResult Gate, Guid? RunId, string? Verdict, DateTimeOffset? FinishedAt, IReadOnlyList<MetricValue> Metrics);

public sealed record DashboardView(Guid AgentId, IReadOnlyDictionary<string, string>? Name, int? CurrentVersion, int LatestVersion, IReadOnlyList<VersionResult> Versions,
    bool CanDesign);

/// <summary>An agent in the evaluation overview, with the gate of its latest version.</summary>
public sealed record EvalAgentRow(Guid AgentId, IReadOnlyDictionary<string, string>? Name, Guid OrgUnitId, string Status, int? CurrentVersion, int LatestVersion, string GateStatus);

public sealed record CompareMetric(string Metric, string Locale, double? A, double? B, double? Delta);

public sealed record CompareSide(Guid RunId, Guid? AgentId, int? Version, string? Verdict, IReadOnlyList<string> Models, IReadOnlyDictionary<string, double?> TokensPerSuccess,
    IReadOnlyDictionary<string, int?> LatencyP50, IReadOnlyDictionary<string, int?> LatencyP95);

/// <summary>A case that passed in one run and failed in the other, matched by suite, dataset, case and locale.</summary>
public sealed record ChangedCase(string Suite, Guid? DatasetId, int CaseN, string Locale, string Input, int CaseInA, int CaseInB);

public sealed record CompareView(CompareSide A, CompareSide B, IReadOnlyList<CompareMetric> Metrics, IReadOnlyList<ChangedCase> NowFailing, IReadOnlyList<ChangedCase> NowPassing);

/// <summary>
/// Results for people (US6): the evaluation overview of the agents in the viewer's scope, an agent's dashboard with a
/// gate and headline metrics per version, the comparison of two runs, and a case with its evidence and the link to
/// its execution record. Everything follows the viewer's scope like the suites (scenario 4); nothing crosses tenants.
/// </summary>
public sealed class ResultsService(IDbSession session, EvalAccess access, IAgentCatalog agents, GateService gates, RunService runs)
{
    public const int MaxVersions = 20;
    public const int MaxChanged = 100;

    public async Task<IReadOnlyList<EvalAgentRow>> AgentsAsync(CancellationToken ct)
    {
        var list = new List<EvalAgentRow>();
        foreach (var agent in await agents.ListAsync(ct))
        {
            if (!await access.MayReadAsync(agent, ct))
            {
                continue;
            }
            var gate = await gates.ResultAsync(agent.Id, agent.LatestVersion, ct);
            list.Add(new EvalAgentRow(agent.Id, agent.Name, agent.OrgUnitId, agent.Status, agent.CurrentVersion, agent.LatestVersion, gate.Status));
        }
        return list;
    }

    public async Task<DashboardView> DashboardAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await access.ReadableAgentAsync(agentId, ct);
        var rows = await session.QueryAsync(
            """
            select distinct on (agent_version) agent_version, id, verdict, finished_at, result::text from evaluation.run
            where agent_id = @agentId and status = 'completed' and result is not null and trigger <> 'route'
            order by agent_version desc, finished_at desc
            """, new { agentId }, r => (Version: r.GetInt32(0), Id: r.GetGuid(1), Verdict: r.Get<string?>("verdict"), At: r.Get<DateTimeOffset?>("finished_at"),
                Result: r.GetString(4)), ct);
        var byVersion = rows.ToDictionary(r => r.Version);
        var versions = new List<VersionResult>();
        for (var v = agent.LatestVersion; v >= 1 && versions.Count < MaxVersions; v--)
        {
            var gate = await gates.ResultAsync(agentId, v, ct);
            if (byVersion.TryGetValue(v, out var run))
            {
                versions.Add(new VersionResult(v, v == agent.CurrentVersion, gate, run.Id, run.Verdict, run.At, Values(JsonSerializer.Deserialize<RunResult>(run.Result, EvalJson.Options)!)));
            }
            else
            {
                versions.Add(new VersionResult(v, v == agent.CurrentVersion, gate, null, null, null, []));
            }
        }
        return new DashboardView(agent.Id, agent.Name, agent.CurrentVersion, agent.LatestVersion, versions, await access.CanDesignAsync(agent, ct));
    }

    internal static List<MetricValue> Values(RunResult result) =>
        (result.Metrics ?? []).SelectMany(m => m.Locales.Select(l => new MetricValue(m.Key, l.Locale, l.Value, l.Status))).ToList();

    private sealed record CaseRow(int N, string Suite, Guid? DatasetId, int CaseN, string Locale, string Input, string Status, string? Metrics, string? Model, int? Latency);

    private async Task<List<CaseRow>> CaseRowsAsync(Guid runId, CancellationToken ct) =>
        await session.QueryAsync(
            "select n, suite_key, dataset_id, case_n, locale, input, status, metrics::text, model, latency_ms from evaluation.case_result where run_id = @runId order by n",
            new { runId }, r => new CaseRow(r.GetInt32(0), r.GetString(1), r.Get<Guid?>("dataset_id"), r.GetInt32(3), r.GetString(4), r.GetString(5), r.GetString(6),
                r.Get<string?>("metrics"), r.Get<string?>("model"), r.Get<int?>("latency_ms")), ct);

    private static bool Failed(CaseRow c) =>
        c.Status == "error" || (c.Metrics is { } m && GateService.Fails(JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(m, EvalJson.Options)!));

    private static bool Passed(CaseRow c) => c.Status == "done" && !Failed(c);

    private static CompareSide Side(RunDetailView view, IReadOnlyList<CaseRow> cases)
    {
        var locales = cases.Select(c => c.Locale).Distinct().Order(StringComparer.Ordinal).ToList();
        int? P50(string l)
        {
            var sorted = cases.Where(c => c.Locale == l && c.Latency is not null).Select(c => c.Latency!.Value).Order().ToList();
            return sorted.Count == 0 ? null : sorted[(sorted.Count - 1) / 2];
        }
        return new CompareSide(view.Run.Id, view.Run.AgentId, view.Run.AgentVersion, view.Run.Verdict,
            cases.Select(c => c.Model).Where(m => m is not null).Select(m => m!).Distinct().Order(StringComparer.Ordinal).ToList(),
            view.Result?.TokensPerSuccess ?? new Dictionary<string, double?>(), locales.ToDictionary(l => l, P50), view.Result?.LatencyP95 ?? new Dictionary<string, int?>());
    }

    /// <summary>Two runs side by side (US6 scenario 2): metrics by locale, models, cost per successful task, p50 and p95 latency, and the cases that changed.</summary>
    public async Task<CompareView> CompareAsync(Guid a, Guid b, CancellationToken ct)
    {
        var left = await runs.GetAsync(a, ct);
        var right = await runs.GetAsync(b, ct);
        var leftCases = await CaseRowsAsync(a, ct);
        var rightCases = await CaseRowsAsync(b, ct);
        var leftValues = left.Result is { } lr ? Values(lr).ToDictionary(v => (v.Metric, v.Locale)) : [];
        var rightValues = right.Result is { } rr ? Values(rr).ToDictionary(v => (v.Metric, v.Locale)) : [];
        var metrics = leftValues.Keys.Union(rightValues.Keys).Order().Select(k =>
        {
            var x = leftValues.GetValueOrDefault(k)?.Value;
            var y = rightValues.GetValueOrDefault(k)?.Value;
            return new CompareMetric(k.Metric, k.Locale, x, y, x is { } p && y is { } q ? Math.Round(q - p, 4) : null);
        }).ToList();

        static (string, Guid?, int, string) Key(CaseRow c) => (c.Suite, c.DatasetId, c.CaseN, c.Locale);
        var byKey = rightCases.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        var nowFailing = new List<ChangedCase>();
        var nowPassing = new List<ChangedCase>();
        foreach (var c in leftCases)
        {
            if (!byKey.TryGetValue(Key(c), out var other))
            {
                continue;
            }
            if (Passed(c) && Failed(other) && nowFailing.Count < MaxChanged)
            {
                nowFailing.Add(new ChangedCase(c.Suite, c.DatasetId, c.CaseN, c.Locale, c.Input, c.N, other.N));
            }
            else if (Failed(c) && Passed(other) && nowPassing.Count < MaxChanged)
            {
                nowPassing.Add(new ChangedCase(c.Suite, c.DatasetId, c.CaseN, c.Locale, c.Input, c.N, other.N));
            }
        }
        return new CompareView(Side(left, leftCases), Side(right, rightCases), metrics, nowFailing, nowPassing);
    }

    /// <summary>One case with its evidence (US6 scenario 3); the trajectory and sources are in the linked execution record.</summary>
    public async Task<CaseResultView> CaseAsync(Guid runId, int n, CancellationToken ct) =>
        (await runs.CasesAsync(runId, ct)).FirstOrDefault(c => c.N == n) ?? throw AppException.NotFound();
}
