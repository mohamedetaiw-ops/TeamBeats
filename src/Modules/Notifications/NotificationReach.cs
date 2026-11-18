using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Notifications;

/// <summary>Reach per channel of notification links (spec 021 FR-009), counted in this module's own tables.</summary>
internal sealed class NotificationReach(IDbSession session) : INotificationReach
{
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>>> DeliveredByChannelAsync(IReadOnlyCollection<string> links, CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        if (links.Count == 0)
        {
            return new Dictionary<string, IReadOnlyDictionary<string, int>>();
        }
        var rows = await session.QueryAsync(
            """
            select n.link, @inApp, count(*)::int from notifications.notification n where n.link = any(@links) group by n.link
            union all
            select n.link, d.channel, count(*)::int
            from notifications.delivery d join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where n.link = any(@links) and d.status = 'sent'
            group by n.link, d.channel
            """, new { links = links.Distinct(StringComparer.Ordinal).ToArray(), inApp = NotificationChannels.InApp },
            r => (Link: r.GetString(0), Channel: r.GetString(1), Count: r.GetInt32(2)), ct);
        foreach (var (link, channel, count) in rows)
        {
            if (!result.TryGetValue(link, out var channels))
            {
                result[link] = channels = new Dictionary<string, int>(StringComparer.Ordinal);
            }
            channels[channel] = count;
        }
        return result.ToDictionary(r => r.Key, r => (IReadOnlyDictionary<string, int>)r.Value, StringComparer.Ordinal);
    }
}
