using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Attendance;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 slice 4, part 1 (US3 scenarios 1, 2 and 5, FR-010, FR-012): web sign in and out with the optional location
/// check, clock data from spec 017, working hours rules per unit, days with hours and exceptions, the team view and the
/// payroll export. The person, their manager and HR officers covering them see a person's month; a sibling unit's
/// officer and other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AttendanceTests(TenantWorld world)
{
    private const string Settings = "/api/v1/hr/attendance-settings";
    private const string Rules = "/api/v1/hr/work-rules";
    private const string Punches = "/api/v1/me/attendance/punches";

    // Two sites a few kilometres apart in Dubai.
    private static readonly object Office = new { name = "Head office", latitude = 25.2048, longitude = 55.2708, radiusMeters = 200 };

    private async Task<(TestTenant T, Guid Gulf, Guid Egypt)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("at-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "kofi", "rana", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        foreach (var name in new[] { "nadia", "hr_egypt" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Egypt");
        }
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["mona"].Id}/manager", new { managerId = t["kofi"].Id })).StatusCode);
        return (t, gulf, egypt);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(v => v.GetProperty("key").GetString()!)).ToArray();
        }
        return body.TryGetProperty("detailKey", out var detail) ? [detail.GetString()!] : [body.GetProperty("code").GetString()!];
    }

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body, string? etag)
    {
        var put = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            put.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        return await client.SendAsync(put);
    }

    private static async Task SetAsync(TestTenant t, object body)
    {
        var current = await t["admin"].Client.GetAsync(Settings);
        await OkAsync(PutAsync(t["admin"].Client, Settings, body, current.Headers.ETag!.Tag));
    }

    private Task<TimeZoneInfo> ZoneAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
            TimeZoneInfo.FindSystemTimeZoneById((await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync()).TimeZone));

    /// <summary>The Monday to Friday days of last month, so every day is in the past.</summary>
    private static (string Month, List<DateOnly> Weekdays) LastMonth(TimeZoneInfo zone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        var days = new List<DateOnly>();
        for (var d = first; d.Month == first.Month; d = d.AddDays(1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                days.Add(d);
            }
        }
        return (first.ToString("yyyy-MM"), days);
    }

    private static DateTimeOffset At(DateOnly date, int hour, int minute, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(hour, minute));
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private Task PunchAsync(TestTenant t, string user, DateTimeOffset at, string direction) =>
        world.Database.OwnerExecInTenantAsync(t.Id,
            "insert into hr.attendance_punch (tenant_id, id, user_id, at, direction, source, remote, created_at) values (@t, gen_random_uuid(), @u, @at, @d, 'web', false, now())",
            ("t", t.Id), ("u", t[user].Id), ("at", at), ("d", direction));

    private static JsonElement Day(JsonElement month, DateOnly date) =>
        month.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == date.ToString("yyyy-MM-dd"));

    private static string[] Flags(JsonElement day) => day.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();

    [Fact]
    public async Task Web_sign_in_is_off_until_a_tenant_wide_HR_officer_turns_it_on_and_the_location_check_flags_or_refuses_outside_the_sites()
    {
        var (t, _, _) = await FreshAsync();
        var mona = t["mona"].Client;
        var off = await mona.PostAsJsonAsync(Punches, new { direction = "in" });
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Contains("conflict.attendance.web_off", await ErrorKeysAsync(off));

        // A unit's HR officer reads the setting but cannot change it; a site is needed before the location check.
        var unit = await OkAsync(t["hr_gulf"].Client.GetAsync(Settings));
        Assert.False(unit.GetProperty("canChange").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(t["hr_gulf"].Client, Settings, new { webSignIn = true }, "\"v0\"")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mona.GetAsync(Settings)).StatusCode);
        var noSite = await PutAsync(t["admin"].Client, Settings, new { webSignIn = true, locationMode = "refuse" }, "\"v0\"");
        Assert.Contains("validation.attendance.sites_needed", await ErrorKeysAsync(noSite));
        var stale = await PutAsync(t["admin"].Client, Settings, new { webSignIn = true }, "\"v9\"");
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        await SetAsync(t, new { webSignIn = true, locationMode = "refuse", sites = new[] { Office } });
        var noLocation = await mona.PostAsJsonAsync(Punches, new { direction = "in" });
        Assert.Contains("validation.attendance.location_needed", await ErrorKeysAsync(noLocation));
        var outside = await mona.PostAsJsonAsync(Punches, new { direction = "in", latitude = 25.10, longitude = 55.17 });
        Assert.Equal(HttpStatusCode.Conflict, outside.StatusCode);
        Assert.Contains("conflict.attendance.outside_sites", await ErrorKeysAsync(outside));
        var inside = await OkAsync(mona.PostAsJsonAsync(Punches, new { direction = "in", latitude = 25.2049, longitude = 55.2709 }), HttpStatusCode.Created);
        Assert.False(inside.GetProperty("remote").GetBoolean());
        var soon = await mona.PostAsJsonAsync(Punches, new { direction = "out", latitude = 25.2049, longitude = 55.2709 });
        Assert.Contains("conflict.attendance.too_soon", await ErrorKeysAsync(soon));

        // Flag: outside every site is recorded as remote. The position itself is never stored.
        await SetAsync(t, new { webSignIn = true, locationMode = "flag", sites = new[] { Office } });
        var rana = await OkAsync(t["rana"].Client.PostAsJsonAsync(Punches, new { direction = "in", latitude = 25.10, longitude = 55.17 }), HttpStatusCode.Created);
        Assert.True(rana.GetProperty("remote").GetBoolean());
        var mine = await OkAsync(t["rana"].Client.GetAsync("/api/v1/me/attendance"));
        Assert.Equal("in", mine.GetProperty("lastPunch").GetProperty("direction").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from information_schema.columns where table_schema = 'hr' and table_name = 'attendance_punch' and column_name in ('latitude', 'longitude')"));
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.HrAttendanceSettingsChanged)));
    }

    [Fact]
    public async Task Each_day_shows_hours_and_exceptions_against_the_nearest_working_hours_rule()
    {
        var (t, gulf, _) = await FreshAsync();
        var zone = await ZoneAsync(t);
        var (month, days) = LastMonth(zone);
        // Late and early against the default rule (09:00 to 17:00, 15 minutes grace); a sign in with no sign out; an absent day.
        await PunchAsync(t, "mona", At(days[0], 9, 40, zone), "in");
        await PunchAsync(t, "mona", At(days[0], 16, 0, zone), "out");
        await PunchAsync(t, "mona", At(days[1], 8, 55, zone), "in");
        await PunchAsync(t, "mona", At(days[2], 9, 0, zone), "in");
        await PunchAsync(t, "mona", At(days[2], 17, 5, zone), "out");

        var view = await OkAsync(t["mona"].Client.GetAsync($"/api/v1/me/attendance?month={month}"));
        var first = Day(view, days[0]);
        Assert.Equal(6.33m, first.GetProperty("hoursWorked").GetDecimal());
        Assert.Equal(new[] { AttendanceFlags.Late, AttendanceFlags.EarlyLeave }, Flags(first));
        Assert.Equal(new[] { AttendanceFlags.MissingPunch }, Flags(Day(view, days[1])));
        Assert.Empty(Flags(Day(view, days[2])));
        Assert.Contains(AttendanceFlags.Absent, Flags(Day(view, days[3])));
        Assert.Equal("day_off", view.GetProperty("days").EnumerateArray().First(d => DateOnly.Parse(d.GetProperty("date").GetString()!).DayOfWeek == DayOfWeek.Saturday)
            .GetProperty("kind").GetString());
        var totals = view.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("late").GetInt32());
        Assert.Equal(days.Count - 3, totals.GetProperty("absent").GetInt32());

        // A rule for Gulf that starts at 10:00 makes the 09:40 arrival on time.
        var bad = await PutAsync(t["hr_gulf"].Client, Rules, new { orgUnitId = gulf, start = "18:00", end = "09:00", graceLateMinutes = 0, graceEarlyMinutes = 0 }, null);
        Assert.Contains("validation.attendance.hours", await ErrorKeysAsync(bad));
        Assert.Equal(HttpStatusCode.Forbidden, (await PutAsync(t["hr_gulf"].Client, Rules,
            new { orgUnitId = (Guid?)null, start = "08:00", end = "16:00", graceLateMinutes = 0, graceEarlyMinutes = 0 }, null)).StatusCode);
        var rule = await OkAsync(PutAsync(t["hr_gulf"].Client, Rules, new { orgUnitId = gulf, start = "10:00", end = "16:00", graceLateMinutes = 0, graceEarlyMinutes = 0 }, null));
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(t["hr_gulf"].Client, Rules,
            new { orgUnitId = gulf, start = "10:00", end = "16:00", graceLateMinutes = 5, graceEarlyMinutes = 0 }, "\"v7\"")).StatusCode);
        Assert.Equal(1, rule.GetProperty("version").GetInt32());
        var again = await OkAsync(t["mona"].Client.GetAsync($"/api/v1/me/attendance?month={month}"));
        Assert.Empty(Flags(Day(again, days[0])));
        Assert.Equal("10:00", again.GetProperty("rule").GetProperty("start").GetString());
        Assert.Empty((await OkAsync(t["hr_egypt"].Client.GetAsync(Rules))).EnumerateArray());
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a and target_id = @id",
            ("a", AuditActions.HrWorkRuleChanged), ("id", rule.GetProperty("id").GetGuid().ToString())));
    }

    [Fact]
    public async Task The_person_their_manager_and_HR_covering_them_see_the_month_and_HR_exports_it_for_payroll()
    {
        var (t, _, _) = await FreshAsync();
        var zone = await ZoneAsync(t);
        var (month, days) = LastMonth(zone);
        await PunchAsync(t, "mona", At(days[0], 9, 0, zone), "in");
        await PunchAsync(t, "mona", At(days[0], 17, 0, zone), "out");
        await world.Database.OwnerExecInTenantAsync(t.Id, "update identity.user_account set display_name = '=HYPERLINK(1)' where id = @u", ("u", t["rana"].Id));

        var url = $"/api/v1/hr/attendance/people/{t["mona"].Id}?month={month}";
        Assert.Equal(8m, Day(await OkAsync(t["kofi"].Client.GetAsync(url)), days[0]).GetProperty("hoursWorked").GetDecimal());
        Assert.False((await OkAsync(t["kofi"].Client.GetAsync(url))).GetProperty("mine").GetBoolean());
        await OkAsync(t["hr_gulf"].Client.GetAsync(url));
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["rana"].Client.GetAsync(url)).StatusCode);
        var other = await world.CreateTenantAsync("at-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(url)).StatusCode);

        var team = await OkAsync(t["kofi"].Client.GetAsync($"/api/v1/hr/attendance/team?month={month}"));
        var row = Assert.Single(team.GetProperty("people").EnumerateArray());
        Assert.Equal(t["mona"].Id, row.GetProperty("person").GetProperty("id").GetGuid());
        Assert.Equal(8m, row.GetProperty("totals").GetProperty("hoursWorked").GetDecimal());

        var export = await t["hr_gulf"].Client.GetAsync($"/api/v1/hr/attendance/export?month={month}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        var csv = Encoding.UTF8.GetString(await export.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("user_id,name,worked_days,hours_worked", lines[0]);
        Assert.Contains(lines, l => l.StartsWith(t["mona"].Id.ToString()) && l.Contains(",1,8,"));
        Assert.Contains(lines, l => l.Contains("\"'=HYPERLINK(1)\""));
        Assert.DoesNotContain(lines, l => l.StartsWith(t["nadia"].Id.ToString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["mona"].Client.GetAsync($"/api/v1/hr/attendance/export?month={month}")).StatusCode);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", AuditActions.HrAttendanceExported)));
        var badMonth = await t["mona"].Client.GetAsync("/api/v1/me/attendance?month=2026-13");
        Assert.Contains("validation.attendance.month", await ErrorKeysAsync(badMonth));
    }

    [Fact]
    public async Task Clock_data_from_a_time_system_becomes_punches_once_and_pairs_unknown_directions_by_time()
    {
        var (t, _, _) = await FreshAsync();
        var zone = await ZoneAsync(t);
        var (month, days) = LastMonth(zone);
        var connection = Guid.NewGuid();
        var file = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @c, 'clock_sftp', 'Clock', 'healthy', true, 600);
            insert into integrations.clock_punch (tenant_id, connection_id, punch_id, user_id, at, direction, file_id, imported_at)
            values (@t, @c, 'P1', @u, @a, 'unknown', @f, now()), (@t, @c, 'P2', @u, @b, 'unknown', @f, now())
            """, ("t", t.Id), ("c", connection), ("u", t["mona"].Id), ("a", At(days[4], 8, 50, zone)), ("b", At(days[4], 17, 10, zone)), ("f", file));
        async Task ImportAsync() => await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ClockDataHandler>().HandleAsync(new ClockDataImported(connection, file, 2, 0, 0, null, null), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        await ImportAsync();
        await ImportAsync();
        Assert.Equal(2L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.attendance_punch where source = 'clock'"));
        var day = Day(await OkAsync(t["mona"].Client.GetAsync($"/api/v1/me/attendance?month={month}")), days[4]);
        Assert.Equal("08:50", day.GetProperty("firstIn").GetString());
        Assert.Equal("17:10", day.GetProperty("lastOut").GetString());
        Assert.Empty(Flags(day));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(
            (await world.CreateTenantAsync("at-c-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus")).Id, "select count(*) from hr.attendance_punch"));
    }
}
