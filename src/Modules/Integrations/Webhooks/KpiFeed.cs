using System.Globalization;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Webhooks;

/// <summary>
/// KPI readings (spec 017 FR-022). An inbound webhook with the target <c>kpi</c> maps its body to the canonical
/// fields; each delivery carries one reading. The same metric, location and period keep one value: a reading measured
/// later replaces it, an older one is ignored. Readings are kept 400 days.
/// </summary>
public sealed class KpiFeed(IDbSession session, IEntitlementService entitlements, IOutbox outbox) : IKpiFeed
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);

    /// <summary>The fields a <c>kpi</c> target must map; <c>location</c> may be mapped too.</summary>
    public static readonly string[] RequiredFields = ["metric", "value", "unit", "period_start", "period_end", "source_time"];

    public const string LocationField = "location";

    public static decimal MaxValue => 99_999_999_999_999m;

    /// <summary>The reading in the mapped fields, or the field that is missing or wrong.</summary>
    public static (KpiReading? Reading, string? BadField) Parse(Guid connectionId, string feed, IReadOnlyDictionary<string, string?> fields, DateTimeOffset now)
    {
        string? F(string name) => fields.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        if (F("metric") is not { Length: <= 100 } metric)
        {
            return (null, "metric");
        }
        if (!decimal.TryParse(F("value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || Math.Abs(value) > MaxValue)
        {
            return (null, "value");
        }
        if (F("unit") is not { Length: <= 30 } unit)
        {
            return (null, "unit");
        }
        if (Time(F("period_start")) is not { } start)
        {
            return (null, "period_start");
        }
        if (Time(F("period_end")) is not { } end || end < start)
        {
            return (null, "period_end");
        }
        if (Time(F("source_time")) is not { } measured || measured > now.AddMinutes(5))
        {
            return (null, "source_time");
        }
        var location = F(LocationField);
        if (location is { Length: > 100 })
        {
            return (null, LocationField);
        }
        return (new KpiReading(connectionId, feed, metric, value, unit, start, end, location, measured, now), null);
    }

    /// <summary>ISO 8601 with an offset, or a date, which is read as midnight UTC.</summary>
    private static DateTimeOffset? Time(string? text)
    {
        if (text is null)
        {
            return null;
        }
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) && (text.EndsWith('Z') || text.Contains('+') || text.LastIndexOf('-') > 9)
            ? t.ToUniversalTime()
            : null;
    }

    /// <summary>Stores the reading unless a later measurement of the same period is already kept. Returns true when stored.</summary>
    public async Task<bool> RecordAsync(KpiReading r, CancellationToken ct)
    {
        var stored = await session.ExecAsync(
            """
            insert into integrations.kpi_reading (tenant_id, connection_id, feed, metric, location, period_start, period_end, value, unit, source_time, received_at)
            values (platform.current_tenant_id(), @ConnectionId, @Feed, @Metric, coalesce(@Location, ''), @PeriodStart, @PeriodEnd, @Value, @Unit, @SourceTime, @ReceivedAt)
            on conflict (tenant_id, connection_id, metric, location, period_start, period_end) do update
              set value = excluded.value, unit = excluded.unit, feed = excluded.feed, source_time = excluded.source_time, received_at = excluded.received_at
              where integrations.kpi_reading.source_time < excluded.source_time
            """, r, ct);
        if (stored > 0)
        {
            await outbox.EnqueueAsync(new KpiReadingReceived(r.ConnectionId, r.Feed, r.Metric, r.Location, r.PeriodStart, r.PeriodEnd), ct);
        }
        return stored > 0;
    }

    public async Task<IReadOnlyList<KpiReading>> ReadAsync(KpiQuery query, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select connection_id, feed, metric, value, unit, period_start, period_end, nullif(location, '') as location, source_time, received_at
            from integrations.kpi_reading
            where (@metric::text is null or metric = @metric) and (@location::text is null or location = @location)
              and (@from::timestamptz is null or period_start >= @from) and (@to::timestamptz is null or period_start < @to)
            order by period_start desc, metric, location limit @limit
            """,
            new { metric = query.Metric, location = query.Location, from = query.From, to = query.To, limit = Math.Clamp(query.Limit, 1, 1000) },
            r => new KpiReading(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetFieldValue<DateTimeOffset>(6), r.Get<string>("location"), r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9)), ct);
    }

    public Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct) =>
        session.ExecAsync("delete from integrations.kpi_reading where received_at < @before", new { before = now - Retention }, ct);
}
