using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 slice 1 (T002): sites, floors, zones and resources with QR codes, the Microsoft 365 room refusal, rules on
/// the server (units, how far ahead, length, opening hours), search, the parallel booking race, recurring bookings that
/// skip holidays, the active booking limit, cancelling, the calendar file, removed resources and leavers, and the
/// package, tenant and scope boundaries. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private sealed record Org(TestTenant T, Guid Gulf, Guid Finance, Guid People, Guid Africa, Guid Site, Guid Floor, Guid Zone);

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
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    /// <summary>The first Monday at least two days after today in Riyadh, so every test day is ahead and a working day.</summary>
    private static DateOnly Monday()
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime).AddDays(2);
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }
        return day;
    }

    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);

    /// <summary>
    /// Holding over Gulf (Finance, People) and Africa. Omar works in Finance, Laila and Hana in People, Kofi in Africa.
    /// Hana is the facilities administrator for Gulf and runs the Riyadh office with one floor and one zone.
    /// </summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("bk-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", gulf, "department");
        var people = await world.CreateUnitAsync(t, "People", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("omar", "Finance"), ("laila", "People"), ("hana", "People"), ("kofi", "Africa"), ("sara", "Finance") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        if (package == "workplace")
        {
            return new Org(t, gulf, finance, people, africa, Guid.Empty, Guid.Empty, Guid.Empty);
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        var hana = t["hana"].Client;
        var site = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office", ar = "مكتب الرياض" }, orgUnitId = gulf, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        var floor = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors", new { names = new { en = "Floor 1", ar = "الطابق 1" }, sort = 1 }),
            HttpStatusCode.Created));
        var zone = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors/{floor}/zones", new { names = new { en = "East wing" } }), HttpStatusCode.Created));
        return new Org(t, gulf, finance, people, africa, site, floor, zone);
    }

    private static async Task<Guid> ResourceAsync(Org o, string kind, string name, int? capacity = null, string[]? attributes = null, Guid? fixedUser = null,
        string? mailbox = null, bool zone = true) =>
        Id(await OkAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = o.Site, floorId = o.Floor, zoneId = zone ? o.Zone : (Guid?)null, kind, names = new { en = name }, capacity, attributes = attributes ?? [],
            fixedUserId = fixedUser, mailbox,
        }), HttpStatusCode.Created));

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid resource, DateOnly date, TimeOnly start, TimeOnly end, object? repeat = null) =>
        client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = resource, date, start, end, title = "Planning", repeat });

    private async Task<int> AuditAsync(Org o, string action) => (int)await
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    private async Task<int> NotifiedAsync(Org o, string user, string template) => (int)await
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    [Fact]
    public async Task A_facilities_administrator_sets_up_a_site_with_resources_and_printable_codes_within_their_scope()
    {
        var o = await SetupAsync();
        var room = await ResourceAsync(o, "room", "Board room", 12, ["screen", "video"]);
        var desk = await ResourceAsync(o, "desk", "Desk 1.01");
        await ResourceAsync(o, "parking", "Bay 12", zone: false);
        await ResourceAsync(o, "equipment", "Projector 3", zone: false);
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingSiteChanged));
        Assert.Equal(4, await AuditAsync(o, AuditActions.BookingResourceCreated));

        var detail = await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/manage/sites/{o.Site}"));
        Assert.Equal(4, detail.GetProperty("resources").GetArrayLength());
        Assert.Equal("East wing", detail.GetProperty("floors")[0].GetProperty("zones")[0].GetProperty("names").GetProperty("en").GetString());
        var equipment = detail.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("kind").GetString() == "equipment");
        Assert.False(equipment.GetProperty("checkIn").GetBoolean());

        // US1 scenario 1: every resource has its own opaque code that leads to the check-in page.
        var labels = (await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/labels"))).EnumerateArray().ToList();
        Assert.Equal(4, labels.Count);
        Assert.Equal(4, labels.Select(l => l.GetProperty("code").GetString()).Distinct().Count());
        Assert.All(labels, l => Assert.Matches("^[0-9a-f]{32}$", l.GetProperty("code").GetString()!));
        Assert.All(labels, l => Assert.Equal($"/booking/scan/{l.GetProperty("code").GetString()}", l.GetProperty("link").GetString()));

        // Employees see the site for booking but not its management, codes or mailboxes.
        var laila = o.T["laila"].Client;
        Assert.Single((await OkAsync(laila.GetAsync($"{Api}/sites"))).EnumerateArray());
        Assert.Equal("authz.denied.booking_manage", await ProblemKeyAsync(laila.GetAsync($"{Api}/manage/sites/{o.Site}"), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await laila.PostAsJsonAsync($"{Api}/manage/sites", new { names = new { en = "Mine" } })).StatusCode);
        var rights = await OkAsync(laila.GetAsync($"{Api}/rights"));
        Assert.True(rights.GetProperty("book").GetBoolean());
        Assert.False(rights.GetProperty("manage").GetBoolean());

        // Hana manages Gulf only: a site for Africa or for the whole tenant is refused, and an Africa site is not found for her.
        var hana = o.T["hana"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await hana.PostAsJsonAsync($"{Api}/manage/sites", new { names = new { en = "Lagos" }, orgUnitId = o.Africa })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hana.PostAsJsonAsync($"{Api}/manage/sites", new { names = new { en = "Everywhere" } })).StatusCode);
        var lagos = Id(await OkAsync(o.T["admin"].Client.PostAsJsonAsync($"{Api}/manage/sites", new { names = new { en = "Lagos" }, orgUnitId = o.Africa, timeZone = "Africa/Lagos" }),
            HttpStatusCode.Created));
        Assert.Equal(HttpStatusCode.NotFound, (await hana.GetAsync($"{Api}/manage/sites/{lagos}")).StatusCode);
        Assert.DoesNotContain((await OkAsync(hana.GetAsync($"{Api}/manage/sites"))).EnumerateArray(), s => Id(s) == lagos);

        // Validation: a room needs a capacity; a zone must belong to the floor; only desks are assigned to a person.
        Assert.Equal("validation.booking.capacity", await ProblemKeyAsync(hana.PostAsJsonAsync($"{Api}/manage/resources",
            new { siteId = o.Site, kind = "room", names = new { en = "No size" } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.fixed_user", await ProblemKeyAsync(hana.PostAsJsonAsync($"{Api}/manage/resources",
            new { siteId = o.Site, kind = "parking", names = new { en = "Bay 1" }, fixedUserId = o.T["omar"].Id }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.time_zone", await ProblemKeyAsync(hana.PostAsJsonAsync($"{Api}/manage/sites",
            new { names = new { en = "Nowhere" }, orgUnitId = o.Gulf, timeZone = "Mars/Olympus" }), HttpStatusCode.BadRequest));

        // Cross-tenant: the site, the resource and its code are unknown elsewhere.
        var other = world.A["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Api}/manage/sites/{o.Site}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"{Api}/resources/{room}/availability?date={Monday():yyyy-MM-dd}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(other, desk, Monday(), Ten, Eleven)).StatusCode);
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(world.A.Id, "select count(*) from booking.resource where qr_code = @c",
            ("c", labels[0].GetProperty("code").GetString()!)));
    }

    [Fact]
    public async Task A_room_that_is_a_Microsoft_365_room_mailbox_is_refused()
    {
        var o = await SetupAsync();
        await world.Database.TenantScalarAsync<int>(o.T.Id,
            "insert into integrations.room_mailbox (tenant_id, address, display_name, refreshed_at) values (@t, 'board.riyadh@example.com', 'Board', now()) returning 1",
            ("t", o.T.Id));
        Assert.Equal("conflict.booking.room_mailbox", await ProblemKeyAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = o.Site, kind = "room", names = new { en = "Board room" }, capacity = 12, mailbox = "Board.Riyadh@example.com",
        }), HttpStatusCode.Conflict));
        // A room with an address that Outlook does not book is accepted, once.
        await ResourceAsync(o, "room", "Huddle", 4, mailbox: "huddle.riyadh@example.com");
        Assert.Equal("conflict.booking.mailbox_taken", await ProblemKeyAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = o.Site, kind = "room", names = new { en = "Copy" }, capacity = 4, mailbox = "huddle.riyadh@example.com",
        }), HttpStatusCode.Conflict));
        // The mailbox of another tenant has no effect here.
        Assert.Equal(0, await world.Database.TenantScalarAsync<long>(world.A.Id, "select count(*) from integrations.room_mailbox"));
    }

    [Fact]
    public async Task Rules_apply_on_the_server_for_units_how_far_ahead_length_and_opening_hours()
    {
        var o = await SetupAsync();
        var room = await ResourceAsync(o, "room", "Finance room", 8, ["screen"]);
        var hana = o.T["hana"].Client;
        // US1 scenario 3: Finance only, up to 14 days ahead, 30 minutes to 4 hours.
        var rule = await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/resources/{room}/rule", new { unitIds = new[] { o.Finance }, advanceDays = 14, minMinutes = 30, maxMinutes = 240 }));
        Assert.Equal("resource", rule.GetProperty("source").GetString());
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingRulesChanged));
        var mon = Monday();

        Assert.Equal("authz.denied.booking_units", await ProblemKeyAsync(BookAsync(o.T["laila"].Client, room, mon, Ten, Eleven), HttpStatusCode.Forbidden));
        Assert.Equal("validation.booking.too_far", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon.AddDays(21), Ten, Eleven), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.too_short", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon, Ten, new TimeOnly(10, 15)), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.too_long", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon, new TimeOnly(9, 0), new TimeOnly(14, 0)), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.outside_hours", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon, new TimeOnly(17, 0), new TimeOnly(19, 0)), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.closed_day", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon.AddDays(5), Ten, Eleven), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.slot", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon, new TimeOnly(10, 7), Eleven), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.overnight", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon, new TimeOnly(17, 0), new TimeOnly(9, 0)), HttpStatusCode.BadRequest));
        await OkAsync(BookAsync(o.T["omar"].Client, room, mon, Ten, Eleven), HttpStatusCode.Created);

        // Section 7: a rule on a parent unit includes its children; another subsidiary is refused.
        var desk = await ResourceAsync(o, "desk", "Gulf desk");
        await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/rules/desk?site={o.Site}", new { unitIds = new[] { o.Gulf } }));
        Assert.Equal("site", (await OkAsync(hana.GetAsync($"{Api}/manage/resources/{desk}/rule"))).GetProperty("source").GetString());
        await OkAsync(BookAsync(o.T["laila"].Client, desk, mon, Ten, Eleven), HttpStatusCode.Created);
        Assert.Equal("authz.denied.booking_units", await ProblemKeyAsync(BookAsync(o.T["kofi"].Client, desk, mon.AddDays(1), Ten, Eleven), HttpStatusCode.Forbidden));

        // A tenant-wide rule needs tenant-wide management; Hana manages Gulf only.
        Assert.Equal(HttpStatusCode.Forbidden, (await hana.PutAsJsonAsync($"{Api}/manage/rules/desk", new { maxActive = 5 })).StatusCode);
        Assert.Equal("tenant", (await OkAsync(o.T["admin"].Client.PutAsJsonAsync($"{Api}/manage/rules/parking", new { maxActive = 2 }))).GetProperty("source").GetString());
        Assert.Equal("validation.booking.range", await ProblemKeyAsync(o.T["admin"].Client.PutAsJsonAsync($"{Api}/manage/rules/parking", new { minMinutes = 600, maxMinutes = 60 }),
            HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Search_shows_only_free_resources_that_match_and_that_the_person_may_book()
    {
        var o = await SetupAsync();
        var big = await ResourceAsync(o, "room", "Majlis", 10, ["screen", "video"]);
        var screenless = await ResourceAsync(o, "room", "Quiet room", 8);
        var small = await ResourceAsync(o, "room", "Phone booth", 2, ["screen"]);
        var taken = await ResourceAsync(o, "room", "Oasis", 6, ["screen"]);
        var financeOnly = await ResourceAsync(o, "room", "Treasury", 6, ["screen"]);
        await ResourceAsync(o, "desk", "Omar's desk", fixedUser: o.T["omar"].Id);
        await OkAsync(o.T["hana"].Client.PutAsJsonAsync($"{Api}/manage/resources/{financeOnly}/rule", new { unitIds = new[] { o.Finance } }));
        var mon = Monday();
        await OkAsync(BookAsync(o.T["omar"].Client, taken, mon, new TimeOnly(9, 30), new TimeOnly(10, 30)), HttpStatusCode.Created);

        // US2 scenario 1: a room for 6 people tomorrow 10:00 to 11:00 with a screen.
        var laila = o.T["laila"].Client;
        var result = await OkAsync(laila.GetAsync($"{Api}/sites/{o.Site}/search?kind=room&date={mon:yyyy-MM-dd}&start=10:00&end=11:00&capacity=6&attributes=screen"));
        Assert.Equal("Asia/Riyadh", result.GetProperty("timeZone").GetString());
        var found = result.GetProperty("resources").EnumerateArray().Select(Id).ToList();
        Assert.Equal([big], found);
        Assert.DoesNotContain(screenless, found);
        Assert.DoesNotContain(small, found);

        // Omar may book the Finance room, and his own fixed desk shows only for him.
        var omars = (await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/sites/{o.Site}/search?date={mon:yyyy-MM-dd}&start=10:00&end=11:00&capacity=6&attributes=screen")))
            .GetProperty("resources").EnumerateArray().Select(Id).ToList();
        Assert.Contains(financeOnly, omars);
        var desks = (await OkAsync(laila.GetAsync($"{Api}/sites/{o.Site}/search?kind=desk&date={mon:yyyy-MM-dd}&start=10:00&end=11:00"))).GetProperty("resources");
        Assert.Equal(0, desks.GetArrayLength());
        Assert.Equal(1, (await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/sites/{o.Site}/search?kind=desk&date={mon:yyyy-MM-dd}&start=10:00&end=11:00")))
            .GetProperty("resources").GetArrayLength());

        // The day view says "booked" without the holder's name unless the tenant shows names.
        var day = await OkAsync(laila.GetAsync($"{Api}/resources/{taken}/availability?date={mon:yyyy-MM-dd}"));
        Assert.True(day.GetProperty("workingDay").GetBoolean());
        var slot = day.GetProperty("busy")[0];
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("holder").ValueKind);
        await OkAsync(o.T["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new { showNames = true }));
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingSettingsChanged));
        Assert.NotEqual(JsonValueKind.Null, (await OkAsync(laila.GetAsync($"{Api}/resources/{taken}/availability?date={mon:yyyy-MM-dd}")))
            .GetProperty("busy")[0].GetProperty("holder").ValueKind);
    }

    [Fact]
    public async Task Of_many_parallel_attempts_on_one_slot_exactly_one_succeeds()
    {
        var o = await SetupAsync();
        var room = await ResourceAsync(o, "room", "Majlis", 10);
        var mon = Monday();
        var clients = new[] { "omar", "laila", "hana", "sara" }.SelectMany(n => Enumerable.Repeat(o.T[n].Client, 5)).ToList();
        var responses = await Task.WhenAll(clients.Select(c => BookAsync(c, room, mon, Ten, Eleven)));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(clients.Count - 1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from booking.booking where resource_id = @r", ("r", room)));

        // An overlapping slot is taken too, and a buffer keeps the next booking apart.
        Assert.Equal("conflict.booking.taken", await ProblemKeyAsync(BookAsync(o.T["omar"].Client, room, mon, new TimeOnly(10, 30), new TimeOnly(11, 30)), HttpStatusCode.Conflict));
        await OkAsync(o.T["hana"].Client.PutAsJsonAsync($"{Api}/manage/resources/{room}/rule", new { bufferMinutes = 15 }));
        await OkAsync(BookAsync(o.T["omar"].Client, room, mon, Eleven, new TimeOnly(12, 0)), HttpStatusCode.Created);
        Assert.Equal("conflict.booking.taken", await ProblemKeyAsync(BookAsync(o.T["laila"].Client, room, mon, new TimeOnly(12, 0), new TimeOnly(13, 0)), HttpStatusCode.Conflict));
        await OkAsync(BookAsync(o.T["laila"].Client, room, mon, new TimeOnly(12, 15), new TimeOnly(13, 0)), HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_weekly_series_skips_a_holiday_and_counts_once_against_the_limit_of_active_bookings()
    {
        var o = await SetupAsync();
        var desks = new List<Guid>();
        for (var i = 1; i <= 4; i++)
        {
            desks.Add(await ResourceAsync(o, "desk", $"Desk 1.0{i}"));
        }
        var mon = Monday();
        var admin = o.T["admin"].Client;
        var calendar = (await OkAsync(admin.GetAsync("/api/v1/hr/calendars")))[0];
        using (var holidays = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{Id(calendar)}/holidays")
        {
            Content = JsonContent.Create(new { year = mon.AddDays(7).Year, holidays = new[] { new { date = mon.AddDays(7), name = new { en = "National day" }, halfDay = false } } }),
        })
        {
            holidays.Headers.TryAddWithoutValidation("If-Match", $"\"v{calendar.GetProperty("version").GetInt32()}\"");
            await OkAsync(admin.SendAsync(holidays));
        }

        // US2 scenario 3: a weekly booking over four weeks with a holiday in the second.
        var laila = o.T["laila"].Client;
        var series = await OkAsync(BookAsync(laila, desks[0], mon, Ten, Eleven, new { pattern = "weekly", until = mon.AddDays(21) }), HttpStatusCode.Created);
        Assert.Equal(3, series.GetProperty("bookings").GetArrayLength());
        var skipped = series.GetProperty("skipped")[0];
        Assert.Equal(mon.AddDays(7).ToString("yyyy-MM-dd"), skipped.GetProperty("date").GetString());
        Assert.Equal("holiday", skipped.GetProperty("reason").GetString());
        Assert.Equal(1, await NotifiedAsync(o, "laila", "booking.series_confirmed"));

        // US2 scenario 4: the series and two more desks make three; a fourth is refused with the limit shown.
        await OkAsync(BookAsync(laila, desks[1], mon.AddDays(1), Ten, Eleven), HttpStatusCode.Created);
        await OkAsync(BookAsync(laila, desks[2], mon.AddDays(2), Ten, Eleven), HttpStatusCode.Created);
        var refused = await BookAsync(laila, desks[3], mon.AddDays(3), Ten, Eleven);
        var problem = await OkAsync(Task.FromResult(refused), HttpStatusCode.Conflict);
        Assert.Equal("conflict.booking.limit", problem.GetProperty("detailKey").GetString());
        Assert.Equal(3, problem.GetProperty("limit").GetInt32());

        // A series names every taken date before anything is booked, and runs no longer than the rules allow.
        var taken = await OkAsync(BookAsync(o.T["omar"].Client, desks[3], mon.AddDays(3), new TimeOnly(9, 0), new TimeOnly(12, 0)), HttpStatusCode.Created);
        Assert.NotEqual(Guid.Empty, Id(taken.GetProperty("bookings")[0]));
        var clash = await OkAsync(BookAsync(o.T["sara"].Client, desks[3], mon, new TimeOnly(13, 0), new TimeOnly(14, 0),
            new { pattern = "weekly", until = mon.AddDays(200) }), HttpStatusCode.BadRequest);
        Assert.Equal("validation.booking.repeat_until", clash.GetProperty("errors").EnumerateObject().First().Value[0].GetProperty("key").GetString());
        var dates = await OkAsync(BookAsync(o.T["sara"].Client, desks[3], mon.AddDays(1), new TimeOnly(9, 0), new TimeOnly(12, 0),
            new { pattern = "daily", until = mon.AddDays(14) }), HttpStatusCode.Conflict);
        Assert.Equal([mon.AddDays(3).ToString("yyyy-MM-dd")], dates.GetProperty("dates").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(o.T.Id, "select count(*) from booking.booking where holder_id = @u", ("u", o.T["sara"].Id)));
    }

    [Fact]
    public async Task People_cancel_their_own_bookings_and_administrators_cancel_with_a_reason_and_get_a_calendar_file()
    {
        var o = await SetupAsync();
        var room = await ResourceAsync(o, "room", "Majlis", 10);
        var mon = Monday();
        var omar = o.T["omar"].Client;
        var booked = await OkAsync(BookAsync(omar, room, mon, Ten, Eleven), HttpStatusCode.Created);
        var id = Id(booked.GetProperty("bookings")[0]);
        var view = booked.GetProperty("bookings")[0];
        Assert.Equal("confirmed", view.GetProperty("status").GetString());
        Assert.Equal("10:00:00", view.GetProperty("localStart").GetString());
        Assert.Equal(new DateTimeOffset(mon.ToDateTime(Ten), TimeSpan.FromHours(3)), view.GetProperty("startsAt").GetDateTimeOffset());
        Assert.Equal(1, await NotifiedAsync(o, "omar", "booking.confirmed"));

        // US2 scenario 2: the confirmation comes with a calendar file.
        var ics = await omar.GetAsync($"{Api}/bookings/{id}/calendar.ics");
        Assert.Equal(HttpStatusCode.OK, ics.StatusCode);
        Assert.Equal("text/calendar", ics.Content.Headers.ContentType!.MediaType);
        var text = await ics.Content.ReadAsStringAsync();
        Assert.Contains($"UID:booking-{id:N}@teambeats", text);
        Assert.Contains($"DTSTART:{mon:yyyyMMdd}T070000Z", text);
        Assert.Contains("SUMMARY:Planning", text);

        // Others do not see it; Hana administers the site and sees it.
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["laila"].Client.GetAsync($"{Api}/bookings/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["laila"].Client.PostAsJsonAsync($"{Api}/bookings/{id}:cancel", new { })).StatusCode);
        await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/bookings/{id}"));

        // An administrator gives a reason, which is audited and told to the holder.
        Assert.Equal("validation.booking.cancel_reason", await ProblemKeyAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/bookings/{id}:cancel", new { }), HttpStatusCode.BadRequest));
        var cancelled = await OkAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/bookings/{id}:cancel", new { reason = "The room is being painted." }));
        Assert.Equal("cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingCancelledByAdmin));
        Assert.Equal(1, await NotifiedAsync(o, "omar", "booking.cancelled_by_admin"));
        Assert.Equal("conflict.booking.not_live", await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/bookings/{id}:cancel", new { }), HttpStatusCode.Conflict));

        // The slot is free again; the holder cancels their own without a reason, and My bookings lists both.
        var again = Id((await OkAsync(BookAsync(omar, room, mon, Ten, Eleven), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        Assert.Equal("cancelled", (await OkAsync(omar.PostAsJsonAsync($"{Api}/bookings/{again}:cancel", new { }))).GetProperty("status").GetString());
        var mine = (await OkAsync(omar.GetAsync($"{Api}/bookings/mine"))).EnumerateArray().ToList();
        Assert.Equal(2, mine.Count);
        Assert.All(mine, b => Assert.False(b.GetProperty("canCancel").GetBoolean()));
    }

    [Fact]
    public async Task Removing_a_resource_or_a_person_leaving_cancels_their_future_bookings()
    {
        var o = await SetupAsync();
        var desk = await ResourceAsync(o, "desk", "Desk 2.01");
        var room = await ResourceAsync(o, "room", "Majlis", 10);
        var mon = Monday();
        await OkAsync(BookAsync(o.T["laila"].Client, desk, mon, Ten, Eleven), HttpStatusCode.Created);
        await OkAsync(BookAsync(o.T["omar"].Client, room, mon, Ten, Eleven), HttpStatusCode.Created);

        // FR-015: holders of the removed desk are told and pointed to the same zone.
        await OkAsync(o.T["hana"].Client.DeleteAsync($"{Api}/manage/resources/{desk}"), HttpStatusCode.NoContent);
        Assert.Equal(1, await AuditAsync(o, AuditActions.BookingResourceRemoved));
        Assert.Equal(1, await NotifiedAsync(o, "laila", "booking.removed"));
        var link = await world.Database.TenantScalarAsync<string>(o.T.Id, "select link from notifications.notification where template_key = 'booking.removed'");
        Assert.Contains($"zone={o.Zone}", link);
        Assert.Equal(HttpStatusCode.NotFound, (await BookAsync(o.T["laila"].Client, desk, mon.AddDays(1), Ten, Eleven)).StatusCode);

        // FR-016: Omar leaves; his booking ends without a notice to him.
        await OkAsync(o.T["admin"].Client.PostAsync($"/api/v1/admin/users/{o.T["omar"].Id}:disable", null), HttpStatusCode.NoContent);
        await world.DispatchTenantAsync(o.T.Id);
        Assert.Equal("left", await world.Database.TenantScalarAsync<string>(o.T.Id, "select end_reason from booking.booking where holder_id = @u", ("u", o.T["omar"].Id)));
        Assert.Equal(0, await NotifiedAsync(o, "omar", "booking.removed"));
        await OkAsync(BookAsync(o.T["laila"].Client, room, mon, Ten, Eleven), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Workplace_has_no_booking()
    {
        var o = await SetupAsync("workplace");
        var rights = await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/rights"));
        Assert.False(rights.GetProperty("book").GetBoolean());
        Assert.Equal(0, rights.GetProperty("kinds").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync($"{Api}/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["admin"].Client.PostAsJsonAsync($"{Api}/manage/sites", new { names = new { en = "Office" } })).StatusCode);
    }
}
