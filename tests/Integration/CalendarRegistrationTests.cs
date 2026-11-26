using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Calendar;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 037 R3 US8 and US10: registration with seats, a waiting list in order of joining, moving up when a seat frees or
/// the capacity grows, a closing time, the calendar file of an occurrence, and attachments read with the audience
/// check. People outside the audience and other tenants get not found. All names and content are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CalendarRegistrationTests(TenantWorld world)
{
    private const string Api = "/api/v1/calendar";

    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Dubai).DateTime);

    private static DateTimeOffset At(DateOnly day, double hours) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(4)).AddHours(hours);

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private sealed record World(TestTenant T, Guid Gulf, Guid Egypt);

    /// <summary>Gulf has Mona, Karim, Rana, Omar and comms_gulf (content publisher of the Gulf subtree); Nadia is in Egypt.</summary>
    private async Task<World> FreshAsync()
    {
        var t = await world.CreateTenantAsync("calr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "karim", "rana", "omar", "comms_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "nadia", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "nadia", "Egypt");
        await world.AssignAsync(t, "comms_gulf", BuiltInRoles.ContentPublisher, "org_subtree", gulf);
        return new World(t, gulf, egypt);
    }

    private static object Course(Guid unit, DateOnly day, int capacity, bool waitlist, DateTimeOffset? closes = null, string? reminder = null) => new
    {
        titles = new Dictionary<string, string> { ["en"] = "First aid course, level 1", ["ar"] = "دورة الإسعافات الأولية" },
        descriptions = new Dictionary<string, string> { ["en"] = "Bring comfortable clothes; lunch is served." },
        kind = "department",
        orgUnitId = unit,
        allDay = false,
        start = At(day, 9),
        end = At(day, 13),
        timeZone = "Asia/Dubai",
        location = "Training room, Dubai",
        rsvp = false,
        registration = true,
        capacity,
        waitlist,
        registrationClosesAt = closes,
        reminder,
    };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : problem.GetProperty("detailKey").GetString()!;
    }

    private static async Task<JsonElement> ItemAsync(HttpClient client, DateOnly day)
    {
        var events = await OkAsync(client.GetAsync($"{Api}/events?from={D(day)}&to={D(day.AddDays(1))}&layers=department"));
        return events.GetProperty("items").EnumerateArray().Single();
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, Guid id, string key) =>
        client.PostAsJsonAsync($"{Api}/events/{id}/registration", new { occurrenceKey = key });

    private static Task<HttpResponseMessage> UnregisterAsync(HttpClient client, Guid id, string key) =>
        client.DeleteAsync($"{Api}/events/{id}/registration?occurrence={Uri.EscapeDataString(key)}");

    private static Task<HttpResponseMessage> AttachAsync(HttpClient client, Guid id, string name, byte[] data, string type)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PostAsync($"{Api}/admin-events/{id}/attachments?name={Uri.EscapeDataString(name)}", content);
    }

    private static byte[] Pdf(string text) => Encoding.ASCII.GetBytes("%PDF-1.4\n" + text + "\n%%EOF\n");

    [Fact]
    public async Task Seats_fill_in_order_the_waiting_list_moves_up_when_a_seat_frees_or_the_capacity_grows_and_the_file_merges_by_UID()
    {
        var w = await FreshAsync();
        var t = w.T;
        var comms = t["comms_gulf"].Client;
        var day = Today().AddDays(10);

        // Validation: RSVP and registration together, no capacity, closing after the start, an unknown reminder.
        Assert.Equal("validation.calendar_event.answers_both", await ProblemKeyAsync(comms.PostAsJsonAsync($"{Api}/admin-events",
            new { titles = new Dictionary<string, string> { ["en"] = "Both" }, kind = "department", orgUnitId = w.Gulf, allDay = false, start = At(day, 9), end = At(day, 10),
                timeZone = "Asia/Dubai", rsvp = true, registration = true, capacity = 5 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.calendar_event.capacity", await ProblemKeyAsync(comms.PostAsJsonAsync($"{Api}/admin-events", Course(w.Gulf, day, 0, false)), HttpStatusCode.BadRequest));
        Assert.Equal("validation.calendar_event.closes", await ProblemKeyAsync(comms.PostAsJsonAsync($"{Api}/admin-events",
            Course(w.Gulf, day, 2, true, closes: At(day, 10))), HttpStatusCode.BadRequest));
        Assert.Equal("validation.calendar_event.reminder", await ProblemKeyAsync(comms.PostAsJsonAsync($"{Api}/admin-events",
            Course(w.Gulf, day, 2, true, reminder: "2h")), HttpStatusCode.BadRequest));

        var created = await OkAsync(comms.PostAsJsonAsync($"{Api}/admin-events", Course(w.Gulf, day, 2, true)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("1h", created.GetProperty("reminder").GetString());
        var item = await ItemAsync(t["mona"].Client, day);
        var key = item.GetProperty("occurrenceKey").GetString()!;
        Assert.Contains("register", item.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(2, item.GetProperty("registration").GetProperty("seatsLeft").GetInt32());

        // Mona and Karim take the two seats; Rana and then Omar join the waiting list in that order.
        Assert.Equal("registered", (await OkAsync(RegisterAsync(t["mona"].Client, id, key))).GetProperty("state").GetString());
        Assert.Equal("registered", (await OkAsync(RegisterAsync(t["karim"].Client, id, key))).GetProperty("state").GetString());
        var rana = await OkAsync(RegisterAsync(t["rana"].Client, id, key));
        Assert.Equal(("waiting", 1, 0), (rana.GetProperty("state").GetString(), rana.GetProperty("position").GetInt32(), rana.GetProperty("seatsLeft").GetInt32()));
        Assert.Equal(2, (await OkAsync(RegisterAsync(t["omar"].Client, id, key))).GetProperty("position").GetInt32());
        // Registering twice keeps the place.
        Assert.Equal(1, (await OkAsync(RegisterAsync(t["rana"].Client, id, key))).GetProperty("position").GetInt32());

        // Each person sees only their own place; the manager sees counts and, with the list, names.
        var monaView = (await ItemAsync(t["mona"].Client, day)).GetProperty("registration");
        Assert.Equal(JsonValueKind.Null, monaView.GetProperty("taken").ValueKind);
        var commsView = (await ItemAsync(comms, day)).GetProperty("registration");
        Assert.Equal((2, 2), (commsView.GetProperty("taken").GetInt32(), commsView.GetProperty("waiting").GetInt32()));
        var list = await OkAsync(comms.GetAsync($"{Api}/admin-events/{id}/registrations"));
        Assert.Equal(4, list.GetProperty("people").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync($"{Api}/admin-events/{id}/registrations")).StatusCode);

        // Karim cancels: Rana takes the seat and is told; Omar is now first on the list.
        await OkAsync(UnregisterAsync(t["karim"].Client, id, key));
        Assert.Equal("registered", (await ItemAsync(t["rana"].Client, day)).GetProperty("registration").GetProperty("state").GetString());
        Assert.Equal(1, (await ItemAsync(t["omar"].Client, day)).GetProperty("registration").GetProperty("position").GetInt32());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["rana"].Id), ("k", CompanyEvents.PromotedTemplate)));

        // The capacity cannot go below the seats taken; raising it moves Omar up.
        var version = (await OkAsync(comms.GetAsync($"{Api}/admin-events/{id}"))).GetProperty("version").GetInt32();
        Assert.Equal("conflict.calendar_event.capacity_taken", await ProblemKeyAsync(comms.PatchAsJsonAsync($"{Api}/admin-events/{id}", Course(w.Gulf, day, 1, true)),
            HttpStatusCode.Conflict));
        await OkAsync(comms.PatchAsJsonAsync($"{Api}/admin-events/{id}", Course(w.Gulf, day, 3, true)));
        Assert.Equal("registered", (await ItemAsync(t["omar"].Client, day)).GetProperty("registration").GetProperty("state").GetString());
        Assert.True((await OkAsync(comms.GetAsync($"{Api}/admin-events/{id}"))).GetProperty("version").GetInt32() > version);

        // Full without a waiting list: refused.
        await OkAsync(comms.PatchAsJsonAsync($"{Api}/admin-events/{id}", Course(w.Gulf, day, 3, false)));
        Assert.Equal("conflict.calendar_event.full", await ProblemKeyAsync(RegisterAsync(t["karim"].Client, id, key), HttpStatusCode.Conflict));

        // The calendar file carries the TeamBeats UID, the time in UTC and the viewer's language.
        var file = await t["mona"].Client.GetAsync($"{Api}/events/{id}/calendar-file?occurrence={Uri.EscapeDataString(key)}");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("text/calendar", file.Content.Headers.ContentType!.MediaType);
        var ics = await file.Content.ReadAsStringAsync();
        Assert.Contains($"UID:{id}@teambeats\r\n", ics, StringComparison.Ordinal);
        Assert.Contains($"DTSTART:{At(day, 9).UtcDateTime:yyyyMMdd'T'HHmmss'Z'}\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:First aid course\\, level 1\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:Bring comfortable clothes\\; lunch is served.", ics, StringComparison.Ordinal);

        // Outside the audience (Egypt) and from another tenant: not found.
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterAsync(t["nadia"].Client, id, key)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["nadia"].Client.GetAsync($"{Api}/events/{id}/calendar-file?occurrence={Uri.EscapeDataString(key)}")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterAsync(other.T["mona"].Client, id, key)).StatusCode);

        // Turning registration off removes every place and tells the people who held one.
        await OkAsync(comms.PatchAsJsonAsync($"{Api}/admin-events/{id}", new
        {
            titles = new Dictionary<string, string> { ["en"] = "First aid course, level 1" }, kind = "department", orgUnitId = w.Gulf, allDay = false, start = At(day, 9),
            end = At(day, 13), timeZone = "Asia/Dubai", rsvp = true,
        }));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from calendar.registration where event_id = @e", ("e", id)));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k",
            ("u", t["mona"].Id), ("k", CompanyEvents.EndedTemplate)));
    }

    [Fact]
    public async Task Parallel_registrations_never_pass_the_capacity_and_registration_closes_at_its_time()
    {
        var w = await FreshAsync();
        var t = w.T;
        var day = Today().AddDays(12);
        var id = (await OkAsync(t["comms_gulf"].Client.PostAsJsonAsync($"{Api}/admin-events", Course(w.Gulf, day, 1, false)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var key = (await ItemAsync(t["mona"].Client, day)).GetProperty("occurrenceKey").GetString()!;
        var results = await Task.WhenAll(new[] { "mona", "karim", "rana", "omar" }.Select(p => RegisterAsync(t[p].Client, id, key)));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(3, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from calendar.registration where event_id = @e and state = 'registered'", ("e", id)));

        // A closing time in the past closes registration for everyone.
        var closed = (await OkAsync(t["comms_gulf"].Client.PostAsJsonAsync($"{Api}/admin-events",
            Course(w.Gulf, day.AddDays(1), 10, true, closes: DateTimeOffset.UtcNow.AddMinutes(-1))), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var closedItem = (await OkAsync(t["mona"].Client.GetAsync($"{Api}/events?from={D(day.AddDays(1))}&to={D(day.AddDays(2))}&layers=department")))
            .GetProperty("items").EnumerateArray().Single();
        Assert.False(closedItem.GetProperty("registration").GetProperty("open").GetBoolean());
        Assert.DoesNotContain("register", closedItem.GetProperty("actions").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("conflict.calendar_event.registration_closed", await ProblemKeyAsync(
            RegisterAsync(t["mona"].Client, closed, closedItem.GetProperty("occurrenceKey").GetString()!), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Attachments_are_checked_scanned_shown_to_the_audience_only_and_go_with_the_event()
    {
        var w = await FreshAsync();
        var t = w.T;
        var comms = t["comms_gulf"].Client;
        var day = Today().AddDays(9);
        var id = (await OkAsync(comms.PostAsJsonAsync($"{Api}/admin-events", Course(w.Gulf, day, 5, false)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        Assert.Equal("validation.calendar_event.attachment_type", await ProblemKeyAsync(AttachAsync(comms, id, "agenda.html", Encoding.UTF8.GetBytes("<script>x</script>"), "text/html"),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.calendar_event.attachment_type", await ProblemKeyAsync(AttachAsync(comms, id, "agenda.docx", Pdf("not a zip"), "application/pdf"),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.calendar_event.attachment_infected", await ProblemKeyAsync(AttachAsync(comms, id, "agenda.pdf", Pdf(TestSignatureScanner.Eicar), "application/pdf"),
            HttpStatusCode.BadRequest));
        // Someone without content.publish cannot attach.
        Assert.Equal(HttpStatusCode.Forbidden, (await AttachAsync(t["mona"].Client, id, "agenda.pdf", Pdf("Agenda"), "application/pdf")).StatusCode);

        var file = await OkAsync(AttachAsync(comms, id, "Agenda.pdf", Pdf("Agenda of the course"), "application/pdf"), HttpStatusCode.Created);
        var fileId = file.GetProperty("id").GetGuid();
        Assert.Equal("application/pdf", file.GetProperty("contentType").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.CalendarAttachmentAdded, id.ToString());
        for (var i = 2; i <= CompanyEvents.MaxAttachments; i++)
        {
            await OkAsync(AttachAsync(comms, id, $"Part {i}.pdf", Pdf($"Part {i}"), "application/pdf"), HttpStatusCode.Created);
        }
        Assert.Equal("conflict.calendar_event.attachment_limit", await ProblemKeyAsync(AttachAsync(comms, id, "More.pdf", Pdf("More"), "application/pdf"), HttpStatusCode.Conflict));

        // Mona in the audience sees and opens it; Nadia (Egypt) and another tenant get not found.
        var item = await ItemAsync(t["mona"].Client, day);
        Assert.Equal(CompanyEvents.MaxAttachments, item.GetProperty("attachments").GetArrayLength());
        var download = await t["mona"].Client.GetAsync($"{Api}/events/{id}/attachments/{fileId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("Agenda of the course", Encoding.ASCII.GetString(await download.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await t["nadia"].Client.GetAsync($"{Api}/events/{id}/attachments/{fileId}")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["mona"].Client.GetAsync($"{Api}/events/{id}/attachments/{fileId}")).StatusCode);

        // Removing one, then deleting the event, takes the files away.
        Assert.Equal(HttpStatusCode.NoContent, (await comms.DeleteAsync($"{Api}/admin-events/{id}/attachments/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["mona"].Client.GetAsync($"{Api}/events/{id}/attachments/{fileId}")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.CalendarAttachmentRemoved, id.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await comms.DeleteAsync($"{Api}/admin-events/{id}")).StatusCode);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from calendar.attachment where event_id = @e", ("e", id)));
    }

    private Task<CalendarMaintenance.Run> MaintainAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var run = await sp.GetRequiredService<CalendarMaintenance>().RunDueAsync(now.ToUniversalTime(), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    private Task<long> NoticesAsync(TestTenant t, string person, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[person].Id), ("k", template));

    [Fact]
    public async Task Reminders_go_once_to_people_registered_or_going_and_a_cancellation_tells_everyone_with_a_place()
    {
        var w = await FreshAsync();
        var t = w.T;
        var comms = t["comms_gulf"].Client;
        var day = Today().AddDays(8);

        // A course with one seat and a waiting list, reminding a day before; and a town hall with RSVP, an hour before.
        var course = (await OkAsync(comms.PostAsJsonAsync($"{Api}/admin-events", Course(w.Gulf, day, 1, true, reminder: "1d")), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var key = (await ItemAsync(t["mona"].Client, day)).GetProperty("occurrenceKey").GetString()!;
        await OkAsync(RegisterAsync(t["mona"].Client, course, key));
        await OkAsync(RegisterAsync(t["karim"].Client, course, key));
        var hall = (await OkAsync(comms.PostAsJsonAsync($"{Api}/admin-events", new
        {
            titles = new Dictionary<string, string> { ["en"] = "Town hall" }, kind = "department", orgUnitId = w.Gulf, allDay = false, start = At(day, 15), end = At(day, 16),
            timeZone = "Asia/Dubai", onlineUrl = "https://meet.example.com/town-hall", rsvp = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var hallKey = (await OkAsync(t["rana"].Client.GetAsync($"{Api}/events?from={D(day)}&to={D(day.AddDays(1))}&layers=department")))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("sourceId").GetString() == hall.ToString()).GetProperty("occurrenceKey").GetString()!;
        await OkAsync(t["rana"].Client.PostAsJsonAsync($"{Api}/events/{hall}/response", new { occurrenceKey = hallKey, answer = "going" }));
        await OkAsync(t["omar"].Client.PostAsJsonAsync($"{Api}/events/{hall}/response", new { occurrenceKey = hallKey, answer = "declined" }));

        // Two days before: nothing yet. A day before the course: Mona is reminded, Karim (waiting) is not.
        Assert.Equal(0, (await MaintainAsync(t, At(day, 9).AddDays(-2))).Reminded);
        Assert.Equal(1, (await MaintainAsync(t, At(day, 9).AddDays(-1).AddMinutes(1))).Reminded);
        Assert.Equal(1L, await NoticesAsync(t, "mona", CalendarMaintenance.ReminderTemplate));
        Assert.Equal(0L, await NoticesAsync(t, "karim", CalendarMaintenance.ReminderTemplate));
        // Running again sends nothing more.
        Assert.Equal(0, (await MaintainAsync(t, At(day, 9).AddDays(-1).AddMinutes(2))).Reminded);

        // An hour before the town hall: Rana (Going) is reminded, Omar (Not going) is not.
        Assert.Equal(1, (await MaintainAsync(t, At(day, 14).AddMinutes(1))).Reminded);
        Assert.Equal(1L, await NoticesAsync(t, "rana", CalendarMaintenance.ReminderTemplate));
        Assert.Equal(0L, await NoticesAsync(t, "omar", CalendarMaintenance.ReminderTemplate));

        // The course is cancelled: Mona (registered) and Karim (waiting) are told once.
        await OkAsync(comms.PostAsync($"{Api}/admin-events/{course}/cancel", null));
        var run = await MaintainAsync(t, DateTimeOffset.UtcNow);
        Assert.Equal(2, run.ToldOfCancellation);
        Assert.Equal(0, (await MaintainAsync(t, DateTimeOffset.UtcNow)).ToldOfCancellation);
        Assert.Equal(1L, await NoticesAsync(t, "karim", CalendarMaintenance.CancelledTemplate));
        Assert.Equal(0L, await NoticesAsync(t, "rana", CalendarMaintenance.CancelledTemplate));
    }
}
