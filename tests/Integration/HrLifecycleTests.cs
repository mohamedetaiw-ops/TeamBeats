using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Integrations.Hr;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 T006 (US2 to US4, FR-005 to FR-008, FR-018): after the baseline run, a mover's org unit, manager, title and
/// location change together once their org code is mapped, a leaver whose last day has passed is disabled at once with
/// sessions revoked, a notice period waits for the end of the last day, a rehire reopens the account only when the
/// tenant allows it, and a hire cancelled before it is due is dropped. Every applied change is audited with its run.
/// </summary>
public sealed partial class HrSyncTests
{
    private async Task<DateOnly> TodayAsync(TestTenant t) =>
        await world.Factory.InScopeAsync(t.Id, async sp => (await sp.GetRequiredService<IWorkingCalendar>().TenantDefaultAsync()).Today(DateTimeOffset.UtcNow));

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement[]> ChangesAsync(TestTenant t, string? status = null) =>
        (await (await t["admin"].Client.GetAsync($"{Hr}/changes" + (status is null ? "" : $"?status={status}"))).ReadAsync<JsonElement[]>())!;

    private async Task<string> MembershipStatusAsync(TestTenant t, string user) =>
        await world.Database.TenantScalarAsync<string>(t.Id, "select status from tenancy.tenant_membership where user_id = @u", ("u", t[user].Id)) ?? "";

    private static async Task PutSettingsAsync(TestTenant t, object body)
    {
        var etag = $"\"v{(await OverviewAsync(t)).GetProperty("settings").GetProperty("version").GetInt32()}\"";
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Hr}/settings") { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await t["admin"].Client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private async Task ApplyDueAsync(TestTenant t)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var applied = await sp.GetRequiredService<HrLifecycle>().ApplyDueAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return applied;
        });
        await DispatchAsync();
    }

    [Fact]
    public async Task Movers_wait_for_their_org_code_leavers_lose_access_on_time_and_rehires_reopen_only_when_allowed()
    {
        var other = await TenantAsync(people: "nadia");
        var otherUnit = await world.CreateUnitAsync(other, "ELSEWHERE", null, "department");
        var t = await TenantAsync(people: ["sara", "omar", "layla", "huda"]);
        var finance = await world.CreateUnitAsync(t, "FINANCE", null, "department");
        var procurement = await world.CreateUnitAsync(t, "PROCUREMENT", null, "department");
        var today = await TodayAsync(t);
        var (_, hr) = await RestConnectionAsync(t);
        // Two of four people change in one run below; the threshold is not what this test is about.
        await PutSettingsAsync(t, new { changeThresholdPercent = 100 });
        hr.Employees =
        [
            Person("R1", t["sara"].Email, "E-100"),
            Person("R2", t["omar"].Email, "E-200", manager: "E-100"),
            Person("R3", t["layla"].Email, "E-300"),
            Person("R4", t["huda"].Email, "E-400"),
        ];

        // The first run is the baseline: it matches and fills profiles, and plans no lifecycle change.
        await SyncAsync(t);
        Assert.Empty(await ChangesAsync(t));
        var codes = await (await t["admin"].Client.GetAsync($"{Hr}/org-codes")).ReadAsync<JsonElement[]>();
        Assert.Equal(("FIN", JsonValueKind.Null, 4), (codes[0].GetProperty("code").GetString(), codes[0].GetProperty("unitId").ValueKind, codes[0].GetProperty("people").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"{Hr}/org-codes/FIN", new { unitId = finance })).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrOrgCodeMapped, "FIN");
        // A unit of another tenant is not a unit here.
        var foreign = await t["admin"].Client.PutAsJsonAsync($"{Hr}/org-codes/FIN", new { unitId = otherUnit });
        Assert.Contains("validation.hr.org_unit", await foreign.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // US3: Omar moves to an unmapped code with a new manager and title. Only his change waits (scenario 3).
        // US4: Layla's last day has passed (immediate); Huda works out a notice period.
        hr.Employees[1] = Person("R2", t["omar"].Email, "E-200", title: "Buyer", dept: "PROC", manager: "E-400");
        hr.Employees[2] = Person("R3", t["layla"].Email, "E-300", status: "Left");
        hr.Employees[2]["left"] = Iso(today.AddDays(-1));
        hr.Employees[3]["left"] = Iso(today.AddDays(10));
        var overview = await SyncAsync(t);
        Assert.Equal("applied", overview.GetProperty("lastRun").GetProperty("status").GetString());

        var waiting = await ChangesAsync(t, "unmapped");
        Assert.Equal(("R2", "assignment", t["omar"].Id), (waiting.Single().GetProperty("recordId").GetString(), waiting.Single().GetProperty("kind").GetString(),
            waiting.Single().GetProperty("userId").GetGuid()));
        Assert.Equal("Accountant", (await OwnFieldsAsync(t["omar"]))["jobTitle"].GetProperty("value").GetString());

        Assert.Equal("disabled", await MembershipStatusAsync(t, "layla"));
        Assert.Contains((await t["layla"].Client.GetAsync("/api/v1/me/profile")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DisabledByHr, t["layla"].Id.ToString());
        var leaving = (await ChangesAsync(t, "pending")).Single();
        Assert.Equal(("R4", "termination", Iso(today.AddDays(10))), (leaving.GetProperty("recordId").GetString(), leaving.GetProperty("kind").GetString(),
            leaving.GetProperty("effectiveDate").GetString()));
        Assert.Equal("active", await MembershipStatusAsync(t, "huda"));

        // Mapping the code applies the whole move at once: unit, manager, title (scenario 1), with its run (FR-018).
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"{Hr}/org-codes/PROC", new { unitId = procurement })).StatusCode);
        await DispatchAsync();
        await DispatchAsync();
        Assert.Equal(procurement, await world.Database.TenantScalarAsync<Guid>(t.Id,
            "select org_unit_id from tenancy.org_membership where user_id = @u and is_primary and valid_to is null", ("u", t["omar"].Id)));
        Assert.Equal(("hr", t["huda"].Id), (await world.Database.TenantScalarAsync<string>(t.Id,
                "select source from people.reporting_line where user_id = @u and valid_to is null", ("u", t["omar"].Id)),
            await world.Database.TenantScalarAsync<Guid>(t.Id, "select manager_user_id from people.reporting_line where user_id = @u and valid_to is null", ("u", t["omar"].Id))));
        Assert.Equal("Buyer", (await OwnFieldsAsync(t["omar"]))["jobTitle"].GetProperty("value").GetString());
        var move = (await ChangesAsync(t, "applied")).Single(c => c.GetProperty("recordId").GetString() == "R2");
        Assert.Equal(overview.GetProperty("lastRun").GetProperty("id").GetGuid(), move.GetProperty("runId").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.HrChangeApplied, move.GetProperty("id").GetString());

        // The end of Huda's last day: access ends without another sync.
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with c as (update integrations.hr_change set due_at = now() - interval '1 minute' where record_id = 'R4' and status = 'pending' returning 1) select count(*) from c"));
        await ApplyDueAsync(t);
        Assert.Equal("applied", await world.Database.TenantScalarAsync<string>(t.Id, "select status from integrations.hr_change where record_id = 'R4' and kind = 'termination'"));
        Assert.Equal("disabled", await MembershipStatusAsync(t, "huda"));

        // A rehire reopens the account only when the tenant allows it (US4 scenario 3).
        hr.Employees[2] = Person("R3", t["layla"].Email, "E-300");
        hr.Employees[2]["hired"] = Iso(today);
        await SyncAsync(t);
        Assert.Equal("disabled", await MembershipStatusAsync(t, "layla"));
        Assert.Contains(await ChangesAsync(t, "applied"), c => c.GetProperty("kind").GetString() == "rehire");

        await PutSettingsAsync(t, new { rehireReactivates = true, onboardingLeadDays = 3 });
        hr.Employees[3] = Person("R4", t["huda"].Email, "E-400");
        hr.Employees[3]["hired"] = Iso(today);
        await SyncAsync(t);
        Assert.Equal("active", await MembershipStatusAsync(t, "huda"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ReenabledByHr, t["huda"].Id.ToString());

        // US2: a joiner is prepared before the start date; a hire cancelled in HR before then is dropped (scenario 3).
        hr.Employees.Add(Person("R9", "new.joiner@hr.example", "E-900"));
        hr.Employees[^1]["hired"] = Iso(today.AddDays(40));
        await SyncAsync(t);
        var hire = (await ChangesAsync(t, "pending")).Single(c => c.GetProperty("kind").GetString() == "hire");
        Assert.Equal((Iso(today.AddDays(40)), JsonValueKind.Null), (hire.GetProperty("effectiveDate").GetString(), hire.GetProperty("userId").ValueKind));
        Assert.True(hire.GetProperty("dueAt").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddDays(40));
        hr.Employees[^1]["state"] = "Terminated";
        await SyncAsync(t);
        var cancelled = (await ChangesAsync(t, "cancelled")).Single(c => c.GetProperty("recordId").GetString() == "R9");
        Assert.Equal("cancelled_in_hr", cancelled.GetProperty("cancelledReason").GetString());

        // HR pages are for HR administrators only, and the plan must include HR integration.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Hr}/changes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.PutAsJsonAsync($"{Hr}/org-codes/FIN", new { unitId = finance })).StatusCode);
        var plain = await TenantAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["admin"].Client.GetAsync($"{Hr}/org-codes")).StatusCode);
    }
}
