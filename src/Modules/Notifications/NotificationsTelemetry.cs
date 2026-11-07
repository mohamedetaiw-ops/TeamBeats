using System.Diagnostics.Metrics;

namespace TeamBeats.Modules.Notifications;

/// <summary>Notification counters (spec 015 section 13). Tags carry tenant, category, channel and status, never content.</summary>
public static class NotificationsTelemetry
{
    public const string MeterName = "TeamBeats.Notifications";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Created = Meter.CreateCounter<long>("notifications.created");
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("notifications.delivery");

    public static void Sent(Guid tenantId, string category, int created, int grouped, int duplicates, int skipped)
    {
        void Add(string outcome, int n)
        {
            if (n > 0)
            {
                Created.Add(n, new("tenant_id", tenantId), new("category", category), new("outcome", outcome));
            }
        }
        Add("created", created);
        Add("grouped", grouped);
        Add("duplicate", duplicates);
        Add("skipped", skipped);
    }

    public static void Delivery(Guid tenantId, string category, EmailOutcome outcome) =>
        Deliveries.Add(1, new("tenant_id", tenantId), new("category", category), new("channel", "email"),
            new("status", outcome switch { EmailOutcome.Sent => "sent", EmailOutcome.HardBounce => "bounced", _ => "retry" }));
}
