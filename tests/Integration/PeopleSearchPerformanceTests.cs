using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 T011 and section "Performance": people search p95 under 300 ms at 50,000 members. The members are written
/// straight into the People read model of one tenant, after the first search has recorded the hierarchy version, so the
/// measurement covers the search endpoint as a user calls it (session, authorization, policy, query and paging).
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PeopleSearchPerformanceTests(TenantWorld world)
{
    private const int Members = 50_000;
    private const double BudgetMs = 300;

    [Fact]
    public async Task People_search_p95_stays_under_300_ms_at_50000_members()
    {
        var t = await world.CreateTenantAsync("ppf-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var admin = t["admin"].Client;
        (await admin.GetAsync("/api/v1/people?q=warm")).EnsureSuccessStatusCode();

        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var session = sp.GetRequiredService<IDbSession>();
            await Sql.ExecAsync(session,
                """
                insert into people.profile (tenant_id, user_id, display_name, email, membership_type, active, search_name, search_skeleton, search_fields)
                select platform.current_tenant_id(), gen_random_uuid(), initcap(f) || ' ' || initcap(l) || ' ' || i, 'p' || i || '@perf.example',
                       'employee', true, f || ' ' || l || ' ' || i, '',
                       jsonb_build_object('jobTitle', (array['analyst','engineer','officer','manager','specialist'])[1 + i % 5],
                                          'department', (array['finance','legal','sales','operations','people'])[1 + (i / 5) % 5],
                                          'workPhone', (4000 + i % 6000)::text)
                from generate_series(1, @n) as i,
                     lateral (select (array['mohamed','layla','omar','sara','kofi','mona','youssef','fatima','ahmed','noura'])[1 + i % 10] as f,
                                     (array['nasser','haddad','mensah','khan','saleh','omar','farouk','hassan','aziz','karim','rahman','ali'])[1 + (i / 10) % 12] as l) names
                """, new { n = Members });
            await session.CommitAsync();
            return true;
        });
        Assert.True(await world.Database.CountInTenantAsync(t.Id, "select count(*) from people.profile") >= Members);

        string[] queries = ["mohamed", "layla nasser", "finance", "engineer", "4521", "sara kh", "omar haddad 12", "noura"];
        foreach (var q in queries)
        {
            (await admin.GetAsync($"/api/v1/people?q={Uri.EscapeDataString(q)}")).EnsureSuccessStatusCode();
        }

        var timings = new List<double>();
        for (var round = 0; round < 5; round++)
        {
            foreach (var q in queries)
            {
                var clock = Stopwatch.StartNew();
                var response = await admin.GetAsync($"/api/v1/people?q={Uri.EscapeDataString(q)}");
                clock.Stop();
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(page.GetProperty("items").GetArrayLength() > 0, q);
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }
        }
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        Assert.True(p95 < BudgetMs, $"people search p95 {p95:F0} ms at {Members} members (budget {BudgetMs} ms); all: {string.Join(", ", timings.Select(x => x.ToString("F0")))}");
    }
}
