using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T014 (US6, FR-010): a unit or location dashboard combines adoption, reach, compliance, requests and
/// attendance for one scope beside the organization figure, which is hidden when it could be one other scope's number;
/// a small scope shows "not enough data"; a unit head sees their unit only, locations need tenant scope, tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ScopeDashboardTests(TenantWorld world)
{
    private const string Scopes = "/api/v1/analytics/scopes";

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

    private static Task SendAsync(TestTenant t, string user, bool read) => OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/analytics/events", new
    {
        events = read
            ? new object[] { new { name = AnalyticsEvents.PageViewed, channel = "web", fields = new { area = "home" } },
                new { name = AnalyticsEvents.ContentViewed, channel = "web", fields = new { source = "content", itemId = Guid.NewGuid(), contentType = "news" } } }
            : new object[] { new { name = AnalyticsEvents.PageViewed, channel = "web", fields = new { area = "home" } } },
    }), HttpStatusCode.Accepted);

    private static Dictionary<string, JsonElement> Tiles(JsonElement view) => view.GetProperty("tiles").EnumerateArray().ToDictionary(t => t.GetProperty("key").GetString()!, t => t);

    private static double? Num(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetDouble();

    private async Task<(TestTenant T, Guid Dubai, Guid Cairo, Guid Abu)> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sd-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        var cairo = await world.CreateUnitAsync(t, "Cairo", holding, "department");
        var abu = await world.CreateUnitAsync(t, "Abu", holding, "department");
        foreach (var (prefix, unit, count) in new[] { ("d", "Dubai", 6), ("c", "Cairo", 5), ("a", "Abu", 2) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
            }
        }
        await world.SeedUserAsync(t, "head", MembershipTypes.Employee);
        await world.AssignAsync(t, "head", BuiltInRoles.AnalyticsViewer, "org_subtree", dubai);
        await DispatchAsync();
        return (t, dubai, cairo, abu);
    }

    [Fact]
    public async Task A_unit_head_sees_their_unit_beside_the_organization_and_a_small_unit_has_not_enough_data()
    {
        var (t, dubai, cairo, abu) = await FreshAsync();
        foreach (var user in new[] { "d1", "d2", "d3", "d4" })
        {
            await SendAsync(t, user, read: user is "d1" or "d2");
        }
        await DispatchAsync();

        var list = await OkAsync(t["head"].Client.GetAsync(Scopes));
        Assert.Equal(dubai.ToString(), Assert.Single(list.GetProperty("units").EnumerateArray()).GetProperty("key").GetString());
        Assert.Empty(list.GetProperty("locations").EnumerateArray());

        var view = await OkAsync(t["head"].Client.GetAsync($"{Scopes}/unit/{dubai}"));
        Assert.Equal("Dubai", view.GetProperty("scope").GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(6, view.GetProperty("people").GetInt32());
        Assert.True(view.GetProperty("enough").GetBoolean());
        Assert.True(view.GetProperty("groupShown").GetBoolean());
        var tiles = Tiles(view);
        // Fifteen active people: thirteen in units, the administrator and the head.
        Assert.Equal((4, 6, 0.6667, 0.2667), (tiles["adoption"].GetProperty("value").GetInt32(), tiles["adoption"].GetProperty("of").GetInt32(),
            Num(tiles["adoption"], "rate"), Num(tiles["adoption"], "groupRate")));
        Assert.Equal(0.3333, Num(tiles["reach"], "rate"));
        Assert.Equal(0, tiles[AnalyticsScopeFigures.OpenRequests].GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, tiles[AnalyticsScopeFigures.OpenRequests].GetProperty("of").ValueKind);
        // No request was filed: breaches have nothing to divide by.
        Assert.False(tiles[AnalyticsScopeFigures.Breaches].GetProperty("enough").GetBoolean());
        Assert.False(tiles.ContainsKey(AnalyticsScopeFigures.Compliance)); // No open campaign.
        Assert.True(tiles[AnalyticsScopeFigures.AttendanceExceptions].GetProperty("of").GetInt32() > 0);

        Assert.Equal(HttpStatusCode.Forbidden, (await t["head"].Client.GetAsync($"{Scopes}/unit/{cairo}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["head"].Client.GetAsync($"{Scopes}/location/dubai")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["d1"].Client.GetAsync(Scopes)).StatusCode);

        var small = await OkAsync(t["admin"].Client.GetAsync($"{Scopes}/unit/{abu}"));
        Assert.Equal(2, small.GetProperty("people").GetInt32());
        Assert.False(small.GetProperty("enough").GetBoolean());
        Assert.All(small.GetProperty("tiles").EnumerateArray(), tile =>
        {
            Assert.False(tile.GetProperty("enough").GetBoolean());
            Assert.Equal(JsonValueKind.Null, tile.GetProperty("value").ValueKind);
        });
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.GetAsync($"{Scopes}/unit/{dubai}?from=2026-03-01&to=2026-01-01")).StatusCode);
    }

    [Fact]
    public async Task A_location_needs_tenant_scope_and_the_organization_figure_hides_when_it_would_be_one_other_scope()
    {
        var (t, dubai, _, _) = await FreshAsync();
        await OkAsync(t["admin"].Client.GetAsync("/api/v1/people"));
        Assert.Equal(6, await world.Database.OwnerExecInTenantAsync(t.Id, "update people.profile set location_key = 'dubai' where user_id = any(@u)",
            ("u", new[] { "d1", "d2", "d3", "d4", "d5", "d6" }.Select(u => t[u].Id).ToArray())));
        await SendAsync(t, "d1", read: false);
        await DispatchAsync();

        var list = await OkAsync(t["admin"].Client.GetAsync(Scopes));
        Assert.Contains(list.GetProperty("locations").EnumerateArray(), l => l.GetProperty("key").GetString() == "dubai");
        Assert.Equal(4, list.GetProperty("units").GetArrayLength());

        // Everyone else has no location: one other group, so the organization figure would be theirs alone.
        var view = await OkAsync(t["admin"].Client.GetAsync($"{Scopes}/location/dubai"));
        Assert.Equal(6, view.GetProperty("people").GetInt32());
        Assert.False(view.GetProperty("groupShown").GetBoolean());
        var adoption = Tiles(view)["adoption"];
        Assert.Equal(0.1667, Num(adoption, "rate"));
        Assert.Null(Num(adoption, "groupRate"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"{Scopes}/location/nowhere")).StatusCode);

        var other = await world.CreateTenantAsync("sd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Scopes}/unit/{dubai}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Scopes}/location/dubai")).StatusCode);

        var basic = await world.CreateTenantAsync("sd-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync(Scopes)).StatusCode);
    }

    [Fact]
    public async Task Compliance_counts_the_scope_s_attestations_in_open_campaigns()
    {
        var (t, dubai, _, _) = await FreshAsync();
        var titles = new Dictionary<string, string> { ["en"] = "Code of conduct", ["ar"] = "مدونة السلوك" };
        await world.SeedUserAsync(t, "co", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "legal", MembershipTypes.Employee);
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        var policy = (await OkAsync(t["co"].Client.PostAsJsonAsync("/api/v1/policies", new { code = "P-1", names = titles, route = new[] { new { approvers = new[] { t["legal"].Id } } } }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var body = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "Conduct" } } } } };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"/api/v1/policies/{policy}/draft", new
        {
            titles, material = true, clauses = new[] { new { level = 1, titles, bodies = new Dictionary<string, object> { ["en"] = body, ["ar"] = body } } },
        }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"/api/v1/policies/{policy}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"/api/v1/policies/{policy}/campaigns", new { }), HttpStatusCode.Created);
        foreach (var user in new[] { "d1", "d2", "d3", "d4", "d5" })
        {
            await OkAsync(t[user].Client.PostAsJsonAsync($"/api/v1/policies/{policy}/attest", new { }), HttpStatusCode.Created);
        }

        var compliance = Tiles(await OkAsync(t["head"].Client.GetAsync($"{Scopes}/unit/{dubai}")))[AnalyticsScopeFigures.Compliance];
        // Seventeen in the audience: the thirteen, the administrator, the head, the compliance officer and legal.
        Assert.Equal((5, 6, 0.8333, 0.2941), (compliance.GetProperty("value").GetInt32(), compliance.GetProperty("of").GetInt32(), Num(compliance, "rate"), Num(compliance, "groupRate")));
    }
}
