using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Booking;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 slice 3 (T008): booking on behalf of someone who delegated to the booker, with the holder's own rules and
/// a notice to them; team booking of one desk each in a zone by a manager, each under the person's own limits; and the
/// refusals for people who did not delegate, are not in the team, or belong to another tenant. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingForOthersTests(TenantWorld world)
{
    private const string Api = "/api/v1/booking";
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private sealed record Org(TestTenant T, Guid Finance, Guid Site, Guid Floor, Guid Zone);

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

    private static readonly TimeOnly Ten = new(10, 0);
    private static readonly TimeOnly Eleven = new(11, 0);

    /// <summary>Omar (a director) and Sara in Finance; Mona, Laila, Hana and Ahmed in People; Kofi in Africa. Hana runs the office.</summary>
    private async Task<Org> SetupAsync()
    {
        var t = await world.CreateTenantAsync("bo-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var finance = await world.CreateUnitAsync(t, "Finance", gulf, "department");
        await world.CreateUnitAsync(t, "People", gulf, "department");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("omar", "Finance"), ("sara", "Finance"), ("mona", "People"), ("laila", "People"), ("hana", "People"), ("ahmed", "People"), ("kofi", "Africa") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", gulf);
        var hana = t["hana"].Client;
        var site = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office" }, orgUnitId = gulf, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        }), HttpStatusCode.Created));
        var floor = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors", new { names = new { en = "Floor 1" }, sort = 1 }), HttpStatusCode.Created));
        var zone = Id(await OkAsync(hana.PostAsJsonAsync($"{Api}/manage/sites/{site}/floors/{floor}/zones", new { names = new { en = "East wing" } }), HttpStatusCode.Created));
        return new Org(t, finance, site, floor, zone);
    }

    private static async Task<Guid> DeskAsync(Org o, string name, bool inZone = true) =>
        Id(await OkAsync(o.T["hana"].Client.PostAsJsonAsync($"{Api}/manage/resources", new
        {
            siteId = o.Site, floorId = o.Floor, zoneId = inZone ? o.Zone : (Guid?)null, kind = "desk", names = new { en = name }, attributes = Array.Empty<string>(),
        }), HttpStatusCode.Created));

    private async Task<int> NotifiedAsync(Org o, string user, string template) => (int)await
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    private static Task<HttpResponseMessage> BookForAsync(HttpClient client, Guid resource, DateOnly date, Guid forUser) =>
        client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = resource, date, start = Ten, end = Eleven, title = "Board prep", forUserId = forUser });

    [Fact]
    public async Task A_delegate_books_on_behalf_under_the_holders_rules_and_the_holder_is_told()
    {
        var o = await SetupAsync();
        var desk = await DeskAsync(o, "Desk 3.01");
        // Desk 3.01 is for Finance only: Omar may book it, Mona may not book it for herself.
        await OkAsync(o.T["hana"].Client.PutAsJsonAsync($"{Api}/manage/resources/{desk}/rule", new { unitIds = new[] { o.Finance } }));
        var mon = Monday();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime);
        var omar = o.T["omar"].Id;
        var mona = o.T["mona"].Client;

        // Without a delegation Mona may not book for Omar.
        Assert.Equal("authz.denied.booking_for", await ProblemKeyAsync(BookForAsync(mona, desk, mon, omar), HttpStatusCode.Forbidden));
        await OkAsync(o.T["omar"].Client.PutAsJsonAsync("/api/v1/me/delegation", new { delegateId = o.T["mona"].Id, startsOn = today.AddDays(-1), endsOn = today.AddDays(7) }));

        // US5 scenario 1: Mona books for Omar; it is Omar's booking under Omar's rules, and he is told who booked it.
        var view = (await OkAsync(BookForAsync(mona, desk, mon, omar), HttpStatusCode.Created)).GetProperty("bookings")[0];
        Assert.Equal(omar, view.GetProperty("holderId").GetGuid());
        Assert.Equal(o.T["mona"].Id, view.GetProperty("bookedBy").GetGuid());
        Assert.Equal(1, await NotifiedAsync(o, "omar", Bookings.BookedForTemplate));
        Assert.Equal(0, await NotifiedAsync(o, "mona", Bookings.ConfirmedTemplate));
        Assert.Single((await OkAsync(o.T["omar"].Client.GetAsync($"{Api}/bookings/mine"))).EnumerateArray());
        Assert.True((await OkAsync(mona.GetAsync($"{Api}/bookings/{Id(view)}"))).GetProperty("canCancel").GetBoolean());
        Assert.Equal("authz.denied.booking_units", await ProblemKeyAsync(
            mona.PostAsJsonAsync($"{Api}/bookings", new { resourceId = desk, date = mon.AddDays(1), start = Ten, end = Eleven }), HttpStatusCode.Forbidden));

        // Laila has no delegation from Omar; a person of another tenant is never someone to book for.
        Assert.Equal("authz.denied.booking_for", await ProblemKeyAsync(BookForAsync(o.T["laila"].Client, desk, mon.AddDays(1), omar), HttpStatusCode.Forbidden));
        var other = await SetupAsync();
        Assert.Equal("authz.denied.booking_for", await ProblemKeyAsync(BookForAsync(mona, desk, mon.AddDays(1), other.T["omar"].Id), HttpStatusCode.Forbidden));

        // Mona cancels it for Omar.
        Assert.Equal("cancelled", (await OkAsync(mona.PostAsJsonAsync($"{Api}/bookings/{Id(view)}:cancel", new { }))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_manager_books_one_desk_each_for_the_team_under_each_persons_own_limits()
    {
        var o = await SetupAsync();
        var desks = new[] { await DeskAsync(o, "Desk 2.01"), await DeskAsync(o, "Desk 2.02"), await DeskAsync(o, "Desk 2.03") };
        var elsewhere = await DeskAsync(o, "Desk 9.01", inZone: false);
        await OkAsync(o.T["hana"].Client.PutAsJsonAsync($"{Api}/manage/rules/desk?site={o.Site}", new { maxActive = 1 }));
        foreach (var person in new[] { "omar", "sara", "laila", "hana" })
        {
            await OkAsync(o.T["admin"].Client.PutAsJsonAsync($"/api/v1/people/{o.T[person].Id}/manager", new { managerId = o.T["ahmed"].Id }));
        }
        var mon = Monday();
        // Omar already holds his one active desk booking.
        await OkAsync(o.T["omar"].Client.PostAsJsonAsync($"{Api}/bookings", new { resourceId = elsewhere, date = mon.AddDays(1), start = Ten, end = Eleven }),
            HttpStatusCode.Created);

        var team = new[] { "omar", "sara", "laila", "ahmed", "hana", "kofi" }.Select(n => o.T[n].Id).ToArray();
        var result = await OkAsync(o.T["ahmed"].Client.PostAsJsonAsync($"{Api}/bookings:team", new
        {
            siteId = o.Site, zoneId = o.Zone, kind = "desk", date = mon, start = Ten, end = Eleven, title = "Team day", people = team,
        }));
        var outcomes = result.GetProperty("people").EnumerateArray().ToDictionary(p => p.GetProperty("personId").GetGuid());
        string? Problem(string n) => outcomes[o.T[n].Id].GetProperty("problem").GetString();

        // US5 scenario 2: each person gets their own booking in the zone; Omar is over his own limit, Kofi is not in Ahmed's team.
        Assert.Equal("conflict.booking.limit", Problem("omar"));
        Assert.Equal("authz.denied.booking_for", Problem("kofi"));
        var booked = new[] { "sara", "laila", "ahmed" }.Select(n => outcomes[o.T[n].Id].GetProperty("booking")).ToList();
        Assert.All(booked, b => Assert.Contains(b.GetProperty("resourceId").GetGuid(), desks));
        Assert.Equal(3, booked.Select(b => b.GetProperty("resourceId").GetGuid()).Distinct().Count());
        Assert.Equal(o.T["sara"].Id, booked[0].GetProperty("holderId").GetGuid());
        Assert.Equal("conflict.booking.none_free", Problem("hana"));
        Assert.Equal(1, await NotifiedAsync(o, "sara", Bookings.BookedForTemplate));

        // Each person can cancel their own.
        Assert.Equal("cancelled", (await OkAsync(o.T["laila"].Client.PostAsJsonAsync($"{Api}/bookings/{Id(booked[1])}:cancel", new { }))).GetProperty("status").GetString());

        // Laila manages nobody, so she can book a team of one: herself.
        var alone = await OkAsync(o.T["laila"].Client.PostAsJsonAsync($"{Api}/bookings:team", new
        {
            siteId = o.Site, kind = "desk", date = mon, start = Ten, end = Eleven, people = new[] { o.T["laila"].Id, o.T["sara"].Id },
        }));
        var lailaOutcomes = alone.GetProperty("people").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Object, lailaOutcomes[0].GetProperty("booking").ValueKind);
        Assert.Equal("authz.denied.booking_for", lailaOutcomes[1].GetProperty("problem").GetString());
        Assert.Equal("validation.booking.team", await ProblemKeyAsync(o.T["laila"].Client.PostAsJsonAsync($"{Api}/bookings:team", new
        {
            siteId = o.Site, kind = "desk", date = mon, start = Ten, end = Eleven, people = Array.Empty<Guid>(),
        }), HttpStatusCode.BadRequest));
    }
}
