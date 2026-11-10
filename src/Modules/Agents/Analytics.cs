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

public sealed record AgentAnalyticsView(Guid AgentId, int Days, DateTimeOffset From, DateTimeOffset To, AgentVersionStats Total, IReadOnlyList<AgentVersionStats> Versions);

/// <summary>
/// Basic agent analytics (spec 020 FR-018, R1 part): for the people who may see the agent, per version and in total over
/// the last days, the runs, active users, task success (runs answered), runs stopped or failed, tool calls and denials,
/// ratings from the assistant turns the agent answered, tokens and estimated cost from the AI Gateway, and the release
/// gate result as the quality score. Test panel and evaluation runs are left out. No person is named.
/// </summary>
public sealed class AgentAnalytics(IDbSession session, AgentRegistry registry, IAiAgentUsage usage, IAgentReleaseGate gate, IClock clock)
{
    public const int DefaultDays = 30;
    public const int MaxDays = 365;

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
            """
            select agent_version, count(*), count(distinct user_id), count(*) filter (where outcome = 'answered'),
              count(*) filter (where status = 'stopped'), count(*) filter (where status = 'failed'),
              coalesce(sum(tool_calls), 0)::bigint, coalesce(sum(denials), 0)::bigint
            from agents.run where agent_id = @id and mode = 'run' and status in ('completed', 'stopped', 'failed') and created_at >= @from and created_at < @to
            group by 1
            """, new { id, from, to },
            r => (Version: r.GetInt32(0), Runs: r.GetInt64(1), Users: r.GetInt64(2), Answered: r.GetInt64(3), Stopped: r.GetInt64(4), Failed: r.GetInt64(5),
                Calls: r.GetInt64(6), Denials: r.GetInt64(7)), ct);
        var users = await session.ScalarAsync<long>(
            "select count(distinct user_id) from agents.run where agent_id = @id and mode = 'run' and status in ('completed', 'stopped', 'failed') and created_at >= @from and created_at < @to",
            new { id, from, to }, ct);
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
        foreach (var number in numbers)
        {
            var run = runs.FirstOrDefault(r => r.Version == number);
            var rating = ratings.FirstOrDefault(r => r.Version == number);
            var cost = costs.FirstOrDefault(c => c.Version == number);
            var quality = detail.Versions.Any(v => v.Number == number && v.PublishedAt is not null) ? (await gate.ResultAsync(id, number, ct)).Status : null;
            versions.Add(new AgentVersionStats(number, run.Runs, run.Users, run.Answered, Rate(run.Answered, run.Runs), run.Stopped, run.Failed, run.Calls,
                run.Denials, rating.Count, rating.Helpful, cost?.Tokens ?? 0, cost?.Cost ?? 0, quality));
        }
        var total = new AgentVersionStats(null, versions.Sum(v => v.Runs), users, versions.Sum(v => v.Answered), Rate(versions.Sum(v => v.Answered), versions.Sum(v => v.Runs)),
            versions.Sum(v => v.Stopped), versions.Sum(v => v.Failed), versions.Sum(v => v.ToolCalls), versions.Sum(v => v.ToolDenials), versions.Sum(v => v.Ratings),
            versions.Sum(v => v.Helpful), versions.Sum(v => v.Tokens), versions.Sum(v => v.Cost), null);
        return new AgentAnalyticsView(id, period, from, to, total, versions);
    }

    private static double? Rate(long part, long whole) => whole == 0 ? null : Math.Round(part / (double)whole, 4);
}
