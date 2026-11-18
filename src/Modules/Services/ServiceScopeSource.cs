using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Requests for a location or unit dashboard (spec 021 US6): how many requests the given people filed are still open,
/// and how many filed in the period breached their service level. Analytics has decided the viewer may see the scope.
/// </summary>
internal sealed class ServiceScopeSource(IDbSession session) : IAnalyticsScopeSource
{
    public async Task<IReadOnlyList<AnalyticsScopeFigure>> FiguresAsync(IReadOnlyCollection<Guid> people, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var rows = await session.QueryAsync(
            """
            select count(*) filter (where status = any(@open)),
                   count(*) filter (where submitted_at >= @from and submitted_at < @to),
                   count(*) filter (where submitted_at >= @from and submitted_at < @to and service_level = 'breached')
            from services.request where requester_user_id = any(@people) and submitted_at is not null
            """, new { people = people.ToArray(), open = RequestStatuses.Cancellable.ToArray(), from, to },
            r => (Open: (int)r.GetInt64(0), Filed: (int)r.GetInt64(1), Breached: (int)r.GetInt64(2)), ct);
        var (open, filed, breached) = rows.Count == 0 ? (0, 0, 0) : rows[0];
        return [new AnalyticsScopeFigure(AnalyticsScopeFigures.OpenRequests, open, null), new AnalyticsScopeFigure(AnalyticsScopeFigures.Breaches, breached, filed)];
    }
}
