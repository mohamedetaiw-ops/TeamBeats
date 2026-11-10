using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// Tells the integration administrators (holders of <c>integration.manage</c>) that a connection became degraded, needs
/// consent or has a credential about to expire (spec 017 FR-023, US1 scenario 5; spec 006 FR-014). A connection an
/// administrator disabled is not an alert. The text names only the connection and what happened; the details stay on
/// the Connections page behind its permission check.
/// </summary>
public sealed class ConnectionAlerts(IDbSession session, IAuthorizationProvisioning roles, INotificationSender notifications)
    : IMessageHandler<ConnectorUnavailable>
{
    public const string Link = "/admin/connections";

    public static string Template(string status) => "integrations.connection." + status;

    public async Task HandleAsync(ConnectorUnavailable message, CancellationToken ct)
    {
        if (message.Status == ConnectionStatuses.Disabled)
        {
            return;
        }
        var name = await session.QuerySingleOrDefaultAsync("select display_name from integrations.connection where id = @id",
            new { id = message.ConnectionId }, r => r.GetString(0), ct);
        if (name is null)
        {
            return;
        }
        var recipients = await roles.TenantHoldersAsync(Permissions.IntegrationManage, ct);
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.IntegrationAlert, Template(message.Status), Link,
            new Dictionary<string, string> { ["connection"] = name },
            DedupeKey: $"connection:{message.ConnectionId}:{message.Status}",
            GroupKey: "connection_alert:" + message.Status), ct);
    }
}
