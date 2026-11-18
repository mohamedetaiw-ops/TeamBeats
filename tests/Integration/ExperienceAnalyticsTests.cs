using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 T015 (US9, FR-017 to FR-021): template use by owning unit from the page inventory, personalization against
/// use and resets with the minimum group size, p75 vitals with slow published variants flagged, and task flows on the
/// outcome contract; whole tenant analytics viewers only, the package decides, and tenants stay apart.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ExperienceAnalyticsTests(TenantWorld world)
{
    private const string Experience = "/api/v1/analytics/experience";

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

    private static Task SendAsync(TestTenant t, string user, params object[] events) =>
        OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/analytics/events", new { events }), HttpStatusCode.Accepted);

    private static object Vitals(Guid page, int release, string device, double lcp) =>
        new { name = AnalyticsEvents.PageVitals, channel = "web", fields = new { pageId = page, release, deviceClass = device, theme = "beats", lcp, inp = 120, cls = 0.02 } };

    private static object Outcome(string outcome, string? step = null) =>
        new { name = AnalyticsEvents.TaskOutcome, channel = "web", fields = step is null ? (object)new { flow = "submit_request", outcome } : new { flow = "submit_request", outcome, step } };

    private async Task<(TestTenant T, Guid Dubai, Guid Published, Guid Draft)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("xa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        for (var i = 1; i <= 6; i++)
        {
            await world.SeedUserAsync(t, "d" + i, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, "d" + i, "Dubai");
        }
        await world.SeedUserAsync(t, "head", MembershipTypes.Employee);
        await world.AssignAsync(t, "head", BuiltInRoles.AnalyticsViewer, "org_subtree", dubai);
        await DispatchAsync();
        var published = (await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "x-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = dubai }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{published}/publish", new { reason = "first version" }));
        var draft = (await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T02", slug = "x-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = (Guid?)null }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return (t, dubai, published, draft);
    }

    [Fact]
    public async Task Templates_personalization_vitals_and_outcomes_add_up_with_the_minimum_group_size()
    {
        var (t, dubai, published, _) = await FreshAsync();
        await SendAsync(t, "admin", [.. Enumerable.Range(0, 6).Select(_ => Vitals(published, 1, "mobile", 4000)), Vitals(published, 1, "desktop", 900), Vitals(published, 1, "desktop", 1100)]);
        for (var i = 1; i <= 6; i++)
        {
            var events = new List<object> { new { name = AnalyticsEvents.PageViewed, channel = "web", fields = new { area = "home" } }, Outcome("start") };
            events.Add(i == 1 ? Outcome("complete") : Outcome("abandon", "form"));
            if (i <= 2)
            {
                events.Add(new { name = AnalyticsEvents.PersonalizationChanged, channel = "web", fields = new { action = "customize" } });
            }
            if (i == 3)
            {
                events.Add(new { name = AnalyticsEvents.PersonalizationChanged, channel = "web", fields = new { action = "reset" } });
            }
            await SendAsync(t, "d" + i, [.. events]);
        }
        await DispatchAsync();

        var view = await OkAsync(t["admin"].Client.GetAsync(Experience));
        var templates = view.GetProperty("templates").EnumerateArray().ToDictionary(r => r.GetProperty("template").GetString()!, r => r);
        Assert.Equal((1, 1), (templates["T01"].GetProperty("pages").GetInt32(), templates["T01"].GetProperty("published").GetInt32()));
        Assert.Equal(0, templates["T02"].GetProperty("published").GetInt32());
        Assert.True(view.GetProperty("layouts").GetArrayLength() >= 1);
        var scope = view.GetProperty("scopes").EnumerateArray().Single(s => s.GetProperty("unit").GetString() == dubai.ToString());
        Assert.Equal("Dubai", scope.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("T01", Assert.Single(scope.GetProperty("templates").EnumerateArray()).GetProperty("key").GetString());

        var personal = view.GetProperty("personalization");
        Assert.Equal((6, 2, 1), (personal.GetProperty("total").GetProperty("people").GetInt32(), personal.GetProperty("total").GetProperty("customized").GetInt32(),
            personal.GetProperty("total").GetProperty("reset").GetInt32()));
        Assert.Equal(dubai.ToString(), Assert.Single(personal.GetProperty("units").EnumerateArray()).GetProperty("group").GetString());

        var performance = view.GetProperty("performance");
        Assert.Equal(8, performance.GetProperty("samples").GetInt32());
        var mobile = performance.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("dimension").GetString() == "device" && r.GetProperty("group").GetString() == "mobile");
        Assert.Equal(4000, mobile.GetProperty("lcp").GetDouble());
        Assert.Contains(performance.GetProperty("rows").EnumerateArray(), r => r.GetProperty("dimension").GetString() == "template" && r.GetProperty("group").GetString() == "T01");
        var flag = Assert.Single(performance.GetProperty("flags").EnumerateArray());
        Assert.Equal((published, "mobile"), (flag.GetProperty("pageId").GetGuid(), flag.GetProperty("deviceClass").GetString()));
        Assert.Equal(["lcp"], flag.GetProperty("missed").EnumerateArray().Select(m => m.GetString()));

        var flow = Assert.Single(view.GetProperty("outcomes").EnumerateArray());
        Assert.Equal(("submit_request", 6, 1, 5, 0.1667, "form"), (flow.GetProperty("flow").GetString(), flow.GetProperty("started").GetInt32(), flow.GetProperty("completed").GetInt32(),
            flow.GetProperty("abandoned").GetInt32(), flow.GetProperty("completionRate").GetDouble(), flow.GetProperty("dropOffStep").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.GetAsync($"{Experience}?from=2026-03-01&to=2026-01-01")).StatusCode);
    }

    [Fact]
    public async Task Only_whole_tenant_viewers_with_the_package_see_it_and_tenants_stay_apart()
    {
        var (t, _, published, _) = await FreshAsync();
        await SendAsync(t, "admin", Vitals(published, 1, "mobile", 4000));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["head"].Client.GetAsync(Experience)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["d1"].Client.GetAsync(Experience)).StatusCode);

        var other = await world.CreateTenantAsync("xa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var theirs = await OkAsync(other["admin"].Client.GetAsync(Experience));
        Assert.Equal(0, theirs.GetProperty("performance").GetProperty("samples").GetInt32());
        Assert.DoesNotContain(published.ToString(), theirs.GetRawText(), StringComparison.OrdinalIgnoreCase);

        // Under the minimum group size the personalization counts are hidden.
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("personalization").GetProperty("total").GetProperty("customized").ValueKind);

        await world.SetCapabilityAsync(other, Capabilities.AnalyticsExperience, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await other["admin"].Client.GetAsync(Experience)).StatusCode);
    }
}
