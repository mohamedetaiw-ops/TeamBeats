using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 slice 4 (T009): utilization and no-show rates by resource, zone, site and week for analysts and facilities
/// administrators, counts only and never who booked, refused to people without the report right in the site's scope,
/// to other tenants and to packages without booking reports. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingReportTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Noon = new(12, 0);

    private sealed record Org(TestTenant T, Guid Gulf, Guid Site, Guid Zone, Guid DeskA, Guid DeskB, Guid Room);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
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

    /// <summary>Omar and Sara in Finance under Gulf; Hana runs the Riyadh office; Noura analyses it. Two desks in the east wing, and a room with check-in off.</summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("br-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Finance", gulf, "department");
        foreach (var name in new[] { "omar", "sara", "hana", "noura" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Finance");
        }
        if (package == "workplace")
        {
            return new Org(t, gulf, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty);
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        await world.AssignAsync(t, "noura", BuiltInRoles.FacilitiesAnalyst, "org_subtree", gulf);
        var hana = t["hana"].Client;
        var site = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office" }, orgUnitId = gulf, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        var floor = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors", new { names = new { en = "Floor 1" }, sort = 1 }), HttpStatusCode.Created));
        var zone = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors/{floor}/zones", new { names = new { en = "East wing" } }), HttpStatusCode.Created));
        async Task<Guid> Resource(string kind, string name, Guid? zoneId, bool? checkIn, int? capacity = null) =>
            Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/resources", new
            {
                siteId = site, floorId = zoneId is null ? (Guid?)null : floor, zoneId, kind, names = new { en = name }, capacity, attributes = Array.Empty<string>(), checkIn,
            }), HttpStatusCode.Created));
        var deskA = await Resource("desk", "Desk A1", zone, true);
        var deskB = await Resource("desk", "Desk A2", zone, true);
        var room = await Resource("room", "Majlis", null, false, 8);
        return new Org(t, gulf, site, zone, deskA, deskB, room);
    }

    private static async Task<Guid> BookAsync(HttpClient client, Guid resource, DateOnly date) =>
        Id((await OkAsync(client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = resource, date, start = Ten, end = Noon, title = "Review" }), HttpStatusCode.Created))
            .GetProperty("bookings")[0]);

    private Task SetAsync(Org o, Guid booking, string status, string? endReason) =>
        world.Database.OwnerExecInTenantAsync(o.T.Id, "update booking.booking set status = @s, end_reason = nullif(@r, '') where id = @id",
            ("s", status), ("r", endReason ?? ""), ("id", booking));

    private static string Report(Org o, DateOnly from, DateOnly to, string? groupBy = null) =>
        $"{Api}/reports/utilization?site={o.Site}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}" + (groupBy is null ? "" : $"&groupBy={groupBy}");

    /// <summary>One checked-in desk, one no-show desk, the room (check-in off) booked and a cancelled booking, all on Monday.</summary>
    private async Task<Org> UsedAsync()
    {
        var o = await SetupAsync();
        var mon = Monday();
        var a = await BookAsync(o.T["omar"].Client, o.DeskA, mon);
        var b = await BookAsync(o.T["sara"].Client, o.DeskB, mon);
        await BookAsync(o.T["sara"].Client, o.Room, mon);
        var cancelled = await BookAsync(o.T["omar"].Client, o.DeskB, mon.AddDays(1));
        await SetAsync(o, a, "checked_in", null);
        await SetAsync(o, b, "released", "no_show");
        await SetAsync(o, cancelled, "cancelled", "cancelled");
        return o;
    }

    [Fact]
    public async Task An_analyst_sees_utilization_and_no_show_rates_by_resource_zone_site_and_week_without_names_of_people()
    {
        var o = await UsedAsync();
        var mon = Monday();
        var noura = o.T["noura"].Client;

        var sites = await OkAsync(noura.GetAsync($"{Api}/reports/sites"));
        Assert.Equal(o.Site, Assert.Single(sites.EnumerateArray()).GetProperty("id").GetGuid());

        var byResource = await OkAsync(noura.GetAsync(Report(o, mon, mon.AddDays(6))));
        Assert.Equal("resource", byResource.GetProperty("groupBy").GetString());
        var total = byResource.GetProperty("total");
        Assert.Equal(3, total.GetProperty("resources").GetInt32());
        Assert.Equal(3, total.GetProperty("bookings").GetInt32());
        Assert.Equal(1, total.GetProperty("checkedIn").GetInt32());
        Assert.Equal(1, total.GetProperty("noShows").GetInt32());
        Assert.Equal(1, total.GetProperty("cancelled").GetInt32());
        Assert.Equal(6, total.GetProperty("bookedHours").GetDouble());
        // Used: the checked-in desk and the room, which has check-in off.
        Assert.Equal(4, total.GetProperty("usedHours").GetDouble());
        var available = total.GetProperty("availableHours").GetDouble();
        Assert.True(available > 0 && available % 30 == 0, $"available {available}");
        Assert.Equal(Math.Round(4 / available, 4), total.GetProperty("utilization").GetDouble());
        Assert.Equal(0.5, total.GetProperty("noShowRate").GetDouble());

        var rows = byResource.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1.0, rows[o.DeskB.ToString()].GetProperty("noShowRate").GetDouble());
        Assert.Equal(0, rows[o.DeskB.ToString()].GetProperty("usedHours").GetDouble());
        Assert.Equal(0.0, rows[o.DeskA.ToString()].GetProperty("noShowRate").GetDouble());
        Assert.Equal(JsonValueKind.Null, rows[o.Room.ToString()].GetProperty("noShowRate").ValueKind);
        Assert.Equal("Majlis", rows[o.Room.ToString()].GetProperty("names").GetProperty("en").GetString());

        var byZone = (await OkAsync(noura.GetAsync(Report(o, mon, mon.AddDays(6), "zone")))).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!);
        Assert.Equal(2, byZone[o.Zone.ToString()].GetProperty("resources").GetInt32());
        Assert.Equal("East wing", byZone[o.Zone.ToString()].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(0.5, byZone[o.Zone.ToString()].GetProperty("noShowRate").GetDouble());
        Assert.Equal(1, byZone["none"].GetProperty("resources").GetInt32());

        var byWeek = (await OkAsync(noura.GetAsync(Report(o, mon.AddDays(-7), mon.AddDays(6), "week")))).GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, byWeek.Count);
        Assert.Equal(mon.AddDays(-7).ToString("yyyy-MM-dd"), byWeek[0].GetProperty("key").GetString());
        Assert.Equal(0, byWeek[0].GetProperty("bookings").GetInt32());
        Assert.Equal(3, byWeek[1].GetProperty("bookings").GetInt32());

        var bySite = await OkAsync(noura.GetAsync(Report(o, mon, mon.AddDays(6), "site")));
        Assert.Single(bySite.GetProperty("rows").EnumerateArray());

        // FR-014: counts only. No user id or name of anyone who booked appears anywhere in the report.
        var text = byResource.GetRawText() + byZone.Values.First().GetRawText();
        foreach (var name in new[] { "omar", "sara" })
        {
            Assert.DoesNotContain(o.T[name].Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
        }

        // The facilities administrator sees the same report.
        Assert.Equal(3, (await OkAsync(o.T["hana"].Client.GetAsync(Report(o, mon, mon.AddDays(6))))).GetProperty("total").GetProperty("bookings").GetInt32());
    }

    [Fact]
    public async Task Reports_check_their_range_and_grouping()
    {
        var o = await SetupAsync();
        var mon = Monday();
        var noura = o.T["noura"].Client;
        Assert.Equal(HttpStatusCode.BadRequest, (await noura.GetAsync(Report(o, mon, mon.AddDays(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await noura.GetAsync(Report(o, mon, mon.AddDays(92)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await noura.GetAsync(Report(o, mon, mon, "person"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await noura.GetAsync($"{Api}/reports/utilization?site={o.Site}")).StatusCode);
        await OkAsync(noura.GetAsync(Report(o, mon, mon.AddDays(91))));
    }

    [Fact]
    public async Task People_without_the_report_right_in_the_site_scope_other_tenants_and_smaller_packages_get_no_report()
    {
        var o = await UsedAsync();
        var mon = Monday();
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["omar"].Client.GetAsync(Report(o, mon, mon.AddDays(6)))).StatusCode);

        // An analyst for another part of the organization does not see this site.
        var africa = await world.CreateUnitAsync(o.T, "Africa", null, "organization");
        await world.SeedUserAsync(o.T, "kofi", MembershipTypes.Employee);
        await world.AddMembershipAsync(o.T, "kofi", "Africa");
        await world.AssignAsync(o.T, "kofi", BuiltInRoles.FacilitiesAnalyst, "org_subtree", africa);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["kofi"].Client.GetAsync(Report(o, mon, mon.AddDays(6)))).StatusCode);
        Assert.Empty((await OkAsync(o.T["kofi"].Client.GetAsync($"{Api}/reports/sites"))).EnumerateArray());

        // Another tenant cannot see the site, even with the right of its own.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["noura"].Client.GetAsync(Report(o, mon, mon.AddDays(6)))).StatusCode);

        var small = await SetupAsync("workplace");
        await world.AssignAsync(small.T, "noura", BuiltInRoles.FacilitiesAnalyst, "tenant", null);
        Assert.Equal(HttpStatusCode.Forbidden, (await small.T["noura"].Client.GetAsync(Report(o, mon, mon.AddDays(6)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await small.T["noura"].Client.GetAsync($"{Api}/reports/sites")).StatusCode);
    }
}
