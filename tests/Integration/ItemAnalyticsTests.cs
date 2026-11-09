using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T005 and T006 (US1, FR-004, FR-005): item analytics give audience size, unique readers, reach,
/// acknowledgement, reactions, comments and a daily trend; the breakdown by unit merges groups under the minimum size into
/// "other" and never leaves "other" alone under it; "remind this group" reaches only the people in the group who have
/// not read, or not acknowledged when the item asks for it; rights are decided by the owning module (content and
/// policies); and nothing is reachable from another tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ItemAnalyticsTests(TenantWorld world)
{
    private const string Analytics = "/api/v1/analytics/items";
    private const string Items = "/api/v1/content/items";
    private const string Policies = "/api/v1/policies";

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

    private Task AggregateAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var read = await sp.GetRequiredService<AnalyticsAggregator>().RunAsync(5000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return read;
        });

    private static Task ViewAsync(TestTenant t, string user, Guid item, string source) =>
        OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/analytics/events",
            new { events = new[] { new { name = AnalyticsEvents.ContentViewed, fields = new { source, itemId = item } } } }), HttpStatusCode.Accepted);

    private Task<long> NotifiedAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static object Units(params Guid[] units) => new { anyOf = new[] { new { orgUnits = units.Select(u => new { id = u }).ToArray() } } };

    private static Dictionary<string, JsonElement> Groups(JsonElement view) =>
        view.GetProperty("groups").EnumerateArray().ToDictionary(g => g.GetProperty("group").GetString()!, g => g);

    private async Task<(TestTenant T, Guid PlantA, Guid PlantB, Guid Cairo)> PlantsAsync()
    {
        var t = await world.CreateTenantAsync("ia-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var units = new Dictionary<string, Guid>
        {
            ["PlantA"] = await world.CreateUnitAsync(t, "PlantA", holding, "department"),
            ["PlantB"] = await world.CreateUnitAsync(t, "PlantB", holding, "department"),
            ["Cairo"] = await world.CreateUnitAsync(t, "Cairo", holding, "department"),
        };
        foreach (var (prefix, unit, count) in new[] { ("a", "PlantA", 6), ("b", "PlantB", 3), ("c", "Cairo", 3) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
            }
        }
        await world.SeedUserAsync(t, "out", MembershipTypes.Employee);
        await DispatchAsync();
        return (t, units["PlantA"], units["PlantB"], units["Cairo"]);
    }

    [Fact]
    public async Task Item_reach_counts_readers_in_the_audience_merges_small_groups_and_reminds_only_non_readers()
    {
        var (t, plantA, plantB, cairo) = await PlantsAsync();
        var admin = t["admin"].Client;
        var created = await OkAsync(admin.PostAsJsonAsync(Items, new
        {
            type = "announcement",
            variants = new Dictionary<string, object> { ["en"] = new { title = "Evacuation procedure", body = Body("Use the east exit.") } },
            audience = Units(plantA, plantB, cairo),
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var view = $"{Analytics}/content/{id}";

        // A draft has no analytics.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(view)).StatusCode);
        await OkAsync(admin.PostAsJsonAsync($"{Items}/{id}/publish", new { material = true }));
        await DispatchAsync();

        foreach (var user in new[] { "a1", "a1", "a2", "a3", "a4", "b1", "out" })
        {
            await ViewAsync(t, user, id, "content");
        }
        await OkAsync(t["a1"].Client.PostAsJsonAsync($"{Items}/{id}/reactions", new { kind = "like" }));
        await AggregateAsync(t);

        // Scenario 1: twelve people are targeted and five of them read it; someone outside the audience is not counted.
        var result = await OkAsync(admin.GetAsync(view));
        Assert.Equal(12, result.GetProperty("audience").GetInt32());
        Assert.Equal(5, result.GetProperty("readers").GetInt32());
        Assert.Equal(0.4167, result.GetProperty("reach").GetDouble());
        Assert.False(result.GetProperty("acknowledgementRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("acknowledged").ValueKind);
        Assert.Equal(1, result.GetProperty("reactions").GetInt32());
        Assert.Equal("Evacuation procedure", result.GetProperty("titles").GetProperty("en").GetString());
        Assert.Equal(5, result.GetProperty("minGroupSize").GetInt32());
        var day = Assert.Single(result.GetProperty("trend").EnumerateArray());
        Assert.Equal(7, day.GetProperty("views").GetInt32());
        Assert.Equal(6, day.GetProperty("readers").GetInt32()); // First reads, including the reader outside the audience.

        // Scenario 2: Plant A has six people and is shown; Plant B and Cairo have three each and are only seen together.
        var groups = Groups(result);
        Assert.Equal(new[] { plantA.ToString(), ItemAnalytics.Other }.Order(StringComparer.Ordinal), groups.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("PlantA", groups[plantA.ToString()].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(6, groups[plantA.ToString()].GetProperty("audience").GetInt32());
        Assert.Equal(4, groups[plantA.ToString()].GetProperty("readers").GetInt32());
        Assert.Equal(6, groups[ItemAnalytics.Other].GetProperty("audience").GetInt32());
        Assert.Equal(1, groups[ItemAnalytics.Other].GetProperty("readers").GetInt32());
        Assert.Empty(groups[ItemAnalytics.Other].GetProperty("names").EnumerateObject());

        // Scenario 3: "remind this group" reaches the five in "other" who have not read it, never the reader; once an hour.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{view}/remind", new { group = plantB.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"{view}/remind", new { })).StatusCode);
        var sent = await OkAsync(admin.PostAsJsonAsync($"{view}/remind", new { group = ItemAnalytics.Other }));
        Assert.Equal(5, sent.GetProperty("sent").GetInt32());
        Assert.Equal(0, await NotifiedAsync(t, "b1", "content.analytics.reminder"));
        Assert.Equal(1, await NotifiedAsync(t, "b2", "content.analytics.reminder"));
        Assert.Equal(1, await NotifiedAsync(t, "c3", "content.analytics.reminder"));
        Assert.Equal(0, await NotifiedAsync(t, "a5", "content.analytics.reminder"));
        Assert.Equal(0, (await OkAsync(admin.PostAsJsonAsync($"{view}/remind", new { group = ItemAnalytics.Other }))).GetProperty("sent").GetInt32());

        // Rights come from Content: a reader without rights on the item is denied, an unknown item or source is not found.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a1"].Client.GetAsync(view)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["a1"].Client.PostAsJsonAsync($"{view}/remind", new { group = ItemAnalytics.Other })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Analytics}/content/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Analytics}/intranet/{id}")).StatusCode);

        // Another tenant's administrator cannot reach the item.
        var other = await world.CreateTenantAsync("ia-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(view)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{view}/remind", new { group = ItemAnalytics.Other })).StatusCode);
    }

    [Fact]
    public async Task A_critical_announcement_counts_confirmations_and_reminds_those_who_have_not_confirmed()
    {
        var (t, plantA, _, _) = await PlantsAsync();
        var admin = t["admin"].Client;
        var id = (await OkAsync(admin.PostAsJsonAsync(Items, new
        {
            type = "announcement",
            variants = new Dictionary<string, object> { ["en"] = new { title = "Gas leak drill", body = Body("Assemble at point B.") } },
            audience = Units(plantA),
            critical = new { presentation = "banner", requireAck = true },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(admin.PostAsJsonAsync($"{Items}/{id}/publish", new { material = true }));
        await DispatchAsync();

        foreach (var user in new[] { "a1", "a2", "a3" })
        {
            await ViewAsync(t, user, id, "content");
        }
        foreach (var user in new[] { "a1", "a4" })
        {
            await OkAsync(t[user].Client.PostAsJsonAsync($"{Items}/{id}/acknowledge", new { channel = "web" }));
        }
        await AggregateAsync(t);

        var view = $"{Analytics}/content/{id}";
        var result = await OkAsync(admin.GetAsync(view));
        Assert.Equal(6, result.GetProperty("audience").GetInt32());
        Assert.Equal(3, result.GetProperty("readers").GetInt32());
        Assert.True(result.GetProperty("acknowledgementRequired").GetBoolean());
        Assert.Equal(2, result.GetProperty("acknowledged").GetInt32());
        Assert.Equal(0.3333, result.GetProperty("acknowledgedShare").GetDouble());
        var plant = Assert.Single(result.GetProperty("groups").EnumerateArray());
        Assert.Equal(2, plant.GetProperty("acknowledged").GetInt32());

        // Reminders go to the four who have not confirmed, including those who read it without confirming.
        var sent = await OkAsync(admin.PostAsJsonAsync($"{view}/remind", new { group = plantA.ToString() }));
        Assert.Equal(4, sent.GetProperty("sent").GetInt32());
        Assert.Equal(1, await NotifiedAsync(t, "a2", "content.analytics.reminder"));
        Assert.Equal(0, await NotifiedAsync(t, "a4", "content.analytics.reminder"));
    }

    [Fact]
    public async Task Policy_analytics_follow_the_open_campaign_and_are_only_for_its_owner_and_compliance_officers()
    {
        var t = await world.CreateTenantAsync("ip-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        await world.SeedUserAsync(t, "co", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "legal", MembershipTypes.Employee);
        foreach (var (name, unit) in new[] { ("g1", "Gulf"), ("g2", "Gulf"), ("g3", "Gulf"), ("g4", "Gulf"), ("g5", "Gulf"), ("e1", "Egypt"), ("e2", "Egypt") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        var titles = new Dictionary<string, string> { ["en"] = "Code of conduct", ["ar"] = "قواعد السلوك" };
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code = "COC", names = titles, orgUnitId = holding, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var view = $"{Analytics}/policies/{id}";
        Assert.Equal(HttpStatusCode.NotFound, (await t["co"].Client.GetAsync(view)).StatusCode); // Nothing is in force yet.

        var clauses = new[]
        {
            new
            {
                level = 1, titles = new Dictionary<string, string> { ["en"] = "Gifts", ["ar"] = "الهدايا" },
                bodies = new Dictionary<string, object> { ["en"] = Body("Declare every gift."), ["ar"] = Body("صرّح بكل هدية.") },
            },
        };
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new { titles, clauses, material = true }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        await DispatchAsync();

        // Before a campaign: the people the policy applies to, with no acknowledgement asked.
        var before = await OkAsync(t["co"].Client.GetAsync(view));
        Assert.Equal(7, before.GetProperty("audience").GetInt32());
        Assert.False(before.GetProperty("acknowledgementRequired").GetBoolean());

        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/campaigns", new { }), HttpStatusCode.Created);
        await DispatchAsync();
        foreach (var user in new[] { "g1", "e1" })
        {
            await ViewAsync(t, user, id, "policies");
        }
        foreach (var user in new[] { "g1", "g2" })
        {
            await OkAsync(t[user].Client.PostAsJsonAsync($"{Policies}/{id}/attest", new { channel = "web" }), HttpStatusCode.Created);
        }
        await AggregateAsync(t);

        var result = await OkAsync(t["co"].Client.GetAsync(view));
        Assert.Equal(7, result.GetProperty("audience").GetInt32());
        Assert.Equal(2, result.GetProperty("readers").GetInt32());
        Assert.True(result.GetProperty("acknowledgementRequired").GetBoolean());
        Assert.Equal(2, result.GetProperty("acknowledged").GetInt32());
        Assert.Equal("Code of conduct", result.GetProperty("titles").GetProperty("en").GetString());

        // Egypt's two would be "other" on their own, under the minimum, so Gulf joins them (complementary suppression).
        var other = Assert.Single(result.GetProperty("groups").EnumerateArray());
        Assert.Equal(ItemAnalytics.Other, other.GetProperty("group").GetString());
        Assert.Equal(7, other.GetProperty("audience").GetInt32());

        // Five have not attested and are reminded; a reader the policy applies to may not see its analytics.
        Assert.Equal(5, (await OkAsync(t["co"].Client.PostAsJsonAsync($"{view}/remind", new { group = ItemAnalytics.Other }))).GetProperty("sent").GetInt32());
        Assert.Equal(0, await NotifiedAsync(t, "g1", "policies.analytics.reminder"));
        Assert.Equal(1, await NotifiedAsync(t, "e2", "policies.analytics.reminder"));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["g3"].Client.GetAsync(view)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["g3"].Client.PostAsJsonAsync($"{view}/remind", new { group = ItemAnalytics.Other })).StatusCode);
    }
}
