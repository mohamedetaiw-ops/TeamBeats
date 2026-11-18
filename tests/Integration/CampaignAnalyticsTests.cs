using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T011 (US2, FR-009): campaign analytics give the funnel per step and in total, deliveries by channel, the
/// breakdown by unit with the minimum group size and complementary suppression, return on effort from the tenant's
/// versioned estimates, and two campaigns side by side; Content decides who may see a campaign, the package needs
/// analytics.advanced, and nothing crosses tenants.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CampaignAnalyticsTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Campaigns = "/api/v1/content/campaigns";
    private const string Analytics = "/api/v1/analytics/campaigns";
    private const string Estimates = "/api/v1/analytics/estimates";

    private sealed record Org(TestTenant T, Guid Holding, Guid Abu);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("can-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Dubai", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        var abu = await world.CreateUnitAsync(t, "Abu", holding, "department");
        foreach (var (prefix, unit, count) in new[] { ("d", "Dubai", 6), ("c", "Cairo", 5), ("a", "Abu", 2) })
        {
            for (var i = 1; i <= count; i++)
            {
                await world.SeedUserAsync(t, prefix + i, MembershipTypes.Employee);
                await world.AddMembershipAsync(t, prefix + i, unit);
            }
        }
        await world.SeedUserAsync(t, "pia", MembershipTypes.Employee);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return new Org(t, holding, abu);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static async Task<Guid> PublishedAsync(HttpClient client, string title)
    {
        var variants = new Dictionary<string, object>
        {
            ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
        };
        var id = Id(await OkAsync(client.PostAsJsonAsync(Items, new { type = "news", variants }), HttpStatusCode.Created));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private static object Audience(Guid unit) => new { anyOf = new[] { new { orgUnits = new[] { new { id = unit, subtree = true } } } } };

    private async Task RunAsync(TestTenant t, DateTimeOffset now)
    {
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 10 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
    }

    /// <summary>A started campaign whose steps are due now, already sent.</summary>
    private async Task<(Guid Campaign, Guid[] Steps)> SentCampaignAsync(Org o, string name, Guid unit, params Guid[] items)
    {
        var pia = o.T["pia"].Client;
        var now = DateTimeOffset.UtcNow;
        var created = await OkAsync(pia.PostAsJsonAsync(Campaigns, new
        {
            name,
            audience = Audience(unit),
            steps = items.Select((item, i) => new { itemId = item, at = now.AddMinutes(-10 + i) }).ToArray(),
        }), HttpStatusCode.Created);
        var id = Id(created);
        await OkAsync(pia.PostAsync($"{Campaigns}/{id}/start", null));
        await RunAsync(o.T, now);
        return (id, created.GetProperty("steps").EnumerateArray().Select(Id).ToArray());
    }

    [Fact]
    public async Task Funnel_counts_steps_and_people_folds_small_units_and_works_out_return_on_effort()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var item = await PublishedAsync(pia, "Benefits enrolment opens");
        var (id, steps) = await SentCampaignAsync(o, "Benefits enrolment", o.Holding, item);

        // Two come through the campaign link and one opens the item another way.
        await OkAsync(o.T["d1"].Client.GetAsync($"{Items}/{item}/view?campaign={steps[0]:N}"));
        await OkAsync(o.T["d2"].Client.GetAsync($"{Items}/{item}/view?campaign={steps[0]:N}"));
        await OkAsync(o.T["c1"].Client.GetAsync($"{Items}/{item}/view"));

        var list = await OkAsync(pia.GetAsync(Analytics));
        Assert.Contains(list.EnumerateArray(), c => Id(c) == id && c.GetProperty("steps").GetInt32() == 1);

        var view = await OkAsync(pia.GetAsync($"{Analytics}/{id}"));
        Assert.Equal(5, view.GetProperty("minGroupSize").GetInt32());
        var total = view.GetProperty("total");
        Assert.Equal(13, total.GetProperty("people").GetInt32());
        Assert.Equal(13, total.GetProperty("delivered").GetInt32());
        Assert.Equal(3, total.GetProperty("viewed").GetInt32());
        Assert.Equal(2, total.GetProperty("clicked").GetInt32());
        Assert.Equal(3, total.GetProperty("completed").GetInt32()); // The item asks for no confirmation, so opening it completes it.
        Assert.Equal(0.2308, total.GetProperty("viewedShare").GetDouble());
        Assert.Equal(13, view.GetProperty("channels").GetProperty("in_app").GetInt32());
        var step = Assert.Single(view.GetProperty("steps").EnumerateArray());
        Assert.Equal("Benefits enrolment opens", step.GetProperty("titles").GetProperty("en").GetString());
        Assert.Equal(13, step.GetProperty("channels").GetProperty("in_app").GetInt32());
        Assert.Equal(2, step.GetProperty("counts").GetProperty("clicked").GetInt32());

        // Abu (two) folds; "other" is still under five, so Cairo (five, the smallest shown) folds with it; Dubai stays.
        var units = view.GetProperty("groups").EnumerateArray().Where(g => g.GetProperty("dimension").GetString() == "unit")
            .ToDictionary(g => g.GetProperty("group").GetString()!, g => g);
        Assert.Equal(2, units.Count);
        Assert.Equal(7, units[ItemAnalytics.Other].GetProperty("audience").GetInt32());
        var dubai = units.Single(u => u.Key != ItemAnalytics.Other).Value;
        Assert.Equal("Dubai", dubai.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal(6, dubai.GetProperty("audience").GetInt32());
        Assert.Equal(2, dubai.GetProperty("viewed").GetInt32());

        // No estimates yet: no return on effort.
        Assert.Equal(JsonValueKind.Null, view.GetProperty("effort").ValueKind);

        // Only a settings administrator sets estimates, with the version as a lock; every change is a new version.
        var admin = o.T["admin"].Client;
        Assert.Equal(0, (await OkAsync(admin.GetAsync(Estimates))).GetProperty("version").GetInt32());
        var request = new { authoringHoursPerItem = 2m, costPerHour = 100m, currency = "SAR", costPerSend = new Dictionary<string, decimal> { ["in_app"] = 0.1m, ["email"] = 0.5m }, version = 0 };
        Assert.Equal(HttpStatusCode.Forbidden, (await pia.PutAsJsonAsync(Estimates, request)).StatusCode);
        Assert.Equal(1, (await OkAsync(admin.PutAsJsonAsync(Estimates, request))).GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.AnalyticsEstimatesChanged, null);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync(Estimates, request)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Estimates, request with { currency = "riyal", version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Estimates, new { authoringHoursPerItem = 2m, costPerHour = 100m, currency = "SAR",
            costPerSend = new Dictionary<string, decimal> { ["fax"] = 1m }, version = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync(Estimates, request with { costPerHour = -1m, version = 1 })).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(o.T.Id, "select count(*) from analytics.effort_estimate"));

        var effort = (await OkAsync(pia.GetAsync($"{Analytics}/{id}"))).GetProperty("effort");
        Assert.Equal("SAR", effort.GetProperty("currency").GetString());
        Assert.Equal(200m, effort.GetProperty("authoringCost").GetDecimal());
        var sends = effort.GetProperty("sendCost").GetDecimal();
        Assert.True(sends >= 1.3m, $"send cost {sends}"); // Thirteen in-app sends at 0.1, and any email that went out.
        var cost = effort.GetProperty("totalCost").GetDecimal();
        Assert.Equal(200m + sends, cost);
        Assert.Equal(Math.Round(cost / 3, 2), effort.GetProperty("perReached").GetDecimal());
        Assert.Equal(Math.Round(cost / 3, 2), effort.GetProperty("perCompleted").GetDecimal());
        Assert.Equal(1, effort.GetProperty("estimatesVersion").GetInt32());
    }

    [Fact]
    public async Task Two_campaigns_compare_side_by_side_and_a_small_one_hides_what_people_did()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var first = await PublishedAsync(pia, "Wellness week");
        var second = await PublishedAsync(pia, "Wellness week, last call");
        var (big, _) = await SentCampaignAsync(o, "Wellness", o.Holding, first, second);
        var (small, _) = await SentCampaignAsync(o, "Abu only", o.Abu, first);

        var comparison = await OkAsync(pia.GetAsync($"{Analytics}/compare?first={big}&second={small}"));
        Assert.Equal("Wellness", comparison.GetProperty("first").GetProperty("campaign").GetProperty("name").GetString());
        Assert.Equal(2, comparison.GetProperty("first").GetProperty("steps").GetArrayLength());
        Assert.Equal(26, comparison.GetProperty("first").GetProperty("total").GetProperty("delivered").GetInt32());
        var smallTotal = comparison.GetProperty("second").GetProperty("total");
        Assert.Equal(2, smallTotal.GetProperty("people").GetInt32());
        Assert.Equal(JsonValueKind.Null, smallTotal.GetProperty("viewed").ValueKind);
        Assert.Equal(JsonValueKind.Null, smallTotal.GetProperty("completed").ValueKind);
        Assert.Empty(comparison.GetProperty("second").GetProperty("groups").EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await pia.GetAsync($"{Analytics}/compare?first={big}&second={big}")).StatusCode);
    }

    [Fact]
    public async Task Content_decides_who_sees_a_campaign_the_package_decides_access_and_tenants_stay_apart()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var item = await PublishedAsync(pia, "Town hall");
        var (id, _) = await SentCampaignAsync(o, "Town hall", o.Holding, item);

        // Someone who received it but may not run campaigns at the site does not find it.
        Assert.Equal(HttpStatusCode.NotFound, (await o.T["d1"].Client.GetAsync($"{Analytics}/{id}")).StatusCode);
        Assert.Empty((await OkAsync(o.T["d1"].Client.GetAsync(Analytics))).EnumerateArray());

        var other = await world.CreateTenantAsync("can-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Analytics}/{id}")).StatusCode);

        // Workplace has no advanced analytics.
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["pia"].Client.GetAsync(Analytics)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["admin"].Client.GetAsync(Estimates)).StatusCode);
    }
}
