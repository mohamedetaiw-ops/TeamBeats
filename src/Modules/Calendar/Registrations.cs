using System.Globalization;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Calendar.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

internal sealed record Seat(Guid EventId, string Key, Guid UserId, string State, DateTimeOffset JoinedAt);

/// <summary>
/// Registration for events with seats (spec 037 US8, FR-028 to FR-031). Seats are taken while the event row is locked,
/// so parallel requests never pass the capacity; the waiting list is first come first served. Each person sees only
/// their own place; managers of the event see counts, and names with <c>content.publish</c> on the owning unit.
/// </summary>
public sealed partial class CompanyEvents
{
    public const string PromotedTemplate = "calendar.registration_promoted";
    public const string EndedTemplate = "calendar.registration_ended";

    /// <summary>Registers the signed-in person for an occurrence, or puts them on the waiting list when it is full.</summary>
    public async Task<RegistrationView> RegisterAsync(Guid id, RegistrationRequest request, CancellationToken ct)
    {
        var me = context.RequireUser();
        var (row, occurrence) = await AudienceOccurrenceAsync(id, request.OccurrenceKey, ct);
        var now = clock.UtcNow;
        if (!row.Registration || !RegistrationOpen(row, occurrence, now))
        {
            throw AppException.Conflict("conflict.calendar_event.registration_closed");
        }
        // The lock makes counting and taking a seat one step for everyone registering at the same time.
        await session.ExecAsync("select 1 from calendar.event where id = @id for update", new { id }, ct);
        var seats = await SeatsAsync([id], ct);
        var mine = seats.FirstOrDefault(s => s.Key == occurrence.Key && s.UserId == me);
        if (mine is null)
        {
            var taken = seats.Count(s => s.Key == occurrence.Key && s.State == RegistrationStates.Registered);
            var state = taken < row.Capacity ? RegistrationStates.Registered : row.Waitlist ? RegistrationStates.Waiting : null;
            if (state is null)
            {
                throw AppException.Conflict("conflict.calendar_event.full");
            }
            await session.ExecAsync(
                """
                insert into calendar.registration (tenant_id, event_id, occurrence_key, user_id, state, joined_at)
                values (platform.current_tenant_id(), @id, @key, @me, @state, @now)
                """, new { id, key = occurrence.Key, me, state, now }, ct);
            await outbox.EnqueueAsync(new EventRegistrationChanged(id, occurrence.Key, me, state == RegistrationStates.Registered ? "registered" : "waitlisted"), ct);
            seats = await SeatsAsync([id], ct);
        }
        return RegistrationOf(row, occurrence, seats, me, false, now)!;
    }

    /// <summary>Gives up a seat or a place on the waiting list. A freed seat goes to the first person waiting.</summary>
    public async Task<RegistrationView> CancelRegistrationAsync(Guid id, string? occurrenceKey, CancellationToken ct)
    {
        var me = context.RequireUser();
        var (row, occurrence) = await AudienceOccurrenceAsync(id, occurrenceKey, ct);
        var now = clock.UtcNow;
        if (!row.Registration || OccurrenceStart(occurrence, row) <= now)
        {
            throw AppException.Conflict("conflict.calendar_event.registration_closed");
        }
        await session.ExecAsync("select 1 from calendar.event where id = @id for update", new { id }, ct);
        var state = await session.QuerySingleOrDefaultAsync(
            "delete from calendar.registration where event_id = @id and occurrence_key = @key and user_id = @me returning state",
            new { id, key = occurrence.Key, me }, r => r.GetString(0), ct);
        if (state is not null)
        {
            await outbox.EnqueueAsync(new EventRegistrationChanged(id, occurrence.Key, me, "cancelled"), ct);
            if (state == RegistrationStates.Registered)
            {
                await PromoteAsync(row, occurrence.Key, ct);
            }
        }
        return RegistrationOf(row, occurrence, await SeatsAsync([id], ct), me, false, now)!;
    }

    /// <summary>Seats taken and the waiting list per occurrence, with names (FR-030).</summary>
    public async Task<RegistrationsView> RegistrationsAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        var seats = await SeatsAsync([id], ct);
        var counts = seats.GroupBy(s => s.Key).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new RegistrationCounts(g.Key, row.Capacity ?? 0,
            g.Count(s => s.State == RegistrationStates.Registered), g.Count(s => s.State == RegistrationStates.Waiting))).ToList();
        var names = await users.GetManyAsync(seats.Select(s => s.UserId).Distinct().ToList(), ct);
        var people = seats.Select(s => new RegistrationPerson(s.Key, new PersonRef(s.UserId, names.GetValueOrDefault(s.UserId)?.DisplayName ?? string.Empty),
            s.State, s.JoinedAt)).ToList();
        return new RegistrationsView(counts, people);
    }

    /// <summary>
    /// The calendar file of one occurrence (FR-031) with the UID TeamBeats gives the event, so an imported copy merges
    /// with the TeamBeats item (FR-004). Text is the viewer's language.
    /// </summary>
    public async Task<string> CalendarFileAsync(Guid id, string occurrenceKey, CancellationToken ct)
    {
        var (row, o) = await AudienceOccurrenceAsync(id, occurrenceKey, ct);
        var text = new StringBuilder();
        void Line(string value)
        {
            // RFC 5545 section 3.1: lines longer than 75 octets continue on the next line after a space.
            var bytes = Encoding.UTF8.GetBytes(value);
            var start = 0;
            var first = true;
            while (start < bytes.Length)
            {
                var length = Math.Min(first ? 75 : 74, bytes.Length - start);
                while (start + length < bytes.Length && (bytes[start + length] & 0xC0) == 0x80)
                {
                    length--;
                }
                text.Append(first ? "" : " ").Append(Encoding.UTF8.GetString(bytes, start, length)).Append("\r\n");
                start += length;
                first = false;
            }
        }
        string Utc(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string Date(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//TeamBeats//Calendar//EN");
        Line("METHOD:PUBLISH");
        Line("BEGIN:VEVENT");
        Line($"UID:{row.Id}@teambeats");
        Line($"DTSTAMP:{Utc(clock.UtcNow)}");
        if (row.AllDay)
        {
            Line($"DTSTART;VALUE=DATE:{Date(o.StartDate!.Value)}");
            Line($"DTEND;VALUE=DATE:{Date(o.EndDate!.Value)}");
            if (row.Recurrence is not null)
            {
                Line($"RECURRENCE-ID;VALUE=DATE:{Date(o.StartDate.Value)}");
            }
        }
        else
        {
            Line($"DTSTART:{Utc(o.Start!.Value)}");
            Line($"DTEND:{Utc(o.End!.Value)}");
            if (row.Recurrence is not null)
            {
                Line($"RECURRENCE-ID:{Utc(o.Start.Value)}");
            }
        }
        Line($"SUMMARY:{Escape(PickLocale(row.Titles))}");
        if (row.Descriptions.Count > 0)
        {
            Line($"DESCRIPTION:{Escape(PickLocale(row.Descriptions))}");
        }
        if (row.Location is not null)
        {
            Line($"LOCATION:{Escape(row.Location)}");
        }
        if (row.OnlineUrl is not null)
        {
            Line($"URL:{row.OnlineUrl}");
        }
        Line($"STATUS:{(row.Status == "cancelled" ? "CANCELLED" : "CONFIRMED")}");
        Line("END:VEVENT");
        Line("END:VCALENDAR");
        return text.ToString();
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal)
            .Replace("\r\n", "\\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);

    private string PickLocale(IReadOnlyDictionary<string, string> values) => PickFor(values, locale.Locale);

    internal static string PickFor(IReadOnlyDictionary<string, string> values, string tag)
    {
        if (values.TryGetValue(tag, out var exact) && !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }
        var language = tag.Split('-')[0];
        return values.FirstOrDefault(p => p.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase)).Value
            ?? values.GetValueOrDefault("en") ?? values.Values.FirstOrDefault() ?? string.Empty;
    }

    // ---- Changes by the event's managers ----

    /// <summary>The capacity cannot go below the seats already taken in any occurrence that has not started (FR-029).</summary>
    private async Task CheckCapacityAsync(EventRow row, EventRequest request, int? capacity, CancellationToken ct)
    {
        if (!row.Registration || !request.Registration || capacity is not { } next || next >= (row.Capacity ?? 0))
        {
            return;
        }
        var taken = await session.ScalarAsync<long?>(
            "select max(c) from (select count(*) c from calendar.registration where event_id = @id and state = 'registered' group by occurrence_key) t",
            new { id = row.Id }, ct) ?? 0;
        if (taken > next)
        {
            throw AppException.Conflict("conflict.calendar_event.capacity_taken");
        }
    }

    /// <summary>
    /// After an event changed: registration turned off clears every place and tells the people who held one; a raised
    /// capacity or a waiting list turned off moves people up or off the list.
    /// </summary>
    private async Task AfterRegistrationChangeAsync(EventRow before, EventRow after, CancellationToken ct)
    {
        if (!before.Registration)
        {
            return;
        }
        if (!after.Registration)
        {
            var gone = await session.QueryAsync("delete from calendar.registration where event_id = @id returning user_id, occurrence_key",
                new { id = after.Id }, r => (User: r.GetGuid(0), Key: r.GetString(1)), ct);
            foreach (var (user, key) in gone)
            {
                await outbox.EnqueueAsync(new EventRegistrationChanged(after.Id, key, user, "cancelled"), ct);
            }
            await NotifyAsync(after, gone.Select(g => g.User).Distinct().ToList(), EndedTemplate, null, $"calendar:event:{after.Id:N}:registration_ended:{after.Version}", ct);
            return;
        }
        var keys = await session.QueryAsync("select distinct occurrence_key from calendar.registration where event_id = @id and state = 'waiting'",
            new { id = after.Id }, r => r.GetString(0), ct);
        foreach (var key in keys)
        {
            await PromoteAsync(after, key, ct);
        }
        if (!after.Waitlist)
        {
            var dropped = await session.QueryAsync("delete from calendar.registration where event_id = @id and state = 'waiting' returning user_id, occurrence_key",
                new { id = after.Id }, r => (User: r.GetGuid(0), Key: r.GetString(1)), ct);
            foreach (var (user, key) in dropped)
            {
                await outbox.EnqueueAsync(new EventRegistrationChanged(after.Id, key, user, "cancelled"), ct);
            }
            await NotifyAsync(after, dropped.Select(d => d.User).Distinct().ToList(), EndedTemplate, null, $"calendar:event:{after.Id:N}:waitlist_ended:{after.Version}", ct);
        }
    }

    /// <summary>Fills free seats of an occurrence from the waiting list, in order of joining, and tells each person moved up.</summary>
    private async Task PromoteAsync(EventRow row, string key, CancellationToken ct)
    {
        var free = await session.ScalarAsync<long>(
            "select @capacity - count(*) from calendar.registration where event_id = @id and occurrence_key = @key and state = 'registered'",
            new { id = row.Id, key, capacity = row.Capacity ?? 0 }, ct);
        if (free <= 0)
        {
            return;
        }
        var promoted = await session.QueryAsync(
            """
            update calendar.registration set state = 'registered'
            where event_id = @id and occurrence_key = @key and user_id in (
              select user_id from calendar.registration where event_id = @id and occurrence_key = @key and state = 'waiting' order by joined_at, user_id limit @free)
            returning user_id
            """, new { id = row.Id, key, free = (int)free }, r => r.GetGuid(0), ct);
        foreach (var user in promoted)
        {
            await outbox.EnqueueAsync(new EventRegistrationChanged(row.Id, key, user, "promoted"), ct);
            await NotifyAsync(row, [user], PromotedTemplate, key, $"calendar:event:{row.Id:N}:{key}:promoted:{user:N}", ct);
        }
    }

    private async Task NotifyAsync(EventRow row, IReadOnlyCollection<Guid> people, string template, string? key, string dedupe, CancellationToken ct)
    {
        if (people.Count == 0)
        {
            return;
        }
        var tag = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var occurrence = key is null ? null : Expand(row, null, null).FirstOrDefault(o => o.Key == key);
        var when = occurrence is null ? "" : WhenText(row, occurrence, tag);
        var date = occurrence?.StartDate ?? (occurrence?.Start is { } s ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(s, row.Zone).DateTime) : (DateOnly?)null);
        var link = date is { } d ? $"/calendar?view=agenda&date={d:yyyy-MM-dd}" : "/calendar";
        await notifications.SendAsync(new NotificationRequest(people, NotificationCategories.Events, template, link,
            new Dictionary<string, string> { ["event"] = PickFor(row.Titles, tag), ["when"] = when }, DedupeKey: dedupe), ct);
    }

    /// <summary>The local date and time of an occurrence in the event's zone, in the given language, for notifications.</summary>
    internal static string WhenText(EventRow row, Occurrence o, string tag)
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(tag);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
        }
        return o.StartDate is { } date
            ? date.ToString("D", culture)
            : TimeZoneInfo.ConvertTime(o.Start!.Value, row.Zone).ToString("f", culture);
    }

    // ---- Reading ----

    private async Task<(EventRow Row, Occurrence Occurrence)> AudienceOccurrenceAsync(Guid id, string? occurrenceKey, CancellationToken ct)
    {
        var me = context.RequireUser();
        var row = await RowAsync(id, ct);
        if (row is null || !await audiences.IncludesAsync(row.AppliesTo, me, ct))
        {
            throw AppException.NotFound();
        }
        var occurrence = occurrenceKey is null ? null : Expand(row, null, null).FirstOrDefault(o => o.Key == occurrenceKey);
        return occurrence is null ? throw AppException.Validation("occurrenceKey", "validation.calendar_event.occurrence") : (row, occurrence);
    }

    internal async Task<IReadOnlyList<Seat>> SeatsAsync(Guid[] ids, CancellationToken ct) =>
        ids.Length == 0 ? [] : await session.QueryAsync(
            "select event_id, occurrence_key, user_id, state, joined_at from calendar.registration where event_id = any(@ids) order by joined_at, user_id",
            new { ids }, r => new Seat(r.GetGuid(0), r.GetString(1), r.GetGuid(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4)), ct);

    internal static RegistrationView? RegistrationOf(EventRow row, Occurrence o, IReadOnlyList<Seat> seats, Guid me, bool manages, DateTimeOffset now)
    {
        if (!row.Registration)
        {
            return null;
        }
        var mine = seats.Where(s => s.EventId == row.Id && s.Key == o.Key).ToList();
        var taken = mine.Count(s => s.State == RegistrationStates.Registered);
        var waiting = mine.Where(s => s.State == RegistrationStates.Waiting).ToList();
        var own = mine.FirstOrDefault(s => s.UserId == me);
        int? position = own?.State == RegistrationStates.Waiting ? waiting.FindIndex(s => s.UserId == me) + 1 : null;
        var left = Math.Max(0, (row.Capacity ?? 0) - taken);
        return new RegistrationView(own?.State, position, left, RegistrationOpen(row, o, now), row.Waitlist,
            manages ? taken : null, manages ? waiting.Count : null);
    }

    private static bool RegistrationOpen(EventRow row, Occurrence o, DateTimeOffset now) =>
        row.Status == "published" && (row.RegistrationClosesAt is not { } closes || now < closes) && OccurrenceStart(o, row) > now;

    internal static DateTimeOffset OccurrenceStart(Occurrence o, EventRow row) =>
        o.Start ?? ToInstant(o.StartDate!.Value.ToDateTime(TimeOnly.MinValue), row.Zone);

    private static string AttachmentKey(Guid eventId, Guid attachmentId) => $"calendar/events/{eventId:N}/{attachmentId:N}";
}
