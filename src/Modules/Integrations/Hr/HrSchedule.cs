using System.Globalization;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.ClockData;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Hr;

/// <summary>
/// Starting HR syncs (spec 019 section 6): the nightly full sync of the tenant's HR connection at its local sync
/// time, and a sync an HR administrator asks for now. Each is a job of the connector runtime, so it is in run history
/// with retry, replay and the circuit breaker.
/// </summary>
public sealed class HrSchedule(IDbSession session, IConnectorRuntime runtime, IEntitlementService entitlements)
{
    private Task<ConnectorCallResult> EnqueueAsync(Guid connectionId, string key, CancellationToken ct) =>
        runtime.EnqueueAsync(new ConnectorCall(connectionId, HrRestPack.SyncEmployees, JsonSerializer.SerializeToElement(new { }), key, "hr-sync"), ct);

    /// <summary>Queues a sync of the enabled HR connection now.</summary>
    public async Task<ConnectorCallResult> SyncNowAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            throw AppException.NotInPackage(Capabilities.HrIntegration);
        }
        var id = await session.QuerySingleOrDefaultAsync(
            "select id from integrations.connection where kind = any(@kinds) and enabled order by created_at limit 1",
            new { kinds = HrSync.PackKinds.ToArray() }, r => (Guid?)r.GetGuid(0), ct) ?? throw AppException.Conflict("conflict.hr.no_connection");
        return await EnqueueAsync(id, $"hr-now:{Ids.New():N}", ct);
    }

    /// <summary>Queues the nightly sync once the connection's local sync time has passed, once per local day.</summary>
    public async Task<int> ScheduleDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var connections = await session.QueryAsync(
            """
            select c.id, coalesce(c.settings->>'syncAt', '01:00'), coalesce(c.settings->>'timeZone', 'UTC'), s.last_date from integrations.connection c
            left join integrations.hr_schedule s on s.connection_id = c.id
            where c.kind = any(@kinds) and c.enabled
            """, new { kinds = HrSync.PackKinds.ToArray() },
            r => (Id: r.GetGuid(0), At: r.GetString(1), Zone: r.GetString(2), Last: r.IsDBNull(3) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(3)), ct);
        var queued = 0;
        foreach (var c in connections)
        {
            if (ClockCsv.FindZone(c.Zone) is not { } zone || !TimeOnly.TryParseExact(c.At, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                continue;
            }
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (c.Last is { } last && last >= today || TimeOnly.FromDateTime(local.DateTime) < at)
            {
                continue;
            }
            await session.ExecAsync(
                """
                insert into integrations.hr_schedule (tenant_id, connection_id, last_date) values (platform.current_tenant_id(), @id, @today)
                on conflict (tenant_id, connection_id) do update set last_date = excluded.last_date
                """, new { id = c.Id, today }, ct);
            await EnqueueAsync(c.Id, $"hr-nightly:{today:yyyy-MM-dd}", ct);
            queued++;
        }
        return queued;
    }
}
