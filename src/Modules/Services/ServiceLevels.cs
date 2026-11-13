using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Services;

/// <summary>
/// Service level targets (spec 016 FR-015, US5 scenario 3). An open request past 75% of its target goes at risk and
/// one past its due time breaches: each is recorded once on Tracking (<c>sla_at_risk</c>, <c>sla_breached</c>), the
/// queue and the service's owners are told, and a breach is counted for spec 021 (<c>request.sla_breached</c>, without
/// the person) and audited. Runs as the system actor from <see cref="ServiceScheduleWorker"/>.
/// </summary>
public sealed class ServiceLevels(IDbSession session, Requests requests, RequestNotifier notifier, IAnalyticsEvents analytics, IAuditWriter audit,
    IClock clock)
{
    public const int PerRun = 500;

    public async Task<int> CheckAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await session.QueryAsync(
            """
            select id, case when due_at <= @now then 'breached' else 'at_risk' end from services.request
            where due_at is not null and status in ('submitted', 'in_approval', 'returned')
              and ((service_level is null and at_risk_at <= @now) or (service_level is distinct from 'breached' and due_at <= @now))
            order by at_risk_at limit @limit
            """, new { now, limit = PerRun }, r => (Id: r.GetGuid(0), State: r.GetString(1)), ct);
        foreach (var (id, state) in due)
        {
            var row = (await requests.RowAsync(id, true, ct))!;
            await session.ExecAsync("update services.request set service_level = @state where id = @id", new { id, state }, ct);
            if (state == ServiceLevelStates.Breached)
            {
                await requests.EventAsync(id, "sla_breached", null, null, null, null, ct);
                await notifier.AlertServiceLevelAsync(row, RequestNotifier.Breached, ct);
                await analytics.EmitAsync(AnalyticsEvents.RequestServiceLevelBreached, new Dictionary<string, object?> { ["serviceId"] = row.ServiceId }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.RequestServiceLevelBreached, "request", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                    Changes: new { row.Number, service = row.ServiceId }), ct);
            }
            else
            {
                await requests.EventAsync(id, "sla_at_risk", null, null, null, null, ct);
                await notifier.AlertServiceLevelAsync(row, RequestNotifier.AtRisk, ct);
            }
        }
        return due.Count;
    }
}
