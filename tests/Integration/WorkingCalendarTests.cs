using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T004: working calendars. The tenant default applies with no calendar, the nearest assigned ancestor wins,
/// holiday changes publish <c>WorkingCalendarChanged</c>, scoped HR officers stay in their subtree, other tenants'
/// calendars and units are not found, and every change is audited.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class WorkingCalendarTests(TenantWorld world)
{
    private static readonly int[] MondayToFriday = [1, 2, 3, 4, 5];
    private static readonly int[] SundayToThursday = [1, 2, 3, 4, 7];

    private async Task<TestTenant> TenantAsync()
    {
        var t = await world.CreateTenantAsync("cal-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Dubai", gulf, "department");
        await world.CreateUnitAsync(t, "Riyadh", gulf, "department");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "hr_gulf", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "employee", "Riyadh");
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        return t;
    }

    private static HttpClient Admin(TestTenant t) => t["admin"].Client;

    private static async Task<JsonElement> CreateAsync(HttpClient client, string name, int[] week, Guid? owner = null, string? country = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/hr/calendars", new { name = new { en = name, ar = "تقويم " + name }, country, weekDays = week, ownerOrgUnitId = owner });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> AssignAsync(HttpClient client, Guid unit, Guid? calendar) =>
        client.PutAsJsonAsync($"/api/v1/hr/assignments/{unit}", new { calendarId = calendar });

    private static Task<HttpResponseMessage> HolidaysAsync(HttpClient client, JsonElement calendar, int year, params (string Date, bool Half)[] days)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{Id(calendar)}/holidays")
        {
            Content = JsonContent.Create(new { year, holidays = days.Select(d => new { date = d.Date, name = new { en = "Holiday " + d.Date }, halfDay = d.Half }) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"v{calendar.GetProperty("version").GetInt32()}\"");
        return client.SendAsync(request);
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static async Task<decimal> WorkingDaysAsync(HttpClient client, Guid unit, string from, string to)
    {
        var response = await client.GetAsync($"/api/v1/hr/working-days?orgUnitId={unit}&from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("workingDays").GetDecimal();
    }

    private Task<WorkingCalendarSnapshot> ForUserAsync(TestTenant t, Guid userId) =>
        world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<IWorkingCalendar>().ForUserAsync(userId));

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    // The week of Monday 30 November to Sunday 6 December 2026.
    private const string WeekStart = "2026-11-30";
    private const string WeekEnd = "2026-12-06";

    private static Task<HttpResponseMessage> PeriodsAsync(HttpClient client, JsonElement calendar, params (string Start, string End, decimal Hours)[] periods)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{Id(calendar)}/periods")
        {
            Content = JsonContent.Create(new { periods = periods.Select(p => new { name = new { en = "Ramadan", ar = "رمضان" }, start = p.Start, end = p.End, hours = p.Hours }) }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"v{calendar.GetProperty("version").GetInt32()}\"");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Reduced_hours_periods_such_as_Ramadan_give_shorter_working_days_and_are_validated()
    {
        var t = await TenantAsync();
        var riyadh = t.Unit("Riyadh");
        var ksa = await CreateAsync(Admin(t), "KSA", SundayToThursday);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(Admin(t), riyadh, Id(ksa))).StatusCode);
        ksa = await (await Admin(t).GetAsync($"/api/v1/hr/calendars/{Id(ksa)}")).ReadAsync<JsonElement>();

        // Invalid periods are refused with their keys: overlap, hours, dates and names.
        Assert.Contains("validation.calendar.period_overlap", await ErrorKeysAsync(await PeriodsAsync(Admin(t), ksa, ("2027-02-08", "2027-03-09", 6), ("2027-03-01", "2027-03-05", 5))));
        Assert.Contains("validation.calendar.period_hours", await ErrorKeysAsync(await PeriodsAsync(Admin(t), ksa, ("2027-02-08", "2027-03-09", 6.3m))));
        Assert.Contains("validation.calendar.period_hours", await ErrorKeysAsync(await PeriodsAsync(Admin(t), ksa, ("2027-02-08", "2027-03-09", 24))));
        Assert.Contains("validation.calendar.period_dates", await ErrorKeysAsync(await PeriodsAsync(Admin(t), ksa, ("2027-03-09", "2027-02-08", 6))));
        Assert.Contains("validation.calendar.period_dates", await ErrorKeysAsync(await PeriodsAsync(Admin(t), ksa, ("2027-01-01", "2027-06-30", 6))));

        var saved = await PeriodsAsync(Admin(t), ksa, ("2027-02-08", "2027-03-09", 6));
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var view = await saved.ReadAsync<JsonElement>();
        var period = Assert.Single(view.GetProperty("periods").EnumerateArray());
        Assert.Equal(("Ramadan", 6m), (period.GetProperty("name").GetProperty("en").GetString(), period.GetProperty("hours").GetDecimal()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrCalendarChanged, Id(ksa).ToString());
        // The old version is refused now.
        Assert.Equal(HttpStatusCode.Conflict, (await PeriodsAsync(Admin(t), ksa, ("2027-02-08", "2027-03-09", 5))).StatusCode);

        // A working day in the period has the reduced hours; the weekend and days outside it do not.
        var snapshot = await ForUserAsync(t, t["employee"].Id);
        Assert.Equal(6m, snapshot.ReducedHours(new DateOnly(2027, 2, 10)));
        Assert.Null(snapshot.ReducedHours(new DateOnly(2027, 2, 12)));
        Assert.Null(snapshot.ReducedHours(new DateOnly(2027, 3, 10)));
        var preview = await (await Admin(t).GetAsync($"/api/v1/hr/working-days?orgUnitId={riyadh}&from=2027-03-08&to=2027-03-11")).ReadAsync<JsonElement>();
        Assert.Equal([6m, 6m, null, null], preview.GetProperty("days").EnumerateArray()
            .Select(d => d.GetProperty("reducedHours").ValueKind == JsonValueKind.Null ? (decimal?)null : d.GetProperty("reducedHours").GetDecimal()).ToArray());

        // Employees cannot set them.
        Assert.Equal(HttpStatusCode.Forbidden, (await PeriodsAsync(t["employee"].Client, view, ("2027-02-08", "2027-03-09", 5))).StatusCode);
    }

    [Fact]
    public async Task With_no_calendar_the_tenant_week_is_Monday_to_Friday_with_no_holidays()
    {
        var t = await TenantAsync();
        var snapshot = await ForUserAsync(t, t["employee"].Id);
        Assert.Null(snapshot.CalendarId);
        Assert.Equal(5m, snapshot.CountWorkingDays(new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 6)));
        Assert.Equal(5m, await WorkingDaysAsync(Admin(t), t.Unit("Riyadh"), WeekStart, WeekEnd));

        var list = await (await Admin(t).GetAsync("/api/v1/hr/calendars")).ReadAsync<JsonElement>();
        var only = Assert.Single(list.EnumerateArray());
        Assert.True(only.GetProperty("isTenantDefault").GetBoolean());
        Assert.Equal(MondayToFriday, only.GetProperty("weekDays").EnumerateArray().Select(d => d.GetInt32()));
    }

    [Fact]
    public async Task An_administrator_can_change_the_tenant_week_to_Sunday_to_Thursday()
    {
        var t = await TenantAsync();
        var list = await (await Admin(t).GetAsync("/api/v1/hr/calendars")).ReadAsync<JsonElement>();
        var defaultCalendar = list[0];
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/hr/calendars/{Id(defaultCalendar)}")
        {
            Content = JsonContent.Create(new { name = new { }, weekDays = SundayToThursday }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", defaultCalendar.GetProperty("eTag").GetString());
        var saved = await Admin(t).SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var snapshot = await ForUserAsync(t, t["employee"].Id);
        Assert.True(snapshot.IsWorkingDay(new DateOnly(2026, 11, 29))); // Sunday
        Assert.False(snapshot.IsWorkingDay(new DateOnly(2026, 12, 4))); // Friday
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrCalendarChanged, Id(defaultCalendar).ToString());
    }

    [Fact]
    public async Task Each_location_uses_its_own_calendar_and_the_nearest_assigned_ancestor_wins()
    {
        var t = await TenantAsync();
        var uae = await CreateAsync(Admin(t), "UAE", MondayToFriday, country: "AE");
        var ksa = await CreateAsync(Admin(t), "KSA", SundayToThursday, country: "sa");
        Assert.Equal("SA", ksa.GetProperty("country").GetString());
        Assert.Equal(HttpStatusCode.OK, (await HolidaysAsync(Admin(t), uae, 2026, ("2026-12-02", false), ("2026-12-03", false))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(Admin(t), t.Unit("Gulf"), Id(uae))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(Admin(t), t.Unit("Riyadh"), Id(ksa))).StatusCode);

        // Dubai inherits the Gulf calendar; Riyadh has its own; Africa still has the tenant default.
        Assert.Equal(3m, await WorkingDaysAsync(Admin(t), t.Unit("Dubai"), WeekStart, WeekEnd));
        Assert.Equal(5m, await WorkingDaysAsync(Admin(t), t.Unit("Riyadh"), WeekStart, WeekEnd));
        Assert.Equal(5m, await WorkingDaysAsync(Admin(t), t.Unit("Africa"), WeekStart, WeekEnd));

        var employee = await ForUserAsync(t, t["employee"].Id);
        Assert.Equal(Id(ksa), employee.CalendarId);
        Assert.True(employee.IsWorkingDay(new DateOnly(2026, 11, 29)));

        // Removing Riyadh's own assignment falls back to the Gulf calendar.
        Assert.Equal(HttpStatusCode.NoContent, (await AssignAsync(Admin(t), t.Unit("Riyadh"), null)).StatusCode);
        Assert.Equal(Id(uae), (await ForUserAsync(t, t["employee"].Id)).CalendarId);
    }

    [Fact]
    public async Task Changing_holidays_publishes_the_change_with_the_affected_dates()
    {
        var t = await TenantAsync();
        var uae = await CreateAsync(Admin(t), "UAE", MondayToFriday);
        await AssignAsync(Admin(t), t.Unit("Gulf"), Id(uae));
        var before = await world.Database.OutboxCountAsync(nameof(WorkingCalendarChanged), t.Id);

        var first = await HolidaysAsync(Admin(t), uae, 2027, ("2027-03-20", false), ("2027-03-21", false));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var updated = await first.ReadAsync<JsonElement>();
        Assert.Equal(2, updated.GetProperty("holidays").GetArrayLength());

        // Announced late: Eid moves by one day.
        var moved = await HolidaysAsync(Admin(t), updated, 2027, ("2027-03-21", false), ("2027-03-22", false));
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        Assert.Equal(before + 2, await world.Database.OutboxCountAsync(nameof(WorkingCalendarChanged), t.Id));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from messaging.outbox where message_type = @m and tenant_id = platform.current_tenant_id() and payload::text like '%2027-03-20%' and payload::text like '%2027-03-22%' and payload::text not like '%2027-03-21%'",
            ("m", nameof(WorkingCalendarChanged))));
        Assert.True(await world.Database.AuditCountAsync(t.Id, AuditActions.HrCalendarChanged, Id(uae).ToString()) >= 3);
    }

    [Fact]
    public async Task A_stale_version_is_refused()
    {
        var t = await TenantAsync();
        var uae = await CreateAsync(Admin(t), "UAE", MondayToFriday);
        Assert.Equal(HttpStatusCode.OK, (await HolidaysAsync(Admin(t), uae, 2026, ("2026-12-02", false))).StatusCode);
        var stale = await HolidaysAsync(Admin(t), uae, 2026, ("2026-12-03", false));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("conflict.calendar.changed", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detailKey").GetString());
    }

    [Fact]
    public async Task A_scoped_HR_officer_manages_calendars_in_their_subtree_only()
    {
        var t = await TenantAsync();
        var hr = t["hr_gulf"].Client;
        var tenantCalendar = await CreateAsync(Admin(t), "Tenant wide", MondayToFriday);
        var africaCalendar = await CreateAsync(Admin(t), "Africa", MondayToFriday, owner: t.Unit("Africa"));

        var gulfCalendar = await CreateAsync(hr, "Gulf", SundayToThursday, owner: t.Unit("Gulf"));
        Assert.True(gulfCalendar.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(hr, t.Unit("Riyadh"), Id(gulfCalendar))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(hr, t.Unit("Dubai"), Id(tenantCalendar))).StatusCode);

        // Sibling subtree, tenant-level calendars and other units' calendars are out of reach.
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(hr, t.Unit("Africa"), Id(gulfCalendar))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await hr.PostAsJsonAsync("/api/v1/hr/calendars", new { name = new { en = "x" }, weekDays = MondayToFriday })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await HolidaysAsync(hr, tenantCalendar, 2026, ("2026-12-02", false))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await hr.GetAsync($"/api/v1/hr/calendars/{Id(africaCalendar)}")).StatusCode);
        Assert.Contains("validation.calendar.unknown", await ErrorKeysAsync(await AssignAsync(hr, t.Unit("Dubai"), Id(africaCalendar))));

        var visible = await (await hr.GetAsync("/api/v1/hr/calendars")).ReadAsync<JsonElement>();
        var ids = visible.EnumerateArray().Select(Id).ToHashSet();
        Assert.Contains(Id(gulfCalendar), ids);
        Assert.Contains(Id(tenantCalendar), ids);
        Assert.DoesNotContain(Id(africaCalendar), ids);
        Assert.False(visible.EnumerateArray().Single(c => Id(c) == Id(tenantCalendar)).GetProperty("canEdit").GetBoolean());
    }

    [Fact]
    public async Task A_unit_calendar_cannot_apply_outside_its_unit()
    {
        var t = await TenantAsync();
        var riyadhOnly = await CreateAsync(Admin(t), "Riyadh", SundayToThursday, owner: t.Unit("Riyadh"));
        Assert.Contains("validation.calendar.not_applicable", await ErrorKeysAsync(await AssignAsync(Admin(t), t.Unit("Dubai"), Id(riyadhOnly))));
        Assert.Equal(HttpStatusCode.OK, (await AssignAsync(Admin(t), t.Unit("Riyadh"), Id(riyadhOnly))).StatusCode);
    }

    [Fact]
    public async Task Employees_without_the_permission_are_denied()
    {
        var t = await TenantAsync();
        var client = t["employee"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/hr/calendars")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(client, t.Unit("Riyadh"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/v1/hr/working-days?from={WeekStart}&to={WeekEnd}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_calendars_and_units_are_not_found()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var bCalendar = await CreateAsync(Admin(b), "B", MondayToFriday);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(a).GetAsync($"/api/v1/hr/calendars/{Id(bCalendar)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(a).DeleteAsync($"/api/v1/hr/calendars/{Id(bCalendar)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(Admin(a), b.Unit("Gulf"), null)).StatusCode);
        Assert.Contains("validation.calendar.unknown", await ErrorKeysAsync(await AssignAsync(Admin(a), a.Unit("Gulf"), Id(bCalendar))));
        Assert.Equal(HttpStatusCode.NotFound, (await Admin(a).GetAsync($"/api/v1/hr/working-days?orgUnitId={b.Unit("Gulf")}&from={WeekStart}&to={WeekEnd}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Admin(a).PostAsJsonAsync("/api/v1/hr/calendars", new { name = new { en = "x" }, weekDays = MondayToFriday })).StatusCode);
        Assert.Contains("validation.calendar.owner_unknown", await ErrorKeysAsync(
            await Admin(a).PostAsJsonAsync("/api/v1/hr/calendars", new { name = new { en = "x" }, weekDays = MondayToFriday, ownerOrgUnitId = b.Unit("Gulf") })));
    }

    [Fact]
    public async Task The_default_calendar_and_calendars_in_use_cannot_be_deleted()
    {
        var t = await TenantAsync();
        var list = await (await Admin(t).GetAsync("/api/v1/hr/calendars")).ReadAsync<JsonElement>();
        var conflict = await Admin(t).DeleteAsync($"/api/v1/hr/calendars/{Id(list[0])}");
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("conflict.calendar.default", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detailKey").GetString());

        var used = await CreateAsync(Admin(t), "Used", MondayToFriday);
        await AssignAsync(Admin(t), t.Unit("Gulf"), Id(used));
        var inUse = await Admin(t).DeleteAsync($"/api/v1/hr/calendars/{Id(used)}");
        Assert.Equal("conflict.calendar.in_use", (await inUse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detailKey").GetString());

        await AssignAsync(Admin(t), t.Unit("Gulf"), null);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin(t).DeleteAsync($"/api/v1/hr/calendars/{Id(used)}")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrCalendarChanged, Id(used).ToString());
    }

    [Fact]
    public async Task Invalid_calendars_and_holidays_are_refused_with_resource_keys()
    {
        var t = await TenantAsync();
        var keys = await ErrorKeysAsync(await Admin(t).PostAsJsonAsync("/api/v1/hr/calendars", new { name = new { }, country = "UAE", weekDays = new[] { 0, 8 } }));
        Assert.Contains("validation.calendar.name", keys);
        Assert.Contains("validation.calendar.country", keys);
        Assert.Contains("validation.calendar.week_days", keys);

        var calendar = await CreateAsync(Admin(t), "UAE", MondayToFriday);
        var holidayKeys = await ErrorKeysAsync(await HolidaysAsync(Admin(t), calendar, 2026, ("2027-01-01", false)));
        Assert.Contains("validation.calendar.holiday_date", holidayKeys);
        var range = await Admin(t).GetAsync($"/api/v1/hr/working-days?from=2026-01-01&to=2027-06-01");
        Assert.Contains("validation.calendar.range", await ErrorKeysAsync(range));
    }
}
