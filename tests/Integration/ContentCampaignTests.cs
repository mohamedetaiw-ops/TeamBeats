using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R2 US8 and FR-015 (T033): a campaign sends its due steps through spec 015 to its audience, only to people who
/// may read the item; "only those who have not acted" leaves out people who opened it; mandatory steps are labelled and
/// optional ones are announcements under each person's preferences; the dashboard counts per step and in total with the
/// minimum group size; the plan, the site's publishers and the tenant bound campaigns.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentCampaignTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Campaigns = "/api/v1/content/campaigns";
    private static readonly string[] Staff = ["s1", "s2", "s3", "s4", "s5", "s6"];

    private sealed record Org(TestTenant T, Guid Dubai);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ccm-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in Staff.Append("pia").Append("out"))
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in Staff)
        {
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return new Org(t, dubai);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<string?> ProblemKeyAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString();
        }
        return problem.TryGetProperty("detailKey", out var key) ? key.GetString() : problem.GetProperty("code").GetString();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static object Variants(string title) => new Dictionary<string, object>
    {
        ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
    };

    private static async Task<Guid> PublishedAsync(HttpClient client, string title, bool publish = true)
    {
        var id = Id(await OkAsync(client.PostAsJsonAsync(Items, new { type = "news", variants = Variants(title) }), HttpStatusCode.Created));
        if (publish)
        {
            await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        }
        return id;
    }

    private static object DubaiAudience(Guid dubai) => new { anyOf = new[] { new { orgUnits = new[] { new { id = dubai, subtree = true } } } } };

    private Task RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    private static async Task<JsonElement[]> NotificationsAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().ToArray();

    private static async Task<JsonElement?> CampaignNoticeAsync(HttpClient client, Guid item) =>
        (await NotificationsAsync(client)).Cast<JsonElement?>().FirstOrDefault(n => n!.Value.GetProperty("link").GetString()!.StartsWith($"/content/read/{item}?c=", StringComparison.Ordinal));

    [Fact]
    public async Task Due_steps_reach_the_audience_and_a_follow_up_goes_only_to_those_who_have_not_opened_it()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var item = await PublishedAsync(pia, "Benefits enrolment opens");
        var now = DateTimeOffset.UtcNow;
        var campaign = await OkAsync(pia.PostAsJsonAsync(Campaigns, new
        {
            name = "Benefits enrolment",
            audience = DubaiAudience(o.Dubai),
            steps = new object[]
            {
                new { itemId = item, at = now.AddHours(2), onlyNotActed = true },
                new { itemId = item, at = now.AddMinutes(-1) },
            },
        }), HttpStatusCode.Created);
        var id = Id(campaign);
        Assert.Equal("draft", campaign.GetProperty("status").GetString());
        var steps = campaign.GetProperty("steps");
        // Steps are kept in time order.
        Assert.False(steps[0].GetProperty("onlyNotActed").GetBoolean());
        Assert.Equal("Benefits enrolment opens", steps[0].GetProperty("itemTitle").GetProperty("en").GetString());
        var first = Id(steps[0]);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCampaignCreated, id.ToString());

        // A draft sends nothing.
        await RunAsync(o.T, now);
        await DispatchAsync();
        Assert.Null(await CampaignNoticeAsync(o.T["s1"].Client, item));

        Assert.Equal("scheduled", (await OkAsync(pia.PostAsync($"{Campaigns}/{id}/start", null))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCampaignStarted, id.ToString());
        Assert.Equal("conflict.content.campaign_started", await ProblemKeyAsync(await pia.PutAsJsonAsync($"{Campaigns}/{id}", new { name = "Changed" }), HttpStatusCode.Conflict));
        await RunAsync(o.T, now);
        await DispatchAsync();
        foreach (var name in Staff)
        {
            var notice = await CampaignNoticeAsync(o.T[name].Client, item);
            Assert.NotNull(notice);
            Assert.False(notice!.Value.GetProperty("mandatory").GetBoolean());
            Assert.Equal("announcement", notice.Value.GetProperty("category").GetString());
        }
        Assert.Null(await CampaignNoticeAsync(o.T["out"].Client, item)); // Not in the audience.
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCampaignStepSent, id.ToString());

        // Two come through the campaign link, one opens the item another way.
        await OkAsync(o.T["s1"].Client.GetAsync($"{Items}/{item}/view?campaign={first:N}"));
        await OkAsync(o.T["s2"].Client.GetAsync($"{Items}/{item}/view?campaign={first:N}"));
        await OkAsync(o.T["s3"].Client.GetAsync($"{Items}/{item}/view"));

        await RunAsync(o.T, now.AddHours(3));
        var view = await OkAsync(pia.GetAsync($"{Campaigns}/{id}"));
        Assert.Equal("completed", view.GetProperty("status").GetString());
        Assert.Equal([6, 3], view.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("recipients").GetInt32()));

        var dashboard = await OkAsync(pia.GetAsync($"{Campaigns}/{id}/dashboard"));
        var total = dashboard.GetProperty("total");
        Assert.Equal(6, total.GetProperty("audience").GetInt32());
        Assert.Equal(3, total.GetProperty("viewed").GetInt32());
        Assert.Equal(2, total.GetProperty("clicked").GetInt32());
        var byStep = dashboard.GetProperty("steps");
        Assert.Equal(6, byStep[0].GetProperty("counts").GetProperty("audience").GetInt32());
        Assert.Equal(2, byStep[0].GetProperty("counts").GetProperty("clicked").GetInt32());
        // Three people are below the minimum group size: what they did is not shown.
        Assert.Equal(3, byStep[1].GetProperty("counts").GetProperty("audience").GetInt32());
        Assert.Equal(JsonValueKind.Null, byStep[1].GetProperty("counts").GetProperty("viewed").ValueKind);
        var unit = Assert.Single(dashboard.GetProperty("byUnit").EnumerateArray());
        Assert.Equal(o.Dubai.ToString(), unit.GetProperty("key").GetString());
        Assert.Equal(6, unit.GetProperty("audience").GetInt32());
    }

    [Fact]
    public async Task A_mandatory_step_is_labelled_an_item_not_live_is_skipped_and_a_cancelled_campaign_stops()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var live = await PublishedAsync(pia, "Fire drill");
        var draft = await PublishedAsync(pia, "Not ready", publish: false);
        var now = DateTimeOffset.UtcNow;
        var id = Id(await OkAsync(pia.PostAsJsonAsync(Campaigns, new
        {
            name = "Safety week",
            audience = DubaiAudience(o.Dubai),
            steps = new object[]
            {
                new { itemId = live, at = now.AddMinutes(-2), mandatory = true },
                new { itemId = draft, at = now.AddMinutes(-1) },
                new { itemId = live, at = now.AddDays(1) },
            },
        }), HttpStatusCode.Created));
        await OkAsync(pia.PostAsync($"{Campaigns}/{id}/start", null));
        await RunAsync(o.T, now);
        await DispatchAsync();
        var notice = await CampaignNoticeAsync(o.T["s1"].Client, live);
        Assert.True(notice!.Value.GetProperty("mandatory").GetBoolean());
        Assert.Null(await CampaignNoticeAsync(o.T["s1"].Client, draft));

        var view = await OkAsync(pia.PostAsync($"{Campaigns}/{id}/cancel", null));
        Assert.Equal("cancelled", view.GetProperty("status").GetString());
        Assert.Equal(["sent", "skipped", "cancelled"], view.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("status").GetString()!));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentCampaignCancelled, id.ToString());
        Assert.Equal("conflict.content.campaign_ended", await ProblemKeyAsync(await pia.PostAsync($"{Campaigns}/{id}/cancel", null), HttpStatusCode.Conflict));
        await RunAsync(o.T, now.AddDays(2));
        Assert.Equal("cancelled", (await OkAsync(pia.GetAsync($"{Campaigns}/{id}"))).GetProperty("steps")[2].GetProperty("status").GetString());

        var empty = Id(await OkAsync(pia.PostAsJsonAsync(Campaigns, new { name = "Empty" }), HttpStatusCode.Created));
        Assert.Equal("conflict.content.campaign_empty", await ProblemKeyAsync(await pia.PostAsync($"{Campaigns}/{empty}/start", null), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task The_plan_the_site_publishers_and_the_tenant_bound_campaigns()
    {
        var o = await FreshAsync();
        var pia = o.T["pia"].Client;
        var item = await PublishedAsync(pia, "Town hall");
        var id = Id(await OkAsync(pia.PostAsJsonAsync(Campaigns, new { name = "Town hall", steps = new[] { new { itemId = item, at = DateTimeOffset.UtcNow.AddDays(1) } } }),
            HttpStatusCode.Created));
        Assert.Contains(id, (await OkAsync(pia.GetAsync(Campaigns))).EnumerateArray().Select(Id));

        // Someone who does not publish at the site cannot plan one, and does not find this one.
        var staff = o.T["s1"].Client;
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PostAsJsonAsync(Campaigns, new { name = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Campaigns}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await staff.GetAsync($"{Campaigns}/{id}/dashboard")).StatusCode);
        Assert.Empty((await OkAsync(staff.GetAsync(Campaigns))).EnumerateArray());

        // Another tenant's units, groups and items are refused, and its people do not find the campaign.
        var other = await FreshAsync();
        var foreignItem = await PublishedAsync(other.T["pia"].Client, "Elsewhere");
        Assert.Equal("validation.content.campaign_audience", await ProblemKeyAsync(await pia.PostAsJsonAsync(Campaigns, new { name = "X", audience = DubaiAudience(other.Dubai) }),
            HttpStatusCode.BadRequest));
        Assert.Equal("validation.content.campaign_audience", await ProblemKeyAsync(await pia.PostAsJsonAsync(Campaigns,
            new { name = "X", audience = new { anyOf = new[] { new { groups = new[] { Guid.NewGuid() } } } } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.content.campaign_item", await ProblemKeyAsync(await pia.PostAsJsonAsync(Campaigns,
            new { name = "X", steps = new[] { new { itemId = foreignItem, at = DateTimeOffset.UtcNow } } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.content.campaign_name", await ProblemKeyAsync(await pia.PostAsJsonAsync(Campaigns, new { name = " " }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["pia"].Client.GetAsync($"{Campaigns}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.PostAsync($"{Campaigns}/{id}/cancel", null)).StatusCode);

        // The Workplace plan has no campaigns.
        var small = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await small.T["pia"].Client.PostAsJsonAsync(Campaigns, new { name = "Small" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await small.T["pia"].Client.GetAsync(Campaigns)).StatusCode);
    }
}
