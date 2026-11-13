using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Security;

/// <summary>
/// Tells the people who handle alerts (holders of <c>security.respond</c> tenant-wide) that a new one was raised (spec 026
/// US5). The notification names only the kind of alert; the facts stay on the alerts page, behind its permission check.
/// </summary>
public sealed class AlertNotifier(IAuthorizationProvisioning roles, INotificationSender notifications, AlertSettingsService settings) : IMessageHandler<SecurityAlertRaised>
{
    /// <summary>One template per alert kind, so the text names the kind in the reader's language.</summary>
    public static string Template(string kind) => "security.alert." + kind;

    /// <summary>The text for API administrators, who look at the app on the apps page (spec 026 US5 scenario 5).</summary>
    public static string AppTemplate(string kind) => "security.alert_app." + kind;

    public const string Link = "/admin/security/alerts";
    public const string AppsLink = "/admin/api/apps";

    public async Task HandleAsync(SecurityAlertRaised message, CancellationToken ct)
    {
        // The tenant may send guest access to Confidential items to the sponsor only (US5 scenario 3).
        var security = message.Kind != AlertKinds.GuestConfidentialAccess || (await settings.EffectiveAsync(ct)).NotifiesSecurity
            ? await roles.TenantHoldersAsync(Permissions.SecurityRespond, ct)
            : [];
        if (security.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(security, NotificationCategories.SecurityNotice, Template(message.Kind), Link,
                DedupeKey: "security_alert:" + message.AlertId,
                GroupKey: "security_alert:" + message.Kind), ct);
        }
        if (!AlertKinds.AboutApps.Contains(message.Kind))
        {
            return;
        }
        var apiAdmins = (await roles.TenantHoldersAsync(Permissions.ApiManage, ct)).Except(security).ToList();
        if (apiAdmins.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(apiAdmins, NotificationCategories.SecurityNotice, AppTemplate(message.Kind), AppsLink,
                DedupeKey: "security_alert:api:" + message.AlertId,
                GroupKey: "security_alert:" + message.Kind), ct);
        }
    }
}

/// <summary>The open alert count for the admin shell badge.</summary>
public sealed class AlertSummary(IDbSession session) : ISecurityAlertSummary
{
    public async Task<int> OpenCountAsync(CancellationToken ct = default) =>
        (int)await session.ScalarAsync<long>("select count(*) from security.alert where status = 'open'", null, ct);
}
