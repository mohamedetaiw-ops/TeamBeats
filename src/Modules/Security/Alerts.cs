using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

public static class AlertKinds
{
    public const string DownloadVolume = "download_volume";
    public const string PrivilegeGrantOutOfHours = "privilege_grant_out_of_hours";
    public const string NetworkBlocked = "network_blocked";

    /// <summary>A refresh token of an app came back a second time (spec 035 US6 scenario 2); its whole family was revoked.</summary>
    public const string ApiTokenReuse = "api_token_reuse";

    /// <summary>A SIEM destination has failed for 24 hours (spec 026 US4); events wait at its place in the stream.</summary>
    public const string SiemFailing = "siem_failing";

    /// <summary>A guest opened a Confidential item (spec 026 US5 scenario 3).</summary>
    public const string GuestConfidentialAccess = "guest_confidential_access";

    /// <summary>An app's calls were refused by authorization more often than the tenant threshold in 10 minutes (US5 scenario 5).</summary>
    public const string ApiDeniedBurst = "api_denied_burst";

    /// <summary>An app's calls in one hour were over the tenant factor times its 30 day hourly average (US5 scenario 5).</summary>
    public const string ApiVolumeSpike = "api_volume_spike";

    /// <summary>The kinds about an app, which API administrators are told about too.</summary>
    public static readonly IReadOnlyList<string> AboutApps = [ApiTokenReuse, ApiDeniedBurst, ApiVolumeSpike];

    public static readonly IReadOnlyList<string> All =
        [DownloadVolume, PrivilegeGrantOutOfHours, NetworkBlocked, ApiTokenReuse, SiemFailing, GuestConfidentialAccess, ApiDeniedBurst, ApiVolumeSpike];
}

/// <summary>Who is told when a guest opens a Confidential item (spec 026 US5 scenario 3).</summary>
public static class GuestNotifyTargets
{
    public const string Security = "security";
    public const string Sponsor = "sponsor";
    public const string Both = "both";

    public static readonly IReadOnlyList<string> All = [Security, Sponsor, Both];
}

/// <summary>
/// Tenant alert thresholds (spec 026 section 6 and US5 defaults), stored in <c>security.policy.alert_settings</c>.
/// Changing them needs <c>security.alerts_advanced</c>; without it the defaults apply.
/// </summary>
public sealed record AlertSettings(int DownloadFiles = 200, long DownloadBytes = 2L * 1024 * 1024 * 1024, string WorkStart = "08:00", string WorkEnd = "18:00",
    int ApiDeniedCount = 50, double ApiVolumeFactor = 5, string GuestConfidentialNotify = GuestNotifyTargets.Security)
{
    public static AlertSettings From(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new AlertSettings();
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var d = new AlertSettings();
        return new AlertSettings(
            root.TryGetProperty("downloadFiles", out var f) && f.TryGetInt32(out var files) ? files : d.DownloadFiles,
            root.TryGetProperty("downloadBytes", out var b) && b.TryGetInt64(out var bytes) ? bytes : d.DownloadBytes,
            root.TryGetProperty("workStart", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : d.WorkStart,
            root.TryGetProperty("workEnd", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : d.WorkEnd,
            root.TryGetProperty("apiDeniedCount", out var c) && c.TryGetInt32(out var count) ? count : d.ApiDeniedCount,
            root.TryGetProperty("apiVolumeFactor", out var v) && v.TryGetDouble(out var factor) ? factor : d.ApiVolumeFactor,
            root.TryGetProperty("guestConfidentialNotify", out var g) && g.ValueKind == JsonValueKind.String && GuestNotifyTargets.All.Contains(g.GetString())
                ? g.GetString()! : d.GuestConfidentialNotify);
    }

    public object ToJson() => new
    {
        downloadFiles = DownloadFiles, downloadBytes = DownloadBytes, workStart = WorkStart, workEnd = WorkEnd,
        apiDeniedCount = ApiDeniedCount, apiVolumeFactor = ApiVolumeFactor, guestConfidentialNotify = GuestConfidentialNotify,
    };

    public bool NotifiesSecurity => GuestConfidentialNotify != GuestNotifyTargets.Sponsor;

    public bool NotifiesSponsor => GuestConfidentialNotify != GuestNotifyTargets.Security;

    /// <summary>Whether a moment falls outside the working week and hours of the calendar, in its time zone.</summary>
    public bool OutOfHours(WorkingCalendarSnapshot calendar, DateTimeOffset at)
    {
        var local = TimeZoneInfo.ConvertTime(at, calendar.Zone);
        if (!calendar.IsWorkingDay(DateOnly.FromDateTime(local.DateTime)))
        {
            return true;
        }
        var time = TimeOnly.FromDateTime(local.DateTime);
        var start = TimeOnly.Parse(WorkStart, CultureInfo.InvariantCulture);
        var end = TimeOnly.Parse(WorkEnd, CultureInfo.InvariantCulture);
        return time < start || time >= end;
    }
}

public sealed record AlertView(Guid Id, string Kind, Guid? SubjectUserId, JsonElement Facts, string Status, Guid? HandledBy, DateTimeOffset? HandledAt,
    string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? SubjectName = null, string? HandledByName = null);

public sealed record HandleAlertRequest(string? Outcome, string? Note);

/// <summary>Raises, lists and handles tenant security alerts (spec 026 US5, FR-013). Facts are counts and references, never content.</summary>
public sealed class AlertService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IOutbox outbox, TeamBeats.Modules.Identity.Contracts.IUserDirectory users)
{
    public const int NoteMax = 1000;

    /// <summary>Raises an alert once per dedupe key; a later call with the same key updates the facts. Returns the new alert's ID, or null.</summary>
    public async Task<Guid?> RaiseAsync(string kind, Guid? subject, object facts, string dedupeKey, CancellationToken ct)
    {
        var id = Ids.New();
        var created = await session.ScalarAsync<Guid?>(
            """
            insert into security.alert (tenant_id, id, kind, subject_user_id, facts, dedupe_key)
            values (platform.current_tenant_id(), @id, @kind, @subject, @facts, @key)
            on conflict (tenant_id, dedupe_key) where dedupe_key is not null do update set facts = excluded.facts, updated_at = now()
            returning case when xmax = 0 then id end
            """, new { id, kind, subject, facts = Jsonb.From(facts), key = dedupeKey }, ct);
        if (created is null)
        {
            return null;
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityAlertRaised, "security_alert", id.ToString(),
            Changes: new { kind, subject }), ct);
        await outbox.EnqueueAsync(new SecurityAlertRaised(id, kind), ct);
        return id;
    }

    private async Task DemandViewAsync(CancellationToken ct)
    {
        foreach (var permission in new[] { Permissions.SecurityView, Permissions.SecurityManage })
        {
            if ((await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                return;
            }
        }
        throw AppException.Denied();
    }

    public async Task<IReadOnlyList<AlertView>> ListAsync(string? status, CancellationToken ct)
    {
        await DemandViewAsync(ct);
        await entitlements.DemandAsync(Capabilities.SecurityAlerts, ct);
        if (status is not null and not ("open" or "handled" or "dismissed"))
        {
            throw AppException.Validation("status", "validation.security.alert_status");
        }
        var alerts = await session.QueryAsync(
            """
            select id, kind, subject_user_id, facts::text, status, handled_by, handled_at, note, created_at, updated_at from security.alert
            where (@status::text is null or status = @status) order by created_at desc, id desc limit 200
            """, new { status }, Read, ct);
        // Names for the alerts page, from the tenant's own user directory.
        var names = new Dictionary<Guid, string?>();
        foreach (var id in alerts.SelectMany(a => new[] { a.SubjectUserId, a.HandledBy }).OfType<Guid>().Distinct())
        {
            names[id] = (await users.GetAsync(id, ct))?.DisplayName;
        }
        return alerts.Select(a => a with
        {
            SubjectName = a.SubjectUserId is { } s ? names.GetValueOrDefault(s) : null,
            HandledByName = a.HandledBy is { } h ? names.GetValueOrDefault(h) : null,
        }).ToList();
    }

    public async Task<AlertView> HandleAsync(Guid id, HandleAlertRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityRespond, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.SecurityAlerts, ct);
        var errors = new ValidationErrors();
        if (request.Outcome is not ("handled" or "dismissed"))
        {
            errors.Add("outcome", "validation.security.alert_outcome");
        }
        var note = request.Note?.Trim();
        if (note is { Length: > NoteMax })
        {
            errors.Add("note", "validation.security.alert_note");
        }
        errors.ThrowIfAny();

        var updated = await session.QuerySingleOrDefaultAsync(
            """
            update security.alert set status = @outcome, handled_by = @actor, handled_at = now(), note = @note, updated_at = now()
            where id = @id and status = 'open'
            returning id, kind, subject_user_id, facts::text, status, handled_by, handled_at, note, created_at, updated_at
            """, new { id, outcome = request.Outcome, actor = context.ActorId, note = string.IsNullOrEmpty(note) ? null : note }, Read, ct);
        if (updated is null)
        {
            var exists = await session.ScalarAsync<bool>("select exists (select 1 from security.alert where id = @id)", new { id }, ct);
            throw exists ? AppException.Conflict("conflict.security.alert_closed") : AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityAlertHandled, "security_alert", id.ToString(),
            Changes: new { kind = updated.Kind, outcome = request.Outcome, note = updated.Note }), ct);
        return updated;
    }

    public Task<long> OpenCountAsync(CancellationToken ct) =>
        session.ScalarAsync<long>("select count(*) from security.alert where status = 'open'", null, ct);

    private static AlertView Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetGuid(2), JsonDocument.Parse(r.GetString(3)).RootElement.Clone(), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetGuid(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.GetFieldValue<DateTimeOffset>(8), r.GetFieldValue<DateTimeOffset>(9));
}

/// <summary>
/// Download volume (spec 026 US5 scenario 1): downloads are counted per user per hour; above the tenant threshold (default
/// 200 files or 2 GB) security officers get one alert for that user and hour, with the volume and the libraries.
/// </summary>
public sealed class DownloadVolumeRule(IDbSession session, IEntitlementService entitlements, AlertSettingsService settingsService, AlertService alerts, IClock clock)
    : IMessageHandler<DocumentDownloaded>
{
    public async Task HandleAsync(DocumentDownloaded message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SecurityAlerts, ct))
        {
            return;
        }
        var now = clock.UtcNow;
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        var totals = await session.QuerySingleOrDefaultAsync(
            """
            insert into security.download_count (tenant_id, user_id, hour_start, files, bytes, libraries)
            values (platform.current_tenant_id(), @u, @h, 1, @b, array[@l]::uuid[])
            on conflict (tenant_id, user_id, hour_start) do update set files = security.download_count.files + 1,
              bytes = security.download_count.bytes + excluded.bytes,
              libraries = case when @l = any(security.download_count.libraries) then security.download_count.libraries
                               else security.download_count.libraries || @l end
            returning files, bytes, libraries
            """, new { u = message.UserId, h = hour, b = Math.Max(message.Bytes, 0), l = message.LibraryId },
            r => (Files: r.GetInt32(0), Bytes: r.GetInt64(1), Libraries: r.GetFieldValue<Guid[]>(2)), ct);
        await session.ExecAsync("delete from security.download_count where hour_start < @cutoff", new { cutoff = hour.AddDays(-1) }, ct);

        var settings = await settingsService.EffectiveAsync(ct);
        if (totals.Files > settings.DownloadFiles || totals.Bytes > settings.DownloadBytes)
        {
            await alerts.RaiseAsync(AlertKinds.DownloadVolume, message.UserId,
                new { files = totals.Files, bytes = totals.Bytes, libraries = totals.Libraries, hourStart = hour },
                $"{AlertKinds.DownloadVolume}:{message.UserId}:{hour:yyyyMMddHH}", ct);
        }
    }
}

/// <summary>
/// Privilege grants out of hours (spec 026 US5 scenario 2, <c>security.alerts_advanced</c>): a role holding an
/// administrative permission granted outside the tenant working week (spec 022 tenant default) and hours raises an alert.
/// </summary>
public sealed class OutOfHoursGrantRule(IEntitlementService entitlements, IAuthorizationProvisioning roles, IWorkingCalendar calendars,
    AlertSettingsService settingsService, AlertService alerts, IClock clock) : IMessageHandler<RoleAssignmentChanged>
{
    public async Task HandleAsync(RoleAssignmentChanged message, CancellationToken ct)
    {
        if (message.Change != "assigned" || !await entitlements.IsEnabledAsync(Capabilities.SecurityAlertsAdvanced, ct))
        {
            return;
        }
        var privileged = (await roles.RolePermissionsAsync(message.RoleKey, ct)).Where(Permissions.Privileged.Contains).Order().ToList();
        if (privileged.Count == 0)
        {
            return;
        }
        var now = clock.UtcNow;
        var settings = await settingsService.EffectiveAsync(ct);
        if (!settings.OutOfHours(await calendars.TenantDefaultAsync(ct), now))
        {
            return;
        }
        await alerts.RaiseAsync(AlertKinds.PrivilegeGrantOutOfHours, message.SubjectId,
            new { assignmentId = message.AssignmentId, role = message.RoleKey, permissions = privileged, at = now },
            $"{AlertKinds.PrivilegeGrantOutOfHours}:{message.AssignmentId}", ct);
    }
}

/// <summary>
/// App token reuse (spec 035 US6 scenario 2, spec 026 US5): a refresh token or code used twice means it may have been
/// stolen. PublicApi already revoked the token family; security officers get one alert per family, with the app and the
/// person, never the token.
/// </summary>
public sealed class ApiTokenReuseRule(IEntitlementService entitlements, AlertService alerts) : IMessageHandler<TeamBeats.Modules.PublicApi.Contracts.TokenFamilyRevoked>
{
    public async Task HandleAsync(TeamBeats.Modules.PublicApi.Contracts.TokenFamilyRevoked message, CancellationToken ct)
    {
        if (message.Reason != TeamBeats.Modules.PublicApi.Contracts.TokenFamilyReasons.ReuseDetected || !await entitlements.IsEnabledAsync(Capabilities.SecurityAlerts, ct))
        {
            return;
        }
        await alerts.RaiseAsync(AlertKinds.ApiTokenReuse, message.UserId, new { appId = message.AppId, familyId = message.FamilyId },
            $"{AlertKinds.ApiTokenReuse}:{message.FamilyId}", ct);
    }
}

/// <summary>
/// Guest access to Confidential items (spec 026 US5 scenario 3, <c>security.alerts_advanced</c>): one alert per guest,
/// item and day. The tenant setting chooses who is told: the security officers (through <see cref="AlertNotifier"/>), the
/// guest's sponsor in that space, or both. The alert is kept on the alerts page either way.
/// </summary>
public sealed class GuestConfidentialRule(IEntitlementService entitlements, AlertSettingsService settingsService, AlertService alerts,
    TeamBeats.Modules.Spaces.Contracts.ISpaceGuestReview guests, TeamBeats.Modules.Notifications.Contracts.INotificationSender notifications, IClock clock)
    : IMessageHandler<GuestOpenedClassified>
{
    public const string SponsorTemplate = "security.guest_confidential.sponsor";

    public async Task HandleAsync(GuestOpenedClassified message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SecurityAlertsAdvanced, ct))
        {
            return;
        }
        var day = clock.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var id = await alerts.RaiseAsync(AlertKinds.GuestConfidentialAccess, message.UserId,
            new { documentId = message.DocumentId, libraryId = message.LibraryId, spaceId = message.SpaceId, classification = message.Classification },
            $"{AlertKinds.GuestConfidentialAccess}:{message.UserId}:{message.DocumentId}:{day}", ct);
        if (id is null || message.SpaceId is not { } space || !(await settingsService.EffectiveAsync(ct)).NotifiesSponsor)
        {
            return;
        }
        if (await guests.SponsorOfAsync(space, message.UserId, ct) is { } sponsor)
        {
            await notifications.SendAsync(new TeamBeats.Modules.Notifications.Contracts.NotificationRequest([sponsor],
                TeamBeats.Modules.Notifications.Contracts.NotificationCategories.SecurityNotice, SponsorTemplate, $"/spaces/{space}",
                DedupeKey: "security_alert:sponsor:" + id), ct);
        }
    }
}

/// <summary>
/// Refused app calls (spec 026 US5 scenario 5): PublicApi tells Security when an app's authorization denials in 10
/// minutes reach the tenant threshold (50 by default). One alert per app and 10 minutes, with the app and the person.
/// </summary>
public sealed class ApiDeniedBurstRule(IEntitlementService entitlements, AlertService alerts) : IMessageHandler<TeamBeats.Modules.PublicApi.Contracts.ApiCallsDenied>
{
    public async Task HandleAsync(TeamBeats.Modules.PublicApi.Contracts.ApiCallsDenied message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SecurityAlerts, ct))
        {
            return;
        }
        var slot = message.WindowStart.ToUnixTimeSeconds() / 600;
        await alerts.RaiseAsync(AlertKinds.ApiDeniedBurst, message.UserId,
            new { appId = message.AppId, appName = message.AppName, count = message.Count, windowStart = message.WindowStart },
            $"{AlertKinds.ApiDeniedBurst}:{message.AppId}:{slot}", ct);
    }
}

/// <summary>
/// App call volume (spec 026 US5 scenario 5): an hour over the tenant factor (5 by default) times the app's 30 day hourly
/// average raises one alert per app and hour, with the calls, the average and the person who made most of them.
/// </summary>
public sealed class ApiVolumeSpikeRule(IEntitlementService entitlements, AlertService alerts) : IMessageHandler<TeamBeats.Modules.PublicApi.Contracts.ApiVolumeSpike>
{
    public async Task HandleAsync(TeamBeats.Modules.PublicApi.Contracts.ApiVolumeSpike message, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.SecurityAlerts, ct))
        {
            return;
        }
        await alerts.RaiseAsync(AlertKinds.ApiVolumeSpike, message.UserId,
            new { appId = message.AppId, appName = message.AppName, calls = message.Calls, hourlyAverage = message.HourlyAverage, hourStart = message.HourStart },
            $"{AlertKinds.ApiVolumeSpike}:{message.AppId}:{message.HourStart:yyyyMMddHH}", ct);
    }
}
