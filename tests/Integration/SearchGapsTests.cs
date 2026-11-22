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
/// Spec 021 US3 (FR-007): the search dashboard lists zero result and no click queries from the spec 012 feed, by
/// locale, only when five people searched for them and never personal data; knowledge editors and search
/// administrators may open it and are told which follow-ups they may take; others, other packages and other tenants
/// see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SearchGapsTests(TenantWorld world)
{
    private const string Gaps = "/api/v1/analytics/search";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> SearchAsync(HttpClient client, string q) => OkAsync(client.GetAsync($"/api/v1/search?q={Uri.EscapeDataString(q)}"));

    private static IEnumerable<string> Queries(JsonElement view, string list) => view.GetProperty(list).EnumerateArray().Select(q => q.GetProperty("query").GetString()!);

    [Fact]
    public async Task Zero_result_queries_five_people_share_are_listed_by_locale_for_the_people_who_may_act_on_them()
    {
        var t = await world.CreateTenantAsync("sg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var people = Enumerable.Range(1, 5).Select(i => $"p{i}").ToList();
        foreach (var name in people.Append("ed").Append("emp"))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "ed", BuiltInRoles.ContentEditor, "tenant", null);
        foreach (var p in people)
        {
            await SearchAsync(t[p].Client, "parking permit");
            await SearchAsync(t[p].Client, "laila@example.test");
        }
        await SearchAsync(t["p1"].Client, "gym hours");

        var view = await OkAsync(t["admin"].Client.GetAsync(Gaps));
        Assert.Equal(11, view.GetProperty("searches").GetInt32());
        Assert.Equal(["parking permit"], Queries(view, "zeroResultQueries"));
        Assert.DoesNotContain("gym hours", Queries(view, "topQueries"));
        Assert.True(view.GetProperty("canAddBestBet").GetBoolean());
        Assert.True(view.GetProperty("canWriteArticle").GetBoolean());

        // By locale: nobody searched in Arabic.
        Assert.Equal(0, (await OkAsync(t["admin"].Client.GetAsync($"{Gaps}?locale=ar"))).GetProperty("searches").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.GetAsync($"{Gaps}?locale=english")).StatusCode);

        // A knowledge editor may open it and write an article, but not add a best bet.
        var editor = await OkAsync(t["ed"].Client.GetAsync(Gaps));
        Assert.True(editor.GetProperty("canWriteArticle").GetBoolean());
        Assert.False(editor.GetProperty("canAddBestBet").GetBoolean());
        Assert.Equal(["parking permit"], Queries(editor, "zeroResultQueries"));

        // An employee and a unit scoped analytics viewer are refused; another tenant sees none of it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync(Gaps)).StatusCode);
        var unit = await world.CreateUnitAsync(t, "Finance", null, "department");
        await world.AssignAsync(t, "p1", "analytics_viewer", "org_subtree", unit);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["p1"].Client.GetAsync(Gaps)).StatusCode);
        var other = await world.CreateTenantAsync("sg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(0, (await OkAsync(other["admin"].Client.GetAsync(Gaps))).GetProperty("searches").GetInt32());

        // It can be scheduled like any dashboard, and needs the advanced analytics package.
        Assert.Equal(HttpStatusCode.Created, (await t["admin"].Client.PostAsJsonAsync("/api/v1/analytics/schedules",
            new { definition = new { dashboard = ReportDashboards.Search, filters = new { locale = "en" } }, cadence = ReportCadences.Weekly })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.PostAsJsonAsync("/api/v1/analytics/schedules",
            new { definition = new { dashboard = ReportDashboards.Search, filters = new { channel = "web" } }, cadence = ReportCadences.Weekly })).StatusCode);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.AnalyticsAdvanced, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await t["admin"].Client.GetAsync(Gaps)).StatusCode);
    }
    [Fact]
    public async Task Searches_split_by_unit_and_refinements_five_people_share_are_counted()
    {
        var t = await world.CreateTenantAsync("sgu-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var plantA = await world.CreateUnitAsync(t, "PlantA", null, "department");
        await world.CreateUnitAsync(t, "PlantB", null, "department");
        await world.CreateUnitAsync(t, "PlantC", null, "department");
        foreach (var (unit, count) in new[] { ("PlantA", 5), ("PlantB", 2), ("PlantC", 5) })
        {
            for (var i = 1; i <= count; i++)
            {
                var name = $"{unit}{i}";
                await world.SeedUserAsync(t, name, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, name, unit);
                if (unit == "PlantA")
                {
                    // A misspelled search without a click, then the corrected one: a refinement.
                    await SearchAsync(t[name].Client, "lave form");
                    await SearchAsync(t[name].Client, "leave form");
                }
                else
                {
                    await SearchAsync(t[name].Client, "parking");
                }
            }
        }

        var view = await OkAsync(t["admin"].Client.GetAsync(Gaps));
        Assert.Equal(17, view.GetProperty("searches").GetInt32());
        Assert.Equal(5, view.GetProperty("refined").GetInt32());
        var refinement = Assert.Single(view.GetProperty("refinements").EnumerateArray());
        Assert.Equal("lave form", refinement.GetProperty("from").GetString());
        Assert.Equal("leave form", refinement.GetProperty("to").GetString());
        Assert.Equal(5, refinement.GetProperty("people").GetInt32());
        // Plant A shows; Plant B has two people, so it folds into Other with Plant C, the next smallest.
        var units = view.GetProperty("units").EnumerateArray().ToArray();
        Assert.Equal([plantA.ToString(), "other"], units.Select(u => u.GetProperty("key").GetString()!).ToArray());
        Assert.Equal(10, units[0].GetProperty("searches").GetInt32());
        Assert.Equal(5, units[0].GetProperty("people").GetInt32());
        Assert.Equal(7, units[1].GetProperty("searches").GetInt32());
        Assert.Equal(JsonValueKind.Null, units[1].GetProperty("people").ValueKind);

        // Filtered to Plant A: only its searches.
        var a = await OkAsync(t["admin"].Client.GetAsync($"{Gaps}?unit={plantA}"));
        Assert.Equal(10, a.GetProperty("searches").GetInt32());
        Assert.Equal(plantA, a.GetProperty("unitId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, a.GetProperty("unitNames").ValueKind);
        Assert.DoesNotContain("parking", Queries(a, "topQueries"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"{Gaps}?unit={Guid.NewGuid()}")).StatusCode);

        // The unit filter is a report filter of the search dashboard.
        Assert.Equal(HttpStatusCode.Created, (await t["admin"].Client.PostAsJsonAsync("/api/v1/analytics/schedules",
            new { definition = new { dashboard = ReportDashboards.Search, filters = new { unitId = plantA } }, cadence = ReportCadences.Weekly })).StatusCode);
    }
}
