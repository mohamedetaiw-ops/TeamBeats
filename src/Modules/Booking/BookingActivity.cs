using TeamBeats.Modules.Analytics.Contracts;

namespace TeamBeats.Modules.Booking;

/// <summary>
/// The <c>booking.activity</c> telemetry of spec 031 section 13: what happened, the resource type and the site, as an
/// unkeyed count that carries no person, so analytics never learns who booked, checked in or did not turn up.
/// </summary>
public sealed class BookingActivity(IAnalyticsEvents analytics)
{
    public async Task RecordAsync(string action, string kind, Guid siteId, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            await analytics.EmitAsync(AnalyticsEvents.BookingActivity, new Dictionary<string, object?> { ["action"] = action, ["type"] = kind, ["siteId"] = siteId }, ct);
        }
    }

    public Task RecordAsync(string action, string kind, Guid siteId, CancellationToken ct) => RecordAsync(action, kind, siteId, 1, ct);
}
