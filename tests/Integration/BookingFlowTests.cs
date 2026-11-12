using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Booking;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 slice 2 (T005 to T007): restricted resources held as tentative on an approval route in the task inbox,
/// approval, rejection with a reason and expiry of the hold; check-in by QR, from the booking and at a kiosk within the
/// window; QR images; release of bookings nobody checked in to; the no-show rule; resources with check-in off; and the
/// tenant boundary. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingFlowTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private sealed record Org(TestTenant T, Guid Gulf, Guid Site);

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

    private static DateOnly Monday()
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime).AddDays(2);
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }
        return day;
    }

    /// <summary>The next quarter hour in Riyadh from now, as a local date and time, so a booking there has its check-in window open.</summary>
    private static (DateOnly Date, TimeOnly Start) NextSlot()
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime;
        var next = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute / 15 * 15, 0).AddMinutes(15);
        return (DateOnly.FromDateTime(next), TimeOnly.FromDateTime(next));
    }

    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);

    /// <summary>Omar and Sara in Finance, Laila and Hana in People, all under Gulf. Hana runs the Riyadh office.</summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("bf-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", gulf, "department");
        await world.CreateUnitAsync(t, "People", gulf, "department");
        foreach (var (name, unit) in new[] { ("omar", "Finance"), ("sara", "Finance"), ("laila", "People"), ("hana", "People") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        if (package == "workplace")
        {
            return new Org(t, gulf, Guid.Empty);
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        var site = Id(await OkAsync(t["hana"].Client.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office" }, orgUnitId = gulf, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        return new Org(t, gulf, site);
    }

    private static async Task<Guid> ResourceAsync(Org o, string kind, string name, int? capacity = null, bool? checkIn = null, bool needsApproval = false,
        Guid[]? approvers = null) =>
        Id(await OkAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = o.Site, kind, names = new { en = name }, capacity, attributes = Array.Empty<string>(), checkIn, needsApproval, approverIds = approvers ?? [],
        }), HttpStatusCode.Created));

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid resource, DateOnly date, TimeOnly start, TimeOnly end) =>
        client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = resource, date, start, end, title = "Review" });

    private async Task<int> NotifiedAsync(Org o, string user, string template) => (int)await
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    private static async Task<List<JsonElement>> InboxAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(t => t.GetProperty("source").GetString() == "booking").ToList();

    private Task<BookingMaintenance.Run> MaintainAsync(Org o, DateTimeOffset now) =>
        world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            var run = await sp.GetRequiredService<BookingMaintenance>().RunDueAsync(now, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    [Fact]
    public async Task A_restricted_room_is_held_until_an_approver_in_the_inbox_approves_or_rejects_it_with_a_reason()
    {
        var o = await SetupAsync();
        var board = await ResourceAsync(o, "room", "Board room", 12, needsApproval: true, approvers: [o.T["sara"].Id]);
        var mon = Monday();
        var omar = o.T["omar"].Client;

        // US3 scenario 1: the slot is held as tentative until the approval hold runs out, and nobody else can take it.
        var view = (await OkAsync(BookAsync(omar, board, mon, Ten, Eleven), HttpStatusCode.Created)).GetProperty("bookings")[0];
        var id = Id(view);
        Assert.Equal("tentative", view.GetProperty("status").GetString());
        var hold = view.GetProperty("holdUntil").GetDateTimeOffset();
        Assert.InRange(hold, DateTimeOffset.UtcNow.AddHours(23), DateTimeOffset.UtcNow.AddHours(25));
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingApprovals.RequestedTemplate));
        Assert.Equal("conflict.booking.taken", await ProblemKeyAsync(BookAsync(o.T["laila"].Client, board, mon, Ten, Eleven), HttpStatusCode.Conflict));

        // The approval goes to the inbox of the named approver and of the site's facilities administrator, never the requester's.
        await world.DispatchTenantAsync(o.T.Id);
        Assert.Equal(1, await NotifiedAsync(o, "sara", BookingApprovals.WaitingTemplate));
        Assert.Empty(await InboxAsync(omar));
        Assert.Empty(await InboxAsync(o.T["laila"].Client));
        Assert.Single(await InboxAsync(o.T["hana"].Client));
        var task = Assert.Single(await InboxAsync(o.T["sara"].Client));
        Assert.Contains("Board room", task.GetProperty("title").GetProperty("en").GetString());
        Assert.StartsWith($"/booking/bookings/{id}", task.GetProperty("link").GetString());

        // US3 scenario 2: approved, the booking is confirmed and Omar is told.
        await OkAsync(o.T["sara"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        var confirmed = await OkAsync(omar.GetAsync($"{Api}/bookings/{id}"));
        Assert.Equal("confirmed", confirmed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, confirmed.GetProperty("holdUntil").ValueKind);
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingApprovals.ApprovedTemplate));
        Assert.Empty(await InboxAsync(o.T["sara"].Client));

        // Rejected by the facilities administrator: the slot frees and Omar sees the reason.
        var second = Id((await OkAsync(BookAsync(omar, board, mon.AddDays(1), Ten, Eleven), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        var hanaTask = Assert.Single(await InboxAsync(o.T["hana"].Client));
        await OkAsync(o.T["hana"].Client.PostAsJsonAsync($"/api/v1/tasks/{hanaTask.GetProperty("taskId").GetGuid()}/decision",
            new { outcome = "reject", comment = "The board meets that day." }));
        var rejected = await OkAsync(omar.GetAsync($"{Api}/bookings/{second}"));
        Assert.Equal("rejected", rejected.GetProperty("status").GetString());
        Assert.Equal("The board meets that day.", rejected.GetProperty("decisionNote").GetString());
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingApprovals.RejectedTemplate));
        await OkAsync(BookAsync(o.T["laila"].Client, board, mon.AddDays(1), Ten, Eleven), HttpStatusCode.Created);

        // Omar withdraws a request by cancelling it; it leaves the approvers' inbox.
        var third = Id((await OkAsync(BookAsync(omar, board, mon.AddDays(2), Ten, Eleven), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        Assert.Equal(2, (await InboxAsync(o.T["hana"].Client)).Count);
        Assert.Equal("cancelled", (await OkAsync(omar.PostAsJsonAsync($"{Api}/bookings/{third}:cancel", new { }))).GetProperty("status").GetString());
        Assert.Single(await InboxAsync(o.T["hana"].Client));
        Assert.Equal("withdrawn", await world.Database.TenantScalarAsync<string>(o.T.Id,
            "select outcome from workflow.process_instance where business_key = @k", ("k", third.ToString("N"))));
    }

    [Fact]
    public async Task A_hold_nobody_decides_in_time_expires_and_frees_the_slot()
    {
        var o = await SetupAsync();
        var board = await ResourceAsync(o, "room", "Board room", 12, needsApproval: true);
        var mon = Monday();
        var id = Id((await OkAsync(BookAsync(o.T["omar"].Client, board, mon, Ten, Eleven), HttpStatusCode.Created)).GetProperty("bookings")[0]);

        Assert.Equal(0, (await MaintainAsync(o, DateTimeOffset.UtcNow.AddHours(1))).Expired);
        Assert.Equal(1, (await MaintainAsync(o, DateTimeOffset.UtcNow.AddHours(25))).Expired);
        var view = await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/bookings/{id}"));
        Assert.Equal("expired", view.GetProperty("status").GetString());
        Assert.False(view.GetProperty("canCancel").GetBoolean());
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingApprovals.ExpiredTemplate));
        Assert.Empty(await InboxAsync(o.T["hana"].Client));
        await OkAsync(BookAsync(o.T["laila"].Client, board, mon, Ten, Eleven), HttpStatusCode.Created);
    }

    [Fact]
    public async Task People_check_in_by_QR_from_the_booking_or_at_a_kiosk_only_within_the_window()
    {
        var o = await SetupAsync();
        // Equipment runs overnight and outside opening hours, so a booking from the next quarter hour is valid at any time of day.
        var camera = await ResourceAsync(o, "equipment", "Camera kit", checkIn: true);
        var (date, start) = NextSlot();
        var omar = o.T["omar"].Client;
        var view = (await OkAsync(BookAsync(omar, camera, date, start, start.AddMinutes(60)), HttpStatusCode.Created)).GetProperty("bookings")[0];
        var id = Id(view);
        Assert.True(view.GetProperty("canCheckIn").GetBoolean());

        // The printed label: an SVG image for facilities administrators only, carrying the check-in address on the tenant's host.
        var label = (await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/labels"))).EnumerateArray().Single();
        var code = label.GetProperty("code").GetString()!;
        var svg = await o.T["hana"].Client.GetAsync($"{Api}/manage/resources/{camera}/qr.svg");
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType!.MediaType);
        Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await omar.GetAsync($"{Api}/manage/resources/{camera}/qr.svg")).StatusCode);

        // Someone else's scan finds the resource but no booking of theirs; another tenant's scan finds nothing.
        var laila = await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/scan/{code}"));
        Assert.Equal(JsonValueKind.Null, laila.GetProperty("booking").ValueKind);
        Assert.Equal("conflict.booking.no_booking_now", await ProblemKeyAsync(o.T["laila"].Client.PostAsync($"{Api}/scan/{code}:check-in", null), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["laila"].Client.PostAsync($"{Api}/bookings/{id}:check-in", null)).StatusCode);
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["omar"].Client.GetAsync($"{Api}/scan/{code}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["omar"].Client.GetAsync($"{Api}/scan/not-a-code")).StatusCode);

        // US4 scenario 1: Omar scans the code in the window and is checked in; scanning again changes nothing.
        var scan = await OkAsync(omar.GetAsync($"{Api}/scan/{code}"));
        Assert.Equal(id, Id(scan.GetProperty("booking")));
        var checkedIn = await OkAsync(omar.PostAsync($"{Api}/scan/{code}:check-in", null));
        Assert.Equal("checked_in", checkedIn.GetProperty("status").GetString());
        Assert.Equal("qr", checkedIn.GetProperty("checkInMethod").GetString());
        Assert.Equal("checked_in", (await OkAsync(omar.PostAsync($"{Api}/scan/{code}:check-in", null))).GetProperty("status").GetString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync("CheckedIn", o.T.Id));

        // At a kiosk: the person's bookings at the site today, and check-in from there.
        var tripod = await ResourceAsync(o, "equipment", "Tripod", checkIn: true);
        var kioskId = Id((await OkAsync(BookAsync(omar, tripod, date, start, start.AddMinutes(30)), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        var kiosk = await OkAsync(omar.GetAsync($"{Api}/sites/{o.Site}/kiosk"));
        Assert.Equal(2, kiosk.GetProperty("bookings").GetArrayLength());
        Assert.Empty((await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/sites/{o.Site}/kiosk"))).GetProperty("bookings").EnumerateArray());
        Assert.Equal("kiosk", (await OkAsync(omar.PostAsync($"{Api}/bookings/{kioskId}:check-in?via=kiosk", null))).GetProperty("checkInMethod").GetString());
        Assert.Equal("validation.booking.check_in_method", await ProblemKeyAsync(omar.PostAsync($"{Api}/bookings/{kioskId}:check-in?via=qr", null), HttpStatusCode.BadRequest));

        // Too early: the window of a booking days ahead has not opened. Check-in off: nothing to check in to.
        var lailaClient = o.T["laila"].Client;
        var later = Id((await OkAsync(BookAsync(lailaClient, camera, Monday(), Ten, Eleven), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        Assert.Equal("conflict.booking.check_in_early", await ProblemKeyAsync(lailaClient.PostAsync($"{Api}/bookings/{later}:check-in", null), HttpStatusCode.Conflict));
        var projector = await ResourceAsync(o, "equipment", "Projector");
        var off = (await OkAsync(BookAsync(lailaClient, projector, date, start, start.AddMinutes(30)), HttpStatusCode.Created)).GetProperty("bookings")[0];
        Assert.False(off.GetProperty("canCheckIn").GetBoolean());
        Assert.Equal("conflict.booking.check_in_off", await ProblemKeyAsync(lailaClient.PostAsync($"{Api}/bookings/{Id(off)}:check-in", null), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task Bookings_nobody_checks_in_to_are_released_and_the_no_show_rule_restricts_how_far_ahead()
    {
        var o = await SetupAsync();
        await OkAsync(o.T["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new { noShowLimit = 2, noShowRestrict = true, noShowRestrictDays = 14, noShowAdvanceDays = 1 }));
        var camera = await ResourceAsync(o, "equipment", "Camera kit", checkIn: true);
        var projector = await ResourceAsync(o, "equipment", "Projector");
        var (date, start) = NextSlot();
        var omar = o.T["omar"].Client;
        var first = Id((await OkAsync(BookAsync(omar, camera, date, start, start.AddMinutes(60)), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        var nextStart = start.AddMinutes(60);
        var nextDate = nextStart < start ? date.AddDays(1) : date;
        var second = Id((await OkAsync(BookAsync(omar, camera, nextDate, nextStart, nextStart.AddMinutes(60)), HttpStatusCode.Created)).GetProperty("bookings")[0]);
        var kept = Id((await OkAsync(BookAsync(omar, projector, date, start, start.AddMinutes(60)), HttpStatusCode.Created)).GetProperty("bookings")[0]);

        // Check-in opens: one reminder for the booking whose window is open, never twice.
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(1, (await MaintainAsync(o, now)).Reminded);
        Assert.Equal(0, (await MaintainAsync(o, now.AddMinutes(1))).Reminded);
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingMaintenance.CheckInOpenTemplate));

        // US4 scenario 2: no check-in by the end of the grace period, released and Omar is told; scenario 4: check-in off is never released.
        var run = await MaintainAsync(o, now.AddHours(3));
        Assert.Equal(2, run.Released);
        Assert.Equal(1, run.Noticed);
        foreach (var id in new[] { first, second })
        {
            var view = await OkAsync(omar.GetAsync($"{Api}/bookings/{id}"));
            Assert.Equal("released", view.GetProperty("status").GetString());
            Assert.Equal("no_show", view.GetProperty("endReason").GetString());
        }
        Assert.Equal("confirmed", (await OkAsync(omar.GetAsync($"{Api}/bookings/{kept}"))).GetProperty("status").GetString());
        Assert.Equal(2, await NotifiedAsync(o, "omar", BookingMaintenance.ReleasedTemplate));
        Assert.Equal(2L, await world.Database.OutboxCountAsync("BookingReleased", o.T.Id));
        await OkAsync(BookAsync(o.T["laila"].Client, camera, date, start, start.AddMinutes(60)), HttpStatusCode.Created);

        // US4 scenario 3: reminded of the rule once, and for 14 days Omar books at most a day ahead; Laila is not restricted.
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingMaintenance.NoShowRestrictedTemplate));
        Assert.Equal(0, (await MaintainAsync(o, now.AddHours(4))).Noticed);
        Assert.Equal("validation.booking.too_far", await ProblemKeyAsync(BookAsync(omar, camera, Monday(), Ten, Eleven), HttpStatusCode.BadRequest));
        Assert.Empty((await OkAsync(omar.GetAsync($"{Api}/sites/{o.Site}/search?kind=equipment&date={Monday():yyyy-MM-dd}&start=10:00&end=11:00")))
            .GetProperty("resources").EnumerateArray());
        await OkAsync(BookAsync(o.T["laila"].Client, camera, Monday(), Ten, Eleven), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Workplace_has_no_check_in()
    {
        var o = await SetupAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync($"{Api}/scan/{new string('a', 32)}")).StatusCode);
    }
}
