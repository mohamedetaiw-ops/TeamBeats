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

    public static readonly IReadOnlyList<string> All = [DownloadVolume, PrivilegeGrantOutOfHours, NetworkBlocked];
}

/// <summary>Tenant alert thresholds (spec 026 section 6 defaults), stored in <c>security.policy.alert_settings</c>.</summary>
public sealed record AlertSettings(int DownloadFiles = 200, long DownloadBytes = 2L * 1024 * 1024 * 1024, string WorkStart = "08:00", string WorkEnd = "18:00")
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
            root.TryGetProperty("workEnd", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : d.WorkEnd);
    }

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
    string? Note, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record HandleAlertRequest(string? Outcome, string? Note);

/// <summary>Raises, lists and handles tenant security alerts (spec 026 US5, FR-013). Facts are counts and references, never content.</summary>
public sealed class AlertService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IOutbox outbox)
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
        return await session.QueryAsync(
            """
            select id, kind, subject_user_id, facts::text, status, handled_by, handled_at, note, created_at, updated_at from security.alert
            where (@status::text is null or status = @status) order by created_at desc, id desc limit 200
            """, new { status }, Read, ct);
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
public sealed class DownloadVolumeRule(IDbSession session, IEntitlementService entitlements, PolicyStore policies, AlertService alerts, IClock clock)
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

        var settings = AlertSettings.From(await policies.AlertSettingsJsonAsync(ct));
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
    PolicyStore policies, AlertService alerts, IClock clock) : IMessageHandler<RoleAssignmentChanged>
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
        var settings = AlertSettings.From(await policies.AlertSettingsJsonAsync(ct));
        if (!settings.OutOfHours(await calendars.TenantDefaultAsync(ct), now))
        {
            return;
        }
        await alerts.RaiseAsync(AlertKinds.PrivilegeGrantOutOfHours, message.SubjectId,
            new { assignmentId = message.AssignmentId, role = message.RoleKey, permissions = privileged, at = now },
            $"{AlertKinds.PrivilegeGrantOutOfHours}:{message.AssignmentId}", ct);
    }
}
