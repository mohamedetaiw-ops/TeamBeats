using System.Text.Json;
using TeamBeats.Modules.Calendar;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Integrations.Google;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 037: recurrence expansion (FR-017), occurrences across a daylight saving change, all-day exclusive ends, the
/// date rule of <see cref="UnifiedCalendar.Touches"/>, and the Google Calendar mapping (FR-011).
/// </summary>
public sealed class CalendarRulesTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [Fact]
    public void Weekly_on_two_weekdays_with_an_interval_and_a_count()
    {
        var start = new DateOnly(2026, 11, 2); // Monday
        var dates = Recurrence.Dates(start, new RecurrenceRule("weekly", 2, [1, 3], Count: 4)).ToList();
        Assert.Equal([start, start.AddDays(2), start.AddDays(14), start.AddDays(16)], dates);
    }

    [Fact]
    public void Monthly_skips_months_without_the_day_and_stops_at_until()
    {
        var dates = Recurrence.Dates(new DateOnly(2027, 1, 31), new RecurrenceRule("monthly", Until: new DateOnly(2027, 6, 30))).ToList();
        Assert.Equal([new DateOnly(2027, 1, 31), new DateOnly(2027, 3, 31), new DateOnly(2027, 5, 31)], dates);
    }

    [Fact]
    public void A_series_never_passes_the_limits()
    {
        var start = new DateOnly(2026, 1, 1);
        Assert.Equal(Recurrence.MaxOccurrences, Recurrence.Dates(start, new RecurrenceRule("daily")).Count());
        Assert.True(Recurrence.Dates(start, new RecurrenceRule("monthly", 12)).Last() <= start.AddYears(Recurrence.MaxYears));
        Assert.Equal([start], Recurrence.Dates(start, null));
    }

    [Fact]
    public void Validation_refuses_unknown_frequencies_bad_weekdays_and_far_ends()
    {
        var start = new DateOnly(2026, 11, 2);
        var errors = new ValidationErrors();
        Recurrence.Validate(new RecurrenceRule("hourly", 0, [9], start.AddYears(6), 500), start, errors);
        var ex = Assert.Throws<AppException>(errors.ThrowIfAny);
        Assert.NotNull(ex);
        var ok = new ValidationErrors();
        Recurrence.Validate(new RecurrenceRule("weekly", 1, [1, 5], Count: 10), start, ok);
        ok.ThrowIfAny();
    }

    private static EventRow Row(bool allDay, DateTimeOffset? start, DateTimeOffset? end, DateOnly? startDate, DateOnly? endDate, RecurrenceRule? rule, string zone = "Europe/London") =>
        new(Guid.NewGuid(), null, EventKinds.Company, new() { ["en"] = "Event" }, [], allDay, start, end, startDate, endDate, zone, null, null,
            ContentAudience.Everyone, rule, false, "published", null, 1, DateTimeOffset.UtcNow);

    [Fact]
    public void A_weekly_series_keeps_its_local_time_across_the_clock_change()
    {
        // 09:00 in London on the Monday before the clocks go back (25 October 2026), then the Monday after.
        var start = new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero);
        var row = Row(false, start, start.AddHours(1), null, null, new RecurrenceRule("weekly", Count: 2));
        var occurrences = CompanyEvents.Expand(row, null, null).ToList();
        Assert.Equal(2, occurrences.Count);
        Assert.All(occurrences, o => Assert.Equal(9, TimeZoneInfo.ConvertTime(o.Start!.Value, London).Hour));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero), occurrences[1].Start);
        Assert.Equal(TimeSpan.FromHours(1), occurrences[1].End - occurrences[1].Start);
    }

    [Fact]
    public void An_all_day_event_ends_on_its_exclusive_date_and_only_touches_its_days()
    {
        var row = Row(true, null, null, new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 4), null, "Asia/Dubai");
        var dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");
        DateTimeOffset Midnight(int day) => new(2026, 12, day, 0, 0, 0, TimeSpan.FromHours(4));
        Assert.Single(CompanyEvents.Expand(row, Midnight(3), Midnight(4)));
        Assert.Empty(CompanyEvents.Expand(row, Midnight(4), Midnight(5)));
        Assert.Empty(CompanyEvents.Expand(row, Midnight(1), Midnight(2)));

        var item = new CalendarItemView("x", CalendarLayers.Company, ItemSources.Events, "x", "Event", true, null, null, new DateOnly(2026, 12, 2), new DateOnly(2026, 12, 4),
            "Asia/Dubai", ItemStatuses.Confirmed, "tenant", []);
        Assert.False(UnifiedCalendar.Touches(item, new DateOnly(2026, 12, 1), dubai));
        Assert.True(UnifiedCalendar.Touches(item, new DateOnly(2026, 12, 3), dubai));
        Assert.False(UnifiedCalendar.Touches(item, new DateOnly(2026, 12, 4), dubai));

        // A timed item that ends at midnight does not touch the next day.
        var late = item with { AllDay = false, StartDate = null, EndDate = null, Start = Midnight(3).AddHours(22), End = Midnight(4) };
        Assert.True(UnifiedCalendar.Touches(late, new DateOnly(2026, 12, 3), dubai));
        Assert.False(UnifiedCalendar.Touches(late, new DateOnly(2026, 12, 4), dubai));
    }

    [Fact]
    public void Google_events_map_to_the_canonical_occurrence()
    {
        using var doc = JsonDocument.Parse("""
            {
              "id": "g1", "summary": "Design review", "iCalUID": "abc@google.com", "status": "confirmed", "visibility": "default",
              "start": { "dateTime": "2026-11-12T10:00:00+04:00" }, "end": { "dateTime": "2026-11-12T11:00:00+04:00" },
              "hangoutLink": "https://meet.google.com/abc-defg-hij", "htmlLink": "https://calendar.google.com/event?eid=1",
              "attendees": [ { "email": "other@acme.example", "responseStatus": "accepted" }, { "self": true, "responseStatus": "tentative" } ]
            }
            """);
        var o = GoogleCalendar.Map(doc.RootElement)!;
        Assert.Equal(("g1", "Design review", "abc@google.com", "google"), (o.Id, o.Subject, o.ICalUId, o.Source));
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 6, 0, 0, TimeSpan.Zero), o.Start);
        Assert.Equal(("tentative", "tentative", false), (o.Response, o.ShowAs, o.IsAllDay));
        Assert.Equal("https://meet.google.com/abc-defg-hij", o.OnlineJoinUrl);

        using var allDay = JsonDocument.Parse("""
            { "id": "g2", "summary": "Offsite", "transparency": "transparent", "visibility": "private",
              "start": { "date": "2026-11-20" }, "end": { "date": "2026-11-22" },
              "conferenceData": { "entryPoints": [ { "entryPointType": "video", "uri": "http://insecure.example" } ] }, "htmlLink": "javascript:alert(1)" }
            """);
        var a = GoogleCalendar.Map(allDay.RootElement)!;
        Assert.True(a.IsAllDay);
        Assert.True(a.IsPrivate);
        Assert.Equal("free", a.ShowAs);
        Assert.Null(a.OnlineJoinUrl);
        Assert.Equal(string.Empty, a.WebLink);
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero), a.End);

        using var broken = JsonDocument.Parse("""{ "id": "g3" }""");
        Assert.Null(GoogleCalendar.Map(broken.RootElement));
    }
}
