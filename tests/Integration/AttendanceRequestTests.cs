using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Hr.Attendance;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 slice 4, part 2 (US3 scenarios 3 and 4, FR-010, FR-011): a correction request for a day routes to the line
/// manager, and on approval the day is corrected and marked; a compensation request for work on a day off credits the
/// leave type the tenant chose. Nobody decides their own request; a sibling unit's officer and other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AttendanceRequestTests(TenantWorld world)
{
    private const string Requests = "/api/v1/me/attendance/requests";
    private const string Settings = "/api/v1/hr/attendance-settings";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("ar-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "mona", "kofi", "hr_gulf" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.SeedUserAsync(t, "hr_egypt", MembershipTypes.Employee);
        await world.AddMembershipAsync(t, "hr_egypt", "Egypt");
        await world.AssignAsync(t, "hr_gulf", BuiltInRoles.HrOfficer, "org_subtree", gulf);
        await world.AssignAsync(t, "hr_egypt", BuiltInRoles.HrOfficer, "org_subtree", egypt);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["mona"].Id}/manager", new { managerId = t["kofi"].Id })).StatusCode);
        return t;
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

    private Task<TimeZoneInfo> ZoneAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
            TimeZoneInfo.FindSystemTimeZoneById((await sp.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync()).TimeZone));

    private static DateOnly Today(TimeZoneInfo zone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone));

    /// <summary>The most recent day of the given kind at least three days back, so it is in the past and inside the 60 days.</summary>
    private static DateOnly Back(TimeZoneInfo zone, Func<DayOfWeek, bool> fits)
    {
        var d = Today(zone).AddDays(-3);
        while (!fits(d.DayOfWeek))
        {
            d = d.AddDays(-1);
        }
        return d;
    }

    private static bool Weekday(DayOfWeek d) => d is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    private Task PunchAsync(TestTenant t, string user, DateOnly date, int hour, string direction, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(new TimeOnly(hour, 0));
        var at = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
        return world.Database.OwnerExecInTenantAsync(t.Id,
            "insert into hr.attendance_punch (tenant_id, id, user_id, at, direction, source, remote, created_at) values (@t, gen_random_uuid(), @u, @at, @d, 'web', false, now())",
            ("t", t.Id), ("u", t[user].Id), ("at", at), ("d", direction));
    }

    private static async Task<JsonElement> DayAsync(HttpClient client, DateOnly date)
    {
        var month = await OkAsync(client.GetAsync($"/api/v1/me/attendance?month={date:yyyy-MM}"));
        return month.GetProperty("days").EnumerateArray().Single(d => d.GetProperty("date").GetString() == date.ToString("yyyy-MM-dd"));
    }

    private static string[] Flags(JsonElement day) => day.GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();

    private static async Task<JsonElement?> TaskForAsync(HttpClient client, Guid requestId) =>
        (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray()
            .Where(i => i.GetProperty("source").GetString() == "attendance" && i.GetProperty("link").GetString()!.Contains(requestId.ToString(), StringComparison.Ordinal))
            .Cast<JsonElement?>().SingleOrDefault();

    private static async Task DecideAsync(HttpClient client, Guid requestId, string outcome)
    {
        var task = await TaskForAsync(client, requestId);
        Assert.NotNull(task);
        await OkAsync(client.PostAsJsonAsync($"/api/v1/tasks/{task.Value.GetProperty("taskId").GetGuid()}/decision", new { outcome, comment = outcome == "approve" ? null : "Not what the records show" }));
    }

    private Task<long> AuditCountAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    [Fact]
    public async Task A_missing_punch_is_corrected_by_the_manager_and_the_day_is_marked_corrected()
    {
        var t = await FreshAsync();
        var zone = await ZoneAsync(t);
        var day = Back(zone, Weekday);
        await PunchAsync(t, "mona", day, 9, "in", zone);
        Assert.Contains(AttendanceFlags.MissingPunch, Flags(await DayAsync(t["mona"].Client, day)));

        var bad = await t["mona"].Client.PostAsJsonAsync(Requests, new { kind = "correction", date = Today(zone).AddDays(1), reason = "", punches = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("validation.attendance.request_date", await ErrorKeysAsync(bad));
        Assert.Contains("validation.attendance.reason", await ErrorKeysAsync(bad));
        Assert.Contains("validation.attendance.punches", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync(Requests,
            new { kind = "correction", date = day, reason = "Forgot", punches = new[] { new { time = "25:00", direction = "out" } } })));
        Assert.Contains("validation.attendance.request_kind", await ErrorKeysAsync(await t["mona"].Client.PostAsJsonAsync(Requests,
            new { kind = "overtime", date = day, reason = "Forgot" })));

        var created = await OkAsync(t["mona"].Client.PostAsJsonAsync(Requests,
            new { kind = "correction", date = day, reason = "Forgot to sign out", punches = new[] { new { time = "17:00", direction = "out" } } }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("pending", created.GetProperty("status").GetString());
        Assert.True(created.GetProperty("rights").GetProperty("withdraw").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsJsonAsync(Requests,
            new { kind = "correction", date = day, reason = "Again", punches = new[] { new { time = "17:00", direction = "out" } } })).StatusCode);

        // The person never gets the task; a sibling unit's officer and another tenant see nothing.
        Assert.Null(await TaskForAsync(t["mona"].Client, id));
        var url = $"/api/v1/hr/attendance/requests/{id}";
        Assert.Equal(HttpStatusCode.NotFound, (await t["hr_egypt"].Client.GetAsync(url)).StatusCode);
        await OkAsync(t["hr_gulf"].Client.GetAsync(url));
        var other = await world.CreateTenantAsync("ar-o-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(url)).StatusCode);
        Assert.True((await OkAsync(t["kofi"].Client.GetAsync(url))).GetProperty("rights").GetProperty("decide").GetBoolean());

        await DecideAsync(t["kofi"].Client, id, "approve");
        var after = await DayAsync(t["mona"].Client, day);
        Assert.Equal(8m, after.GetProperty("hoursWorked").GetDecimal());
        Assert.DoesNotContain(AttendanceFlags.MissingPunch, Flags(after));
        Assert.Contains(AttendanceFlags.Corrected, Flags(after));
        Assert.Equal("approved", (await OkAsync(t["mona"].Client.GetAsync(url))).GetProperty("status").GetString());
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrAttendanceCorrected));
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrAttendanceRequested));
        Assert.Equal(1L, await world.Database.ScalarInTenantAsync<long>(t.Id,
            "select count(*) from messaging.outbox where tenant_id = @t and message_type like '%AttendanceCorrected%'", ("t", t.Id)));

        // A rejected request changes nothing; a withdrawn one can be asked again.
        var second = day.AddDays(-7);
        var rejected = (await OkAsync(t["mona"].Client.PostAsJsonAsync(Requests,
            new { kind = "correction", date = second, reason = "Clock was down", punches = new[] { new { time = "09:00", direction = "in" }, new { time = "17:00", direction = "out" } } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await DecideAsync(t["kofi"].Client, rejected, "reject");
        Assert.Equal(0m, (await DayAsync(t["mona"].Client, second)).GetProperty("hoursWorked").GetDecimal());
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrAttendanceRequestRejected));
        var withdrawn = (await OkAsync(t["mona"].Client.PostAsJsonAsync(Requests,
            new { kind = "correction", date = second, reason = "Clock was down", punches = new[] { new { time = "09:00", direction = "in" } } }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["kofi"].Client.PostAsync($"/api/v1/hr/attendance/requests/{withdrawn}:withdraw", null)).StatusCode);
        Assert.Equal("withdrawn", (await OkAsync(t["mona"].Client.PostAsync($"/api/v1/hr/attendance/requests/{withdrawn}:withdraw", null))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsync($"/api/v1/hr/attendance/requests/{withdrawn}:withdraw", null)).StatusCode);
        Assert.Null(await TaskForAsync(t["kofi"].Client, withdrawn));
        Assert.Equal(3, (await OkAsync(t["mona"].Client.GetAsync(Requests))).GetArrayLength());
    }

    [Fact]
    public async Task Work_on_a_day_off_is_credited_as_a_compensation_day_after_the_manager_approves()
    {
        var t = await FreshAsync();
        var zone = await ZoneAsync(t);
        var saturday = Back(zone, d => d == DayOfWeek.Saturday);
        var weekday = Back(zone, Weekday);
        await PunchAsync(t, "mona", saturday, 9, "in", zone);
        await PunchAsync(t, "mona", saturday, 17, "out", zone);

        var off = await t["mona"].Client.PostAsJsonAsync(Requests, new { kind = "compensation", date = saturday, reason = "Release weekend" });
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Contains("conflict.attendance.compensation_off", await ErrorKeysAsync(off));

        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/hr/leave/types/comp",
            new { names = new Dictionary<string, string> { ["en"] = "Compensation", ["ar"] = "تعويض" } }));
        var current = await t["admin"].Client.GetAsync(Settings);
        var put = new HttpRequestMessage(HttpMethod.Put, Settings)
        {
            Content = JsonContent.Create(new { webSignIn = false, locationMode = "off", sites = Array.Empty<object>(), compensationTypeKey = "nope" }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        Assert.Contains("validation.attendance.compensation_type", await ErrorKeysAsync(await t["admin"].Client.SendAsync(put)));
        put = new HttpRequestMessage(HttpMethod.Put, Settings)
        {
            Content = JsonContent.Create(new { webSignIn = false, locationMode = "off", sites = Array.Empty<object>(), compensationTypeKey = "comp" }),
        };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        Assert.Equal("comp", (await OkAsync(t["admin"].Client.SendAsync(put))).GetProperty("compensationTypeKey").GetString());

        var none = await t["mona"].Client.PostAsJsonAsync(Requests, new { kind = "compensation", date = weekday, reason = "Worked" });
        Assert.Contains("conflict.attendance.no_compensation", await ErrorKeysAsync(none));

        var created = await OkAsync(t["mona"].Client.PostAsJsonAsync(Requests, new { kind = "compensation", date = saturday, reason = "Release weekend" }), HttpStatusCode.Created);
        Assert.Equal(1m, created.GetProperty("days").GetDecimal());
        Assert.Equal("comp", created.GetProperty("leaveTypeKey").GetString());
        var id = created.GetProperty("id").GetGuid();
        Assert.Contains(AttendanceFlags.WorkedDayOff, Flags(await DayAsync(t["mona"].Client, saturday)));

        await DecideAsync(t["kofi"].Client, id, "approve");
        Assert.Equal(1m, await world.Database.TenantScalarAsync<decimal>(t.Id,
            "select coalesce(sum(delta), 0) from hr.leave_ledger where reason = 'compensation' and user_id = @u", ("u", t["mona"].Id)));
        Assert.Equal(1L, await AuditCountAsync(t, AuditActions.HrCompensationCredited));
        Assert.Equal(HttpStatusCode.Conflict, (await t["mona"].Client.PostAsJsonAsync(Requests, new { kind = "compensation", date = saturday, reason = "Again" })).StatusCode);
    }
}
