using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Integrations.Hr;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.People;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 019 T014, section 15: a full sync of 20,000 employees finishes under 30 minutes, from the first page read from
/// HR to the profiles holding the HR values, both for the first run and for a later run with changes; and the payslip
/// list loads under 2 seconds p95 when payroll takes close to its 1 second to answer.
/// </summary>
public sealed partial class HrSyncTests
{
    private const int Workforce = 20_000;

    /// <summary>Seeds the tenant's people straight into the tables, as a directory sync would have left them.</summary>
    private async Task SeedWorkforceAsync(TestTenant t, int count)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await session.ExecAsync(
                """
                insert into identity.user_account (tenant_id, id, display_name, email, status, created_via)
                select platform.current_tenant_id(), gen_random_uuid(), 'Person ' || i, 'p' || i || '@' || @key || '.example', 'active', 'admin'
                from generate_series(0, @last) as i
                """, new { key = t.Key, last = count - 1 });
            await session.ExecAsync(
                """
                insert into tenancy.tenant_membership (tenant_id, user_id, membership_type, status)
                select u.tenant_id, u.id, 'employee', 'active' from identity.user_account u
                where u.email like 'p%' and not exists (select 1 from tenancy.tenant_membership m where m.user_id = u.id)
                """, null);
            await sp.GetRequiredService<ProfileStore>().RefreshAllAsync(CancellationToken.None);
            await session.CommitAsync();
            return 0;
        });
    }

    /// <summary>One full sync, timed from the request to the profiles holding the HR values.</summary>
    private async Task<(JsonElement Run, TimeSpan Took)> TimedSyncAsync(TestTenant t)
    {
        var watch = Stopwatch.StartNew();
        var started = await t["admin"].Client.PostAsync($"{Hr}/runs", null);
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync());
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var ran = await sp.GetRequiredService<ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return ran;
        });
        // The connector tick applies due changes in batches; run the ticks the changes need.
        while (await world.Factory.InScopeAsync(t.Id, async sp =>
               {
                   var applied = await sp.GetRequiredService<HrLifecycle>().ApplyDueAsync(CancellationToken.None);
                   await sp.GetRequiredService<IDbSession>().CommitAsync();
                   return applied;
               }) > 0)
        {
        }
        // What the HrSyncCompleted handler does, run here so the shared outbox is not drained for other tests.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<HrProfiles>().ApplyAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        watch.Stop();
        await world.Database.OwnerExecAsync("delete from messaging.outbox where tenant_id = @t and dispatched_at is null", ("t", t.Id));
        return ((await OverviewAsync(t)).GetProperty("lastRun"), watch.Elapsed);
    }

    [Fact]
    public async Task A_full_sync_of_twenty_thousand_employees_finishes_well_within_thirty_minutes()
    {
        var t = await TenantAsync();
        await SeedWorkforceAsync(t, Workforce);
        var (_, hr) = await RestConnectionAsync(t, pageSize: 1000);
        hr.Employees = Enumerable.Range(0, Workforce)
            .Select(i => Person($"R{i:D5}", $"p{i}@{t.Key}.example", $"E-{i:D5}", dept: $"D{i % 40:D2}", manager: i < 40 ? null : $"E-{i % 40:D5}"))
            .ToList();

        // The first run: every record read, matched and stored, and every profile given its HR values.
        var (first, firstTook) = await TimedSyncAsync(t);
        Assert.Equal(("applied", Workforce, Workforce), (first.GetProperty("status").GetString(), first.GetProperty("total").GetInt32(), first.GetProperty("matched").GetInt32()));
        Assert.Equal((long)Workforce, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from people.profile where hr_values <> '{}'::jsonb"));
        Assert.True(firstTook < TimeSpan.FromMinutes(10), $"The first sync of {Workforce} took {firstTook.TotalSeconds:F0} s.");

        // A later run with a normal month of changes, under the change threshold: new titles, moves and leavers.
        var leaving = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 40; i < 1_040; i++)
        {
            ((Dictionary<string, object?>)hr.Employees[i]["job"]!)["title"] = "Senior accountant";
        }
        for (var i = 1_040; i < 1_540; i++)
        {
            hr.Employees[i]["dept"] = "D99";
        }
        for (var i = 1_540; i < 1_640; i++)
        {
            hr.Employees[i]["state"] = "Left";
            hr.Employees[i]["left"] = leaving;
        }
        var (second, secondTook) = await TimedSyncAsync(t);
        Assert.Equal(("applied", Workforce), (second.GetProperty("status").GetString(), second.GetProperty("matched").GetInt32()));
        Assert.Equal((100, 500), (second.GetProperty("terminations").GetInt32(), second.GetProperty("moves").GetInt32()));
        // Title changes apply on the day, moves wait for their unmapped code and leavers for their last day.
        var changes = await world.Database.TenantScalarAsync<string>(t.Id,
            "select string_agg(kind || ':' || status || ':' || n, ',' order by kind, status) from (select kind, status, count(*) n from integrations.hr_change group by 1, 2) c");
        Assert.Equal("assignment:applied:1000,assignment:unmapped:500,termination:pending:100", changes);
        Assert.Equal(1_000L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from people.profile where hr_values->>'jobTitle' = 'Senior accountant'"));
        Assert.True(secondTook < TimeSpan.FromMinutes(10), $"The second sync of {Workforce} took {secondTook.TotalSeconds:F0} s.");
    }

    [Fact]
    public async Task The_payslip_list_loads_p95_under_two_seconds_when_payroll_takes_close_to_a_second()
    {
        var t = await TenantAsync(people: "sara");
        var (_, hr) = await RestConnectionAsync(t, payslips: true);
        hr.Employees = [Person("R1", t["sara"].Email, "E-100")];
        hr.Payslips["R1"] = Enumerable.Range(1, 24).Select(m => Slip($"{2024 + (m - 1) / 12}-{(m - 1) % 12 + 1:D2}", "10500", "12000", "1500")).ToList();
        await SyncAsync(t);
        hr.PayslipDelay = TimeSpan.FromMilliseconds(900);

        // Read live every time: nothing is cached, so each load waits for payroll.
        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var clock = Stopwatch.StartNew();
            var list = await (await t["sara"].Client.GetAsync(Payslips)).ReadAsync<JsonElement>();
            clock.Stop();
            Assert.Equal(("ok", 24), (list.GetProperty("state").GetString(), list.GetProperty("payslips").GetArrayLength()));
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        Assert.True(timings[0] >= 900, "Payroll's delay was not felt, so the check measured nothing.");
        Assert.True(p95 < 2000, $"p95 was {p95:F0} ms.");
    }
}
