using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Attendance;
using TeamBeats.Modules.Hr.Rosters;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 R3 T028 (US10, FR-029): HR officers of the whole organization keep shift patterns; a manager plans a week
/// for their direct reports only, sees approved leave as a conflict, and publishes it. Each planned person is told, a
/// later publish tells only the people whose days changed, attendance judges the day against the shift, HR officers of
/// the unit see the team's roster, and another team, the plan and other tenants are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class RosterTests(TenantWorld world)
{
    private const string Patterns = "/api/v1/hr/shift-patterns";
    private const string Weeks = "/api/v1/hr/rosters";

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ro-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "kofi", "dana", "eli", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var name in new[] { "lead", "sam", "hr_egypt" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Egypt");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        foreach (var (person, manager) in new[] { ("dana", "kofi"), ("eli", "kofi"), ("sam", "lead") })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[person].Id}/manager", new { managerId = t[manager].Id })).StatusCode);
        }
        return t;
    }

    private static Task<HttpResponseMessage> PatternAsync(HttpClient client, string en, string start, string end, int breakMinutes = 30) =>
        client.PostAsJsonAsync(Patterns, new { names = new Dictionary<string, string> { ["en"] = en, ["ar"] = en }, start, end, breakMinutes, tone = "info" });

    private Task<long> NotesAsync(TestTenant t, string user, string template) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private static object Entry(TestTenant t, string user, DateOnly date, Guid? pattern) => new { userId = t[user].Id, date = Iso(date), patternId = pattern, dayOff = pattern is null };

    [Fact]
    public async Task Only_HR_officers_of_the_whole_organization_keep_shift_patterns()
    {
        var t = await FreshAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await PatternAsync(t["hr_gulf"].Client, "Early", "06:00", "14:00")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PatternAsync(t["kofi"].Client, "Early", "06:00", "14:00")).StatusCode);
        var backwards = await PatternAsync(t["admin"].Client, "Night", "22:00", "06:00");
        Assert.Contains("validation.hr.shift_end", await backwards.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var early = await OkAsync(PatternAsync(t["admin"].Client, "Early", "06:00", "14:00"), HttpStatusCode.Created);
        var late = await OkAsync(PatternAsync(t["admin"].Client, "Late", "14:00", "22:00"), HttpStatusCode.Created);
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Patterns}/{late.GetProperty("id").GetGuid()}",
            new { names = new Dictionary<string, string> { ["en"] = "Late" }, start = "14:00", end = "22:00", breakMinutes = 30, tone = "warning", active = false }));
        var seen = await OkAsync(t["kofi"].Client.GetAsync(Patterns));
        Assert.False(seen.GetProperty("canManage").GetBoolean());
        Assert.Equal([early.GetProperty("id").GetGuid()], seen.GetProperty("patterns").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToArray());
        Assert.Equal(2, (await OkAsync(t["admin"].Client.GetAsync(Patterns))).GetProperty("patterns").GetArrayLength());
    }

    [Fact]
    public async Task A_manager_plans_and_publishes_their_team_and_a_change_tells_only_those_affected()
    {
        var t = await FreshAsync();
        var early = (await OkAsync(PatternAsync(t["admin"].Client, "Early", "06:00", "14:00"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var lateShift = (await OkAsync(PatternAsync(t["admin"].Client, "Late", "14:00", "22:00"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var week = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14);
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into hr.leave_type (tenant_id, id, key, names, sensitive, created_at, updated_at) values (@t, @type, 'annual', '{"en":"Annual leave"}', false, now(), now());
            insert into hr.leave_request (tenant_id, id, number, user_id, filed_by, leave_type_id, start_date, end_date, days, status, created_at, updated_at)
            values (@t, @request, 'L-1', @dana, @dana, @type, @day, @day, 1, 'approved', now(), now());
            insert into hr.leave_request_day (tenant_id, request_id, date, fraction, leave_year) values (@t, @request, @day, 1, 2027);
            """, ("t", t.Id), ("type", Guid.NewGuid()), ("request", Guid.NewGuid()), ("dana", t["dana"].Id), ("day", week.AddDays(1)));

        var kofi = t["kofi"].Client;
        var blank = await OkAsync(kofi.GetAsync($"{Weeks}/{Iso(week)}"));
        Assert.Equal("new", blank.GetProperty("status").GetString());
        Assert.Equal(2, blank.GetProperty("people").GetArrayLength());

        // Only direct reports, only inside the week, only active patterns.
        var other = await kofi.PutAsJsonAsync($"{Weeks}/{Iso(week)}", new { entries = new[] { Entry(t, "sam", week, early) } });
        Assert.Contains("validation.hr.roster_person", await other.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var outside = await kofi.PutAsJsonAsync($"{Weeks}/{Iso(week)}", new { entries = new[] { Entry(t, "dana", week.AddDays(7), early) } });
        Assert.Contains("validation.hr.roster_date", await outside.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["dana"].Client.PutAsJsonAsync($"{Weeks}/{Iso(week)}", new { entries = Array.Empty<object>() })).StatusCode);

        var saved = await OkAsync(kofi.PutAsJsonAsync($"{Weeks}/{Iso(week)}",
            new { entries = new[] { Entry(t, "dana", week, early), Entry(t, "dana", week.AddDays(1), early), Entry(t, "eli", week, lateShift), Entry(t, "eli", week.AddDays(1), null) } }));
        Assert.Equal("draft", saved.GetProperty("status").GetString());
        var dana = saved.GetProperty("people").EnumerateArray().Single(p => p.GetProperty("person").GetProperty("id").GetGuid() == t["dana"].Id);
        var leaveDay = dana.GetProperty("days")[1];
        Assert.True(leaveDay.GetProperty("leave").GetBoolean());
        Assert.True(leaveDay.GetProperty("conflict").GetBoolean());
        Assert.False(dana.GetProperty("days")[0].GetProperty("conflict").GetBoolean());
        // Nothing reaches anyone before the week is published.
        Assert.Empty((await OkAsync(t["dana"].Client.GetAsync($"/api/v1/me/shifts?from={Iso(week)}&to={Iso(week.AddDays(6))}"))).EnumerateArray());

        var published = await OkAsync(kofi.PostAsync($"{Weeks}/{Iso(week)}:publish", null));
        Assert.Equal("published", published.GetProperty("status").GetString());
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1L, await NotesAsync(t, "dana", TeamBeats.Modules.Hr.Rosters.Rosters.PublishedTemplate));
        Assert.Equal(1L, await NotesAsync(t, "eli", TeamBeats.Modules.Hr.Rosters.Rosters.PublishedTemplate));
        var mine = await OkAsync(t["eli"].Client.GetAsync($"/api/v1/me/shifts?from={Iso(week)}&to={Iso(week.AddDays(6))}"));
        Assert.Equal("14:00", mine[0].GetProperty("pattern").GetProperty("start").GetString());
        Assert.True(mine[1].GetProperty("dayOff").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await kofi.PostAsync($"{Weeks}/{Iso(week)}:publish", null)).StatusCode);

        // Eli's days change, Dana's do not: only Eli hears of it.
        await OkAsync(kofi.PutAsJsonAsync($"{Weeks}/{Iso(week)}",
            new { entries = new[] { Entry(t, "dana", week, early), Entry(t, "dana", week.AddDays(1), early), Entry(t, "eli", week, early), Entry(t, "eli", week.AddDays(1), null) } }));
        Assert.True((await OkAsync(kofi.GetAsync($"{Weeks}/{Iso(week)}"))).GetProperty("changed").GetBoolean());
        await OkAsync(kofi.PostAsync($"{Weeks}/{Iso(week)}:publish", null));
        await world.DispatchTenantAsync(t.Id);
        Assert.Equal(1L, await NotesAsync(t, "eli", TeamBeats.Modules.Hr.Rosters.Rosters.ChangedTemplate));
        Assert.Equal(0L, await NotesAsync(t, "dana", TeamBeats.Modules.Hr.Rosters.Rosters.ChangedTemplate));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.HrRosterChanged)));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.HrRosterPublished)));

        // HR officers of the unit see the team's week; another team's manager and another unit's HR officer do not.
        var hrView = await OkAsync(t["hr_gulf"].Client.GetAsync($"{Weeks}/{Iso(week)}?managerId={t["kofi"].Id}"));
        Assert.False(hrView.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await t["lead"].Client.GetAsync($"{Weeks}/{Iso(week)}?managerId={t["kofi"].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync($"{Weeks}/{Iso(week)}?managerId={t["kofi"].Id}")).StatusCode);
        // An overlapping week of the same manager is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await kofi.PutAsJsonAsync($"{Weeks}/{Iso(week.AddDays(3))}", new { entries = Array.Empty<object>() })).StatusCode);
    }

    [Fact]
    public async Task Attendance_judges_a_rostered_day_against_its_shift()
    {
        var t = await FreshAsync();
        var lateShift = (await OkAsync(PatternAsync(t["admin"].Client, "Late", "14:00", "22:00"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var zone = await world.Factory.InScopeAsync(t.Id, async sp =>
            TimeZoneInfo.FindSystemTimeZoneById((await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync()).TimeZone));
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
        // A past week starting on a Monday, so its first day is a working weekday and the sixth a Saturday.
        var monday = today.AddDays(-14 - (((int)today.DayOfWeek + 6) % 7));
        await OkAsync(t["kofi"].Client.PutAsJsonAsync($"{Weeks}/{Iso(monday)}",
            new { entries = new[] { Entry(t, "dana", monday, lateShift), Entry(t, "dana", monday.AddDays(1), null), Entry(t, "dana", monday.AddDays(5), lateShift) } }));
        await OkAsync(t["kofi"].Client.PostAsync($"{Weeks}/{Iso(monday)}:publish", null));
        DateTimeOffset At(DateOnly d, int h, int m)
        {
            var local = d.ToDateTime(new TimeOnly(h, m));
            return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        }
        foreach (var (at, direction) in new[] { (At(monday, 14, 5), "in"), (At(monday, 21, 55), "out") })
        {
            await world.Database.OwnerExecInTenantAsync(t.Id,
                "insert into hr.attendance_punch (tenant_id, id, user_id, at, direction, source, remote, created_at) values (@t, gen_random_uuid(), @u, @at, @d, 'web', false, now())",
                ("t", t.Id), ("u", t["dana"].Id), ("at", at), ("d", direction));
        }
        var months = new[] { monday, monday.AddDays(5) }.Select(d => d.ToString("yyyy-MM", CultureInfo.InvariantCulture)).Distinct();
        var days = new List<JsonElement>();
        foreach (var month in months)
        {
            days.AddRange((await OkAsync(t["dana"].Client.GetAsync($"/api/v1/me/attendance?month={month}"))).GetProperty("days").EnumerateArray());
        }
        JsonElement Day(DateOnly d) => days.Single(x => x.GetProperty("date").GetString() == Iso(d));
        string[] Flags(JsonElement day) => day.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
        // On time for the 14:00 shift, though the default rule starts at 09:00; the break comes off the expected hours.
        Assert.Empty(Flags(Day(monday)));
        Assert.Equal(7.5m, Day(monday).GetProperty("expectedHours").GetDecimal());
        // A day off on the roster is not an absence; a shift on Saturday is a working day, so missing it is.
        Assert.Equal(AttendanceDayKinds.DayOff, Day(monday.AddDays(1)).GetProperty("kind").GetString());
        Assert.Empty(Flags(Day(monday.AddDays(1))));
        Assert.Contains(AttendanceFlags.Absent, Flags(Day(monday.AddDays(5))));
        // Eli has no shift that week: the working week still applies, so Tuesday is an absence.
        var eli = await OkAsync(t["eli"].Client.GetAsync($"/api/v1/me/attendance?month={monday.ToString("yyyy-MM", CultureInfo.InvariantCulture)}"));
        Assert.Contains(AttendanceFlags.Absent, eli.GetProperty("days").EnumerateArray().Single(x => x.GetProperty("date").GetString() == Iso(monday.AddDays(1)))
            .GetProperty("flags").EnumerateArray().Select(f => f.GetString()!));
    }

    [Fact]
    public async Task Rosters_need_the_plan_and_stay_in_their_tenant()
    {
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["kofi"].Client.GetAsync($"{Weeks}/2027-03-01")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["dana"].Client.GetAsync("/api/v1/me/shifts")).StatusCode);

        var t = await FreshAsync();
        var early = (await OkAsync(PatternAsync(t["admin"].Client, "Early", "06:00", "14:00"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var other = await FreshAsync();
        // Another tenant's pattern is unknown here, and its manager is not found.
        var foreign = await other["kofi"].Client.PutAsJsonAsync($"{Weeks}/2027-03-01", new { entries = new[] { Entry(other, "dana", new DateOnly(2027, 3, 1), early) } });
        Assert.Contains("validation.hr.roster_pattern", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await other["hr_gulf"].Client.GetAsync($"{Weeks}/2027-03-01?managerId={t["kofi"].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PutAsJsonAsync($"{Patterns}/{early}",
            new { names = new Dictionary<string, string> { ["en"] = "Mine" }, start = "06:00", end = "14:00", breakMinutes = 0, tone = "info" })).StatusCode);
    }
}
