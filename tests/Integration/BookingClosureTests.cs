using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Booking;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 slice 5: a site closed for a day by its administrator or by a new holiday on its calendar (section 4 edge
/// case, section 12) cancels that day's bookings with notice and refuses new ones; bookings that ended over a year ago
/// keep counts only (section 8); and <c>booking.activity</c> counts what happens with no person (section 13). Names are
/// fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingClosureTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);
    private const string Nobody = "00000000-0000-0000-0000-000000000000";

    private sealed record Org(TestTenant T, Guid Site, Guid Desk, Guid Camera);

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

    /// <summary>Omar and Laila in Finance; Hana runs the Riyadh office with a desk and a camera kit (equipment keeps no working hours).</summary>
    private async Task<Org> SetupAsync()
    {
        var t = await world.CreateTenantAsync("bc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Finance", holding, "department");
        foreach (var name in new[] { "omar", "laila", "hana" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Finance");
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", holding);
        var hana = t["hana"].Client;
        var site = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office" }, orgUnitId = holding, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        async Task<Guid> Resource(string kind, string name) => Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = site, kind, names = new { en = name }, attributes = Array.Empty<string>(),
        }), HttpStatusCode.Created));
        return new Org(t, site, await Resource("desk", "Desk 1.01"), await Resource("equipment", "Camera kit"));
    }

    private static Task<HttpResponseMessage> BookAsync(HttpClient client, Guid resource, DateOnly date, object? repeat = null) =>
        client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = resource, date, start = Ten, end = Eleven, title = "Review", repeat });

    private async Task<Guid> BookedAsync(HttpClient client, Guid resource, DateOnly date) =>
        Id((await OkAsync(BookAsync(client, resource, date), HttpStatusCode.Created)).GetProperty("bookings")[0]);

    private async Task<string> StatusAsync(Org o, Guid booking) =>
        (await world.Database.ScalarInTenantAsync<string>(o.T.Id, "select status || ':' || coalesce(end_reason, '') from booking.booking where id = @id", ("id", booking)))!;

    private async Task<int> NotifiedAsync(Org o, string user, string template) => (int)await
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    private Task<long> AuditedAsync(Org o, string action) =>
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    [Fact]
    public async Task An_administrator_closes_a_site_for_a_day_which_cancels_its_bookings_with_the_reason_and_refuses_new_ones_until_it_opens_again()
    {
        var o = await SetupAsync();
        var mon = Monday();
        var omar = o.T["omar"].Client;
        var hana = o.T["hana"].Client;
        var desk = await BookedAsync(omar, o.Desk, mon);
        var tuesday = await BookedAsync(omar, o.Desk, mon.AddDays(1));
        var closures = $"{Api}/manage/sites/{o.Site}/closures";

        var closed = await OkAsync(hana.PostAsJsonAsync(closures, new { date = mon, reason = "Water leak on the first floor" }), HttpStatusCode.Created);
        Assert.Equal(1, closed.GetProperty("cancelled").GetInt32());
        Assert.Equal("cancelled:site_closed", await StatusAsync(o, desk));
        Assert.Equal("confirmed:", await StatusAsync(o, tuesday));
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingClosures.ClosedTemplate));
        Assert.Equal("Water leak on the first floor", await world.Database.ScalarInTenantAsync<string>(o.T.Id,
            "select parameters->>'reason' from notifications.notification where user_id = @u and template_key = @k", ("u", o.T["omar"].Id), ("k", BookingClosures.ClosedTemplate)));
        Assert.Equal(1, await AuditedAsync(o, AuditActions.BookingSiteClosed));
        Assert.Equal(mon.ToString("yyyy-MM-dd"), Assert.Single((await OkAsync(hana.GetAsync(closures))).EnumerateArray()).GetProperty("date").GetString());

        // Nobody books the site that day, not even what keeps no working hours; search finds nothing and the day shows closed.
        Assert.Equal("validation.booking.site_closed", await ProblemKeyAsync(BookAsync(o.T["laila"].Client, o.Desk, mon), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.site_closed", await ProblemKeyAsync(BookAsync(o.T["laila"].Client, o.Camera, mon), HttpStatusCode.BadRequest));
        var search = await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/sites/{o.Site}/search?kind=desk&date={mon:yyyy-MM-dd}&start=10:00&end=11:00"));
        Assert.Empty(search.GetProperty("resources").EnumerateArray());
        Assert.False((await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/resources/{o.Desk}/availability?date={mon:yyyy-MM-dd}"))).GetProperty("workingDay").GetBoolean());

        // A daily series over the closed day leaves it out and says why.
        var series = await OkAsync(BookAsync(o.T["laila"].Client, o.Camera, mon.AddDays(-1), new { pattern = "daily", until = mon.AddDays(1) }), HttpStatusCode.Created);
        Assert.Contains(series.GetProperty("skipped").EnumerateArray(), s => s.GetProperty("reason").GetString() == "closed" && s.GetProperty("date").GetString() == mon.ToString("yyyy-MM-dd"));

        // Twice is refused; a past day, a day more than a year ahead and a missing reason are refused.
        Assert.Equal("conflict.booking.already_closed", await ProblemKeyAsync(hana.PostAsJsonAsync(closures, new { date = mon, reason = "Again" }), HttpStatusCode.Conflict));
        Assert.Equal("validation.booking.closure_date",
            await ProblemKeyAsync(hana.PostAsJsonAsync(closures, new { date = mon.AddDays(-10), reason = "Late" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.closure_date",
            await ProblemKeyAsync(hana.PostAsJsonAsync(closures, new { date = mon.AddDays(400), reason = "Far" }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.closure_reason", await ProblemKeyAsync(hana.PostAsJsonAsync(closures, new { date = mon.AddDays(2), reason = " " }), HttpStatusCode.BadRequest));

        // Opened again, the day takes bookings; the cancelled one stays cancelled.
        Assert.Equal(HttpStatusCode.NoContent, (await hana.DeleteAsync($"{closures}/{mon:yyyy-MM-dd}")).StatusCode);
        Assert.Equal(1, await AuditedAsync(o, AuditActions.BookingSiteReopened));
        Assert.Equal("cancelled:site_closed", await StatusAsync(o, desk));
        await BookedAsync(o.T["laila"].Client, o.Desk, mon);
        Assert.Equal(HttpStatusCode.NotFound, (await hana.DeleteAsync($"{closures}/{mon:yyyy-MM-dd}")).StatusCode);
    }

    [Fact]
    public async Task Only_the_site_administrator_closes_it_and_other_tenants_never_reach_it()
    {
        var o = await SetupAsync();
        var closures = $"{Api}/manage/sites/{o.Site}/closures";
        var body = new { date = Monday(), reason = "Fire drill" };
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.PostAsJsonAsync(closures, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["omar"].Client.GetAsync(closures)).StatusCode);
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["hana"].Client.PostAsJsonAsync(closures, body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["hana"].Client.DeleteAsync($"{closures}/{Monday():yyyy-MM-dd}")).StatusCode);
        Assert.Equal(0, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from booking.site_closure"));
    }

    [Fact]
    public async Task A_new_holiday_cancels_that_days_bookings_of_resources_that_keep_working_days_with_notice()
    {
        var o = await SetupAsync();
        var mon = Monday();
        var omar = o.T["omar"].Client;
        var desk = await BookedAsync(omar, o.Desk, mon);
        var camera = await BookedAsync(o.T["laila"].Client, o.Camera, mon);
        var tuesday = await BookedAsync(omar, o.Desk, mon.AddDays(1));

        var admin = o.T["admin"].Client;
        var calendar = (await OkAsync(admin.GetAsync("/api/v1/hr/calendars")))[0];
        using (var holidays = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{Id(calendar)}/holidays")
        {
            Content = JsonContent.Create(new { year = mon.Year, holidays = new[] { new { date = mon, name = new { en = "National day" }, halfDay = false } } }),
        })
        {
            holidays.Headers.TryAddWithoutValidation("If-Match", $"\"v{calendar.GetProperty("version").GetInt32()}\"");
            await OkAsync(admin.SendAsync(holidays));
        }
        await world.DispatchTenantAsync(o.T.Id);

        Assert.Equal("cancelled:holiday", await StatusAsync(o, desk));
        Assert.Equal("confirmed:", await StatusAsync(o, camera));
        Assert.Equal("confirmed:", await StatusAsync(o, tuesday));
        Assert.Equal(1, await NotifiedAsync(o, "omar", BookingClosures.HolidayTemplate));
        Assert.Equal(0, await NotifiedAsync(o, "laila", BookingClosures.HolidayTemplate));
    }

    [Fact]
    public async Task Bookings_that_ended_over_a_year_ago_keep_counts_only_and_activity_is_counted_without_people()
    {
        var o = await SetupAsync();
        var mon = Monday();
        var booked = await BookedAsync(o.T["omar"].Client, o.Desk, mon);
        Assert.Equal(HttpStatusCode.OK, (await o.T["omar"].Client.PostAsJsonAsync($"{Api}/bookings/{booked}:cancel", new { })).StatusCode);

        // section 13: one count each for the booking and the cancellation, with no person, unit, locale or exact time.
        Assert.Equal(2, await world.Database.CountInTenantAsync(o.T.Id,
            "select count(*) from analytics.event where name = 'booking.activity' and user_key is null and locale is null and fields->>'type' = 'desk' and fields->>'siteId' = @s",
            ("s", o.Site.ToString())));
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from analytics.event where name = 'booking.activity' and fields->>'action' = 'cancelled'"));

        // section 8: a booking that ended 400 days ago loses who held it; a recent one keeps it.
        var old = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(-400);
        await world.Database.OwnerExecInTenantAsync(o.T.Id,
            """
            insert into booking.booking (tenant_id, id, resource_id, holder_id, booked_by, starts_at, ends_at, held, title, status, checked_in_at, check_in_method, created_at)
            values (current_setting('app.tenant_id')::uuid, @id, @r, @u, @u, @s, @s + interval '1 hour', tstzrange(@s, @s + interval '1 hour'), 'Old review', 'checked_in', @s, 'qr', @s)
            """, ("id", old), ("r", o.Desk), ("u", o.T["omar"].Id), ("s", start));
        var run = await world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            var result = await sp.GetRequiredService<BookingMaintenance>().RunDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });
        Assert.Equal(1, run.Anonymized);
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id,
            $"select count(*) from booking.booking where id = @id and holder_id = '{Nobody}' and booked_by = '{Nobody}' and title is null and status = 'checked_in'", ("id", old)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from booking.booking where id = @id and holder_id = @u",
            ("id", booked), ("u", o.T["omar"].Id)));
        // The person no longer sees it among their bookings.
        var mine = await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/bookings/mine"));
        Assert.DoesNotContain(old.ToString(), mine.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }
}
