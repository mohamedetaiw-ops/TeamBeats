using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 FR-018 R3 (T042): an event item links one company event of the calendar by ID and never copies it. Readers
/// see the event card with times, place and their own registration when the event's audience includes them; others read
/// the text without the card. Only an event item takes an event, and only a published event of the tenant. All names
/// and content are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentEventItemTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Calendar = "/api/v1/calendar";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> KeyAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        return problem.GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString();
    }

    private static object Variants(string title) => new Dictionary<string, object>
    {
        ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
    };

    [Fact]
    public async Task An_event_item_shows_the_calendar_event_and_the_readers_own_registration()
    {
        var t = await world.CreateTenantAsync("cev-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("mona", "Gulf"), ("karim", "Gulf"), ("nadia", "Egypt"), ("pia", "Gulf") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var pia = t["pia"].Client;
        var day = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var start = new DateTimeOffset(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.FromHours(4));
        var course = (await OkAsync(pia.PostAsJsonAsync($"{Calendar}/admin-events", new
        {
            titles = new Dictionary<string, string> { ["en"] = "First aid course" },
            kind = "department", orgUnitId = gulf, allDay = false, start, end = start.AddHours(3), timeZone = "Asia/Dubai",
            location = "Training room, Dubai", rsvp = false, registration = true, capacity = 2, waitlist = true,
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var choices = await OkAsync(pia.GetAsync($"{Items}/event-choices"));
        Assert.Contains(choices.EnumerateArray(), c => c.GetProperty("id").GetGuid() == course);
        Assert.Empty((await OkAsync(t["nadia"].Client.GetAsync($"{Items}/event-choices"))).EnumerateArray()); // Outside the event's audience.
        Assert.Equal("validation.content.event", await KeyAsync(pia.PostAsJsonAsync(Items, new { type = "event", variants = Variants("First aid") })));
        Assert.Equal("validation.content.event", await KeyAsync(pia.PostAsJsonAsync(Items, new { type = "event", eventId = Guid.NewGuid(), variants = Variants("First aid") })));
        Assert.Equal("validation.content.event_type", await KeyAsync(pia.PostAsJsonAsync(Items, new { type = "news", eventId = course, variants = Variants("First aid") })));

        var created = await OkAsync(pia.PostAsJsonAsync(Items, new { type = "event", eventId = course, variants = Variants("Learn first aid with us") }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(course, created.GetProperty("eventId").GetGuid());
        await OkAsync(pia.PostAsJsonAsync($"{Items}/{id}/publish", new { }));

        var read = await OkAsync(t["mona"].Client.GetAsync($"{Items}/{id}/view"));
        var card = read.GetProperty("event");
        Assert.Equal(course, card.GetProperty("id").GetGuid());
        Assert.Equal("Training room, Dubai", card.GetProperty("location").GetString());
        Assert.Equal(2, card.GetProperty("registration").GetProperty("seatsLeft").GetInt32());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("registration").GetProperty("state").ValueKind);

        // Registering in the calendar shows on the card: the event exists once.
        var key = card.GetProperty("occurrenceKey").GetString();
        await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Calendar}/events/{course}/registration", new { occurrenceKey = key }));
        card = (await OkAsync(t["mona"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("event");
        Assert.Equal("registered", card.GetProperty("registration").GetProperty("state").GetString());
        Assert.Equal(1, card.GetProperty("registration").GetProperty("seatsLeft").GetInt32());
        Assert.Equal(1, (await OkAsync(t["karim"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("event").GetProperty("registration").GetProperty("seatsLeft").GetInt32());

        // Outside the event's audience: the text, no card.
        Assert.Equal(JsonValueKind.Null, (await OkAsync(t["nadia"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("event").ValueKind);

        // A cancelled event shows as cancelled.
        await OkAsync(pia.PostAsync($"{Calendar}/admin-events/{course}/cancel", null));
        Assert.Equal("cancelled", (await OkAsync(t["mona"].Client.GetAsync($"{Items}/{id}/view"))).GetProperty("event").GetProperty("status").GetString());

        // Another tenant neither reads the item nor links the event.
        var other = await world.CreateTenantAsync("cev-o-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(other, "olga", MembershipTypes.Employee);
        await world.AssignAsync(other, "olga", BuiltInRoles.ContentPublisher, "tenant", null);
        Assert.Equal(HttpStatusCode.NotFound, (await other["olga"].Client.GetAsync($"{Items}/{id}/view")).StatusCode);
        Assert.Equal("validation.content.event", await KeyAsync(other["olga"].Client.PostAsJsonAsync(Items, new { type = "event", eventId = course, variants = Variants("Theirs") })));
    }
}
