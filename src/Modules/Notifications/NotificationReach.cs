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

    public async Task<DeliveryReport> ReportAsync(IReadOnlyCollection<string> links, CancellationToken ct)
    {
        var distinct = links.Distinct(StringComparer.Ordinal).ToArray();
        if (distinct.Length == 0)
        {
            return new DeliveryReport(0, 0, []);
        }
        var inApp = (await session.QueryAsync(
            "select count(distinct user_id)::int, count(*)::int, count(read_at)::int from notifications.notification where link = any(@links)",
            new { links = distinct }, r => (People: r.GetInt32(0), Total: r.GetInt32(1), Read: r.GetInt32(2)), ct)).Single();
        var rows = await session.QueryAsync(
            """
            select d.channel, d.status,
              (d.channel = 'email' and d.status = 'failed' and exists (select 1 from notifications.address_state a
                 where a.tenant_id = d.tenant_id and a.address = d.address and a.bounced_at is not null)) as bounced,
              count(*)::int
            from notifications.delivery d join notifications.notification n on n.tenant_id = d.tenant_id and n.id = d.notification_id
            where n.link = any(@links)
            group by 1, 2, 3
            """, new { links = distinct }, r => (Channel: r.GetString(0), Status: r.GetString(1), Bounced: r.GetBoolean(2), Count: r.GetInt32(3)), ct);
        var channels = new List<ChannelReport> { new(NotificationChannels.InApp, inApp.Total, inApp.Read, 0, 0, null, 0) };
        foreach (var g in rows.GroupBy(r => r.Channel).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            int Sum(Func<(string Channel, string Status, bool Bounced, int Count), bool> f) => g.Where(f).Sum(r => r.Count);
            channels.Add(new ChannelReport(g.Key, Sum(r => r.Status == "sent"), null, Sum(r => r.Status is "pending" or "digest"),
                Sum(r => r.Status == "failed" && !r.Bounced), g.Key == NotificationChannels.Email ? Sum(r => r.Bounced) : null, Sum(r => r.Status == "dropped")));
        }
        return new DeliveryReport(inApp.People, inApp.Total, channels);
    }
}
