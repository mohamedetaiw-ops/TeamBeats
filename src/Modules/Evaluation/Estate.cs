using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A metric of an agent's latest evaluated version against its previous evaluated version, in one locale.</summary>
public sealed record EstateMetric(string Metric, string Locale, double? Value, double? Previous, double? Delta, bool Regressed);

/// <summary>An agent in the estate (spec 034 R3): its latest evaluated version, the one before it, and the change per metric and locale.</summary>
public sealed record EstateAgent(Guid AgentId, IReadOnlyDictionary<string, string>? Name, Guid OrgUnitId, int? Version, int? PreviousVersion, string? Verdict,
    DateTimeOffset? EvaluatedAt, IReadOnlyList<EstateMetric> Metrics, bool Regressed);

/// <summary>One month of an estate benchmark: the mean over agents of their latest evaluation run that month, and how many agents it covers.</summary>
public sealed record EstatePoint(string Month, double Value, int Agents);

public sealed record EstateSeries(string Metric, string Locale, IReadOnlyList<EstatePoint> Points);

public sealed record EstateTrendsView(IReadOnlyList<string> Months, IReadOnlyList<EstateSeries> Benchmarks, IReadOnlyList<EstateAgent> Agents, double NoiseMargin);

/// <summary>
/// Cross-version trend benchmarks across a tenant's agent estate (spec 034 R3, US6 at estate level), under
/// <c>ai.evals.advanced</c>. Only agents the viewer may read are counted (US6 scenario 4). A change between two versions is
/// a regression only beyond the noise margin and in the metric's bad direction; locales are never averaged together.
/// </summary>
public sealed class EstateTrends(IDbSession session, EvalAccess access, IAgentCatalog agents, IEntitlementService entitlements, IClock clock)
{
    public const int Months = 12;
    public const int MaxRuns = 5000;

    private sealed record RunRow(Guid Agent, int Version, string? Verdict, DateTimeOffset At, List<MetricValue> Values);

    public async Task<EstateTrendsView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiEvalsAdvanced, ct);
        var readable = new List<AgentFacts>();
        foreach (var agent in await agents.ListAsync(ct))
        {
            if (await access.MayReadAsync(agent, ct))
            {
                readable.Add(agent);
            }
        }
        var ids = readable.Select(a => a.Id).ToArray();
        var now = clock.UtcNow;
        var first = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-(Months - 1));
        RunRow Map(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetInt32(1), r.Get<string?>("verdict"), r.GetFieldValue<DateTimeOffset>(3),
            ResultsService.Values(JsonSerializer.Deserialize<RunResult>(r.GetString(4), EvalJson.Options)!));
        // The latest completed run of each version, as on the agent dashboard (route and comparison runs left out).
        var versions = ids.Length == 0 ? [] : await session.QueryAsync(
            """
            select distinct on (agent_id, agent_version) agent_id, agent_version, verdict, finished_at, result::text from evaluation.run
            where agent_id = any(@ids) and status = 'completed' and result is not null and trigger not in ('route', 'comparison')
            order by agent_id, agent_version desc, finished_at desc limit @max
            """, new { ids, max = MaxRuns }, Map, ct);
        // The latest run of each agent in each of the last months, for the estate benchmarks.
        var monthly = ids.Length == 0 ? [] : await session.QueryAsync(
            """
            select distinct on (agent_id, date_trunc('month', finished_at at time zone 'UTC')) agent_id, agent_version, verdict, finished_at, result::text
            from evaluation.run
            where agent_id = any(@ids) and status = 'completed' and result is not null and trigger not in ('route', 'comparison') and finished_at >= @first
            order by agent_id, date_trunc('month', finished_at at time zone 'UTC'), finished_at desc limit @max
            """, new { ids, first, max = MaxRuns }, Map, ct);

        var months = Enumerable.Range(0, Months).Select(i => first.AddMonths(i).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var benchmarks = monthly
            .SelectMany(run => run.Values.Where(v => v.Value is not null).Select(v => (Month: run.At.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), v.Metric, v.Locale, Value: v.Value!.Value)))
            .GroupBy(x => (x.Metric, x.Locale))
            .OrderBy(g => g.Key.Metric, StringComparer.Ordinal).ThenBy(g => g.Key.Locale, StringComparer.Ordinal)
            .Select(g => new EstateSeries(g.Key.Metric, g.Key.Locale, g.GroupBy(x => x.Month).OrderBy(m => m.Key, StringComparer.Ordinal)
                .Select(m => new EstatePoint(m.Key, Math.Round(m.Average(x => x.Value), 4), m.Count())).ToList()))
            .ToList();

        var byAgent = versions.GroupBy(v => v.Agent).ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version).ToList());
        var list = new List<EstateAgent>();
        foreach (var agent in readable)
        {
            var runs = byAgent.GetValueOrDefault(agent.Id) ?? [];
            var latest = runs.FirstOrDefault();
            var previous = runs.Skip(1).FirstOrDefault();
            var metrics = latest is null ? [] : latest.Values.Select(v =>
            {
                var before = previous?.Values.FirstOrDefault(p => p.Metric == v.Metric && p.Locale == v.Locale)?.Value;
                double? delta = v.Value is { } current && before is { } earlier ? Math.Round(current - earlier, 4) : null;
                var higherIsBetter = MetricCatalog.Find(v.Metric)?.HigherIsBetter ?? true;
                var regressed = delta is { } d && (higherIsBetter ? d < -RunScorer.DefaultNoise : d > RunScorer.DefaultNoise);
                return new EstateMetric(v.Metric, v.Locale, v.Value, before, delta, regressed);
            }).ToList();
            list.Add(new EstateAgent(agent.Id, agent.Name, agent.OrgUnitId, latest?.Version, previous?.Version, latest?.Verdict, latest?.At, metrics,
                metrics.Any(m => m.Regressed)));
        }
        return new EstateTrendsView(months, benchmarks, list.OrderByDescending(a => a.Regressed).ThenByDescending(a => a.EvaluatedAt).ToList(), RunScorer.DefaultNoise);
    }
}
