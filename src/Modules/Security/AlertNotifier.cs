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
public sealed class AlertNotifier(IAuthorizationProvisioning roles, INotificationSender notifications) : IMessageHandler<SecurityAlertRaised>
{
    /// <summary>One template per alert kind, so the text names the kind in the reader's language.</summary>
    public static string Template(string kind) => "security.alert." + kind;

    public const string Link = "/admin/security/alerts";

    public async Task HandleAsync(SecurityAlertRaised message, CancellationToken ct)
    {
        var recipients = await roles.TenantHoldersAsync(Permissions.SecurityRespond, ct);
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SecurityNotice, Template(message.Kind), Link,
            DedupeKey: "security_alert:" + message.AlertId,
            GroupKey: "security_alert:" + message.Kind), ct);
    }
}

/// <summary>The open alert count for the admin shell badge.</summary>
public sealed class AlertSummary(IDbSession session) : ISecurityAlertSummary
{
    public async Task<int> OpenCountAsync(CancellationToken ct = default) =>
        (int)await session.ScalarAsync<long>("select count(*) from security.alert where status = 'open'", null, ct);
}
