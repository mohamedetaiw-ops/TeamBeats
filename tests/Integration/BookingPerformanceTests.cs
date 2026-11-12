using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 031 T010, section 15: search p95 under 700 ms for a site with 1,000 resources, with half of them booked on the
/// searched day. The usage report over the same site is timed too, against the same budget. Resources and bookings are
/// written straight into the tables, so the measurements cover the endpoints as they run for users. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BookingPerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Api = "/api/v1/booking";
    private const int Resources = 1_000;
    private const double BudgetMs = 700;
    private static readonly TimeZoneInfo Riyadh = TimeZoneInfo.FindSystemTimeZoneById("Asia/Riyadh");

    private static double P95(List<double> timings)
    {
        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private static async Task<List<double>> TimeAsync(HttpClient client, string url, int rounds)
    {
        for (var i = 0; i < 3; i++)
        {
            (await client.GetAsync(url)).EnsureSuccessStatusCode();
        }
        var timings = new List<double>();
        for (var i = 0; i < rounds; i++)
        {
            var clock = Stopwatch.StartNew();
            var response = await client.GetAsync(url);
            clock.Stop();
            response.EnsureSuccessStatusCode();
            timings.Add(clock.Elapsed.TotalMilliseconds);
        }
        return timings;
    }

    private Task SeedAsync(TestTenant t, string sql, object args) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await Sql.ExecAsync(session, sql, args);
            await session.CommitAsync();
            return true;
        });

    private static DateOnly Monday()
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Riyadh).DateTime).AddDays(2);
        while (day.DayOfWeek != DayOfWeek.Monday)
        {
            day = day.AddDays(1);
        }
        return day;
    }

    [Fact]
    public async Task Search_and_the_usage_report_stay_under_700_ms_p95_at_1000_resources()
    {
        var t = await world.CreateTenantAsync("bpf-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Finance", holding, "department");
        foreach (var name in new[] { "omar", "hana" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Finance");
        }
        await world.AssignAsync(t, "hana", BuiltInRoles.FacilitiesAdministrator, "org_subtree", holding);
        var hana = t["hana"].Client;
        var created = await hana.PostAsJsonAsync($"{Api}/manage/sites", new
        {
            names = new { en = "Riyadh office" }, orgUnitId = holding, timeZone = "Asia/Riyadh", opensAt = new TimeOnly(8, 0), closesAt = new TimeOnly(18, 0),
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var site = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var mon = Monday();
        var start = new DateTimeOffset(mon.ToDateTime(new TimeOnly(10, 0)), TimeSpan.FromHours(3)).ToUniversalTime();

        // 1,000 desks, every tenth with a standing desk; the even ones are booked from ten to noon on Monday.
        await SeedAsync(t,
            """
            insert into booking.resource (tenant_id, id, site_id, kind, names, attributes, qr_code, created_by, created_at, updated_at)
            select platform.current_tenant_id(), gen_random_uuid(), @site, 'desk', jsonb_build_object('en', 'Desk ' || g),
                   case when g % 10 = 0 then array['standing_desk'] else '{}'::text[] end, md5(random()::text || g), @by, now(), now()
            from generate_series(1, @count) g
            """, new { site, by = t["hana"].Id, count = Resources });
        await SeedAsync(t,
            """
            insert into booking.booking (tenant_id, id, resource_id, holder_id, booked_by, starts_at, ends_at, held, status, created_at)
            select platform.current_tenant_id(), gen_random_uuid(), r.id, @holder, @holder, @start, @start + interval '2 hours',
                   tstzrange(@start, @start + interval '2 hours'), 'confirmed', now()
            from (select id, row_number() over (order by id) n from booking.resource where site_id = @site) r
            where r.n % 2 = 0
            """, new { site, holder = t["omar"].Id, start });

        var omar = t["omar"].Client;
        var search = $"{Api}/sites/{site}/search?kind=desk&date={mon:yyyy-MM-dd}&start=10:00&end=11:00";
        var first = await (await omar.GetAsync(search)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.ValueKind is JsonValueKind.Array or JsonValueKind.Object, first.GetRawText());

        var searchTimings = await TimeAsync(omar, search, 40);
        var report = $"{Api}/reports/utilization?site={site}&from={mon.AddDays(-28):yyyy-MM-dd}&to={mon.AddDays(6):yyyy-MM-dd}";
        var reportBody = await (await hana.GetAsync(report)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Resources, reportBody.GetProperty("total").GetProperty("resources").GetInt32());
        Assert.Equal(Resources / 2, reportBody.GetProperty("total").GetProperty("bookings").GetInt32());
        var reportTimings = await TimeAsync(hana, report, 20);

        output.WriteLine($"search p95 {P95(searchTimings):F1} ms, report p95 {P95(reportTimings):F1} ms");
        Assert.True(P95(searchTimings) < BudgetMs, $"search p95 {P95(searchTimings):F1} ms");
        Assert.True(P95(reportTimings) < BudgetMs, $"report p95 {P95(reportTimings):F1} ms");
    }
}
