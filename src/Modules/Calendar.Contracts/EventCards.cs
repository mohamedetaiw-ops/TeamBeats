namespace TeamBeats.Modules.Calendar.Contracts;

/// <summary>Registration of the reader for the occurrence shown on a card.</summary>
public sealed record CalendarEventRegistration(string? State, int? Position, int SeatsLeft, bool Open, bool Waitlist);

/// <summary>
/// A company event as a card for another module (spec 009 FR-018 R3, spec 037): its times, place, online link and the
/// reader's registration for the next occurrence, or the last one when all are over. The event stays in the calendar.
/// </summary>
public sealed record CalendarEventCard(Guid Id, IReadOnlyDictionary<string, string> Titles, string Status, bool AllDay, DateTimeOffset? Start, DateTimeOffset? End,
    DateOnly? StartDate, DateOnly? EndDate, string TimeZone, string? Location, string? OnlineUrl, string OccurrenceKey, CalendarEventRegistration? Registration);

/// <summary>A coming company event the signed-in person sees, for choosing it in another module.</summary>
public sealed record CalendarEventChoice(Guid Id, IReadOnlyDictionary<string, string> Titles, DateTimeOffset? Start, DateOnly? StartDate);

/// <summary>Company events of the tenant in the session, read for the signed-in person.</summary>
public interface ICalendarEventCards
{
    /// <summary>Whether a published company event with this ID exists in the tenant.</summary>
    Task<bool> PublishedAsync(Guid eventId, CancellationToken ct);

    /// <summary>The card for the signed-in person; null when the event is gone or its audience does not include them.</summary>
    Task<CalendarEventCard?> ForReaderAsync(Guid eventId, CancellationToken ct);

    /// <summary>Published events the signed-in person sees with an occurrence in the coming year, soonest first, at most 100.</summary>
    Task<IReadOnlyList<CalendarEventChoice>> UpcomingAsync(CancellationToken ct);
}
