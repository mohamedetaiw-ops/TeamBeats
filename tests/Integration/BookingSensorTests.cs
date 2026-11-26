using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Booking;
using TeamBeats.Modules.Integrations.Occupancy;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 R3 T015 (US9, FR-026, FR-027): an occupancy REST connection through spec 017; facilities map its sensors to
/// the site's resources; the clock reads every 5 minutes and keeps only counts; with sensor check-in on, an occupied
/// reading in the check-in window checks the booking in, and off it does not; a silent connector changes nothing;
/// reports show sensed hours; the package, scope and tenant boundaries. Names and sensor IDs are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingSensorTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private const string Packs = "/api/v1/integrations/pack-connections";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private sealed record Org(TestTenant T, Guid Gulf, Guid Site, Guid Floor, Guid Desk, Guid Room);

    private FakeRestApi Rest => world.Factory.Rest;

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
        while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
        return day;
    }

    private static DateTimeOffset At(DateOnly day, int hour, int minute) =>
        new DateTimeOffset(day.ToDateTime(new TimeOnly(hour, minute)), Riyadh.GetUtcOffset(day.ToDateTime(new TimeOnly(hour, minute))));

    private async Task<Org> SetupAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("se-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var gulf = await world.CreateUnitAsync(t, "Gulf", null, "organization");
        foreach (var name in new[] { "omar", "laila", "hana" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        var hana = t["hana"].Client;
        var site = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office" }, orgUnitId = gulf, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        var floor = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors", new { names = new { en = "Floor 1" }, sort = 1 }), HttpStatusCode.Created));
        async Task<Guid> ResourceAsync(string kind, string name) => Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = site, floorId = floor, kind, names = new { en = name }, capacity = kind == "room" ? 6 : (int?)null, attributes = Array.Empty<string>(),
        }), HttpStatusCode.Created));
        return new Org(t, gulf, site, floor, await ResourceAsync("desk", "Desk 1.01"), await ResourceAsync("room", "Palm room"));
    }

    /// <summary>The fake sensor service: each sensor's state, and an optional outage.</summary>
    private sealed class FakeSensors
    {
        public Dictionary<string, bool> State { get; } = new(StringComparer.Ordinal);

        public bool Down { get; set; }

        public HttpResponseMessage Respond(FakeRestApi.Sent sent)
        {
            if (Down)
            {
                return FakeRestApi.Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            // The system also sends who badged in; the pack drops it.
            var sensors = State.Select(s => new { id = s.Key, occupied = s.Value, badge_holder = "someone.private@example.test" });
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { sensors }));
        }
    }

    private async Task<Guid> ConnectAsync(TestTenant t, string host)
    {
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "sensor-key"), "fictional-sensor-key");
        var created = await OkAsync(admin.PostAsJsonAsync(Packs, new
        {
            kind = OccupancyRestPack.KindName, displayName = "Sensors", settings = new { baseUrl = $"https://{host}", auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "sensor-key") } },
        }), HttpStatusCode.Created);
        var id = Id(created);
        var test = await OkAsync(admin.PostAsync($"{Packs}/{id}:test", null));
        Assert.True(test.GetProperty("status").GetString() == "ok", test.GetRawText());
        await OkAsync(admin.PostAsync($"{Packs}/{id}:enable", null));
        return id;
    }

    private Task<BookingMaintenance.Run> MaintainAsync(Org o, DateTimeOffset now) =>
        world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            var run = await sp.GetRequiredService<BookingMaintenance>().RunDueAsync(now.ToUniversalTime(), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });

    private Task<string?> StatusAsync(Org o, Guid booking) =>
        world.Database.TenantScalarAsync<string>(o.T.Id, "select status from booking.booking where id = @id", ("id", booking));

    [Fact]
    public async Task Sensors_are_mapped_read_every_five_minutes_check_bookings_in_when_turned_on_and_show_in_reports()
    {
        var o = await SetupAsync();
        var hana = o.T["hana"].Client;
        var host = $"sensors-{Guid.NewGuid():N}"[..20] + ".rest.test";
        var fake = new FakeSensors();
        fake.State["desk-101"] = true;
        fake.State["room-palm"] = false;
        fake.State["unmapped-9"] = true;
        Rest.On(host, fake.Respond);
        var connection = await ConnectAsync(o.T, host);

        // US9 scenario 1: Hana maps the sensors of this connection to the site's resources.
        var view = await OkAsync(hana.GetAsync($"{Api}/manage/sites/{o.Site}/sensors"));
        Assert.Equal(connection, Id(Assert.Single(view.GetProperty("connections").EnumerateArray())));
        Assert.Equal("validation.booking.sensor_connection", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/sites/{o.Site}/sensors",
            new { connectionId = Guid.NewGuid(), mappings = Array.Empty<object>() }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.sensor_resource", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/sites/{o.Site}/sensors",
            new { connectionId = connection, mappings = new[] { new { sensorId = "desk-101", resourceId = Guid.NewGuid() } } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.sensor_id", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/sites/{o.Site}/sensors",
            new { connectionId = connection, mappings = new[] { new { sensorId = "desk 101", resourceId = o.Desk } } }), HttpStatusCode.BadRequest));
        view = await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/sites/{o.Site}/sensors", new
        {
            connectionId = connection, mappings = new[] { new { sensorId = "desk-101", resourceId = o.Desk }, new { sensorId = "room-palm", resourceId = o.Room } },
        }));
        Assert.Equal(2, view.GetProperty("sensors").GetArrayLength());
        Assert.False(view.GetProperty("sensorCheckIn").GetBoolean());
        Assert.Equal(1L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.BookingSensorsMapped)));

        // Laila books the desk and Omar the room on Monday at 10:00.
        var mon = Monday();
        var desk = Id((await OkAsync(o.T["laila"].Client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = o.Desk, date = mon, start = new TimeOnly(10, 0), end = new TimeOnly(11, 0) }),
            HttpStatusCode.Created)).GetProperty("bookings")[0]);
        var room = Id((await OkAsync(o.T["omar"].Client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = o.Room, date = mon, start = new TimeOnly(10, 0), end = new TimeOnly(11, 0) }),
            HttpStatusCode.Created)).GetProperty("bookings")[0]);

        // Sensor check-in is off: the desk reads occupied in the window, but nobody is checked in.
        await MaintainAsync(o, At(mon, 9, 55));
        Assert.Equal("confirmed", await StatusAsync(o, desk));
        var reads = Rest.SentTo(host).Count;
        view = await OkAsync(hana.GetAsync($"{Api}/manage/sites/{o.Site}/sensors"));
        Assert.True(view.GetProperty("sensors").EnumerateArray().Single(s => s.GetProperty("sensorId").GetString() == "desk-101").GetProperty("lastOccupied").GetBoolean());
        // A read again within 5 minutes is skipped.
        await MaintainAsync(o, At(mon, 9, 57));
        Assert.Equal(reads, Rest.SentTo(host).Count);

        // FR-027: with sensor check-in on, the next read checks the desk in; the vacant room stays as it is.
        await OkAsync(o.T["admin"].Client.PutAsJsonAsync($"{Api}/manage/settings", new { sensorCheckIn = true }));
        var run = await MaintainAsync(o, At(mon, 10, 5));
        Assert.Equal(1, run.SensorCheckIns);
        Assert.Equal("checked_in", await StatusAsync(o, desk));
        Assert.Equal("sensor", await world.Database.TenantScalarAsync<string>(o.T.Id, "select check_in_method from booking.booking where id = @id", ("id", desk)));
        Assert.Equal("confirmed", await StatusAsync(o, room));

        // A silent connector changes nothing: the room's occupied state is never seen, so it is not checked in.
        fake.State["room-palm"] = true;
        fake.Down = true;
        await MaintainAsync(o, At(mon, 10, 11));
        Assert.Equal("confirmed", await StatusAsync(o, room));
        Assert.False(await world.Database.TenantScalarAsync<bool>(o.T.Id, "select last_occupied from booking.sensor where sensor_id = 'room-palm'"));

        // No personal data is kept: only IDs, states and counts.
        Assert.Equal(0L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from booking.sensor where sensor_id like '%private%'"));
        Assert.Equal(0L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from booking.sensor where sensor_id = 'unmapped-9'"));

        // Reports show the sensed occupied hours as counts: two occupied reads of the desk, 5 minutes each.
        var report = await OkAsync(hana.GetAsync($"{Api}/reports/utilization?site={o.Site}&from={mon:yyyy-MM-dd}&to={mon:yyyy-MM-dd}&groupBy=resource"));
        var deskRow = report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("key").GetString() == o.Desk.ToString());
        Assert.Equal(0.17, deskRow.GetProperty("sensedHours").GetDouble());
        Assert.Equal(0.17, report.GetProperty("total").GetProperty("sensedHours").GetDouble());

        // Removing a mapping stops its reads counting.
        view = await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/sites/{o.Site}/sensors", new { connectionId = connection, mappings = new[] { new { sensorId = "room-palm", resourceId = o.Room } } }));
        Assert.Single(view.GetProperty("sensors").EnumerateArray());
    }

    [Fact]
    public async Task Sensors_need_the_package_and_the_site_scope_and_stay_in_their_tenant()
    {
        var o = await SetupAsync();
        Assert.Empty((await OkAsync(o.T["hana"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/sensors"))).GetProperty("connections").EnumerateArray());

        // Another tenant and a facilities administrator of another unit reach nothing.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["hana"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/sensors")).StatusCode);
        await world.SeedUserAsync(o.T, "kofi", MembershipTypes.Employee);
        var africa = await world.CreateUnitAsync(o.T, "Africa", null, "organization");
        await world.AddMembershipAsync(o.T, "kofi", "Africa");
        await world.AssignAsync(o.T, "kofi", BuiltInRoles.FacilitiesAdministrator, "org_subtree", africa);
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["kofi"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/sensors")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["laila"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/sensors")).StatusCode);

        // Workplace plus has no sensors: the mapping is refused and reports carry no sensed hours.
        var plus = await SetupAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.Forbidden, (await plus.T["hana"].Client.GetAsync($"{Api}/manage/sites/{plus.Site}/sensors")).StatusCode);
        var mon = Monday();
        var report = await OkAsync(plus.T["hana"].Client.GetAsync($"{Api}/reports/utilization?site={plus.Site}&from={mon:yyyy-MM-dd}&to={mon:yyyy-MM-dd}"));
        Assert.Equal(JsonValueKind.Null, report.GetProperty("total").GetProperty("sensedHours").ValueKind);
        await world.SetCapabilityAsync(o.T, Capabilities.BookingSensors, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["hana"].Client.GetAsync($"{Api}/manage/sites/{o.Site}/sensors")).StatusCode);
    }
}
