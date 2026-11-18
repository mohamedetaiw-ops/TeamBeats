using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Agents;

/// <summary>Usage and quality of one agent version, or of the whole agent when <see cref="Version"/> is null.</summary>
public sealed record AgentVersionStats(
    int? Version,
    long Runs,
    long Users,
    long Answered,
    double? SuccessRate,
    long Stopped,
    long Failed,
    long ToolCalls,
    long ToolDenials,
    long Ratings,
    long Helpful,
    long Tokens,
    decimal Cost,
    string? Quality);

public sealed record AgentAnalyticsView(Guid AgentId, int Days, DateTimeOffset From, DateTimeOffset To, AgentVersionStats Total, IReadOnlyList<AgentVersionStats> Versions,
    AgentAnalyticsDetail? Detail = null);

/// <summary>
/// Detailed agent analytics (spec 020 FR-018, R2 part, under <c>analytics.advanced</c>): conversations and escalations per
/// version with the evaluation scores of spec 034, how runs ended, tool success and failure, and a day by day trend.
/// </summary>
public sealed record AgentAnalyticsDetail(
    IReadOnlyList<AgentVersionDetail> Versions,
    IReadOnlyList<AgentOutcomeCount> Outcomes,
    IReadOnlyList<AgentToolStats> Tools,
    IReadOnlyList<AgentDayStats> Trend);

/// <summary>
/// One version, or the total when <see cref="Version"/> is null: the assistant conversations it answered in, the runs that
/// ended without an answer the person could use (escalations), the share resolved without escalation, and the metric
/// scores of its release gate run.
/// </summary>
public sealed record AgentVersionDetail(int? Version, long Conversations, long Escalations, double? ResolvedRate, IReadOnlyList<AgentQualityScore> Quality);

public sealed record AgentQualityScore(string Metric, string Locale, double? Value);

public sealed record AgentOutcomeCount(string Outcome, long Runs);

public sealed record AgentFailureReason(string Reason, long Calls);

public sealed record AgentToolStats(string Tool, long Calls, long Succeeded, long Failed, double? SuccessRate, long AverageMs, IReadOnlyList<AgentFailureReason> Failures);

public sealed record AgentDayStats(DateOnly Day, long Runs, long Answered, long Escalations, long Ratings, long Helpful);

/// <summary>
/// Agent analytics (spec 020 FR-018): for the people who may see the agent, per version and in total over the last days,
/// the runs, active users, task success (runs answered), runs stopped or failed, tool calls and denials, ratings from the
/// assistant turns the agent answered, tokens and estimated cost from the AI Gateway, and the release gate result as the
/// quality score. With <c>analytics.advanced</c> (R2) the detail follows. Test panel and evaluation runs are left out.
/// No person is named.
/// </summary>
public sealed class AgentAnalytics(IDbSession session, AgentRegistry registry, IAiAgentUsage usage, IAgentReleaseGate gate, IEntitlementService entitlements, IClock clock)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;
    public const int TopFailures = 3;

    /// <summary>
    /// The outcomes that count as an escalation: the run ended without an answer the person could use, which leaves them
    /// with the agent's escalation path. A run the person stopped themselves is not one.
    /// </summary>
    public static readonly string[] Escalated =
        [RunOutcomes.LimitReached, RunOutcomes.PolicyBlocked, RunOutcomes.Unavailable, RunOutcomes.Suspended, RunOutcomes.Expired, RunOutcomes.Interrupted];

    /// <summary>The finished runs of the agent in the period, as filtered on <c>agents.run r</c>.</summary>
    private const string Finished =
        "r.agent_id = @id and r.mode = 'run' and r.status in ('completed', 'stopped', 'failed') and r.created_at >= @from and r.created_at < @to";

    public async Task<AgentAnalyticsView> ForAgentAsync(Guid id, int? days, CancellationToken ct)
    {
        var period = days ?? DefaultDays;
        if (period is < 1 or > MaxDays)
        {
            throw AppException.Validation("days", "validation.agent.analytics_days");
        }
        var detail = await registry.GetAsync(id, ct);
        var to = clock.UtcNow;
        var from = to.AddDays(-period);
        var runs = await session.QueryAsync(
            $"""
            select r.agent_version, count(*), count(distinct r.user_id), count(*) filter (where r.outcome = 'answered'),
              count(*) filter (where r.status = 'stopped'), count(*) filter (where r.status = 'failed'),
              coalesce(sum(r.tool_calls), 0)::bigint, coalesce(sum(r.denials), 0)::bigint
            from agents.run r where {Finished}
            group by 1
            """, new { id, from, to },
            r => (Version: r.GetInt32(0), Runs: r.GetInt64(1), Users: r.GetInt64(2), Answered: r.GetInt64(3), Stopped: r.GetInt64(4), Failed: r.GetInt64(5),
                Calls: r.GetInt64(6), Denials: r.GetInt64(7)), ct);
        var users = await session.ScalarAsync<long>($"select count(distinct r.user_id) from agents.run r where {Finished}", new { id, from, to }, ct);
        var ratings = await session.QueryAsync(
            """
            select r.agent_version, count(*), count(*) filter (where f.helpful)
            from agents.feedback f join agents.turn t on t.id = f.turn_id join agents.run r on r.id = t.run_id
            where r.agent_id = @id and f.created_at >= @from and f.created_at < @to
            group by 1
            """, new { id, from, to }, r => (Version: r.GetInt32(0), Count: r.GetInt64(1), Helpful: r.GetInt64(2)), ct);
        var costs = await usage.AgentCostsAsync(id, from, to, ct);

        var numbers = runs.Select(r => r.Version).Concat(ratings.Select(r => r.Version)).Concat(costs.Select(c => c.Version))
            .Concat(detail.Agent.CurrentVersion is { } current ? [current] : []).Distinct().OrderDescending().ToList();
        var versions = new List<AgentVersionStats>();
        var gates = new Dictionary<int, AgentGateResult>();
        foreach (var number in numbers)
        {
            var run = runs.FirstOrDefault(r => r.Version == number);
            var rating = ratings.FirstOrDefault(r => r.Version == number);
            var cost = costs.FirstOrDefault(c => c.Version == number);
            if (detail.Versions.Any(v => v.Number == number && v.PublishedAt is not null))
            {
                gates[number] = await gate.ResultAsync(id, number, ct);
            }
            versions.Add(new AgentVersionStats(number, run.Runs, run.Users, run.Answered, Rate(run.Answered, run.Runs), run.Stopped, run.Failed, run.Calls,
                run.Denials, rating.Count, rating.Helpful, cost?.Tokens ?? 0, cost?.Cost ?? 0, gates.GetValueOrDefault(number)?.Status));
        }
        var total = new AgentVersionStats(null, versions.Sum(v => v.Runs), users, versions.Sum(v => v.Answered), Rate(versions.Sum(v => v.Answered), versions.Sum(v => v.Runs)),
            versions.Sum(v => v.Stopped), versions.Sum(v => v.Failed), versions.Sum(v => v.ToolCalls), versions.Sum(v => v.ToolDenials), versions.Sum(v => v.Ratings),
            versions.Sum(v => v.Helpful), versions.Sum(v => v.Tokens), versions.Sum(v => v.Cost), null);
        var more = await entitlements.IsEnabledAsync(Capabilities.AnalyticsAdvanced, ct) ? await DetailAsync(id, from, to, versions, total, gates, ct) : null;
        return new AgentAnalyticsView(id, period, from, to, total, versions, more);
    }

    private async Task<AgentAnalyticsDetail> DetailAsync(Guid id, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AgentVersionStats> stats,
        AgentVersionStats total, IReadOnlyDictionary<int, AgentGateResult> gates, CancellationToken ct)
    {
        var escalated = Escalated;
        var escalations = await session.QueryAsync(
            $"select r.agent_version, count(*) from agents.run r where {Finished} and r.outcome = any(@escalated) group by 1",
            new { id, from, to, escalated }, r => (Version: r.GetInt32(0), Count: r.GetInt64(1)), ct);
        var conversations = await session.QueryAsync(
            $"""
            select r.agent_version, count(distinct t.conversation_id)
            from agents.turn t join agents.run r on r.id = t.run_id
            where {Finished}
            group by 1
            """, new { id, from, to }, r => (Version: r.GetInt32(0), Count: r.GetInt64(1)), ct);
        var allConversations = await session.ScalarAsync<long>(
            $"select count(distinct t.conversation_id) from agents.turn t join agents.run r on r.id = t.run_id where {Finished}", new { id, from, to }, ct);
        var outcomes = await session.QueryAsync(
            $"select r.outcome, count(*) from agents.run r where {Finished} and r.outcome is not null group by 1 order by 2 desc, 1",
            new { id, from, to }, r => new AgentOutcomeCount(r.GetString(0), r.GetInt64(1)), ct);
        var steps = await session.QueryAsync(
            $"""
            select s.tool, s.decision, s.reason, count(*), coalesce(sum(s.duration_ms), 0)::bigint
            from agents.run_step s join agents.run r on r.id = s.run_id
            where s.kind = 'tool' and s.tool is not null and {Finished}
            group by 1, 2, 3
            """, new { id, from, to }, r => (Tool: r.GetString(0), Decision: r.GetString(1), Reason: r.Get<string?>("reason"), Count: r.GetInt64(3), Ms: r.GetInt64(4)), ct);
        var days = await session.QueryAsync(
            $"""
            select (r.created_at at time zone 'UTC')::date, count(*), count(*) filter (where r.outcome = 'answered'),
              count(*) filter (where r.outcome = any(@escalated))
            from agents.run r where {Finished}
            group by 1
            """, new { id, from, to, escalated }, r => (Day: DateOnly.FromDateTime(r.GetDateTime(0)), Runs: r.GetInt64(1), Answered: r.GetInt64(2), Escalations: r.GetInt64(3)), ct);
        var dayRatings = await session.QueryAsync(
            """
            select (f.created_at at time zone 'UTC')::date, count(*), count(*) filter (where f.helpful)
            from agents.feedback f join agents.turn t on t.id = f.turn_id join agents.run r on r.id = t.run_id
            where r.agent_id = @id and f.created_at >= @from and f.created_at < @to
            group by 1
            """, new { id, from, to }, r => (Day: DateOnly.FromDateTime(r.GetDateTime(0)), Count: r.GetInt64(1), Helpful: r.GetInt64(2)), ct);

        var versions = new List<AgentVersionDetail>();
        foreach (var version in stats)
        {
            var number = version.Version!.Value;
            var escalation = escalations.FirstOrDefault(e => e.Version == number).Count;
            var scores = gates.GetValueOrDefault(number)?.Diff?.Select(d => new AgentQualityScore(d.Metric, d.Locale, d.Value)).ToList() ?? [];
            versions.Add(new AgentVersionDetail(number, conversations.FirstOrDefault(c => c.Version == number).Count, escalation,
                Rate(version.Runs - escalation, version.Runs), scores));
        }
        var allEscalations = versions.Sum(v => v.Escalations);
        versions.Add(new AgentVersionDetail(null, allConversations, allEscalations, Rate(total.Runs - allEscalations, total.Runs), []));

        var tools = steps.GroupBy(s => s.Tool).Select(g =>
        {
            var calls = g.Sum(s => s.Count);
            var succeeded = g.Where(s => s.Decision is ToolDecisions.Executed or ToolDecisions.Confirmed).Sum(s => s.Count);
            var failed = g.Where(s => s.Decision == ToolDecisions.Refused).Sum(s => s.Count);
            var failures = g.Where(s => s.Decision == ToolDecisions.Refused && s.Reason is not null).GroupBy(s => s.Reason!)
                .Select(r => new AgentFailureReason(r.Key, r.Sum(s => s.Count)))
                .OrderByDescending(r => r.Calls).ThenBy(r => r.Reason, StringComparer.Ordinal).Take(TopFailures).ToList();
            return new AgentToolStats(g.Key, calls, succeeded, failed, Rate(succeeded, succeeded + failed), g.Sum(s => s.Ms) / calls, failures);
        }).OrderByDescending(t => t.Calls).ThenBy(t => t.Tool, StringComparer.Ordinal).ToList();

        var trend = days.Select(d => d.Day).Concat(dayRatings.Select(d => d.Day)).Distinct().Order().Select(day =>
        {
            var run = days.FirstOrDefault(d => d.Day == day);
            var rating = dayRatings.FirstOrDefault(d => d.Day == day);
            return new AgentDayStats(day, run.Runs, run.Answered, run.Escalations, rating.Count, rating.Helpful);
        }).ToList();
        return new AgentAnalyticsDetail(versions, outcomes, tools, trend);
    }

    private static double? Rate(long part, long whole) => whole == 0 ? null : Math.Round(part / (double)whole, 4);
}
