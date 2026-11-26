using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 R3 (T026): benchmarks across anonymized tenants with consent. Nothing is given or shown until a tenant
/// administrator opts in; a tenant that opted in sees its own figure and, from five tenants, the quartiles across them,
/// never another tenant's figure or name; opting out removes what it gave; the package decides; only analytics viewers
/// and tenant administrators read it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BenchmarkTests(TenantWorld world)
{
    private const string Path = "/api/v1/analytics/benchmarks";

    private static DateOnly LastWeek() => Adoption.WeekOf(DateOnly.FromDateTime(DateTime.UtcNow)).AddDays(-7);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A tenant with the administrator and five employees, <paramref name="active"/> of the six active last week.</summary>
    private async Task<TestTenant> TenantAsync(int active, string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("bm-" + Guid.NewGuid().ToString("N")[..6], package);
        for (var i = 0; i < 5; i++)
        {
            await world.SeedUserAsync(t, "e" + i, MembershipTypes.Employee);
        }
        await world.Database.TenantScalarAsync<int>(t.Id,
            "insert into analytics.adoption_weekly (tenant_id, week, org_unit_id, active_users) values (@t, @week, @whole, @active) returning 1",
            ("t", t.Id), ("week", LastWeek()), ("whole", AnalyticsAggregator.WholeTenant), ("active", active));
        return t;
    }

    private static JsonElement Figure(JsonElement view) => view.GetProperty("figures").EnumerateArray().Single();

    [Fact]
    public async Task Tenants_that_opted_in_see_quartiles_from_five_tenants_and_opting_out_removes_their_figure()
    {
        await world.Database.OwnerExecAsync("delete from analytics.benchmark_contribution where week = @week", ("week", LastWeek()));
        var tenants = new List<TestTenant>();
        for (var active = 1; active <= 5; active++)
        {
            tenants.Add(await TenantAsync(active));
        }
        var first = tenants[0];

        // Off until the tenant opts in; employees read nothing.
        var off = await OkAsync(first["admin"].Client.GetAsync(Path));
        Assert.True(off.GetProperty("available").GetBoolean());
        Assert.False(off.GetProperty("enabled").GetBoolean());
        Assert.Empty(off.GetProperty("figures").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await first["e0"].Client.GetAsync(Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await first["e0"].Client.PutAsJsonAsync(Path, new { enabled = true })).StatusCode);

        foreach (var t in tenants.Take(4))
        {
            Assert.True((await OkAsync(t["admin"].Client.PutAsJsonAsync(Path, new { enabled = true }))).GetProperty("enabled").GetBoolean());
        }
        await world.Database.AssertAuditedAsync(first.Id, AuditActions.AnalyticsBenchmarksChanged);

        // Four tenants: the count and the tenant's own figure, no quartiles.
        var four = Figure(await OkAsync(first["admin"].Client.GetAsync(Path)));
        Assert.Equal(AnalyticsBenchmarks.WeeklyActiveShare, four.GetProperty("metric").GetString());
        Assert.Equal(16.7m, four.GetProperty("own").GetDecimal());
        Assert.Equal(4, four.GetProperty("tenants").GetInt32());
        Assert.Equal(JsonValueKind.Null, four.GetProperty("median").ValueKind);

        // Five tenants: quartiles across them, and nothing that names or lists another tenant.
        await OkAsync(tenants[4]["admin"].Client.PutAsJsonAsync(Path, new { enabled = true }));
        var five = Figure(await OkAsync(first["admin"].Client.GetAsync(Path)));
        Assert.Equal(5, five.GetProperty("tenants").GetInt32());
        Assert.Equal(50.0m, five.GetProperty("median").GetDecimal());
        Assert.Equal(33.3m, five.GetProperty("lower").GetDecimal());
        Assert.Equal(66.7m, five.GetProperty("upper").GetDecimal());
        Assert.Equal(["metric", "week", "own", "tenants", "lower", "median", "upper"], five.EnumerateObject().Select(p => p.Name));

        // The tenant role cannot read the figures table itself.
        Assert.Equal(HttpStatusCode.OK, (await first["admin"].Client.GetAsync(Path)).StatusCode);
        await Assert.ThrowsAnyAsync<Exception>(() => world.Factory.InScopeAsync(first.Id, async sp =>
            await sp.GetRequiredService<IDbSession>().ScalarAsync<long>("select count(*) from analytics.benchmark_contribution")));

        // Opting out removes the figure it gave and stops showing the others.
        Assert.False((await OkAsync(tenants[4]["admin"].Client.PutAsJsonAsync(Path, new { enabled = false }))).GetProperty("enabled").GetBoolean());
        Assert.Empty((await OkAsync(tenants[4]["admin"].Client.GetAsync(Path))).GetProperty("figures").EnumerateArray());
        var again = Figure(await OkAsync(first["admin"].Client.GetAsync(Path)));
        Assert.Equal(4, again.GetProperty("tenants").GetInt32());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("median").ValueKind);
    }

    [Fact]
    public async Task The_package_decides_the_choice_is_checked_and_the_job_gives_once_a_week()
    {
        var plus = await TenantAsync(3, "workplace_plus");
        var refused = await plus["admin"].Client.PutAsJsonAsync(Path, new { enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("validation.analytics.benchmarks_package", await refused.Content.ReadAsStringAsync());
        Assert.False((await OkAsync(plus["admin"].Client.GetAsync(Path))).GetProperty("available").GetBoolean());

        var t = await TenantAsync(3);
        var missing = await t["admin"].Client.PutAsJsonAsync(Path, new { });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("validation.analytics.benchmarks_choice", await missing.Content.ReadAsStringAsync());

        await OkAsync(t["admin"].Client.PutAsJsonAsync(Path, new { enabled = true }));
        var given = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var n = await sp.GetRequiredService<AnalyticsBenchmarks>().ContributeAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });
        Assert.Equal(0, given);
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "select count(*) from analytics.benchmark_contribution where tenant_id = @t and week = @week", ("t", t.Id), ("week", LastWeek())));
    }
}
