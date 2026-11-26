using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Booking.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

public sealed record SensorMappingRequest(string? SensorId, Guid ResourceId);

/// <param name="Mappings">Every mapping of this connection for the site's resources; ones left out are removed.</param>
public sealed record SensorsRequest(Guid ConnectionId, IReadOnlyList<SensorMappingRequest>? Mappings);

/// <summary>A sensor mapped to a resource, with what it last said. LastOccupied is null before the first read.</summary>
public sealed record SensorView(Guid Id, Guid ConnectionId, string SensorId, Guid ResourceId, bool? LastOccupied, DateTimeOffset? LastObservedAt, DateTimeOffset? LastReadAt);

/// <summary>A site's sensor mappings and the occupancy connections they may come from.</summary>
public sealed record SiteSensors(Guid SiteId, IReadOnlyList<OccupancyConnection> Connections, IReadOnlyList<SensorView> Sensors, bool SensorCheckIn);

/// <summary>
/// Sensor based occupancy (spec 031 R3 US9, FR-026, FR-027). Facilities administrators of a site map sensor IDs of an
/// occupancy connection (spec 017) to the site's resources. The clock reads each connection with mappings every 5
/// minutes through <see cref="IOccupancyReadings"/>; each read adds to the resource's hourly counts, which reports turn
/// into sensed occupied hours. With the tenant's sensor check-in on, an occupied reading in a booking's check-in window
/// checks it in. A connection that does not answer changes nothing. No personal data is read or kept.
/// </summary>
public sealed partial class Sensors(
    IDbSession session,
    ITenantContext context,
    Facilities facilities,
    BookingRules rules,
    IEntitlementService entitlements,
    IOccupancyReadings readings,
    IOutbox outbox,
    BookingActivity activity,
    IAuditWriter audit,
    IClock clock)
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    public const int MaxMappings = 2000;

    [GeneratedRegex("^[A-Za-z0-9._:/-]{1,128}$")]
    private static partial Regex SensorIdPattern();

    private async Task<SiteRow> ManagedAsync(Guid siteId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.BookingSensors, ct);
        return await facilities.ManagedSiteAsync(siteId, ct);
    }

    private async Task<SiteSensors> ViewAsync(SiteRow site, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select s.id, s.connection_id, s.sensor_id, s.resource_id, s.last_occupied, s.last_observed_at, s.last_read_at
            from booking.sensor s join booking.resource r on r.id = s.resource_id
            where r.site_id = @site order by s.sensor_id, s.id
            """, new { site = site.Id },
            r => new SensorView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.Get<bool?>("last_occupied"), r.Get<DateTimeOffset?>("last_observed_at"),
                r.Get<DateTimeOffset?>("last_read_at")), ct);
        return new SiteSensors(site.Id, await readings.ConnectionsAsync(ct), rows, (await rules.SettingsAsync(ct)).SensorCheckIn);
    }

    public async Task<SiteSensors> GetAsync(Guid siteId, CancellationToken ct) => await ViewAsync(await ManagedAsync(siteId, ct), ct);

    /// <summary>Replaces the site's mappings from one connection (US9 scenario 1). A sensor maps to one resource per connection.</summary>
    public async Task<SiteSensors> MapAsync(Guid siteId, SensorsRequest request, CancellationToken ct)
    {
        var site = await ManagedAsync(siteId, ct);
        if ((await readings.ConnectionsAsync(ct)).All(c => c.Id != request.ConnectionId))
        {
            throw AppException.Validation("connectionId", "validation.booking.sensor_connection");
        }
        var mappings = request.Mappings ?? [];
        var errors = new ValidationErrors();
        if (mappings.Count > MaxMappings)
        {
            errors.Add("mappings", "validation.booking.sensor_count");
        }
        var ids = mappings.Select(m => m.ResourceId).Distinct().ToArray();
        var onSite = ids.Length == 0 ? [] : (await session.QueryAsync(
            "select id from booking.resource where id = any(@ids) and site_id = @site and status = 'active'", new { ids, site = site.Id }, r => r.GetGuid(0), ct)).ToHashSet();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < mappings.Count; i++)
        {
            var m = mappings[i];
            var sensor = m.SensorId?.Trim() ?? "";
            if (!SensorIdPattern().IsMatch(sensor) || !seen.Add(sensor))
            {
                errors.Add($"mappings[{i}].sensorId", "validation.booking.sensor_id");
            }
            if (!onSite.Contains(m.ResourceId))
            {
                errors.Add($"mappings[{i}].resourceId", "validation.booking.sensor_resource");
            }
        }
        errors.ThrowIfAny();
        // The same sensor of this connection must not already belong to another site.
        var elsewhere = seen.Count == 0 ? false : await session.ScalarAsync<bool>(
            """
            select exists (select 1 from booking.sensor s join booking.resource r on r.id = s.resource_id
                           where s.connection_id = @connection and s.sensor_id = any(@sensors) and r.site_id <> @site)
            """, new { connection = request.ConnectionId, sensors = seen.ToArray(), site = site.Id }, ct);
        if (elsewhere)
        {
            throw AppException.Conflict("conflict.booking.sensor_taken");
        }
        await session.ExecAsync(
            """
            delete from booking.sensor s using booking.resource r
            where r.id = s.resource_id and r.site_id = @site and s.connection_id = @connection and not (s.sensor_id = any(@sensors))
            """, new { site = site.Id, connection = request.ConnectionId, sensors = seen.ToArray() }, ct);
        var now = clock.UtcNow;
        foreach (var m in mappings)
        {
            await session.ExecAsync(
                """
                insert into booking.sensor (tenant_id, id, connection_id, sensor_id, resource_id, created_by, created_at)
                values (platform.current_tenant_id(), @id, @connection, @sensor, @resource, @user, @now)
                on conflict (tenant_id, connection_id, sensor_id) do update set resource_id = excluded.resource_id,
                  last_occupied = case when booking.sensor.resource_id = excluded.resource_id then booking.sensor.last_occupied end,
                  last_observed_at = case when booking.sensor.resource_id = excluded.resource_id then booking.sensor.last_observed_at end
                """, new { id = Guid.NewGuid(), connection = request.ConnectionId, sensor = m.SensorId!.Trim(), resource = m.ResourceId, user = context.RequireUser(), now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSensorsMapped, Facilities.SiteType, site.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { connection = request.ConnectionId, mapped = mappings.Count }), ct);
        return await ViewAsync(site, ct);
    }

    /// <summary>
    /// Reads every connection with mappings that is due (US9 scenarios 2 and 3). Returns how many bookings were
    /// checked in by a sensor.
    /// </summary>
    internal async Task<int> RunDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.BookingSensors, ct))
        {
            return 0;
        }
        var due = await session.QueryAsync(
            """
            select distinct s.connection_id from booking.sensor s
            left join booking.sensor_read r on r.connection_id = s.connection_id
            where r.read_at is null or r.read_at <= @cutoff
            """, new { cutoff = now - Every + TimeSpan.FromSeconds(30) }, r => r.GetGuid(0), ct);
        var settings = await rules.SettingsAsync(ct);
        var checkedIn = 0;
        foreach (var connection in due)
        {
            var read = await readings.ReadAsync(connection, ct);
            await session.ExecAsync(
                """
                insert into booking.sensor_read (tenant_id, connection_id, read_at, result) values (platform.current_tenant_id(), @connection, @now, @result)
                on conflict (tenant_id, connection_id) do update set read_at = excluded.read_at, result = excluded.result
                """, new { connection, now, result = read.Result }, ct);
            if (read.Result != TrackerResults.Ok)
            {
                continue;
            }
            var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            foreach (var reading in read.Sensors)
            {
                var resource = await session.QuerySingleOrDefaultAsync(
                    """
                    update booking.sensor set last_occupied = @occupied, last_observed_at = @observed, last_read_at = @now
                    where connection_id = @connection and sensor_id = @sensor
                    returning resource_id
                    """, new { connection, sensor = reading.SensorId, occupied = reading.Occupied, observed = reading.ObservedAt, now }, r => (Guid?)r.GetGuid(0), ct);
                if (resource is not { } resourceId)
                {
                    continue;
                }
                await session.ExecAsync(
                    """
                    insert into booking.occupancy_hour (tenant_id, resource_id, hour, reads, occupied_reads)
                    values (platform.current_tenant_id(), @resourceId, @hour, 1, @occupied)
                    on conflict (tenant_id, resource_id, hour) do update set reads = booking.occupancy_hour.reads + 1,
                      occupied_reads = booking.occupancy_hour.occupied_reads + excluded.occupied_reads
                    """, new { resourceId, hour, occupied = reading.Occupied ? 1 : 0 }, ct);
                if (settings.SensorCheckIn && reading.Occupied)
                {
                    checkedIn += await CheckInAsync(resourceId, now, settings, ct);
                }
            }
        }
        return checkedIn;
    }

    /// <summary>Checks in the confirmed booking of the resource whose check-in window is open now (FR-027).</summary>
    private async Task<int> CheckInAsync(Guid resourceId, DateTimeOffset now, Settings settings, CancellationToken ct)
    {
        var done = await session.QueryAsync(
            """
            update booking.booking b set status = 'checked_in', checked_in_at = @now, check_in_method = 'sensor', row_version = b.row_version + 1
            from booking.resource r
            where r.id = b.resource_id and b.resource_id = @resourceId and r.check_in and b.status = 'confirmed'
              and b.starts_at - make_interval(mins => @before) <= @now and greatest(b.starts_at, b.created_at) + make_interval(mins => @grace) > @now
            returning b.id, b.resource_id, r.site_id, r.kind, b.holder_id
            """, new { resourceId, now, before = settings.CheckInBeforeMinutes, grace = settings.CheckInGraceMinutes },
            r => (Id: r.GetGuid(0), Resource: r.GetGuid(1), Site: r.GetGuid(2), Kind: r.GetString(3), Holder: r.GetGuid(4)), ct);
        foreach (var b in done)
        {
            await outbox.EnqueueAsync(new CheckedIn(b.Id, b.Resource, b.Site, b.Kind, b.Holder, CheckInMethods.Sensor), ct);
            await activity.RecordAsync("checked_in", b.Kind, b.Site, ct);
        }
        return done.Count;
    }

    /// <summary>
    /// Sensed occupied hours per resource and local day of the site for a range (FR-027): counts only. Null when sensors
    /// are not in the plan.
    /// </summary>
    internal async Task<IReadOnlyList<(Guid Resource, DateOnly Day, double Hours)>?> SensedAsync(IReadOnlyCollection<Guid> resourceIds, DateTimeOffset from, DateTimeOffset to,
        TimeZoneInfo zone, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.BookingSensors, ct))
        {
            return null;
        }
        var rows = await session.QueryAsync(
            "select resource_id, hour, occupied_reads from booking.occupancy_hour where resource_id = any(@ids) and hour >= @from and hour < @to and occupied_reads > 0",
            new { ids = resourceIds.ToArray(), from, to }, r => (Id: r.GetGuid(0), Hour: r.GetFieldValue<DateTimeOffset>(1), Reads: r.GetInt32(2)), ct);
        return rows.GroupBy(r => (r.Id, Day: Visits.LocalDay(r.Hour, zone)))
            .Select(g => (g.Key.Id, g.Key.Day, g.Sum(x => x.Reads) * Every.TotalHours)).ToList();
    }
}
