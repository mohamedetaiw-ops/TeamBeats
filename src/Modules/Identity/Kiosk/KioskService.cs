using QRCoder;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Kiosk;

/// <summary>
/// Kiosk devices and badges (spec 030 US4, FR-014 to FR-016). A device administrator creates a kiosk for an org unit;
/// the tablet redeems the one-time code and from then on presents an opaque device token. People sign in there with a
/// badge and a PIN; five wrong PINs lock the badge for 15 minutes on every kiosk of the tenant and tell its owner.
/// </summary>
public sealed class KioskService(
    IDbSession session,
    TenantContextAccessor context,
    IAuthorizationService authz,
    IAuthorizationProvisioning provisioning,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    IOrganizationHierarchy org,
    ITenantMemberships memberships,
    IEmployeeNumbers employeeNumbers,
    INotificationSender notifications,
    SessionService sessions)
{
    private const string Columns =
        "id, name, org_unit_id, location, features, idle_minutes, allow_other_units, enrol_expires_at, enrolled_at, last_seen_at, created_at, revoked_at, version, token_hash";

    private sealed record Row(Guid Id, string Name, Guid OrgUnitId, string? Location, string[] Features, int IdleMinutes, bool AllowOtherUnits,
        DateTimeOffset? EnrolExpiresAt, DateTimeOffset? EnrolledAt, DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt, int Version,
        bool HasToken)
    {
        public string Status => RevokedAt is not null ? KioskStatuses.Revoked : HasToken ? KioskStatuses.Enrolled : KioskStatuses.Waiting;

        public KioskDeviceView View => new(Id, Name, OrgUnitId, Location, Features, IdleMinutes, AllowOtherUnits, Status,
            Status == KioskStatuses.Waiting ? EnrolExpiresAt : null, EnrolledAt, LastSeenAt, CreatedAt, Version);
    }

    private sealed record Badge(Guid UserId, string? PinHash, int FailedCount, DateTimeOffset? LockedUntil, DateTimeOffset IssuedAt);

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetFieldValue<string[]>(4), r.GetInt32(5), r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetFieldValue<DateTimeOffset>(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10),
        r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11), r.GetInt32(12), !r.IsDBNull(13));

    // Administration (device administrators, FR-014).

    public async Task<IReadOnlyList<KioskDeviceView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        var coverage = await authz.CoverageAsync(Permissions.DeviceManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var rows = await session.QueryAsync($"select {Columns} from identity.kiosk_device order by revoked_at nulls first, name, id", null, Read, ct);
        return rows.Where(r => coverage.Covers(r.OrgUnitId)).Select(r => r.View).ToList();
    }

    public async Task<KioskEnrolment> CreateAsync(KioskDeviceRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        var errors = KioskValidation.Check(request, create: true);
        if (request.OrgUnitId is { } unit && !await org.UnitExistsAsync(unit, ct))
        {
            errors["orgUnitId"] = ["validation.kiosk.unit_unknown"];
        }
        KioskValidation.Throw(errors);
        await authz.DemandAsync(Permissions.DeviceManage, AuthzTarget.OrgUnit(request.OrgUnitId!.Value), ct);

        var id = Ids.New();
        var code = KioskCodes.NewEnrolCode();
        var expires = clock.UtcNow + KioskRules.EnrolCodeLifetime;
        var features = (request.Features ?? SharedDeviceRules.Features).Distinct().ToArray();
        await session.ExecAsync(
            """
            insert into identity.kiosk_device (tenant_id, id, name, org_unit_id, location, features, idle_minutes, allow_other_units, enrol_code_hash,
              enrol_expires_at, created_by)
            values (platform.current_tenant_id(), @id, @name, @unit, @location, @features, @idle, @other, @code, @expires, @by)
            """,
            new
            {
                id, name = request.Name!.Trim(), unit = request.OrgUnitId, location = Blank(request.Location), features,
                idle = request.IdleMinutes ?? KioskRules.DefaultIdleMinutes, other = request.AllowOtherUnits ?? false, code = KioskCodes.Hash(code), expires,
                by = context.RequireUser(),
            }, ct);
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskEnrolled, "kiosk_device", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Name, row.Location, row.Features, row.IdleMinutes, row.AllowOtherUnits }), ct);
        await outbox.EnqueueAsync(new DeviceEnrolled(id, row.OrgUnitId), ct);
        return new KioskEnrolment(row.View, code, expires);
    }

    public async Task<KioskDeviceView> UpdateAsync(Guid id, KioskDeviceRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        if (row.RevokedAt is not null)
        {
            throw AppException.Conflict("conflict.kiosk.revoked");
        }
        if (request.Version is { } version && version != row.Version)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        var errors = KioskValidation.Check(request, create: false);
        if (request.OrgUnitId is { } unit && unit != row.OrgUnitId && !await org.UnitExistsAsync(unit, ct))
        {
            errors["orgUnitId"] = ["validation.kiosk.unit_unknown"];
        }
        KioskValidation.Throw(errors);
        if (request.OrgUnitId is { } moved && moved != row.OrgUnitId)
        {
            await authz.DemandAsync(Permissions.DeviceManage, AuthzTarget.OrgUnit(moved), ct);
        }
        var next = row with
        {
            Name = request.Name?.Trim() ?? row.Name,
            OrgUnitId = request.OrgUnitId ?? row.OrgUnitId,
            Location = request.Location is null ? row.Location : Blank(request.Location),
            Features = request.Features?.Distinct().ToArray() ?? row.Features,
            IdleMinutes = request.IdleMinutes ?? row.IdleMinutes,
            AllowOtherUnits = request.AllowOtherUnits ?? row.AllowOtherUnits,
        };
        await session.ExecAsync(
            """
            update identity.kiosk_device set name = @Name, org_unit_id = @OrgUnitId, location = @Location, features = @Features, idle_minutes = @IdleMinutes,
              allow_other_units = @AllowOtherUnits, version = version + 1
            where id = @Id
            """, new { next.Id, next.Name, next.OrgUnitId, next.Location, next.Features, next.IdleMinutes, next.AllowOtherUnits }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskChanged, "kiosk_device", id.ToString(), TargetOrgUnitId: next.OrgUnitId,
            Changes: new
            {
                before = new { row.Name, row.OrgUnitId, row.Location, row.Features, row.IdleMinutes, row.AllowOtherUnits },
                after = new { next.Name, next.OrgUnitId, next.Location, next.Features, next.IdleMinutes, next.AllowOtherUnits },
            }), ct);
        return (await RowAsync(id, ct))!.View;
    }

    /// <summary>Revokes a kiosk (US4 scenario 5): its token stops working at once and every session on it ends.</summary>
    public async Task<KioskDeviceView> RevokeAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        if (row.RevokedAt is not null)
        {
            return row.View;
        }
        await session.ExecAsync(
            """
            update identity.kiosk_device set revoked_at = now(), revoked_by = @by, token_hash = null, enrol_code_hash = null, enrol_expires_at = null,
              version = version + 1
            where id = @id
            """, new { id, by = context.RequireUser() }, ct);
        var ended = await EndSessionsAsync(id, KioskRules.Revoked, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskRevoked, "kiosk_device", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { row.Name, sessionsEnded = ended }), ct);
        await outbox.EnqueueAsync(new DeviceRevoked(id, row.OrgUnitId), ct);
        return (await RowAsync(id, ct))!.View;
    }

    /// <summary>A new one-time code, to enrol a replacement tablet: the tablet enrolled before stops working.</summary>
    public async Task<KioskEnrolment> NewEnrolCodeAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        if (row.RevokedAt is not null)
        {
            throw AppException.Conflict("conflict.kiosk.revoked");
        }
        var code = KioskCodes.NewEnrolCode();
        var expires = clock.UtcNow + KioskRules.EnrolCodeLifetime;
        await session.ExecAsync(
            """
            update identity.kiosk_device set enrol_code_hash = @code, enrol_expires_at = @expires, token_hash = null, enrolled_at = null, version = version + 1
            where id = @id
            """, new { id, code = KioskCodes.Hash(code), expires }, ct);
        var ended = await EndSessionsAsync(id, KioskRules.Revoked, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskChanged, "kiosk_device", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { enrolCode = "renewed", sessionsEnded = ended }), ct);
        return new KioskEnrolment((await RowAsync(id, ct))!.View, code, expires);
    }

    // The tablet itself (anonymous on the tenant host).

    /// <summary>Redeems an enrolment code: the answer is the device token the tablet keeps in its cookie.</summary>
    public async Task<string> EnrolAsync(string? code, CancellationToken ct)
    {
        HostTenant();
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        var id = await session.ScalarAsync<Guid?>(
            "select id from identity.kiosk_device where enrol_code_hash = @h and enrol_expires_at > @now and revoked_at is null",
            new { h = KioskCodes.Hash(code ?? ""), now = clock.UtcNow }, ct);
        if (id is null)
        {
            throw AppException.Validation("code", "validation.kiosk.enrol_code");
        }
        var token = SessionTokens.NewToken();
        await session.ExecAsync(
            """
            update identity.kiosk_device set token_hash = @token, enrolled_at = now(), last_seen_at = now(), enrol_code_hash = null, enrol_expires_at = null,
              version = version + 1
            where id = @id
            """, new { id, token = SessionTokens.Hash(token) }, ct);
        var unit = await session.ScalarAsync<Guid>("select org_unit_id from identity.kiosk_device where id = @id", new { id }, ct);
        context.SetActor(ActorType.System, null);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskChanged, "kiosk_device", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { enrolled = true }), ct);
        context.SetActor(ActorType.Anonymous, null);
        return token;
    }

    /// <summary>Whether this tablet is an enrolled kiosk of the tenant. A revoked or unknown token reads as not enrolled.</summary>
    public async Task<KioskDeviceInfo> InfoAsync(string? token, CancellationToken ct)
    {
        HostTenant();
        if (!await entitlements.IsEnabledAsync(Capabilities.MobileKiosk, ct) || await ActiveDeviceAsync(token, ct) is not { } device)
        {
            return new KioskDeviceInfo(false, null, null, KioskRules.DefaultIdleMinutes);
        }
        await session.ExecAsync("update identity.kiosk_device set last_seen_at = now() where id = @id", new { id = device.Id }, ct);
        return new KioskDeviceInfo(true, device.Name, device.Location, device.IdleMinutes);
    }

    /// <summary>
    /// Badge and PIN sign-in on a kiosk (US4 scenarios 1 and 4, FR-015). The PIN is checked before anything about the
    /// person is, so a refusal says nothing about who holds the badge. A badge with no PIN yet sets one only when its
    /// printed code is scanned, never from the employee number.
    /// </summary>
    public async Task<(string Token, Guid UserId)> SignInAsync(string? deviceToken, KioskSignInRequest request, CancellationToken ct)
    {
        HostTenant();
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        var device = await ActiveDeviceAsync(deviceToken, ct) ?? throw AppException.Denied("authz.denied.kiosk_not_enrolled");
        var text = request.Badge?.Trim() ?? "";
        if (text.Length is 0 or > 100)
        {
            throw AppException.Validation("badge", "validation.required");
        }

        var code = KioskCodes.Normalize(text);
        var badge = code.StartsWith("TB", StringComparison.Ordinal) && code.Length == 22 ? await BadgeByCodeAsync(KioskCodes.Hash(code), ct) : null;
        var byCode = badge is not null;
        if (badge is null && (await employeeNumbers.ResolveAsync([text], ct)).TryGetValue(text, out var numbered))
        {
            badge = await BadgeOfAsync(numbered, ct);
        }
        if (badge is null)
        {
            await RefuseAsync(device, null, "unknown_badge", ct);
            throw AppException.Validation("pin", "validation.kiosk.not_accepted");
        }
        var now = clock.UtcNow;
        if (badge.LockedUntil > now)
        {
            await RefuseAsync(device, badge.UserId, "locked", ct);
            throw AppException.Conflict("conflict.kiosk.badge_locked");
        }

        if (badge.PinHash is null)
        {
            if (!byCode)
            {
                throw AppException.Conflict("conflict.kiosk.scan_to_set_pin");
            }
            if (!request.SetPin)
            {
                throw AppException.Conflict("conflict.kiosk.pin_not_set");
            }
            if (!KioskRules.ValidPin(request.Pin))
            {
                throw AppException.Validation("pin", "validation.kiosk.pin_format");
            }
            await EligibleAsync(device, badge.UserId, ct);
            await session.ExecAsync("update identity.kiosk_badge set pin_hash = @pin, pin_set_at = now(), failed_count = 0 where user_id = @u",
                new { pin = KioskPins.Hash(request.Pin!), u = badge.UserId }, ct);
        }
        else if (!KioskRules.ValidPin(request.Pin) || !KioskPins.Verify(request.Pin!, badge.PinHash))
        {
            await WrongPinAsync(device, badge, ct);
        }
        else
        {
            await EligibleAsync(device, badge.UserId, ct);
        }

        await session.ExecAsync("update identity.kiosk_badge set failed_count = 0 where user_id = @u and failed_count > 0", new { u = badge.UserId }, ct);
        // One person at a time: whoever was signed in on this kiosk before is signed out.
        await EndSessionsAsync(device.Id, KioskRules.NextPerson, ct);
        context.SetActor(ActorType.User, badge.UserId, null);
        var token = await sessions.CreateUserSessionAsync(badge.UserId, KioskRules.Method, ct, device.Id);
        await session.ExecAsync("update identity.kiosk_device set last_seen_at = now() where id = @id", new { id = device.Id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskSignIn, "user", badge.UserId.ToString(), TargetOrgUnitId: device.OrgUnitId,
            Changes: new { device = device.Id, via = byCode ? "badge" : "employee_number" }), ct);
        await outbox.EnqueueAsync(new UserSignedIn(badge.UserId, KioskRules.Method), ct);
        return (token, badge.UserId);
    }

    // Badges (device administrators issue them; people set and change their own PIN).

    public async Task<BadgeView> BadgeAsync(Guid userId, CancellationToken ct)
    {
        await ManagedUserAsync(userId, ct);
        return await BadgeViewAsync(userId, ct);
    }

    /// <summary>
    /// Issues a badge, or a new one for a lost badge: the old code stops working at once. The code and its QR image are
    /// answered this once, for printing; only the hash is kept. The PIN stays, so its owner keeps using it.
    /// </summary>
    public async Task<BadgeIssued> IssueBadgeAsync(Guid userId, CancellationToken ct)
    {
        await ManagedUserAsync(userId, ct);
        if (await session.ScalarAsync<string?>("select status from identity.user_account where id = @u and not test_identity", new { u = userId }, ct) != "active")
        {
            throw AppException.Conflict("conflict.kiosk.user_inactive");
        }
        var code = KioskCodes.NewBadgeCode();
        var reissued = await session.ScalarAsync<bool>("select exists (select 1 from identity.kiosk_badge where user_id = @u)", new { u = userId }, ct);
        await session.ExecAsync(
            """
            insert into identity.kiosk_badge (tenant_id, user_id, code_hash, issued_by)
            values (platform.current_tenant_id(), @u, @code, @by)
            on conflict (tenant_id, user_id) do update set code_hash = excluded.code_hash, issued_by = excluded.issued_by, issued_at = now()
            """, new { u = userId, code = KioskCodes.Hash(code), by = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceBadgeIssued, "user", userId.ToString(), Changes: new { reissued }), ct);
        using var data = QRCodeGenerator.GenerateQrCode(code, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(8, "#000000", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
        return new BadgeIssued(userId, code, svg);
    }

    /// <summary>Clears a forgotten PIN and any lock: the owner chooses a new one by scanning the badge on a kiosk.</summary>
    public async Task<BadgeView> ResetPinAsync(Guid userId, CancellationToken ct)
    {
        await ManagedUserAsync(userId, ct);
        var changed = await session.ExecAsync(
            "update identity.kiosk_badge set pin_hash = null, pin_set_at = null, failed_count = 0, locked_until = null where user_id = @u", new { u = userId }, ct);
        if (changed == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceBadgePinReset, "user", userId.ToString(), Changes: new { by = "administrator" }), ct);
        return await BadgeViewAsync(userId, ct);
    }

    public async Task<BadgeView> MyBadgeAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        return await BadgeViewAsync(context.RequireUser(), ct);
    }

    /// <summary>The person sets or changes their own PIN while signed in on their own device; this also lifts a lock.</summary>
    public async Task<BadgeView> SetMyPinAsync(string? pin, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        if (!KioskRules.ValidPin(pin))
        {
            throw AppException.Validation("pin", "validation.kiosk.pin_format");
        }
        var user = context.RequireUser();
        var changed = await session.ExecAsync(
            "update identity.kiosk_badge set pin_hash = @pin, pin_set_at = now(), failed_count = 0, locked_until = null where user_id = @u",
            new { pin = KioskPins.Hash(pin!), u = user }, ct);
        if (changed == 0)
        {
            throw AppException.Conflict("conflict.kiosk.no_badge");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceBadgePinReset, "user", user.ToString(), Changes: new { by = "self" }), ct);
        return await BadgeViewAsync(user, ct);
    }

    // Helpers.

    private async Task WrongPinAsync(Row device, Badge badge, CancellationToken ct)
    {
        var failures = badge.FailedCount + 1;
        if (failures >= KioskRules.MaxFailures)
        {
            var until = clock.UtcNow + KioskRules.Lockout;
            await session.ExecAsync("update identity.kiosk_badge set failed_count = 0, locked_until = @until where user_id = @u", new { until, u = badge.UserId }, ct);
            context.SetActor(ActorType.System, null);
            await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskLockout, "user", badge.UserId.ToString(), AuditOutcome.Denied, device.OrgUnitId,
                new { device = device.Id, failures, lockedUntil = until }), ct);
            await notifications.SendAsync(new NotificationRequest([badge.UserId], NotificationCategories.SecurityNotice, "kiosk.badge_locked", "/settings/kiosk",
                new Dictionary<string, string> { ["device"] = device.Name, ["minutes"] = ((int)KioskRules.Lockout.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                Urgent: true), ct);
            context.SetActor(ActorType.Anonymous, null);
            await session.CommitAsync(ct);
            throw AppException.Conflict("conflict.kiosk.badge_locked");
        }
        await session.ExecAsync("update identity.kiosk_badge set failed_count = @failures where user_id = @u", new { failures, u = badge.UserId }, ct);
        await RefuseAsync(device, badge.UserId, "wrong_pin", ct);
        throw AppException.Validation("pin", "validation.kiosk.not_accepted");
    }

    /// <summary>The person may use this kiosk: an active member, in the kiosk's unit or below unless it allows others, holding kiosk sign-in there.</summary>
    private async Task EligibleAsync(Row device, Guid userId, CancellationToken ct)
    {
        var active = await session.ScalarAsync<string?>("select status from identity.user_account where id = @u and not test_identity", new { u = userId }, ct) == "active"
            && await memberships.GetAsync(userId, ct) is { Status: MembershipStatus.Active };
        string? reason = active ? null : "no_access";
        if (reason is null && !device.AllowOtherUnits)
        {
            var inScope = false;
            foreach (var m in await org.GetActiveMembershipsAsync(userId, ct))
            {
                if (await org.IsAncestorOrSelfAsync(device.OrgUnitId, m.OrgUnitId, ct))
                {
                    inScope = true;
                    break;
                }
            }
            reason = inScope ? null : "other_unit";
        }
        if (reason is null && !await provisioning.UserHoldsAsync(userId, Permissions.KioskSignIn, AuthzTarget.OrgUnit(device.OrgUnitId), ct))
        {
            reason = "not_permitted";
        }
        if (reason is not null)
        {
            await RefuseAsync(device, userId, reason, ct);
            throw AppException.Denied("authz.denied.kiosk_not_allowed");
        }
    }

    /// <summary>Records a refused sign-in and keeps the record although the request fails.</summary>
    private async Task RefuseAsync(Row device, Guid? userId, string reason, CancellationToken ct)
    {
        context.SetActor(ActorType.Anonymous, null);
        await audit.WriteAsync(new AuditEntry(AuditActions.DeviceKioskSignIn, "user", userId?.ToString(), AuditOutcome.Denied, device.OrgUnitId,
            new { device = device.Id, reason }), ct);
        await session.CommitAsync(ct);
    }

    private Task<int> EndSessionsAsync(Guid deviceId, string reason, CancellationToken ct) =>
        session.ExecAsync("update identity.user_session set revoked_at = now(), revoked_reason = @r where kiosk_device_id = @id and revoked_at is null",
            new { id = deviceId, r = reason }, ct);

    private void HostTenant()
    {
        if (context.Host is not { Kind: HostScopeKind.Tenant, Tenant: { } tenant })
        {
            throw AppException.NotFound();
        }
        context.SetTenant(tenant.Id);
    }

    private async Task<Row?> ActiveDeviceAsync(string? token, CancellationToken ct) =>
        string.IsNullOrEmpty(token) ? null
            : await session.QuerySingleOrDefaultAsync($"select {Columns} from identity.kiosk_device where token_hash = @h and revoked_at is null",
                new { h = SessionTokens.Hash(token) }, Read, ct);

    private Task<Row?> RowAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from identity.kiosk_device where id = @id", new { id }, Read, ct);

    private async Task<Row> ManagedAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        await authz.DemandVisibleAsync(Permissions.DeviceManage, AuthzTarget.OrgUnit(row.OrgUnitId), ct);
        return row;
    }

    /// <summary>A device administrator handles badges of people in the units they manage devices for; others read as not found.</summary>
    private async Task ManagedUserAsync(Guid userId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.MobileKiosk, ct);
        var coverage = await authz.CoverageAsync(Permissions.DeviceManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        if (await memberships.GetAsync(userId, ct) is null)
        {
            throw AppException.NotFound();
        }
        if (!coverage.WholeTenant && !(await org.GetActiveMembershipsAsync(userId, ct)).Any(m => coverage.Covers(m.OrgUnitId)))
        {
            throw AppException.NotFound();
        }
    }

    private Task<Badge?> BadgeByCodeAsync(string hash, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync("select user_id, pin_hash, failed_count, locked_until, issued_at from identity.kiosk_badge where code_hash = @h",
            new { h = hash }, ReadBadge, ct);

    private Task<Badge?> BadgeOfAsync(Guid userId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync("select user_id, pin_hash, failed_count, locked_until, issued_at from identity.kiosk_badge where user_id = @u",
            new { u = userId }, ReadBadge, ct);

    private static Badge ReadBadge(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt32(2),
        r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4));

    private async Task<BadgeView> BadgeViewAsync(Guid userId, CancellationToken ct)
    {
        var badge = await BadgeOfAsync(userId, ct);
        return badge is null ? new BadgeView(userId, false, false, null, null)
            : new BadgeView(userId, true, badge.PinHash is not null, badge.IssuedAt, badge.LockedUntil > clock.UtcNow ? badge.LockedUntil : null);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
