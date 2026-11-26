using TeamBeats.Modules.Calendar.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Calendar;

/// <summary>Event cards for other modules (spec 009 event items): the same audience check as the calendar, on every read.</summary>
public sealed partial class CompanyEvents : ICalendarEventCards
{
    public async Task<bool> PublishedAsync(Guid eventId, CancellationToken ct) =>
        await session.ScalarAsync<bool>("select exists (select 1 from calendar.event where id = @eventId and status = 'published')", new { eventId }, ct);

    public async Task<CalendarEventCard?> ForReaderAsync(Guid eventId, CancellationToken ct)
    {
        var me = context.RequireUser();
        var now = clock.UtcNow;
        var row = await RowAsync(eventId, ct);
        // A cancelled event shows as cancelled for as long as the calendar shows it, then the card is gone.
        if (row is null || (row.Status != "published" && !(row.CancelledAt > now - CancelledShownFor)) || !await audiences.IncludesAsync(row.AppliesTo, me, ct))
        {
            return null;
        }
        var occurrence = Expand(row, now, now.AddYears(2)).FirstOrDefault(o => OccurrenceEnd(o, row) > now) ?? Expand(row, null, null).LastOrDefault();
        if (occurrence is null)
        {
            return null;
        }
        var registration = row.Registration ? RegistrationOf(row, occurrence, await SeatsAsync([row.Id], ct), me, false, now) : null;
        return new CalendarEventCard(row.Id, row.Titles, row.Status, row.AllDay, occurrence.Start, occurrence.End, occurrence.StartDate, occurrence.EndDate,
            row.TimeZone, row.Location, row.OnlineUrl, occurrence.Key,
            registration is null ? null : new CalendarEventRegistration(registration.State, registration.Position, registration.SeatsLeft, registration.Open, registration.Waitlist));
    }

    public async Task<IReadOnlyList<CalendarEventChoice>> UpcomingAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var list = new List<CalendarEventChoice>();
        var seen = new HashSet<Guid>();
        foreach (var (o, _, _, _) in await VisibleAsync(now, now.AddYears(1), ct))
        {
            if (o.Event.Status == "published" && seen.Add(o.Event.Id) && OccurrenceEnd(o, o.Event) > now)
            {
                list.Add(new CalendarEventChoice(o.Event.Id, o.Event.Titles, o.Start, o.StartDate));
            }
        }
        return list.OrderBy(c => c.Start ?? new DateTimeOffset(c.StartDate!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)).Take(100).ToList();
    }
}
