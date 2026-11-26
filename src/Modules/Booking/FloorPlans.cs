using System.Buffers.Binary;
using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

public sealed record PositionRequest(Guid ResourceId, decimal? X, decimal? Y);

public sealed record PositionsRequest(IReadOnlyList<PositionRequest>? Positions);

/// <summary>A floor's plan image as the reader may fetch it. Url changes with each new image.</summary>
public sealed record FloorPlanImage(int Version, string ContentType, int Width, int Height, string Url);

/// <summary>A resource of the floor on its plan. X and Y are percentages; null when it is not placed.</summary>
public sealed record PlacedResource(Guid Id, string Kind, IReadOnlyDictionary<string, string> Names, int? Capacity, IReadOnlyList<string> Attributes, Guid? ZoneId,
    IReadOnlyDictionary<string, string>? Zone, decimal? X, decimal? Y, bool NeedsApproval, string State);

/// <summary>
/// The floor for a time (US7 scenario 2): its plan, and each resource as free (the viewer may book it now), busy (held by
/// a booking) or unavailable (rules, fixed desk). Holders are never named here.
/// </summary>
public sealed record FloorMapView(Guid FloorId, Guid SiteId, IReadOnlyDictionary<string, string> Names, string TimeZone, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    FloorPlanImage? Plan, IReadOnlyList<PlacedResource> Resources, bool Manages);

public static class PlaceStates
{
    public const string Free = "free";
    public const string Busy = "busy";
    public const string Unavailable = "unavailable";
}

/// <summary>
/// Floor plan maps (spec 031 R3 US7, FR-019 to FR-021). A facilities administrator of the site uploads one PNG, JPEG or
/// WebP image per floor (SVG is refused because it can carry script), checked by its signature and size and scanned for
/// malware before it is stored, and places the floor's resources on it as percentages. Anyone who may book sees the plan
/// with each resource's state for a time; booking from it goes through the same path and rules as search. The list the
/// page shows next to the map is the same <see cref="FloorMapView.Resources"/>, in the same order.
/// </summary>
public sealed class FloorPlans(
    IDbSession session,
    ITenantContext context,
    Facilities facilities,
    Bookings bookings,
    IEntitlementService entitlements,
    TenantObjectStorage storage,
    IMalwareScanner scanner,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxSide = 8000;

    private sealed record FloorRow(Guid Id, Guid SiteId, Dictionary<string, string> Names, string Status, string? PlanType, int? Width, int? Height, int PlanVersion);

    private Task<FloorRow?> FloorAsync(Guid floorId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            "select id, site_id, names::text, status, plan_type, plan_width, plan_height, plan_version from booking.floor where id = @floorId", new { floorId },
            r => new FloorRow(r.GetGuid(0), r.GetGuid(1), Names.Read(r, 2), r.GetString(3), r.Get<string>("plan_type"), r.Get<int?>("plan_width"), r.Get<int?>("plan_height"),
                r.GetInt32(7)), ct);

    internal static string ObjectKey(Guid floorId, int version) => $"booking/floor-plans/{floorId:N}/{version.ToString(CultureInfo.InvariantCulture)}";

    private static FloorPlanImage? Image(FloorRow f) => f.PlanType is { } type && f.Width is { } w && f.Height is { } h
        ? new FloorPlanImage(f.PlanVersion, type, w, h, $"/api/v1/booking/floors/{f.Id}/plan?v={f.PlanVersion.ToString(CultureInfo.InvariantCulture)}")
        : null;

    private async Task<(FloorRow Floor, SiteRow Site)> ManagedFloorAsync(Guid floorId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.BookingFloorPlans, ct);
        var floor = await FloorAsync(floorId, ct) ?? throw AppException.NotFound();
        var site = await facilities.ManagedSiteAsync(floor.SiteId, ct);
        if (floor.Status != "active" || site.Status != "active")
        {
            throw AppException.NotFound();
        }
        return (floor, site);
    }

    /// <summary>Stores a new plan image for the floor (US7 scenario 1). Positions stay, as they are relative to the image.</summary>
    public async Task<FloorPlanImage> UploadAsync(Guid floorId, string? contentType, byte[] data, CancellationToken ct)
    {
        var (floor, site) = await ManagedFloorAsync(floorId, ct);
        if (data.Length == 0 || data.Length > MaxBytes)
        {
            throw AppException.Validation("file", "validation.booking.plan_size");
        }
        if (Probe(data) is not { } probe)
        {
            throw AppException.Validation("file", "validation.booking.plan_type");
        }
        var declared = (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (declared.Length > 0 && declared != probe.Type)
        {
            throw AppException.Validation("file", "validation.booking.plan_type");
        }
        if (probe.Width is < 1 or > MaxSide || probe.Height is < 1 or > MaxSide)
        {
            throw AppException.Validation("file", "validation.booking.plan_dimensions");
        }
        ScanVerdict verdict;
        using (var scan = new MemoryStream(data, writable: false))
        {
            verdict = await scanner.ScanAsync(scan, ct);
        }
        if (verdict != ScanVerdict.Clean)
        {
            throw AppException.Validation("file", verdict == ScanVerdict.Infected ? "validation.booking.plan_infected" : "validation.booking.plan_scan_failed");
        }
        var version = floor.PlanVersion + 1;
        using (var stream = new MemoryStream(data, writable: false))
        {
            await storage.PutAsync(ObjectKey(floorId, version), stream, probe.Type, ct);
        }
        await session.ExecAsync(
            """
            update booking.floor set plan_type = @type, plan_width = @width, plan_height = @height, plan_bytes = @bytes, plan_version = @version,
              plan_updated_by = @user, plan_updated_at = @now
            where id = @floorId
            """, new { floorId, type = probe.Type, width = probe.Width, height = probe.Height, bytes = data.Length, version, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingFloorPlanChanged, "booking_floor", floorId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { change = "image", version, width = probe.Width, height = probe.Height }), ct);
        return Image((await FloorAsync(floorId, ct))!)!;
    }

    /// <summary>Takes the plan away. Positions go with it.</summary>
    public async Task RemoveAsync(Guid floorId, CancellationToken ct)
    {
        var (floor, site) = await ManagedFloorAsync(floorId, ct);
        if (floor.PlanType is null)
        {
            return;
        }
        await session.ExecAsync(
            "update booking.floor set plan_type = null, plan_width = null, plan_height = null, plan_bytes = null, plan_updated_by = @user, plan_updated_at = @now where id = @floorId",
            new { floorId, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await session.ExecAsync("update booking.resource set plan_x = null, plan_y = null where floor_id = @floorId", new { floorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingFloorPlanChanged, "booking_floor", floorId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { change = "removed" }), ct);
    }

    /// <summary>Places resources of this floor on its plan, or takes them off with null positions.</summary>
    public async Task<FloorMapView> PlaceAsync(Guid floorId, PositionsRequest request, CancellationToken ct)
    {
        var (floor, site) = await ManagedFloorAsync(floorId, ct);
        if (floor.PlanType is null)
        {
            throw AppException.Conflict("conflict.booking.plan_missing");
        }
        var positions = request.Positions ?? [];
        var errors = new ValidationErrors();
        if (positions.Count is 0 or > 2000)
        {
            errors.Add("positions", "validation.booking.plan_positions");
        }
        var ids = positions.Select(p => p.ResourceId).Distinct().ToArray();
        var onFloor = ids.Length == 0 ? [] : (await session.QueryAsync(
            "select id from booking.resource where id = any(@ids) and floor_id = @floorId and status = 'active'", new { ids, floorId }, r => r.GetGuid(0), ct)).ToHashSet();
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            if (!onFloor.Contains(p.ResourceId))
            {
                errors.Add($"positions[{i}].resourceId", "validation.booking.plan_resource");
            }
            if ((p.X is null) != (p.Y is null) || p.X is < 0 or > 100 || p.Y is < 0 or > 100)
            {
                errors.Add($"positions[{i}]", "validation.booking.plan_position");
            }
        }
        errors.ThrowIfAny();
        foreach (var p in positions)
        {
            await session.ExecAsync("update booking.resource set plan_x = @x, plan_y = @y where id = @id",
                new { id = p.ResourceId, x = p.X is { } x ? Math.Round(x, 2, MidpointRounding.AwayFromZero) : (decimal?)null, y = p.Y is { } y ? Math.Round(y, 2, MidpointRounding.AwayFromZero) : (decimal?)null }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingFloorPlanChanged, "booking_floor", floorId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { change = "positions", placed = positions.Count(p => p.X is not null), removed = positions.Count(p => p.X is null) }), ct);
        return await MapAsync(floorId, null, null, null, ct);
    }

    /// <summary>The plan image, for anyone who may book at the site.</summary>
    public async Task<(Stream Content, string ContentType)> ImageAsync(Guid floorId, CancellationToken ct)
    {
        var floor = await ReadableFloorAsync(floorId, ct);
        if (floor.PlanType is null)
        {
            throw AppException.NotFound();
        }
        var content = await storage.OpenReadAsync(ObjectKey(floorId, floor.PlanVersion), ct) ?? throw AppException.NotFound();
        return (content, floor.PlanType);
    }

    private async Task<FloorRow> ReadableFloorAsync(Guid floorId, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await entitlements.DemandAsync(Capabilities.BookingFloorPlans, ct);
        var floor = await FloorAsync(floorId, ct) ?? throw AppException.NotFound();
        await facilities.ActiveSiteAsync(floor.SiteId, ct);
        return floor.Status == "active" ? floor : throw AppException.NotFound();
    }

    /// <summary>
    /// The floor for a time (US7 scenario 2). Without a time, the next whole hour for one hour in the site's time zone.
    /// Free means search would offer it to the viewer for that time.
    /// </summary>
    public async Task<FloorMapView> MapAsync(Guid floorId, DateOnly? date, TimeOnly? start, TimeOnly? end, CancellationToken ct)
    {
        var floor = await ReadableFloorAsync(floorId, ct);
        var site = await facilities.ActiveSiteAsync(floor.SiteId, ct);
        if (date is null || start is null || end is null)
        {
            var local = TimeZoneInfo.ConvertTime(clock.UtcNow, site.Zone);
            var next = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0).AddHours(1);
            date ??= DateOnly.FromDateTime(next);
            start ??= TimeOnly.FromDateTime(next);
            end ??= start.Value.AddHours(1);
        }
        var found = await bookings.SearchAsync(site.Id, null, date, start, end, null, null, null, ct);
        var free = found.Resources.Select(r => r.Id).ToHashSet();
        var kinds = (await facilities.KindsAsync(ct)).ToArray();
        var rows = await session.QueryAsync(
            """
            select r.id, r.kind, r.names::text, r.capacity, r.attributes, r.zone_id, z.names::text, r.plan_x, r.plan_y, r.needs_approval,
              exists (select 1 from booking.booking b where b.resource_id = r.id and b.status = any(@live) and b.held && tstzrange(@from, @to, '[)'))
            from booking.resource r left join booking.zone z on z.id = r.zone_id
            where r.floor_id = @floorId and r.status = 'active' and r.kind = any(@kinds)
            order by z.names->>'en' nulls first, r.kind, r.names->>'en', r.id
            """, new { floorId, kinds, live = BookingStatuses.Live, from = found.StartsAt, to = found.EndsAt },
            r => new PlacedResource(r.GetGuid(0), r.GetString(1), Names.Read(r, 2), r.Get<int?>("capacity"), r.GetFieldValue<string[]>(4), r.Get<Guid?>("zone_id"),
                r.IsDBNull(6) ? null : Names.Read(r, 6), r.Get<decimal?>("plan_x"), r.Get<decimal?>("plan_y"), r.GetBoolean(9),
                free.Contains(r.GetGuid(0)) ? PlaceStates.Free : r.GetBoolean(10) ? PlaceStates.Busy : PlaceStates.Unavailable), ct);
        return new FloorMapView(floor.Id, site.Id, floor.Names, site.TimeZone, found.StartsAt, found.EndsAt, Image(floor), rows, await facilities.ManagesAsync(site, ct));
    }

    // ---- Image checks ----

    internal sealed record Probed(string Type, int Width, int Height);

    /// <summary>The type and size of a PNG, JPEG or WebP from its own bytes; null for anything else.</summary>
    internal static Probed? Probe(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 24 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && d.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return new("image/png", BinaryPrimitives.ReadInt32BigEndian(d.Slice(16, 4)), BinaryPrimitives.ReadInt32BigEndian(d.Slice(20, 4)));
        }
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF)
                {
                    return null;
                }
                var marker = d[i + 1];
                if (marker == 0xFF)
                {
                    i++;
                    continue;
                }
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                {
                    return new("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 7, 2)), BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 5, 2)));
                }
                if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
                {
                    i += 2;
                    continue;
                }
                i += 2 + BinaryPrimitives.ReadUInt16BigEndian(d.Slice(i + 2, 2));
            }
            return null;
        }
        if (d.Length >= 30 && d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            var chunk = d.Slice(12, 4);
            if (chunk.SequenceEqual("VP8X"u8))
            {
                return new("image/webp", 1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
            }
            if (chunk.SequenceEqual("VP8 "u8) && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
            {
                return new("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF);
            }
            if (chunk.SequenceEqual("VP8L"u8) && d[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
                return new("image/webp", (int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
        }
        return null;
    }
}
