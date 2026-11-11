using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.ClockData;

/// <summary>
/// Starting clock data imports (spec 017 US7): the daily SFTP import, an import an administrator asks for now, and an
/// uploaded CSV file. Every import is a job of the connector runtime, so it is in run history with retry, replay and
/// the connection's circuit breaker.
/// </summary>
public sealed class ClockImports(IDbSession session, IConnectorRuntime runtime, ClockImportStore store, IEntitlementService entitlements, ITenantContext context,
    IAuditWriter audit)
{
    public const long MaxUploadBytes = 20L * 1024 * 1024;

    private async Task<ClockImportSettings> ConnectionAsync(Guid connectionId, CancellationToken ct)
    {
        var settings = await session.QuerySingleOrDefaultAsync("select settings::text from integrations.connection where id = @connectionId and kind = @kind",
            new { connectionId, kind = ClockImportPack.KindName }, r => r.GetString(0), ct) ?? throw AppException.NotFound();
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            throw AppException.NotInPackage(Capabilities.IntegrationConnectors);
        }
        using var doc = JsonDocument.Parse(settings);
        return ClockImportPack.Parse(doc.RootElement.Clone()) ?? throw AppException.NotFound();
    }

    private Task<ConnectorCallResult> EnqueueAsync(Guid connectionId, string key, CancellationToken ct) =>
        runtime.EnqueueAsync(new ConnectorCall(connectionId, ClockImportPack.Import, JsonSerializer.SerializeToElement(new { }), key, "clock-import"), ct);

    /// <summary>Stores an uploaded CSV file and queues its import. The same content uploaded twice is refused.</summary>
    public async Task<ClockFileView> UploadAsync(Guid connectionId, string? name, byte[] content, CancellationToken ct)
    {
        await ConnectionAsync(connectionId, ct);
        var clean = name?.Trim();
        if (clean is null || clean.Length is 0 or > 255 || clean.Any(c => char.IsControl(c) || c is '/' or '\\'))
        {
            throw AppException.Validation("name", "validation.clock.file_name");
        }
        if (content.Length == 0)
        {
            throw AppException.Validation("file", "validation.clock.file_empty");
        }
        var user = context.RequireUser();
        var id = await store.AddUploadAsync(connectionId, clean, content, user, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ClockDataUploaded, "connection", connectionId.ToString(), Changes: new { file = id, bytes = content.Length }), ct);
        await EnqueueAsync(connectionId, $"clock-upload:{id:N}", ct);
        return (await store.FilesAsync(connectionId, null, 50, ct)).Items.First(f => f.Id == id);
    }

    /// <summary>Queues an import of what is waiting now, outside the daily schedule.</summary>
    public async Task<ConnectorCallResult> ImportNowAsync(Guid connectionId, CancellationToken ct)
    {
        await ConnectionAsync(connectionId, ct);
        return await EnqueueAsync(connectionId, $"clock-now:{Ids.New():N}", ct);
    }

    public async Task<ClockFilePage> FilesAsync(Guid connectionId, string? cursor, CancellationToken ct)
    {
        await ConnectionAsync(connectionId, ct);
        return await store.FilesAsync(connectionId, cursor, 50, ct);
    }

    public async Task<IReadOnlyList<ClockProblemView>> ProblemsAsync(Guid connectionId, Guid fileId, CancellationToken ct)
    {
        await ConnectionAsync(connectionId, ct);
        return await store.ProblemsAsync(connectionId, fileId, ct) ?? throw AppException.NotFound();
    }

    /// <summary>
    /// Queues the daily import of each enabled SFTP connection once its local import time has passed, once per local
    /// day. A failed import is retried by the runtime, not queued again by the schedule.
    /// </summary>
    public async Task<int> ScheduleDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        var connections = await session.QueryAsync(
            """
            select c.id, c.settings::text, s.last_date from integrations.connection c
            left join integrations.clock_schedule s on s.connection_id = c.id
            where c.kind = @kind and c.enabled and c.settings->>'source' = 'sftp'
            """, new { kind = ClockImportPack.KindName }, r => (Id: r.GetGuid(0), Settings: r.GetString(1), Last: r.IsDBNull(2) ? (DateOnly?)null : r.GetFieldValue<DateOnly>(2)), ct);
        var queued = 0;
        foreach (var c in connections)
        {
            using var doc = JsonDocument.Parse(c.Settings);
            if (ClockImportPack.Parse(doc.RootElement) is not { } settings || ClockCsv.FindZone(settings.TimeZone ?? "UTC") is not { } zone)
            {
                continue;
            }
            var local = TimeZoneInfo.ConvertTime(now, zone);
            var today = DateOnly.FromDateTime(local.DateTime);
            if (c.Last is { } last && last >= today || TimeOnly.FromDateTime(local.DateTime) < settings.ImportTime)
            {
                continue;
            }
            await session.ExecAsync(
                """
                insert into integrations.clock_schedule (tenant_id, connection_id, last_date) values (platform.current_tenant_id(), @id, @today)
                on conflict (tenant_id, connection_id) do update set last_date = excluded.last_date
                """, new { id = c.Id, today }, ct);
            await EnqueueAsync(c.Id, $"clock-daily:{today:yyyy-MM-dd}", ct);
            queued++;
        }
        return queued;
    }
}
