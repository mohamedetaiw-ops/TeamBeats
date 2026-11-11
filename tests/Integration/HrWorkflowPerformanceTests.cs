using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Hr.Journeys;
using TeamBeats.Modules.Hr.Leave;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 022 T024, section 15: the leave request page with balance and overlap loads p95 under 1 second, and journey
/// steps fire within 5 minutes of their time even on a day when many are due. The 50,000 punch clock import is in
/// <see cref="ClockImportTests"/>.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class HrWorkflowPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Leave = "/api/v1/hr/leave";
    private const int Colleagues = 40;
    private const int Joiners = 30;
    private const int StepsPerJourney = 50;

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private static double P95(List<double> timings)
    {
        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private static async Task OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private static DateOnly Monday(int weeksAhead)
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
        return d;
    }

    [Fact]
    public async Task The_leave_request_page_with_balance_and_overlap_loads_p95_under_one_second()
    {
        var t = await world.CreateTenantAsync("lvp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Sales", holding, "department");
        var names = Enumerable.Range(0, Colleagues).Select(i => $"c{i:D2}").Prepend("mona").Append("kofi").ToArray();
        foreach (var name in names)
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Sales");
        }
        foreach (var name in names.Where(n => n != "kofi"))
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t[name].Id}/manager", new { managerId = t["kofi"].Id }));
        }
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Leave}/types/annual", new { names = Names("Annual leave", "إجازة سنوية"), sensitive = false, carryOverMax = 5 }));
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Leave}/policies", new { typeKey = "annual", bands = new[] { new { days = 30 } }, accrual = "yearly" }),
            HttpStatusCode.Created);
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var written = await sp.GetRequiredService<LeaveLedger>().RunAccrualAsync(default);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return written;
        });

        // Every colleague is away for a day in the week Mona asks for, and Mona has a history of requests.
        var week = Monday(2);
        for (var i = 0; i < Colleagues; i++)
        {
            var day = week.AddDays(i % 5);
            await OkAsync(t[$"c{i:D2}"].Client.PostAsJsonAsync($"{Leave}/requests", new { typeKey = "annual", start = day, end = day }), HttpStatusCode.Created);
        }
        foreach (var day in Enumerable.Range(0, 10).Select(i => week.AddDays(28 + 7 * i + i % 5)))
        {
            await OkAsync(t["mona"].Client.PostAsJsonAsync($"{Leave}/requests", new { typeKey = "annual", start = day, end = day }), HttpStatusCode.Created);
        }

        var mona = t["mona"].Client;
        var input = new { typeKey = "annual", start = week, end = week.AddDays(4) };
        var preview = await (await mona.PostAsJsonAsync($"{Leave}/preview", input)).ReadAsync<JsonElement>();
        Assert.Equal(5m, preview.GetProperty("days").GetDecimal());
        Assert.Equal(Colleagues, preview.GetProperty("overlap").GetArrayLength());

        // The page: who the person is, their balances and requests, then the preview as the dates are picked.
        for (var i = 0; i < 3; i++)
        {
            (await mona.GetAsync("/api/v1/me/leave")).EnsureSuccessStatusCode();
            (await mona.PostAsJsonAsync($"{Leave}/preview", input)).EnsureSuccessStatusCode();
        }
        var timings = new List<double>();
        for (var i = 0; i < 40; i++)
        {
            var clock = Stopwatch.StartNew();
            (await mona.GetAsync("/api/v1/me")).EnsureSuccessStatusCode();
            (await mona.GetAsync("/api/v1/me/leave")).EnsureSuccessStatusCode();
            (await mona.PostAsJsonAsync($"{Leave}/preview", input)).EnsureSuccessStatusCode();
            clock.Stop();
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        var p95 = P95(timings);
        output.WriteLine($"Leave request page p95: {p95:F1} ms with {Colleagues} colleagues away");
        Assert.True(p95 < 1000, $"p95 was {p95:F1} ms.");
    }

    [Fact]
    public async Task Journey_steps_due_on_a_busy_day_all_open_in_one_tick_well_within_five_minutes()
    {
        var t = await world.CreateTenantAsync("jrp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Ops", holding, "department");
        for (var i = 0; i < Joiners; i++)
        {
            await world.SeedUserAsync(t, $"j{i:D2}", MembershipTypes.Employee);
            await world.AddMembershipAsync(t, $"j{i:D2}", "Ops");
        }
        var steps = Enumerable.Range(0, StepsPerJourney)
            .Select(i => new { key = $"step_{i:D2}", names = Names($"Step {i}", $"الخطوة {i}"), offsetDays = 1, assignee = "person" }).ToArray();
        await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/hr/journey-templates",
            new { key = "big_onboarding", kind = "onboarding", names = Names("Onboarding", "التعيين"), startOn = "manual", steps }), HttpStatusCode.Created);
        // Ahead of today in any time zone, so nothing opens when the journeys start.
        var anchor = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        for (var i = 0; i < Joiners; i++)
        {
            await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/hr/journeys", new { templateKey = "big_onboarding", userId = t[$"j{i:D2}"].Id, anchorDate = anchor }),
                HttpStatusCode.Created);
        }
        // The day comes for every step at once: three times the batch of one transaction.
        var due = Joiners * StepsPerJourney;
        Assert.True(due > 2 * JourneyService.DueBatch);
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            var moved = await Sql.ExecAsync(session, "update hr.journey_step set scheduled_on = scheduled_on - 30 where status = 'scheduled'", new { });
            await session.CommitAsync();
            return moved;
        });

        var worker = new JourneyWorker(world.Factory.Services.GetRequiredService<IServiceScopeFactory>(), world.Factory.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JourneyWorker>>());
        var watch = Stopwatch.StartNew();
        var opened = await worker.RunOnceAsync(default, [t.Id]);
        watch.Stop();
        output.WriteLine($"{opened} journey steps opened in {watch.Elapsed.TotalSeconds:F1} s");
        Assert.Equal(due, opened);
        Assert.Equal(0, await world.Database.ScalarInTenantAsync<long>(t.Id, "select count(*) from hr.journey_step where tenant_id = @t and status = 'scheduled'", ("t", t.Id)));
        // A step waits at most one tick before the pass that opens it.
        Assert.True(JourneyWorker.Tick + watch.Elapsed < TimeSpan.FromMinutes(5), $"Opening took {watch.Elapsed}.");
    }
}
