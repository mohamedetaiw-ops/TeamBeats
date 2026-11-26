using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 037 R3 US11: published shifts and roster days off in the My shifts layer, drafts never shown, the layer only for
/// people with a roster and only with <c>hr.rosters</c>; team availability takes working days from the published roster;
/// provisional and observed holidays from spec 022 are marked. All names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CalendarShiftTests(TenantWorld world)
{
    private const string Api = "/api/v1/calendar";
    private const string Patterns = "/api/v1/hr/shift-patterns";
    private const string Weeks = "/api/v1/hr/rosters";

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Kofi manages Dana and Eli in Gulf; Sam works elsewhere with no roster.</summary>
    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("cals-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "kofi", "dana", "eli", "sam" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var person in new[] { "dana", "eli" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[person].Id}/manager", new { managerId = t["kofi"].Id })).StatusCode);
        }
        return t;
    }

    /// <summary>A Sunday at least two weeks ahead, so the week has the tenant default's weekend (Saturday and Sunday) at both ends.</summary>
    private static DateOnly Sunday()
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        while (d.DayOfWeek != DayOfWeek.Sunday)
        {
            d = d.AddDays(1);
        }
        while (d.Year != d.AddDays(7).Year)
        {
            d = d.AddDays(7);
        }
        return d;
    }

    private static object Entry(TestTenant t, string user, DateOnly date, Guid? pattern) => new { userId = t[user].Id, date = D(date), patternId = pattern, dayOff = pattern is null };

    [Fact]
    public async Task Published_shifts_and_days_off_show_in_my_shifts_and_decide_working_days_in_team_availability()
    {
        var t = await FreshAsync();
        var night = (await OkAsync(t["admin"].Client.PostAsJsonAsync(Patterns,
            new { names = new Dictionary<string, string> { ["en"] = "Night shift", ["ar"] = "الوردية الليلية" }, start = "22:00", end = "23:30", breakMinutes = 0, tone = "info" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var sunday = Sunday();
        var monday = sunday.AddDays(1);

        // Dana works the Sunday (a weekend day of the default calendar) and has the Monday off. Eli has nothing planned.
        await OkAsync(t["kofi"].Client.PutAsJsonAsync($"{Weeks}/{D(sunday)}", new { entries = new[] { Entry(t, "dana", sunday, night), Entry(t, "dana", monday, null) } }));
        // A draft is never shown, and the layer is not offered until a roster is published.
        var prefs = await OkAsync(t["dana"].Client.GetAsync($"{Api}/preferences"));
        Assert.DoesNotContain("my_shifts", prefs.GetProperty("available").EnumerateArray().Select(l => l.GetString()));
        await OkAsync(t["kofi"].Client.PostAsync($"{Weeks}/{D(sunday)}:publish", null));

        prefs = await OkAsync(t["dana"].Client.GetAsync($"{Api}/preferences"));
        Assert.Contains("my_shifts", prefs.GetProperty("available").EnumerateArray().Select(l => l.GetString()));
        Assert.DoesNotContain("my_shifts", (await OkAsync(t["sam"].Client.GetAsync($"{Api}/preferences"))).GetProperty("available").EnumerateArray().Select(l => l.GetString()));

        var items = (await OkAsync(t["dana"].Client.GetAsync($"{Api}/events?from={D(sunday)}&to={D(sunday.AddDays(7))}&layers=my_shifts")))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var shift = items.Single(i => i.GetProperty("typeKey").GetString() == "shift");
        Assert.Equal("Night shift", shift.GetProperty("title").GetString());
        Assert.False(shift.GetProperty("allDay").GetBoolean());
        var off = items.Single(i => i.GetProperty("typeKey").GetString() == "day_off");
        Assert.True(off.GetProperty("allDay").GetBoolean());
        Assert.Equal(D(monday), off.GetProperty("startDate").GetString());

        // Kofi's team: Sunday counts Dana as working (1 of 1), Monday counts only Eli.
        var team = await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/calendar/team-availability?from={D(sunday)}&to={D(sunday.AddDays(7))}"));
        var days = team.GetProperty("days").EnumerateArray().ToDictionary(d => d.GetProperty("date").GetString()!);
        Assert.True(days[D(sunday)].GetProperty("working").GetBoolean());
        Assert.Equal(1, days[D(sunday)].GetProperty("total").GetInt32());
        Assert.Equal(1, days[D(monday)].GetProperty("total").GetInt32());
        Assert.Equal(2, days[D(monday.AddDays(1))].GetProperty("total").GetInt32());

        // Eli cannot read Dana's shifts through the feed: only the viewer and their reports.
        Assert.Empty((await OkAsync(t["eli"].Client.GetAsync($"{Api}/events?from={D(sunday)}&to={D(sunday.AddDays(7))}&layers=my_shifts")))
            .GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Without_rosters_in_the_plan_there_is_no_shifts_layer()
    {
        var t = await FreshAsync("workplace");
        var prefs = await OkAsync(t["dana"].Client.GetAsync($"{Api}/preferences"));
        Assert.DoesNotContain("my_shifts", prefs.GetProperty("available").EnumerateArray().Select(l => l.GetString()));
    }

    [Fact]
    public async Task Provisional_and_observed_holidays_are_marked_and_kept_apart()
    {
        var t = await FreshAsync();
        var sunday = Sunday();
        var monday = sunday.AddDays(1);
        var calendar = (await OkAsync(t["admin"].Client.GetAsync("/api/v1/hr/calendars"))).EnumerateArray().Single(c => c.GetProperty("isTenantDefault").GetBoolean());
        HttpRequestMessage Put(object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{calendar.GetProperty("id").GetGuid()}/holidays") { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("If-Match", $"\"v{calendar.GetProperty("version").GetInt32()}\"");
            return request;
        }

        var bad = await t["admin"].Client.SendAsync(Put(new
        {
            year = monday.Year,
            holidays = new object[] { new { date = monday, name = new { en = "National Day" }, halfDay = false, observedFor = monday } },
        }));
        Assert.Contains("validation.calendar.holiday_observed", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        await OkAsync(t["admin"].Client.SendAsync(Put(new
        {
            year = monday.Year,
            holidays = new object[]
            {
                new { date = monday, name = new { en = "National Day", ar = "اليوم الوطني" }, halfDay = false, observedFor = sunday.AddDays(-1) },
                new { date = monday.AddDays(2), name = new { en = "Eid al-Adha", ar = "عيد الأضحى" }, halfDay = false, provisional = true },
                new { date = monday.AddDays(3), name = new { en = "Eid al-Adha", ar = "عيد الأضحى" }, halfDay = false, provisional = true },
            },
        })));

        var holidays = await OkAsync(t["dana"].Client.GetAsync($"{Api}/holidays?from={D(monday)}&to={D(monday.AddDays(7))}"));
        Assert.Equal(2, holidays.GetArrayLength());
        Assert.Equal(D(sunday.AddDays(-1)), holidays[0].GetProperty("observedFor").GetString());
        Assert.False(holidays[0].GetProperty("provisional").GetBoolean());
        Assert.True(holidays[1].GetProperty("provisional").GetBoolean());
        Assert.Equal(D(monday.AddDays(4)), holidays[1].GetProperty("endDate").GetString());
        var items = (await OkAsync(t["dana"].Client.GetAsync($"{Api}/events?from={D(monday)}&to={D(monday.AddDays(7))}&layers=holiday")))
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("tentative", items[1].GetProperty("status").GetString());
        Assert.True(items[1].GetProperty("provisional").GetBoolean());
        // Both still count as holidays for working days.
        var team = await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/calendar/team-availability?from={D(monday)}&to={D(monday.AddDays(5))}"));
        Assert.All(team.GetProperty("days").EnumerateArray().Where(d => d.GetProperty("date").GetString() != D(monday.AddDays(1)) && d.GetProperty("date").GetString() != D(monday.AddDays(4))),
            d => Assert.False(d.GetProperty("working").GetBoolean()));
    }
}
