using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R2 FR-003 multi-step approval (T031): a site route runs its steps in order with an inbox task each; any step
/// may return the item; the author is never a candidate at any step, and a step left with nobody goes to the site's
/// publishers; more than one step needs <c>content.multistep_approval</c>, and a route has at most five steps.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentRouteStepsTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private sealed record Org(TestTenant T, Guid Site);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("crs-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "layla", "pia", "kim", "rana" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "layla", BuiltInRoles.ContentEditor, "tenant", null);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var sites = await (await t["admin"].Client.GetAsync("/api/v1/content/sites")).ReadAsync<JsonElement>();
        return new Org(t, sites[0].GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> RouteAsync(Org o, params Guid[][] steps) =>
        o.T["admin"].Client.PutAsJsonAsync($"/api/v1/content/sites/{o.Site}", new
        {
            routes = new Dictionary<string, object> { ["news"] = new { approval = true, steps = steps.Select(s => new { approvers = s }).ToArray() } },
        });

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.ReadAsync<JsonElement>();
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, string title)
    {
        var id = (await OkAsync(client.PostAsJsonAsync(Items, new
        {
            type = "news",
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
            },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal("in_review", (await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/submit", new { }))).GetProperty("status").GetString());
        return id;
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) => (await OkAsync(client.GetAsync("/api/v1/tasks"))).EnumerateArray().ToArray();

    private static async Task DecideAsync(HttpClient client, string outcome, string? comment = null)
    {
        var task = Assert.Single(await InboxAsync(client)).GetProperty("taskId").GetGuid();
        await OkAsync(client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment }));
    }

    private static async Task<string[]> HistoryAsync(HttpClient client, Guid id) =>
        (await OkAsync(client.GetAsync($"{Items}/{id}/reviews"))).EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToArray();

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<string[]> NotificationLinksAsync(HttpClient client) =>
        (await OkAsync(client.GetAsync("/api/v1/me/notifications"))).GetProperty("items").EnumerateArray().Select(n => n.GetProperty("link").GetString()!).ToArray();

    [Fact]
    public async Task Steps_run_in_order_with_a_task_each_and_any_step_may_return_the_item()
    {
        var o = await FreshAsync();
        var layla = o.T["layla"].Client;
        var kim = o.T["kim"];
        var rana = o.T["rana"];
        await OkAsync(RouteAsync(o, [kim.Id], [rana.Id]));
        var route = (await OkAsync(o.T["admin"].Client.GetAsync($"/api/v1/content/sites/{o.Site}"))).GetProperty("routes").GetProperty("news");
        Assert.Equal(2, route.GetProperty("steps").GetArrayLength());

        var id = await SubmitAsync(layla, "Quarterly results");
        await DispatchAsync();
        Assert.Contains(await NotificationLinksAsync(kim.Client), l => l.StartsWith($"/content/items/{id}", StringComparison.Ordinal));
        Assert.Empty(await InboxAsync(rana.Client)); // The second step waits for the first.

        await DecideAsync(kim.Client, "approve");
        Assert.Empty(await InboxAsync(kim.Client));
        var item = await OkAsync(layla.GetAsync($"{Items}/{id}"));
        Assert.Equal("in_review", item.GetProperty("status").GetString());
        await DispatchAsync();
        Assert.Contains(await NotificationLinksAsync(rana.Client), l => l.StartsWith($"/content/items/{id}", StringComparison.Ordinal));

        await DecideAsync(rana.Client, "approve");
        Assert.Equal("published", (await OkAsync(layla.GetAsync($"{Items}/{id}"))).GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.ContentApproved, id.ToString());
        Assert.Equal(["submitted", "step_approved", "approved"], await HistoryAsync(layla, id));
        var versions = (await OkAsync(layla.GetAsync($"{Items}/{id}/versions"))).EnumerateArray().ToArray();
        Assert.Equal(rana.Id, versions[0].GetProperty("approver").GetProperty("id").GetGuid());

        // The second step returns the next item with a comment: it goes back to draft and nothing goes live.
        var next = await SubmitAsync(layla, "Office move");
        await DecideAsync(kim.Client, "approve");
        await DecideAsync(rana.Client, "return", "Add the new address");
        var returned = await OkAsync(layla.GetAsync($"{Items}/{next}"));
        Assert.Equal("draft", returned.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("publishedVersion").ValueKind);
        Assert.Equal(["submitted", "step_approved", "returned"], await HistoryAsync(layla, next));

        // The first step can return it too, and the second never opens.
        var third = await SubmitAsync(layla, "Parking");
        await DecideAsync(kim.Client, "return", "Not yet");
        Assert.Empty(await InboxAsync(rana.Client));
        Assert.Equal(["submitted", "returned"], await HistoryAsync(layla, third));
    }

    [Fact]
    public async Task The_author_is_never_a_candidate_and_a_step_left_with_nobody_goes_to_the_publishers()
    {
        var o = await FreshAsync();
        var layla = o.T["layla"];
        var kim = o.T["kim"];
        // Layla is named at both steps; as the author she is taken out, so step 1 is Kim's and step 2 the publishers'.
        await OkAsync(RouteAsync(o, [layla.Id, kim.Id], [layla.Id]));
        var id = await SubmitAsync(layla.Client, "Canteen menu");
        Assert.Empty(await InboxAsync(layla.Client));
        Assert.Empty(await InboxAsync(o.T["pia"].Client));
        await DecideAsync(kim.Client, "approve");

        Assert.Empty(await InboxAsync(layla.Client));
        Assert.Empty(await InboxAsync(kim.Client));
        await DecideAsync(o.T["pia"].Client, "approve");
        Assert.Equal("published", (await OkAsync(layla.Client.GetAsync($"{Items}/{id}"))).GetProperty("status").GetString());

        // A publisher who wrote the item is not a candidate of the publisher step either.
        var own = await SubmitAsync(o.T["pia"].Client, "Pia's own news");
        await DecideAsync(kim.Client, "approve");
        Assert.Empty(await InboxAsync(o.T["pia"].Client));
        Assert.Equal("in_review", (await OkAsync(o.T["pia"].Client.GetAsync($"{Items}/{own}"))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task More_than_one_step_needs_the_plan_and_a_route_has_at_most_five_steps()
    {
        var small = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await RouteAsync(small, [small.T["kim"].Id], [small.T["rana"].Id])).StatusCode);
        // One step written as a list is still one step, so the Workplace plan keeps it.
        await OkAsync(RouteAsync(small, [small.T["kim"].Id]));

        var o = await FreshAsync();
        var kim = o.T["kim"].Id;
        Assert.Equal(HttpStatusCode.BadRequest, (await RouteAsync(o, [kim], [kim], [kim], [kim], [kim], [kim])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await o.T["admin"].Client.PutAsJsonAsync($"/api/v1/content/sites/{o.Site}", new
        {
            routes = new Dictionary<string, object> { ["news"] = new { approval = true, approvers = new[] { kim }, steps = new[] { new { approvers = new[] { kim } } } } },
        })).StatusCode);
        var stranger = await world.CreateTenantAsync("crs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.BadRequest, (await RouteAsync(o, [kim], [stranger["admin"].Id])).StatusCode);
        await OkAsync(RouteAsync(o, [kim], [kim], [kim], [kim], [kim]));
    }
}
