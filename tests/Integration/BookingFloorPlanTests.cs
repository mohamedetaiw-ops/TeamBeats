using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 R3 T011 (US7, FR-019 to FR-021): a floor plan image per floor, checked by its own bytes, its size and the
/// malware scan; resources placed on it as percentages; the map for a time with each resource free, busy or not for the
/// viewer; booking from it through the same rules; moved resources and replaced or removed plans; the package, scope and
/// tenant boundaries. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingFloorPlanTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");
    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);

    private sealed record Org(TestTenant T, Guid Gulf, Guid Finance, Guid Site, Guid Floor, Guid Floor2, Guid Zone);

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

    /// <summary>The start of a PNG: its signature and header with the given size, and some bytes after it.</summary>
    private static byte[] Png(int width, int height, string? tail = null)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 };
        bytes.AddRange("IHDR"u8.ToArray());
        var size = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(size, width);
        BinaryPrimitives.WriteInt32BigEndian(size.AsSpan(4), height);
        bytes.AddRange(size);
        bytes.AddRange(new byte[] { 8, 6, 0, 0, 0, 0, 0, 0, 0 });
        bytes.AddRange(Encoding.ASCII.GetBytes(tail ?? "fictional floor plan pixels"));
        return bytes.ToArray();
    }

    /// <summary>A baseline JPEG start with one frame header of the given size.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };
        bytes.AddRange("JFIF\0"u8.ToArray());
        bytes.AddRange(new byte[] { 1, 1, 0, 0, 1, 0, 1, 0, 0 });
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1 });
        bytes.AddRange(new byte[] { 0xFF, 0xD9 });
        return bytes.ToArray();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid floor, byte[] data, string type)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PutAsync($"{Api}/manage/floors/{floor}/plan", content);
    }

    /// <summary>Hana manages the Riyadh office of Gulf with two floors; Omar works in Finance and Laila in People.</summary>
    private async Task<Org> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("fp-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", gulf, "department");
        await world.CreateUnitAsync(t, "People", gulf, "department");
        foreach (var (name, unit) in new[] { ("omar", "Finance"), ("laila", "People"), ("hana", "People") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        var hana = t["hana"].Client;
        var site = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office", ar = "مكتب الرياض" }, orgUnitId = gulf, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        var floor = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors", new { names = new { en = "Floor 2", ar = "الطابق 2" }, sort = 2 }), HttpStatusCode.Created));
        var floor2 = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors", new { names = new { en = "Floor 3" }, sort = 3 }), HttpStatusCode.Created));
        var zone = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors/{floor}/zones", new { names = new { en = "East wing" } }), HttpStatusCode.Created));
        return new Org(t, gulf, finance, site, floor, floor2, zone);
    }

    private static async Task<Guid> ResourceAsync(Org o, string kind, string name, Guid? floor = null) =>
        Id(await OkAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = o.Site, floorId = floor ?? o.Floor, zoneId = floor is null ? o.Zone : (Guid?)null, kind, names = new { en = name }, capacity = kind == "room" ? 8 : (int?)null,
            attributes = Array.Empty<string>(),
        }), HttpStatusCode.Created));

    private static JsonElement Placed(JsonElement map, Guid id) => map.GetProperty("resources").EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task A_floor_plan_is_checked_placed_and_shows_who_may_book_what_for_a_time()
    {
        var o = await SetupAsync();
        var hana = o.T["hana"].Client;
        var laila = o.T["laila"].Client;
        var desk1 = await ResourceAsync(o, "desk", "Desk 2.01");
        var desk2 = await ResourceAsync(o, "desk", "Desk 2.02");
        var room = await ResourceAsync(o, "room", "Focus room");
        var upstairs = await ResourceAsync(o, "desk", "Desk 3.01", o.Floor2);
        await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/resources/{room}/rule", new { unitIds = new[] { o.Finance } }));

        // Only PNG, JPEG and WebP, checked by their own bytes, up to 5 MB and 8,000 pixels a side, and clean.
        Assert.Equal("validation.booking.plan_type", await ProblemKeyAsync(UploadAsync(hana, o.Floor, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>"),
            "image/svg+xml"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_type", await ProblemKeyAsync(UploadAsync(hana, o.Floor, "GIF89a fictional"u8.ToArray(), "image/gif"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_type", await ProblemKeyAsync(UploadAsync(hana, o.Floor, Png(1200, 800), "image/jpeg"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_dimensions", await ProblemKeyAsync(UploadAsync(hana, o.Floor, Png(9000, 800), "image/png"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_size", await ProblemKeyAsync(UploadAsync(hana, o.Floor, new byte[FloorPlansMax + 1], "image/png"), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_infected", await ProblemKeyAsync(UploadAsync(hana, o.Floor, Png(1200, 800, TestSignatureScanner.Eicar), "image/png"),
            HttpStatusCode.BadRequest));
        // Placing needs a plan first.
        Assert.Equal("conflict.booking.plan_missing", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/floors/{o.Floor}/positions",
            new { positions = new[] { new { resourceId = desk1, x = 10m, y = 20m } } }), HttpStatusCode.Conflict));
        // Employees do not upload plans.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(laila, o.Floor, Png(1200, 800), "image/png")).StatusCode);

        var plan = await OkAsync(UploadAsync(hana, o.Floor, Png(1200, 800), "image/png"));
        Assert.Equal((1, "image/png", 1200, 800), (plan.GetProperty("version").GetInt32(), plan.GetProperty("contentType").GetString(), plan.GetProperty("width").GetInt32(),
            plan.GetProperty("height").GetInt32()));

        // Positions are percentages of the image, both or neither, for resources of this floor only.
        Assert.Equal("validation.booking.plan_resource", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/floors/{o.Floor}/positions",
            new { positions = new[] { new { resourceId = upstairs, x = 10m, y = 20m } } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_position", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/floors/{o.Floor}/positions",
            new { positions = new object[] { new { resourceId = desk1, x = 10m, y = (decimal?)null } } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.booking.plan_position", await ProblemKeyAsync(hana.PutAsJsonAsync($"{Api}/manage/floors/{o.Floor}/positions",
            new { positions = new[] { new { resourceId = desk1, x = 120m, y = 20m } } }), HttpStatusCode.BadRequest));
        var placed = await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/floors/{o.Floor}/positions", new
        {
            positions = new[] { new { resourceId = desk1, x = 12.345m, y = 20m }, new { resourceId = desk2, x = 30m, y = 40m }, new { resourceId = room, x = 70m, y = 55m } },
        }));
        Assert.Equal(12.35m, Placed(placed, desk1).GetProperty("x").GetDecimal());
        Assert.True(placed.GetProperty("manages").GetBoolean());

        // Omar books desk 2 for Monday 10:00; Laila opens the floor for that hour.
        var mon = Monday();
        await OkAsync(o.T["omar"].Client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = desk2, date = mon, start = Ten, end = Eleven }), HttpStatusCode.Created);
        var map = await OkAsync(laila.GetAsync($"{Api}/floors/{o.Floor}/map?date={mon:yyyy-MM-dd}&start=10:00&end=11:00"));
        Assert.False(map.GetProperty("manages").GetBoolean());
        Assert.Equal("Asia/Riyadh", map.GetProperty("timeZone").GetString());
        Assert.Equal((PlaceStates.Free, PlaceStates.Busy, PlaceStates.Unavailable), (Placed(map, desk1).GetProperty("state").GetString(),
            Placed(map, desk2).GetProperty("state").GetString(), Placed(map, room).GetProperty("state").GetString()));
        Assert.Equal(3, map.GetProperty("resources").GetArrayLength());
        Assert.DoesNotContain("omar", map.GetRawText(), StringComparison.OrdinalIgnoreCase);
        // The image is served to people who may book, as an image only.
        var image = await laila.GetAsync(map.GetProperty("plan").GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(Png(1200, 800), await image.Content.ReadAsByteArrayAsync());

        // Booking from the map is the same booking path and its rules.
        await OkAsync(laila.PostAsJsonAsync($"{Api}/bookings", new { resourceId = desk1, date = mon, start = Ten, end = Eleven }), HttpStatusCode.Created);
        Assert.Equal("authz.denied.booking_units", await ProblemKeyAsync(laila.PostAsJsonAsync($"{Api}/bookings", new { resourceId = room, date = mon, start = Ten, end = Eleven }),
            HttpStatusCode.Forbidden));
        map = await OkAsync(laila.GetAsync($"{Api}/floors/{o.Floor}/map?date={mon:yyyy-MM-dd}&start=10:00&end=11:00"));
        Assert.Equal(PlaceStates.Busy, Placed(map, desk1).GetProperty("state").GetString());
        // Omar in Finance may book the room.
        var forOmar = await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/floors/{o.Floor}/map?date={mon:yyyy-MM-dd}&start=10:00&end=11:00"));
        Assert.Equal(PlaceStates.Free, Placed(forOmar, room).GetProperty("state").GetString());

        // A new image keeps the places; moving a desk to another floor takes it off this plan.
        var jpeg = await OkAsync(UploadAsync(hana, o.Floor, Jpeg(1600, 900), "image/jpeg"));
        Assert.Equal((2, "image/jpeg", 1600, 900), (jpeg.GetProperty("version").GetInt32(), jpeg.GetProperty("contentType").GetString(), jpeg.GetProperty("width").GetInt32(),
            jpeg.GetProperty("height").GetInt32()));
        var detail = await OkAsync(hana.GetAsync($"{Api}/manage/sites/{o.Site}"));
        var desk2View = detail.GetProperty("resources").EnumerateArray().Single(r => Id(r) == desk2);
        await OkAsync(hana.PutAsJsonAsync($"{Api}/manage/resources/{desk2}", new
        {
            floorId = o.Floor2, names = new { en = "Desk 3.02" }, attributes = Array.Empty<string>(), version = desk2View.GetProperty("version").GetInt32(),
        }));
        map = await OkAsync(laila.GetAsync($"{Api}/floors/{o.Floor}/map"));
        Assert.Equal(2, map.GetProperty("resources").GetArrayLength());
        Assert.Equal(12.35m, Placed(map, desk1).GetProperty("x").GetDecimal());
        var moved = await OkAsync(laila.GetAsync($"{Api}/floors/{o.Floor2}/map"));
        Assert.Equal(JsonValueKind.Null, Placed(moved, desk2).GetProperty("x").ValueKind);
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("plan").ValueKind);

        // Removing the plan takes the places with it.
        Assert.Equal(HttpStatusCode.NoContent, (await hana.DeleteAsync($"{Api}/manage/floors/{o.Floor}/plan")).StatusCode);
        map = await OkAsync(laila.GetAsync($"{Api}/floors/{o.Floor}/map"));
        Assert.Equal(JsonValueKind.Null, map.GetProperty("plan").ValueKind);
        Assert.All(map.GetProperty("resources").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("x").ValueKind));
        Assert.Equal(HttpStatusCode.NotFound, (await laila.GetAsync($"{Api}/floors/{o.Floor}/plan")).StatusCode);
        Assert.Equal(4L, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.BookingFloorPlanChanged)));
    }

    private const int FloorPlansMax = TeamBeats.Modules.Booking.FloorPlans.MaxBytes;

    private static class PlaceStates
    {
        public const string Free = "free";
        public const string Busy = "busy";
        public const string Unavailable = "unavailable";
    }

    [Fact]
    public async Task Floor_plans_need_the_package_the_site_scope_and_stay_in_their_tenant()
    {
        var o = await SetupAsync();
        var hana = o.T["hana"].Client;
        await ResourceAsync(o, "desk", "Desk 2.01");
        await OkAsync(UploadAsync(hana, o.Floor, Png(1000, 700), "image/png"));

        // Another tenant reaches neither the map nor the image nor the upload.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["laila"].Client.GetAsync($"{Api}/floors/{o.Floor}/map")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["laila"].Client.GetAsync($"{Api}/floors/{o.Floor}/plan")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other.T["hana"].Client, o.Floor, Png(1000, 700), "image/png")).StatusCode);

        // A facilities administrator of another unit does not manage this site's plans.
        await world.SeedUserAsync(o.T, "kofi", MembershipTypes.Employee);
        var africa = await world.CreateUnitAsync(o.T, "Africa", null, "organization");
        await world.AddMembershipAsync(o.T, "kofi", "Africa");
        await world.AssignAsync(o.T, "kofi", BuiltInRoles.FacilitiesAdministrator, "org_subtree", africa);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(o.T["kofi"].Client, o.Floor, Png(1000, 700), "image/png")).StatusCode);

        // Without booking.floor_plans the map is refused; booking by search still works.
        await world.SetCapabilityAsync(o.T, Capabilities.BookingFloorPlans, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await o.T["laila"].Client.GetAsync($"{Api}/floors/{o.Floor}/map")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(hana, o.Floor, Png(1000, 700), "image/png")).StatusCode);
        await OkAsync(o.T["laila"].Client.GetAsync($"{Api}/sites/{o.Site}/search?date={Monday():yyyy-MM-dd}&start=10:00&end=11:00"));

        // Workplace has no booking at all.
        var workplace = await world.CreateTenantAsync("fw-" + Guid.NewGuid().ToString("N")[..8], "workplace", "en", ["en"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await workplace["admin"].Client.GetAsync($"{Api}/floors/{Guid.NewGuid()}/map")).StatusCode);
    }
}
