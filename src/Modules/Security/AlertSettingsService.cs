using System.Globalization;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Security;

public sealed record AlertSettingsView(int DownloadFiles, long DownloadBytes, string WorkStart, string WorkEnd, int ApiDeniedCount, double ApiVolumeFactor,
    string GuestConfidentialNotify, bool Editable);

public sealed record AlertSettingsRequest(int? DownloadFiles, long? DownloadBytes, string? WorkStart, string? WorkEnd, int? ApiDeniedCount,
    double? ApiVolumeFactor, string? GuestConfidentialNotify);

/// <summary>
/// Tenant alert thresholds (spec 026 FR-013, US5): download files and bytes, working hours, the app denial count and
/// volume factor, and who is told when a guest opens a Confidential item. Anyone who sees alerts sees the thresholds;
/// changing them needs <c>security.manage</c>, <c>security.alerts_advanced</c> and step-up, and is audited with before
/// and after values. Stored thresholds keep applying after a move to a smaller package, so alerts never get noisier
/// because of a downgrade; the tenant can no longer change them there.
/// </summary>
public sealed class AlertSettingsService(IDbSession session, IAuthorizationService authz, IEntitlementService entitlements, IAuditWriter audit,
    PolicyStore store) : ISecurityAlertThresholds
{
    public const int MinDownloadFiles = 10;
    public const int MaxDownloadFiles = 100_000;
    public const long MinDownloadBytes = 10L * 1000 * 1000;
    public const long MaxDownloadBytes = 1000L * 1000 * 1000 * 1000;
    public const int MinDeniedCount = 5;
    public const int MaxDeniedCount = 10_000;
    public const double MinVolumeFactor = 2;
    public const double MaxVolumeFactor = 100;

    /// <summary>The thresholds that apply now: the stored ones, or the defaults when none were stored.</summary>
    public async Task<AlertSettings> EffectiveAsync(CancellationToken ct) => AlertSettings.From(await store.AlertSettingsJsonAsync(ct));

    async Task<AlertThresholds> ISecurityAlertThresholds.GetAsync(CancellationToken ct)
    {
        var settings = await EffectiveAsync(ct);
        return new AlertThresholds(settings.ApiDeniedCount, settings.ApiVolumeFactor);
    }

    private async Task DemandReadAsync(CancellationToken ct)
    {
        foreach (var permission in new[] { Permissions.SecurityManage, Permissions.SecurityView })
        {
            if ((await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed)
            {
                return;
            }
        }
        throw AppException.Denied();
    }

    public async Task<AlertSettingsView> GetAsync(CancellationToken ct)
    {
        await DemandReadAsync(ct);
        await entitlements.DemandAsync(Capabilities.SecurityAlerts, ct);
        return await ViewAsync(await EffectiveAsync(ct), ct);
    }

    private async Task<AlertSettingsView> ViewAsync(AlertSettings s, CancellationToken ct) =>
        new(s.DownloadFiles, s.DownloadBytes, s.WorkStart, s.WorkEnd, s.ApiDeniedCount, s.ApiVolumeFactor, s.GuestConfidentialNotify,
            await entitlements.IsEnabledAsync(Capabilities.SecurityAlertsAdvanced, ct) && (await authz.CheckAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct)).Allowed);

    public async Task<AlertSettingsView> UpdateAsync(AlertSettingsRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.SecurityAlertsAdvanced, ct);
        var current = await EffectiveAsync(ct);
        var errors = new ValidationErrors();
        var files = request.DownloadFiles ?? current.DownloadFiles;
        if (files is < MinDownloadFiles or > MaxDownloadFiles)
        {
            errors.Add("downloadFiles", "validation.security.alert_threshold");
        }
        var bytes = request.DownloadBytes ?? current.DownloadBytes;
        if (bytes is < MinDownloadBytes or > MaxDownloadBytes)
        {
            errors.Add("downloadBytes", "validation.security.alert_threshold");
        }
        var start = request.WorkStart?.Trim() ?? current.WorkStart;
        var end = request.WorkEnd?.Trim() ?? current.WorkEnd;
        if (!Time(start, out var s) || !Time(end, out var e) || s >= e)
        {
            errors.Add("workStart", "validation.security.work_hours");
        }
        var denied = request.ApiDeniedCount ?? current.ApiDeniedCount;
        if (denied is < MinDeniedCount or > MaxDeniedCount)
        {
            errors.Add("apiDeniedCount", "validation.security.alert_threshold");
        }
        var factor = request.ApiVolumeFactor ?? current.ApiVolumeFactor;
        if (double.IsNaN(factor) || factor is < MinVolumeFactor or > MaxVolumeFactor)
        {
            errors.Add("apiVolumeFactor", "validation.security.alert_threshold");
        }
        var notify = request.GuestConfidentialNotify ?? current.GuestConfidentialNotify;
        if (!GuestNotifyTargets.All.Contains(notify))
        {
            errors.Add("guestConfidentialNotify", "validation.security.guest_notify");
        }
        errors.ThrowIfAny();

        var next = new AlertSettings(files, bytes, start, end, denied, Math.Round(factor, 1), notify);
        if (next == current)
        {
            return await ViewAsync(current, ct);
        }
        await session.ExecAsync("insert into security.policy (tenant_id) values (platform.current_tenant_id()) on conflict do nothing", null, ct);
        await session.ExecAsync("update security.policy set alert_settings = @json, updated_at = now()", new { json = Jsonb.From(next.ToJson()) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityAlertSettingsChanged, "security_policy", null,
            Changes: new { before = current.ToJson(), after = next.ToJson() }), ct);
        return await ViewAsync(next, ct);
    }

    private static bool Time(string value, out TimeOnly time) =>
        TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
