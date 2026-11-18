using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T013 (US5): the attestation rate of each open campaign by unit and location, overdue counts and the trend to
/// the deadline, all with the minimum group size; Policies decides whose attestations count (policy.manage coverage),
/// the package needs analytics.advanced, and tenants stay apart. No person appears in the view.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ComplianceAnalyticsTests(TenantWorld world)
{
    private const string Policies = "/api/v1/policies";
    private const string Analytics = "/api/v1/analytics/compliance";
    private static readonly Dictionary<string, string> Titles = new() { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static object Clauses() => new[]
    {
        new
        {
            level = 1, titles = Titles,
            bodies = new Dictionary<string, object>
            {
                ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Conduct text" } } } } },
                ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "نص السلوك" } } } } },
            },
        },
    };

    private async Task<(TestTenant T, Guid Gulf, Guid Oman, Guid Policy, Guid Campaign)> LaunchedAsync()
    {
        var t = await world.CreateTenantAsync("cpa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        var oman = await world.CreateUnitAsync(t, "Oman", holding, "subsidiary");
        foreach (var (unit, people) in new[] { ("Gulf", new[] { "co", "gco", "legal", "g1", "g2", "g3" }), ("Egypt", new[] { "e1", "e2", "e3", "e4", "e5" }), ("Oman", new[] { "o1", "o2" }) })
        {
            foreach (var name in people)
            {
                await world.SeedUserAsync(t, name, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, name, unit);
            }
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.AssignAsync(t, "gco", BuiltInRoles.ComplianceOfficer, "org_subtree", gulf);
        var id = Id(await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new { code = "P-1", names = Titles, route = new[] { new { approvers = new[] { t["legal"].Id } } } }),
            HttpStatusCode.Created));
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles = Titles, clauses = Clauses(), material = true }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        var campaign = Id(await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/campaigns", new { }), HttpStatusCode.Created));
        return (t, gulf, oman, id, campaign);
    }

    private static (int Audience, int? Attested, int? Overdue, double? Rate) Counts(JsonElement e) =>
        (e.GetProperty("audience").GetInt32(), e.GetProperty("attested").ValueKind == JsonValueKind.Null ? null : e.GetProperty("attested").GetInt32(),
            e.GetProperty("overdue").ValueKind == JsonValueKind.Null ? null : e.GetProperty("overdue").GetInt32(),
            e.GetProperty("rate").ValueKind == JsonValueKind.Null ? null : e.GetProperty("rate").GetDouble());

    [Fact]
    public async Task Open_campaigns_show_rates_by_unit_and_location_with_overdue_and_a_trend_and_hide_small_groups()
    {
        var (t, gulf, _, policy, campaign) = await LaunchedAsync();
        foreach (var name in new[] { "co", "g1", "g2", "g3", "e1" })
        {
            await OkAsync(t[name].Client.PostAsJsonAsync($"{Policies}/{policy}/attest", new { }), HttpStatusCode.Created);
        }
        await OkAsync(t["admin"].Client.GetAsync("/api/v1/people"));
        Assert.Equal(2, await world.Database.OwnerExecInTenantAsync(t.Id, "update policies.campaign_member set status = 'overdue' where user_id = any(@u)",
            ("u", new[] { t["e2"].Id, t["e3"].Id })));
        Assert.Equal(5, await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set location_key = 'dubai' where user_id = any(@u)",
            ("u", new[] { t["co"].Id, t["gco"].Id, t["g1"].Id, t["g2"].Id, t["g3"].Id })));

        var view = await OkAsync(t["co"].Client.GetAsync(Analytics));
        // Fourteen in the audience: thirteen in units and the administrator with none.
        Assert.Equal((14, 5, 2, 0.3571), Counts(view.GetProperty("total")));
        var c = Assert.Single(view.GetProperty("campaigns").EnumerateArray());
        Assert.Equal(campaign, c.GetProperty("campaignId").GetGuid());
        Assert.Equal(policy, c.GetProperty("policyId").GetGuid());
        Assert.Equal("Code of conduct", c.GetProperty("names").GetProperty("en").GetString());
        Assert.True(c.GetProperty("daysLeft").GetInt32() > 0);
        // Oman (two) and no unit (one) fold, and Egypt (five) with them so "other" reaches the minimum.
        var units = c.GetProperty("units").EnumerateArray().ToDictionary(g => g.GetProperty("group").GetString()!, g => g);
        Assert.Equal(2, units.Count);
        Assert.Equal((6, 4, 0, 0.6667), Counts(units[gulf.ToString()].GetProperty("counts")));
        Assert.Equal("Gulf", units[gulf.ToString()].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal((8, 1, 2, 0.125), Counts(units[ItemAnalytics.Other].GetProperty("counts")));
        var locations = c.GetProperty("locations").EnumerateArray().ToDictionary(g => g.GetProperty("group").GetString()!, g => g);
        Assert.Equal((5, 4, 0, 0.8), Counts(locations["dubai"].GetProperty("counts")));
        Assert.Equal((9, 1, 2, 0.1111), Counts(locations[ItemAnalytics.NoUnit].GetProperty("counts")));
        var point = Assert.Single(c.GetProperty("trend").EnumerateArray());
        Assert.Equal(0.3571, point.GetProperty("rate").GetDouble());
        Assert.DoesNotContain(t["g1"].Id.ToString(), view.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // The Gulf compliance officer counts only the Gulf: six people, no other groups to fold.
        var gulfView = await OkAsync(t["gco"].Client.GetAsync(Analytics));
        var g = Assert.Single(gulfView.GetProperty("campaigns").EnumerateArray());
        Assert.Equal((6, 4, 0, 0.6667), Counts(g.GetProperty("total")));
        Assert.Single(g.GetProperty("units").EnumerateArray());
    }

    [Fact]
    public async Task Only_compliance_officers_with_the_package_see_it_and_tenants_stay_apart()
    {
        var (t, _, oman, _, _) = await LaunchedAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t["g1"].Client.GetAsync(Analytics)).StatusCode);

        // Under the minimum group size, counts and the trend are hidden.
        await world.AssignAsync(t, "o1", BuiltInRoles.ComplianceOfficer, "org_subtree", oman);
        var small = Assert.Single((await OkAsync(t["o1"].Client.GetAsync(Analytics))).GetProperty("campaigns").EnumerateArray());
        Assert.Equal((2, null, null, null), Counts(small.GetProperty("total")));
        Assert.Empty(small.GetProperty("trend").EnumerateArray());
        Assert.Empty(small.GetProperty("units").EnumerateArray());

        var other = await world.CreateTenantAsync("cpa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Analytics))).GetProperty("campaigns").EnumerateArray());

        var basic = await world.CreateTenantAsync("cpa-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(Analytics)).StatusCode);
    }
}
