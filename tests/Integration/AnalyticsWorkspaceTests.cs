using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T010 (US2, FR-004, FR-006): the analytics workspace lists the dashboards the package opens and answers metric
/// queries with filters and breakdowns; a scoped viewer only sees the units they cover and is denied any other; groups
/// under the minimum size are folded into "other" and a selection under it is suppressed; filters and breakdowns come
/// from a fixed list; the package decides access; and nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AnalyticsWorkspaceTests(TenantWorld world)
{
    private const string Workspace = "/api/v1/analytics/workspace";
    private const string Query = "/api/v1/analytics/query";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    private static Task SendAsync(TestTenant t, string user, string channel, params object[] events) =>
        OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/analytics/events", new { events = events.Select(e => Event(e, channel)).ToArray() }), HttpStatusCode.Accepted);

    private static object Event(object e, string channel) => e switch
    {
        string area => new { name = AnalyticsEvents.PageViewed, channel, fields = new { area } },
        (string type, Guid item) => new { name = AnalyticsEvents.ContentViewed, channel, fields = new { source = "content", itemId = item, contentType = type } },
        _ => throw new ArgumentException("event"),
    };

    private static Dictionary<string, JsonElement> Groups(JsonElement result) =>
        result.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("group").GetString()!, g => g);

    private async Task<(TestTenant T, Guid Holding, Guid PlantA, Guid PlantB)> PlantsAsync()
    {
        var t = await world.CreateTenantAsync("aw-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var plantA = await world.CreateUnitAsync(t, "PlantA", holding, "department");
        var plantB = await world.CreateUnitAsync(t, "PlantB", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var (prefix, unit, count) in new[] { ("a", "PlantA", 6), ("b", "PlantB", 3), ("c", "Cairo", 3) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
            }
        }
        await DispatchAsync();
        var news = Guid.NewGuid();
        var policy = Guid.NewGuid();
        for (var i = 1; i <= 6; i++)
        {
            await SendAsync(t, "a" + i, AnalyticsChannels.Web, "home", ("news", news));
        }
        for (var i = 1; i <= 3; i++)
        {
            await SendAsync(t, "b" + i, AnalyticsChannels.Mobile, "home", ("policy", policy));
            await SendAsync(t, "c" + i, AnalyticsChannels.Web, "home");
        }
        await DispatchAsync();
        return (t, holding, plantA, plantB);
    }

    [Fact]
    public async Task Whole_tenant_viewer_sees_active_people_with_small_groups_folded_and_small_selections_suppressed()
    {
        var (t, holding, plantA, plantB) = await PlantsAsync();
        var admin = t["admin"].Client;

        var workspace = await OkAsync(admin.GetAsync(Workspace));
        Assert.True(workspace.GetProperty("wholeTenant").GetBoolean());
        Assert.Equal(5, workspace.GetProperty("minGroupSize").GetInt32());
        Assert.Contains(workspace.GetProperty("dashboards").EnumerateArray(), d => d.GetProperty("key").GetString() == Modules.Analytics.Workspace.Overview);
        var units = workspace.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(units, u => u == Guid.Empty);

        // Twelve people were active; Plant A (six) is shown, Plant B and Cairo (three each) only together.
        var result = await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "unit" }));
        Assert.False(result.GetProperty("suppressed").GetBoolean());
        Assert.Equal(12, result.GetProperty("total").GetInt32());
        Assert.Equal(12, Assert.Single(result.GetProperty("weeks").EnumerateArray()).GetProperty("value").GetInt32());
        var groups = Groups(result);
        Assert.Equal(new[] { plantA.ToString(), ItemAnalytics.Other }.Order(StringComparer.Ordinal), groups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(6, groups[plantA.ToString()].GetProperty("value").GetInt32());
        Assert.Equal("PlantA", groups[plantA.ToString()].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(6, groups[ItemAnalytics.Other].GetProperty("value").GetInt32());

        // By channel: nine on the web and three on mobile. Showing the web alone would reveal mobile from the total, so both fold.
        var channels = Groups(await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "channel" })));
        Assert.Equal(new[] { ItemAnalytics.Other }, channels.Keys.ToArray());
        Assert.Equal(12, channels[ItemAnalytics.Other].GetProperty("value").GetInt32());

        // The membership type is recorded with each event.
        var types = Groups(await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "membership_type" })));
        Assert.Equal(12, types[MembershipTypes.Employee].GetProperty("value").GetInt32());

        // A unit filter with its subtree: the holding covers everyone.
        var holdingOnly = await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { unitId = holding } }));
        Assert.Equal(12, holdingOnly.GetProperty("total").GetInt32());

        // Plant B alone has three people: the selection is suppressed and no figure is given.
        var small = await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "unit", filters = new { unitId = plantB } }));
        Assert.True(small.GetProperty("suppressed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, small.GetProperty("total").ValueKind);
        Assert.Empty(small.GetProperty("weeks").EnumerateArray());
        Assert.Empty(small.GetProperty("groups").EnumerateArray());

        // The mobile channel has three people, under the minimum.
        var mobile = await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { channel = "mobile" } }));
        Assert.True(mobile.GetProperty("suppressed").GetBoolean());
    }

    [Fact]
    public async Task Content_views_filter_by_content_type_and_validate_filters_breakdowns_and_ranges()
    {
        var (t, _, _, _) = await PlantsAsync();
        var admin = t["admin"].Client;

        var views = await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ContentViews, groupBy = "content_type" }));
        Assert.Equal(9, views.GetProperty("total").GetInt32());
        var byType = Groups(views);
        Assert.Equal(new[] { ItemAnalytics.Other }, byType.Keys.ToArray()); // Three policy readers fold, and news with them.
        Assert.Equal(9, byType[ItemAnalytics.Other].GetProperty("value").GetInt32());
        var news = await OkAsync(admin.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ContentViews, filters = new { contentType = "news" } }));
        Assert.Equal(6, news.GetProperty("total").GetInt32());

        async Task BadAsync(object body, string field)
        {
            var error = await OkAsync(admin.PostAsJsonAsync(Query, body), HttpStatusCode.BadRequest);
            Assert.Contains(field, error.GetRawText(), StringComparison.Ordinal);
        }
        await BadAsync(new { metric = "salaries" }, "validation.analytics.metric");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "content_type" }, "validation.analytics.group_by");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "e.user_key" }, "validation.analytics.group_by");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { contentType = "news" } }, "validation.analytics.filter");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { channel = "fax" } }, "validation.analytics.filter");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { membershipType = "robot" } }, "validation.analytics.filter");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { from = "2026-03-01", to = "2026-01-01" } }, "validation.analytics.range");
        await BadAsync(new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { from = "2024-01-01", to = "2026-01-01" } }, "validation.analytics.range");
    }

    [Fact]
    public async Task Scoped_viewer_sees_only_covered_units_and_others_are_denied()
    {
        var (t, holding, plantA, plantB) = await PlantsAsync();
        await world.SeedUserAsync(t, "viewer", MembershipTypes.Employee);
        await world.AssignAsync(t, "viewer", BuiltInRoles.AnalyticsViewer, "org_subtree", plantA);
        var viewer = t["viewer"].Client;

        var workspace = await OkAsync(viewer.GetAsync(Workspace));
        Assert.False(workspace.GetProperty("wholeTenant").GetBoolean());
        Assert.Equal(new[] { plantA }, workspace.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToArray());

        // Without a unit filter the viewer sees their units only.
        var result = await OkAsync(viewer.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "unit" }));
        Assert.Equal(6, result.GetProperty("total").GetInt32());
        Assert.Equal(new[] { plantA.ToString() }, Groups(result).Keys.ToArray());

        // Another unit, or a parent that covers more than they do, is denied.
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { unitId = plantB } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { unitId = holding } })).StatusCode);

        // An employee without analytics rights sees nothing.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a1"].Client.GetAsync(Workspace)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a1"].Client.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople })).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_sees_none_of_the_data_and_the_package_decides_access()
    {
        var (t, _, plantA, _) = await PlantsAsync();
        var other = await world.CreateTenantAsync("aw-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");

        // The other tenant's admin has no events and cannot name this tenant's unit.
        var empty = await OkAsync(other["admin"].Client.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople }));
        Assert.True(empty.GetProperty("suppressed").GetBoolean());
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync(Workspace))).GetProperty("units").EnumerateArray());
        var foreign = await other["admin"].Client.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople, filters = new { unitId = plantA } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);

        // Without the analytics capability the workspace is closed, whatever the role.
        await world.SetCapabilityAsync(t, Capabilities.AnalyticsCore, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await t["admin"].Client.GetAsync(Workspace)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["admin"].Client.PostAsJsonAsync(Query, new { metric = Modules.Analytics.Workspace.ActivePeople })).StatusCode);
    }
}
