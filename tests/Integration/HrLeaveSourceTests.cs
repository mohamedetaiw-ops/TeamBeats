using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Integrations.Hr;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 T013 (US7, FR-013) with spec 022 T015 (FR-006): the HR administrator makes HR the leave balance source.
/// Balances are then read live from HR, less what TeamBeats holds, and the last known balance is shown with its time
/// when HR is down, where a request cannot go in unless the tenant allows it. Approval and cancellation go to HR as
/// durable hand-offs and never touch the ledger; a refused hand-off shows on the HR integration page, where it is sent
/// again. Adjustments and accrual stop. People without a matched record are told so. The setting needs
/// <c>hr.integration_manage</c> and the plan feature, and other tenants see none of it.
/// </summary>
public sealed partial class HrSyncTests
{
    private const string LeaveApi = "/api/v1/hr/leave";

    private async Task<(TestTenant T, FakeHr Hr)> LeaveTenantAsync()
    {
        var t = await TenantAsync(people: ["sara", "omar", "layla"]);
        await world.CreateUnitAsync(t, "Finance", null, "organization");
        foreach (var name in new[] { "sara", "omar", "layla" })
        {
            await world.AddMembershipAsync(t, name, "Finance");
        }
        foreach (var name in new[] { "sara", "layla" })
        {
            Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[name].Id}/manager", new { managerId = t["omar"].Id })).StatusCode);
        }
        var (_, hr) = await RestConnectionAsync(t, leave: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100"), Person("R2", t["omar"].Email, "E-200")];
        await SyncAsync(t);
        var type = await t["admin"].Client.PutAsJsonAsync($"{LeaveApi}/types/annual",
            new { names = new Dictionary<string, string> { ["en"] = "Annual leave", ["ar"] = "إجازة سنوية" }, sensitive = false });
        Assert.True(type.StatusCode == HttpStatusCode.OK, await type.Content.ReadAsStringAsync());
        return (t, hr);
    }

    private static async Task<HttpResponseMessage> PutSourceAsync(HttpClient client, string? ifMatch, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{Hr}/leave-source") { Content = JsonContent.Create(body) };
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }
        return await client.SendAsync(request);
    }

    private static async Task UseHrAsync(TestTenant t, bool allowStale = false)
    {
        var current = await t["admin"].Client.GetAsync($"{Hr}/leave-source");
        var changed = await PutSourceAsync(t["admin"].Client, current.Headers.ETag!.Tag, new { source = "hr", allowStaleSubmit = allowStale });
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync());
    }

    /// <summary>A Monday to Friday week a few weeks ahead, inside one calendar year.</summary>
    private static (DateOnly Mon, DateOnly Fri) LeaveWeek(int weeksAhead)
    {
        var d = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14 + 7 * weeksAhead);
        while (d.DayOfWeek != DayOfWeek.Monday)
        {
            d = d.AddDays(1);
        }
        while (d.Year != d.AddDays(4).Year)
        {
            d = d.AddDays(7);
        }
        return (d, d.AddDays(4));
    }

    private static async Task<JsonElement> PreviewAsync(HttpClient client, DateOnly start, DateOnly end)
    {
        var response = await client.PostAsJsonAsync($"{LeaveApi}/preview", new { typeKey = "annual", start, end });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.ReadAsync<JsonElement>();
    }

    private static string[] Problems(JsonElement preview) => preview.GetProperty("problems").EnumerateArray().Select(p => p.GetProperty("key").GetString()!).ToArray();

    private static async Task<Guid> ApprovedLeaveAsync(TestTenant t, DateOnly start, DateOnly end)
    {
        var submitted = await t["sara"].Client.PostAsJsonAsync($"{LeaveApi}/requests", new { typeKey = "annual", start, end });
        Assert.True(submitted.StatusCode == HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var id = (await submitted.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        var task = (await (await t["omar"].Client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray()
            .Single(i => i.GetProperty("source").GetString() == "leave" && i.GetProperty("link").GetString()!.Contains(id.ToString(), StringComparison.Ordinal));
        var decided = await t["omar"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" });
        Assert.True(decided.IsSuccessStatusCode, await decided.Content.ReadAsStringAsync());
        return id;
    }

    /// <summary>Runs the due connector jobs and delivers what they publish, as the worker does.</summary>
    private async Task RunJobsAsync(TestTenant t, TimeSpan? later = null)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var now = DateTimeOffset.UtcNow + (later ?? TimeSpan.FromSeconds(1));
            await sp.GetRequiredService<HrLeave>().RetryDueAsync(now, 20, CancellationToken.None);
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(now, 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });
        await world.DispatchTenantAsync(t.Id);
    }

    private static async Task<JsonElement> RequestAsync(HttpClient client, Guid id) => await (await client.GetAsync($"{LeaveApi}/requests/{id}")).ReadAsync<JsonElement>();

    private static async Task<JsonElement> AnnualBalanceAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/me/leave")).ReadAsync<JsonElement>()).GetProperty("balances").EnumerateArray()
            .Single(b => b.GetProperty("typeKey").GetString() == "annual");

    [Fact]
    public async Task With_HR_as_the_source_balances_come_from_HR_and_approved_leave_is_handed_to_HR_not_the_ledger()
    {
        var (t, hr) = await LeaveTenantAsync();
        hr.Leave["R1"] = [new() { ["code"] = "AL", ["left"] = 12 }, new() { ["code"] = "XX-unknown", ["left"] = 3 }];

        // By default TeamBeats keeps balances; the HR administrator changes that with the current version only.
        var initial = await t["admin"].Client.GetAsync($"{Hr}/leave-source");
        var setting = await initial.ReadAsync<JsonElement>();
        Assert.Equal(("teambeats", false), (setting.GetProperty("source").GetString(), setting.GetProperty("fromHr").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, (await PutSourceAsync(t["admin"].Client, "\"v9\"", new { source = "hr" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PutSourceAsync(t["admin"].Client, initial.Headers.ETag!.Tag, new { source = "payroll" })).StatusCode);
        await UseHrAsync(t);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveSourceChanged, "tenant");
        Assert.True((await (await t["admin"].Client.GetAsync($"{Hr}/leave-source")).ReadAsync<JsonElement>()).GetProperty("fromHr").GetBoolean());

        // The balance is HR's, read live, under HR's code mapped back to the TeamBeats type.
        var balance = await AnnualBalanceAsync(t["sara"].Client);
        Assert.Equal(("hr", 12m, 12m, false), (balance.GetProperty("source").GetString(), balance.GetProperty("balance").GetDecimal(),
            balance.GetProperty("available").GetDecimal(), balance.GetProperty("stale").GetBoolean()));
        var (mon, fri) = LeaveWeek(1);
        var preview = await PreviewAsync(t["sara"].Client, mon, fri);
        Assert.Equal(("hr", 12m, 7m), (preview.GetProperty("balanceSource").GetString(), preview.GetProperty("years")[0].GetProperty("available").GetDecimal(),
            preview.GetProperty("years")[0].GetProperty("after").GetDecimal()));
        Assert.Empty(Problems(preview));

        // Approval hands the leave to HR, keyed by request and change, and writes no ledger row.
        var id = await ApprovedLeaveAsync(t, mon, fri);
        Assert.Equal("queued", (await RequestAsync(t["sara"].Client, id)).GetProperty("hrState").GetString());
        Assert.Equal(7m, (await AnnualBalanceAsync(t["sara"].Client)).GetProperty("available").GetDecimal());
        await RunJobsAsync(t);
        var sent = Assert.Single(hr.LeaveSent);
        Assert.Equal(("AL", "approved", "R1", "E-100", 5m), (sent.Body.GetProperty("type").GetString(), sent.Body.GetProperty("kind").GetString(),
            sent.Body.GetProperty("record_id").GetString(), sent.Body.GetProperty("employee_number").GetString(), sent.Body.GetProperty("days").GetDecimal()));
        Assert.Equal($"teambeats-leave-{id}-1", sent.Key);
        var recorded = await RequestAsync(t["sara"].Client, id);
        Assert.Equal("recorded", recorded.GetProperty("hrState").GetString());
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.leave_ledger where request_id = @id", ("id", id)));

        // Once HR recorded it, HR's balance already takes it, so it is not taken twice.
        hr.Leave["R1"] = [new() { ["code"] = "AL", ["left"] = 7 }];
        Assert.Equal(7m, (await AnnualBalanceAsync(t["sara"].Client)).GetProperty("available").GetDecimal());

        // A cancellation goes to HR as the next change of the same request.
        var cancelled = await t["sara"].Client.PostAsync($"{LeaveApi}/requests/{id}:cancel", null);
        Assert.True(cancelled.StatusCode == HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync());
        await RunJobsAsync(t);
        Assert.Equal(2, hr.LeaveSent.Count);
        Assert.Equal(("cancelled", "2"), (hr.LeaveSent[1].Body.GetProperty("kind").GetString(), hr.LeaveSent[1].Body.GetProperty("change").GetString()));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from hr.leave_ledger where request_id = @id", ("id", id)));

        // HR keeps balances: no adjustment and no accrual in TeamBeats.
        var adjust = await t["admin"].Client.PostAsJsonAsync($"{LeaveApi}/adjustments", new { userId = t["sara"].Id, typeKey = "annual", delta = 2, note = "Correction" });
        Assert.Equal(HttpStatusCode.Conflict, adjust.StatusCode);
        Assert.Contains("conflict.leave.hr_owns_balances", await adjust.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await world.Factory.InScopeAsync(t.Id, sp => sp.GetRequiredService<LeaveLedger>().RunAccrualAsync(default)));

        // Someone HR does not know cannot file leave against a balance nobody can read.
        Assert.Contains("validation.leave.hr_not_matched", Problems(await PreviewAsync(t["layla"].Client, mon, fri)));
    }

    [Fact]
    public async Task When_HR_is_down_the_last_known_balance_shows_and_a_refused_hand_off_is_sent_again_from_the_HR_page()
    {
        var (t, hr) = await LeaveTenantAsync();
        hr.Leave["R1"] = [new() { ["code"] = "AL", ["left"] = 10 }];
        await UseHrAsync(t);
        var (mon, fri) = LeaveWeek(2);
        Assert.Empty(Problems(await PreviewAsync(t["sara"].Client, mon, fri)));

        // HR down: the balance read last is shown with its time, and a request needs the tenant to allow stale balances.
        hr.Down = true;
        var stale = await AnnualBalanceAsync(t["sara"].Client);
        Assert.Equal((10m, true), (stale.GetProperty("balance").GetDecimal(), stale.GetProperty("stale").GetBoolean()));
        Assert.NotEqual(JsonValueKind.Null, stale.GetProperty("asOf").ValueKind);
        Assert.Contains("validation.leave.hr_unavailable", Problems(await PreviewAsync(t["sara"].Client, mon, fri)));
        await UseHrAsync(t, allowStale: true);
        Assert.Empty(Problems(await PreviewAsync(t["sara"].Client, mon, fri)));
        hr.Down = false;

        // HR refuses the leave: the hand-off fails and shows on the HR integration page.
        hr.LeaveAnswer = HttpStatusCode.UnprocessableEntity;
        var id = await ApprovedLeaveAsync(t, mon, fri);
        await RunJobsAsync(t);
        Assert.Equal("failed", (await RequestAsync(t["sara"].Client, id)).GetProperty("hrState").GetString());
        var handoff = (await (await t["admin"].Client.GetAsync($"{Hr}/leave-handoffs")).ReadAsync<JsonElement>()).EnumerateArray().Single(h => h.GetProperty("requestId").GetGuid() == id);
        Assert.Equal(("failed", "approved"), (handoff.GetProperty("state").GetString(), handoff.GetProperty("kind").GetString()));
        // Failed leave is still held against the balance, since HR has not taken it.
        Assert.Equal(5m, (await AnnualBalanceAsync(t["sara"].Client)).GetProperty("available").GetDecimal());

        // After it is fixed in HR, the HR administrator sends it again.
        hr.LeaveAnswer = null;
        var handoffId = handoff.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Accepted, (await t["admin"].Client.PostAsync($"{Hr}/leave-handoffs/{handoffId}:retry", null)).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrLeaveHandOffRetried, handoffId.ToString());
        await RunJobsAsync(t);
        Assert.Equal("recorded", (await RequestAsync(t["sara"].Client, id)).GetProperty("hrState").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PostAsync($"{Hr}/leave-handoffs/{handoffId}:retry", null)).StatusCode);

        // Other tenants see no hand-offs and cannot send them again; employees cannot read or change the setting.
        var other = await TenantAsync(people: "nadia");
        Assert.Empty((await (await other["admin"].Client.GetAsync($"{Hr}/leave-handoffs")).ReadAsync<JsonElement>()).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Hr}/leave-handoffs/{handoffId}:retry", null)).StatusCode);
        Assert.Equal("teambeats", (await (await other["admin"].Client.GetAsync($"{Hr}/leave-source")).ReadAsync<JsonElement>()).GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Hr}/leave-source")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutSourceAsync(t["sara"].Client, "\"v2\"", new { source = "teambeats" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Hr}/leave-handoffs")).StatusCode);
    }

    [Fact]
    public async Task Without_HR_integration_in_the_plan_HR_cannot_become_the_leave_source()
    {
        var t = await TenantAsync("workplace");
        var current = await t["admin"].Client.GetAsync($"{Hr}/leave-source");
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutSourceAsync(t["admin"].Client, current.Headers.ETag!.Tag, new { source = "hr" })).StatusCode);
    }

    [Fact]
    public async Task A_hand_off_with_no_usable_connection_waits_and_fails_after_three_days()
    {
        var (t, _) = await LeaveTenantAsync();
        await UseHrAsync(t);
        var requestId = Guid.NewGuid();
        var state = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<HrLeave>().HandOffAsync(new(requestId, t["layla"].Id, "approved", "annual",
                new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 2), false, false, 2m), 1, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });
        Assert.Equal("queued", state);
        Assert.Equal("not_matched", await world.Database.TenantScalarAsync<string>(t.Id, "select error_class from integrations.hr_leave_handoff where request_id = @id", ("id", requestId)));
        await RunJobsAsync(t, HrLeave.RetryWindow + TimeSpan.FromHours(1));
        Assert.Equal("failed", await world.Database.TenantScalarAsync<string>(t.Id, "select state from integrations.hr_leave_handoff where request_id = @id", ("id", requestId)));
    }
}
