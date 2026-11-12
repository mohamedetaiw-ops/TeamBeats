using System.Globalization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Booking.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Booking;

/// <summary>
/// Ends live bookings that will not happen and tells their holders (spec 031 FR-013, FR-015, FR-016): a resource or site
/// removed, a person who left, or a facilities administrator cancelling with a reason.
/// </summary>
public sealed class BookingCancellations(IDbSession session, ITenantSettingsReader tenantSettings, INotificationSender notifications, BookingApprovals approvals, IOutbox outbox,
    BookingActivity activity, IClock clock)
{
    public const string RemovedTemplate = "booking.removed";
    public const string CancelledByAdminTemplate = "booking.cancelled_by_admin";

    internal sealed record Ended(Guid Id, Guid ResourceId, Guid HolderId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Kind, Guid SiteId, Guid? ZoneId,
        Dictionary<string, string> ResourceNames, string TimeZone, Guid? InstanceId);

    /// <summary>
    /// Cancels the live bookings that have not ended and match <paramref name="where"/> (a condition on the booking with
    /// <c>@key</c>, and <c>@ids</c>, <c>@from</c> and <c>@to</c> when given). Holders are told unless the reason is that they
    /// left, with <paramref name="template"/> when given. Returns how many were cancelled.
    /// </summary>
    internal async Task<int> CancelFutureAsync(string where, Guid key, string reason, CancellationToken ct, string? adminReason = null, Guid[]? ids = null,
        DateTimeOffset? from = null, DateTimeOffset? to = null, string? template = null)
    {
        var now = clock.UtcNow;
        var ended = await session.QueryAsync(
            $"""
            with cancelled as (
              update booking.booking set status = 'cancelled', ended_at = @now, end_reason = @reason, row_version = row_version + 1
              where {where} and status in ('tentative', 'confirmed', 'checked_in') and ends_at > @now
              returning id, resource_id, holder_id, starts_at, ends_at, instance_id
            )
            select c.id, c.resource_id, c.holder_id, c.starts_at, c.ends_at, r.kind, r.site_id, r.zone_id, r.names::text, s.time_zone, c.instance_id
            from cancelled c
            join booking.resource r on r.id = c.resource_id
            join booking.site s on s.id = r.site_id
            order by c.starts_at
            """, new { key, reason, now, ids = ids ?? [], from = from ?? now, to = to ?? now },
            r => new Ended(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.GetString(5), r.GetGuid(6),
                r.Get<Guid?>("zone_id"), Names.Read(r, 8), r.GetString(9), r.Get<Guid?>("instance_id")), ct);
        // A request nobody needs to decide any more leaves the approvers' inbox.
        foreach (var instance in ended.Select(e => e.InstanceId).OfType<Guid>().Distinct())
        {
            await approvals.EndIfIdleAsync(instance, ct);
        }
        foreach (var e in ended)
        {
            await outbox.EnqueueAsync(new BookingCancelled(e.Id, e.ResourceId, e.SiteId, e.Kind, e.HolderId, adminReason is not null || reason != "cancelled"), ct);
            await activity.RecordAsync("cancelled", e.Kind, e.SiteId, ct);
        }
        if (reason != "left" && ended.Count > 0)
        {
            var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
            foreach (var e in ended)
            {
                var parameters = Describe(e.ResourceNames, e.TimeZone, e.StartsAt, e.EndsAt, locale);
                if (adminReason is not null)
                {
                    parameters["reason"] = adminReason;
                }
                // Alternatives in the same zone, or the same site when the resource had no zone (edge case "resource removed").
                var link = adminReason is null || template is not null
                    ? $"/booking?site={e.SiteId}&kind={e.Kind}" + (e.ZoneId is { } z ? $"&zone={z}" : "")
                    : Bookings.Link(e.Id);
                await notifications.SendAsync(new NotificationRequest([e.HolderId], NotificationCategories.Bookings,
                    template ?? (adminReason is null ? RemovedTemplate : CancelledByAdminTemplate), link, parameters, DedupeKey: $"booking:{e.Id:N}:cancelled"), ct);
            }
        }
        return ended.Count;
    }

    /// <summary>Resource name, date and times in the site's time zone, for notification text.</summary>
    internal static Dictionary<string, string> Describe(IReadOnlyDictionary<string, string> resourceNames, string timeZone, DateTimeOffset startsAt, DateTimeOffset endsAt,
        string locale)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var start = TimeZoneInfo.ConvertTime(startsAt, zone);
        var end = TimeZoneInfo.ConvertTime(endsAt, zone);
        return new Dictionary<string, string>
        {
            ["resource"] = Names.Pick(resourceNames, locale),
            ["date"] = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["start"] = start.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["end"] = end.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["timeZone"] = timeZone,
        };
    }
}
