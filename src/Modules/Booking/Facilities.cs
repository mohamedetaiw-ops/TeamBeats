using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <summary>
/// Sites, floors, zones and resources (spec 031 US1, FR-001, FR-002). Facilities administrators hold
/// <c>booking.manage</c> in the scope of a site's org unit, or tenant-wide for a site without one. Each resource gets an
/// opaque QR code; a room whose address is a Microsoft 365 room mailbox is refused, so it is never booked in both.
/// </summary>
public sealed partial class Facilities(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    ITenantSettingsReader tenantSettings,
    IRoomMailboxes roomMailboxes,
    BookingCancellations cancellations,
    IAuditWriter audit,
    IClock clock)
{
    public const string SiteType = "booking_site";
    public const string ResourceType = "booking_resource";
    public const int MaxAttributes = 20;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]{1,190}\.[^@\s]{2,}$")]
    private static partial Regex MailboxPattern();

    /// <summary>The kinds of resource in the plan (section 9). Nothing to manage or book when none is.</summary>
    internal async Task<IReadOnlyList<string>> KindsAsync(CancellationToken ct)
    {
        var kinds = new List<string>();
        foreach (var kind in ResourceKinds.All)
        {
            if (await entitlements.IsEnabledAsync(ResourceKinds.Capability(kind), ct))
            {
                kinds.Add(kind);
            }
        }
        return kinds;
    }

    internal async Task RequireAnyAsync(CancellationToken ct)
    {
        if ((await KindsAsync(ct)).Count == 0)
        {
            throw AppException.NotInPackage(Capabilities.BookingDesks);
        }
    }

    internal static AuthzTarget Scope(Guid? orgUnitId) => orgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;

    internal async Task<bool> ManagesAsync(SiteRow site, CancellationToken ct) => (await authz.CheckAsync(Permissions.BookingManage, Scope(site.OrgUnitId), ct)).Allowed;

    public async Task<BookingRights> RightsAsync(CancellationToken ct)
    {
        var kinds = await KindsAsync(ct);
        if (kinds.Count == 0)
        {
            return new BookingRights(false, false, false, []);
        }
        var book = (await authz.CheckAsync(Permissions.BookingBook, AuthzTarget.Tenant, ct)).Allowed;
        var manage = await authz.HoldsAnywhereAsync(Permissions.BookingManage, ct);
        var report = await entitlements.IsEnabledAsync(Capabilities.BookingReports, ct) && await authz.HoldsAnywhereAsync(Permissions.BookingReport, ct);
        return new BookingRights(book, manage, report, kinds);
    }

    // ---- Sites ----

    internal Task<SiteRow?> SiteRowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {SiteRow.Columns} from booking.site where id = @id", new { id }, SiteRow.Read, ct);

    /// <summary>An active site anyone in the tenant may see.</summary>
    internal async Task<SiteRow> ActiveSiteAsync(Guid id, CancellationToken ct) =>
        await SiteRowAsync(id, ct) is { Status: "active" } site ? site : throw AppException.NotFound();

    /// <summary>A site the viewer manages. Others are told it is not found, so a site outside their scope stays out of reach.</summary>
    internal async Task<SiteRow> ManagedSiteAsync(Guid id, CancellationToken ct)
    {
        await RequireAnyAsync(ct);
        var site = await SiteRowAsync(id, ct) ?? throw AppException.NotFound();
        if (!await ManagesAsync(site, ct))
        {
            throw await authz.HoldsAnywhereAsync(Permissions.BookingManage, ct) ? AppException.NotFound() : AppException.Denied("authz.denied.booking_manage");
        }
        return site;
    }

    private async Task<SiteView> SiteViewAsync(SiteRow s, CancellationToken ct) =>
        new(s.Id, s.Names, s.LocationKey, s.OrgUnitId, s.TimeZone, s.OpensAt, s.ClosesAt, s.Status, await ManagesAsync(s, ct), s.Version);

    /// <summary>Active sites for booking; with <paramref name="managed"/>, every site the viewer manages, removed ones too.</summary>
    public async Task<IReadOnlyList<SiteView>> SitesAsync(bool managed, CancellationToken ct)
    {
        await RequireAnyAsync(ct);
        var rows = await session.QueryAsync($"select {SiteRow.Columns} from booking.site where (@managed or status = 'active') order by created_at, id",
            new { managed }, SiteRow.Read, ct);
        var views = new List<SiteView>();
        foreach (var row in rows)
        {
            var view = await SiteViewAsync(row, ct);
            if (!managed || view.Manages)
            {
                views.Add(view);
            }
        }
        return views;
    }

    internal static Dictionary<string, string> CleanNames(Dictionary<string, string>? names, string field, ValidationErrors errors)
    {
        var clean = (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key.Trim(), n => n.Value.Trim(), StringComparer.Ordinal);
        if (clean.Count == 0 || clean.Count > 10 || clean.Any(n => n.Value.Length > Names.MaxLength || !Primitives.Locales.IsSupported(n.Key)))
        {
            errors.Add(field, "validation.booking.name");
        }
        return clean;
    }

    private async Task<(Dictionary<string, string> Names, string Zone, TimeOnly Opens, TimeOnly Closes)> ValidateSiteAsync(SiteRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var names = CleanNames(request.Names, "names", errors);
        var zone = string.IsNullOrWhiteSpace(request.TimeZone) ? (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone : request.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            errors.Add("timeZone", "validation.booking.time_zone");
        }
        var opens = request.OpensAt ?? new TimeOnly(8, 0);
        var closes = request.ClosesAt ?? new TimeOnly(18, 0);
        if (opens >= closes)
        {
            errors.Add("closesAt", "validation.booking.hours");
        }
        if (request.LocationKey is { Length: > 64 })
        {
            errors.Add("locationKey", "validation.booking.location");
        }
        if (request.OrgUnitId is { } unit && !(await hierarchy.GetPathsAsync([unit], ct)).ContainsKey(unit))
        {
            errors.Add("orgUnitId", "validation.booking.unit");
        }
        errors.ThrowIfAny();
        return (names, zone, opens, closes);
    }

    public async Task<SiteView> CreateSiteAsync(SiteRequest request, CancellationToken ct)
    {
        await RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingManage, Scope(request.OrgUnitId), ct);
        var (names, zone, opens, closes) = await ValidateSiteAsync(request, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into booking.site (tenant_id, id, names, location_key, org_unit_id, time_zone, opens_at, closes_at, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @names, @location, @unit, @zone, @opens, @closes, @actor, @now, @now)
            """, new { id, names = Jsonb.From(names), location = Blank(request.LocationKey), unit = request.OrgUnitId, zone, opens, closes, actor = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSiteChanged, SiteType, id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { change = "created", timeZone = zone }), ct);
        return await SiteViewAsync((await SiteRowAsync(id, ct))!, ct);
    }

    public async Task<SiteView> UpdateSiteAsync(Guid id, SiteRequest request, CancellationToken ct)
    {
        var site = await ManagedSiteAsync(id, ct);
        if (request.Version is { } v && v != site.Version)
        {
            throw AppException.Conflict("conflict.booking.changed");
        }
        // Moving a site to another unit needs the right in the new scope too.
        if (request.OrgUnitId != site.OrgUnitId)
        {
            await authz.DemandAsync(Permissions.BookingManage, Scope(request.OrgUnitId), ct);
        }
        var (names, zone, opens, closes) = await ValidateSiteAsync(request, ct);
        await session.ExecAsync(
            """
            update booking.site set names = @names, location_key = @location, org_unit_id = @unit, time_zone = @zone, opens_at = @opens, closes_at = @closes,
              updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id, names = Jsonb.From(names), location = Blank(request.LocationKey), unit = request.OrgUnitId, zone, opens, closes, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSiteChanged, SiteType, id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { change = "updated", timeZone = zone, previousUnit = site.OrgUnitId }), ct);
        return await SiteViewAsync((await SiteRowAsync(id, ct))!, ct);
    }

    /// <summary>Removes a site and its resources; holders of future bookings are told (FR-015).</summary>
    public async Task RemoveSiteAsync(Guid id, CancellationToken ct)
    {
        var site = await ManagedSiteAsync(id, ct);
        if (site.Status != "active")
        {
            return;
        }
        var resources = await session.QueryAsync("select id from booking.resource where site_id = @id and status = 'active'", new { id }, r => r.GetGuid(0), ct);
        foreach (var resource in resources)
        {
            await RemoveResourceCoreAsync(resource, "site_removed", ct);
        }
        await session.ExecAsync("update booking.site set status = 'removed', updated_at = @now, row_version = row_version + 1 where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSiteChanged, SiteType, id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { change = "removed", resources = resources.Count }), ct);
    }

    public async Task<SiteDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var site = await ManagedSiteAsync(id, ct);
        var floors = await session.QueryAsync("select id, names::text, sort from booking.floor where site_id = @id and status = 'active' order by sort, id",
            new { id }, r => (Id: r.GetGuid(0), Names: Names.Read(r, 1), Sort: r.GetInt32(2)), ct);
        var zones = await session.QueryAsync(
            "select z.id, z.floor_id, z.names::text from booking.zone z join booking.floor f on f.id = z.floor_id where f.site_id = @id and z.status = 'active' order by z.id",
            new { id }, r => (Id: r.GetGuid(0), Floor: r.GetGuid(1), Names: Names.Read(r, 2)), ct);
        var resources = await session.QueryAsync($"select {ResourceRow.Columns} from booking.resource where site_id = @id and status = 'active' order by kind, created_at, id",
            new { id }, ResourceRow.Read, ct);
        return new SiteDetail(await SiteViewAsync(site, ct),
            floors.Select(f => new FloorView(f.Id, f.Names, f.Sort, zones.Where(z => z.Floor == f.Id).Select(z => new ZoneView(z.Id, z.Names)).ToList())).ToList(),
            resources.Select(r => View(r, true)).ToList());
    }

    // ---- Floors and zones ----

    public async Task<FloorView> AddFloorAsync(Guid siteId, PlaceRequest request, CancellationToken ct)
    {
        await ManagedSiteAsync(siteId, ct);
        var errors = new ValidationErrors();
        var names = CleanNames(request.Names, "names", errors);
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync("insert into booking.floor (tenant_id, id, site_id, names, sort) values (platform.current_tenant_id(), @id, @siteId, @names, @sort)",
            new { id, siteId, names = Jsonb.From(names), sort = request.Sort ?? 0 }, ct);
        return new FloorView(id, names, request.Sort ?? 0, []);
    }

    public async Task<ZoneView> AddZoneAsync(Guid siteId, Guid floorId, PlaceRequest request, CancellationToken ct)
    {
        await ManagedSiteAsync(siteId, ct);
        if (!await session.ScalarAsync<bool>("select exists (select 1 from booking.floor where id = @floorId and site_id = @siteId and status = 'active')", new { floorId, siteId }, ct))
        {
            throw AppException.NotFound();
        }
        var errors = new ValidationErrors();
        var names = CleanNames(request.Names, "names", errors);
        errors.ThrowIfAny();
        var id = Ids.New();
        await session.ExecAsync("insert into booking.zone (tenant_id, id, floor_id, names) values (platform.current_tenant_id(), @id, @floorId, @names)",
            new { id, floorId, names = Jsonb.From(names) }, ct);
        return new ZoneView(id, names);
    }

    // ---- Resources ----

    internal Task<ResourceRow?> ResourceRowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {ResourceRow.Columns} from booking.resource where id = @id", new { id }, ResourceRow.Read, ct);

    internal static ResourceView View(ResourceRow r, bool manages) => new(r.Id, r.SiteId, r.FloorId, r.ZoneId, r.Kind, r.Names, r.Capacity, r.Attributes, manages ? r.FixedUserId : null,
        manages ? r.Mailbox : null, r.CheckIn, r.NeedsApproval, r.Status, manages ? r.QrCode : null, r.Version);

    private async Task<(Dictionary<string, string> Names, string[] Attributes, string? Mailbox)> ValidateResourceAsync(Guid siteId, string kind, ResourceRequest request, Guid? self,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var names = CleanNames(request.Names, "names", errors);
        if (request.ZoneId is not null && request.FloorId is null)
        {
            errors.Add("zoneId", "validation.booking.zone");
        }
        if (request.FloorId is { } floor && !await session.ScalarAsync<bool>(
                "select exists (select 1 from booking.floor where id = @floor and site_id = @siteId and status = 'active')", new { floor, siteId }, ct))
        {
            errors.Add("floorId", "validation.booking.floor");
        }
        if (request.ZoneId is { } zone && !await session.ScalarAsync<bool>(
                "select exists (select 1 from booking.zone where id = @zone and floor_id = @floor and status = 'active')", new { zone, floor = request.FloorId }, ct))
        {
            errors.Add("zoneId", "validation.booking.zone");
        }
        if (kind == ResourceKinds.Room ? request.Capacity is not (>= 1 and <= 1000) : request.Capacity is not null and not (>= 1 and <= 1000))
        {
            errors.Add("capacity", "validation.booking.capacity");
        }
        var attributes = (request.Attributes ?? []).Select(a => a.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (attributes.Length > MaxAttributes || attributes.Any(a => !AttributePattern().IsMatch(a)))
        {
            errors.Add("attributes", "validation.booking.attributes");
        }
        if (request.FixedUserId is { } fixedUser
            && (kind != ResourceKinds.Desk || await memberships.GetAsync(fixedUser, ct) is not { Status: MembershipStatus.Active }))
        {
            errors.Add("fixedUserId", "validation.booking.fixed_user");
        }
        var mailbox = Blank(request.Mailbox)?.ToLowerInvariant();
        if (mailbox is not null && (kind != ResourceKinds.Room || mailbox.Length > 254 || !MailboxPattern().IsMatch(mailbox)))
        {
            errors.Add("mailbox", "validation.booking.mailbox");
        }
        errors.ThrowIfAny();
        if (mailbox is not null)
        {
            // FR-002: a Microsoft 365 room mailbox is booked through Outlook (spec 018), never here as well.
            if (await roomMailboxes.IsRoomMailboxAsync(mailbox, ct))
            {
                throw AppException.Conflict("conflict.booking.room_mailbox");
            }
            if (await session.ScalarAsync<bool>("select exists (select 1 from booking.resource where lower(mailbox) = @mailbox and status = 'active' and id <> @self)",
                    new { mailbox, self = self ?? Guid.Empty }, ct))
            {
                throw AppException.Conflict("conflict.booking.mailbox_taken");
            }
        }
        return (names, attributes, mailbox);
    }

    public async Task<ResourceView> CreateResourceAsync(ResourceRequest request, CancellationToken ct)
    {
        var site = await ManagedSiteAsync(request.SiteId ?? throw AppException.Validation("siteId", "validation.booking.site"), ct);
        if (site.Status != "active")
        {
            throw AppException.Conflict("conflict.booking.site_removed");
        }
        var kind = request.Kind ?? "";
        if (!ResourceKinds.All.Contains(kind))
        {
            throw AppException.Validation("kind", "validation.booking.kind");
        }
        await entitlements.DemandAsync(ResourceKinds.Capability(kind), ct);
        var (names, attributes, mailbox) = await ValidateResourceAsync(site.Id, kind, request, null, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into booking.resource (tenant_id, id, site_id, floor_id, zone_id, kind, names, capacity, attributes, fixed_user_id, mailbox, check_in, needs_approval,
              qr_code, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @site, @floor, @zone, @kind, @names, @capacity, @attributes, @fixedUser, @mailbox, @checkIn, false,
              @qr, @actor, @now, @now)
            """, new
            {
                id, site = site.Id, floor = request.FloorId, zone = request.ZoneId, kind, names = Jsonb.From(names), capacity = request.Capacity, attributes,
                fixedUser = request.FixedUserId, mailbox, checkIn = request.CheckIn ?? kind != ResourceKinds.Equipment, qr = NewCode(), actor = context.RequireUser(), now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingResourceCreated, ResourceType, id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { kind, site = site.Id, capacity = request.Capacity }), ct);
        return View((await ResourceRowAsync(id, ct))!, true);
    }

    public async Task<ResourceView> UpdateResourceAsync(Guid id, ResourceRequest request, CancellationToken ct)
    {
        var resource = await ResourceRowAsync(id, ct) ?? throw AppException.NotFound();
        await ManagedSiteAsync(resource.SiteId, ct);
        if (!resource.Active)
        {
            throw AppException.Conflict("conflict.booking.resource_removed");
        }
        if (request.Version is { } v && v != resource.Version)
        {
            throw AppException.Conflict("conflict.booking.changed");
        }
        var (names, attributes, mailbox) = await ValidateResourceAsync(resource.SiteId, resource.Kind, request, id, ct);
        await session.ExecAsync(
            """
            update booking.resource set floor_id = @floor, zone_id = @zone, names = @names, capacity = @capacity, attributes = @attributes, fixed_user_id = @fixedUser,
              mailbox = @mailbox, check_in = @checkIn, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new
            {
                id, floor = request.FloorId, zone = request.ZoneId, names = Jsonb.From(names), capacity = request.Capacity, attributes, fixedUser = request.FixedUserId,
                mailbox, checkIn = request.CheckIn ?? resource.CheckIn, now = clock.UtcNow,
            }, ct);
        return View((await ResourceRowAsync(id, ct))!, true);
    }

    /// <summary>Removes a resource. Holders of its future bookings are told and pointed to the same zone (FR-015).</summary>
    public async Task RemoveResourceAsync(Guid id, CancellationToken ct)
    {
        var resource = await ResourceRowAsync(id, ct) ?? throw AppException.NotFound();
        await ManagedSiteAsync(resource.SiteId, ct);
        if (resource.Active)
        {
            await RemoveResourceCoreAsync(id, "resource_removed", ct);
        }
    }

    private async Task RemoveResourceCoreAsync(Guid id, string reason, CancellationToken ct)
    {
        var resource = (await ResourceRowAsync(id, ct))!;
        var site = (await SiteRowAsync(resource.SiteId, ct))!;
        await session.ExecAsync("update booking.resource set status = 'removed', mailbox = null, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, now = clock.UtcNow }, ct);
        var cancelled = await cancellations.CancelFutureAsync("resource_id = @key", id, reason, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingResourceRemoved, ResourceType, id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { kind = resource.Kind, reason, cancelled }), ct);
    }

    /// <summary>The QR labels of a site's resources to print (US1 scenario 1).</summary>
    public async Task<IReadOnlyList<QrLabel>> LabelsAsync(Guid siteId, CancellationToken ct)
    {
        await ManagedSiteAsync(siteId, ct);
        return await session.QueryAsync(
            """
            select r.id, r.kind, r.names::text, f.names::text, z.names::text, r.qr_code
            from booking.resource r
            left join booking.floor f on f.id = r.floor_id
            left join booking.zone z on z.id = r.zone_id
            where r.site_id = @siteId and r.status = 'active'
            order by f.sort nulls first, r.kind, r.created_at, r.id
            """, new { siteId },
            r => new QrLabel(r.GetGuid(0), r.GetString(1), Names.Read(r, 2), r.IsDBNull(3) ? null : Names.Read(r, 3), r.IsDBNull(4) ? null : Names.Read(r, 4), r.GetString(5),
                CheckInLink(r.GetString(5))), ct);
    }

    /// <summary>Where the printed code leads: the check-in page of the scanning user's own tenant.</summary>
    public static string CheckInLink(string code) => $"/booking/scan/{code}";

    /// <summary>128 random bits; the code says nothing about the tenant or the resource.</summary>
    private static string NewCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
