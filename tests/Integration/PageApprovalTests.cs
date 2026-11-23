using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T023 (US6 scenario 1, FR-051): approval routes for pages. Where a route covers a page's org unit, publishing
/// directly is refused and the editor submits; the approvers (page.approve at the scope, and the route's named people) get
/// an inbox task with a structural diff and a preview of what was sent, and approve, which publishes it, or return it with
/// a comment. The submitter never decides; a scoped approver of another unit sees nothing; pages outside the route and
/// packages without approval routes publish in one step; other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PageApprovalTests(TenantWorld world)
{
    private const string Pages = "/api/v1/experience/pages";

    private static readonly object Text = new { body = new { en = "Wear a helmet on the plant floor.", ar = "ارتد الخوذة في أرض المصنع." } };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pap-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "editor", "publisher", "named", "far" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "far" ? "Africa" : "Gulf");
        }
        await world.AssignAsync(t, "editor", "page_editor", "org_subtree", gulf);
        await world.AssignAsync(t, "publisher", "page_publisher", "org_subtree", gulf);
        await world.AssignAsync(t, "far", "page_publisher", "org_subtree", africa);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    /// <summary>Creates a canvas page in a unit and saves it with the given placement IDs in its main region.</summary>
    private static async Task<JsonNode> PageAsync(TestTenant t, Guid unit, string user, params string[] placements)
    {
        var created = await t[user].Client.PostAsJsonAsync(Pages, new { templateId = "T01", slug = "p-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = unit });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        return await SaveAsync(t, JsonNode.Parse(await created.Content.ReadAsStringAsync())!, user, placements);
    }

    private static async Task<JsonNode> SaveAsync(TestTenant t, JsonNode page, string user, params string[] placements)
    {
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(placements
            .Select(id => (JsonNode)new JsonObject { ["id"] = id, ["componentRef"] = "reference.alpha.text@1", ["props"] = JsonSerializer.SerializeToNode(Text) }).ToArray());
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Pages}/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        var response = await t[user].Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonElement[]> TasksAsync(TestTenant t, string user) =>
        (await OkAsync(t[user].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Where(i => i.GetProperty("source").GetString() == PageApprovals.Source).ToArray();

    private Task<long> NotificationsAsync(TestTenant t, string user, string template) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", t[user].Id), ("k", template));

    private async Task DispatchUntilAsync(Func<Task<bool>> done)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 50 && !await done(); i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
        Assert.True(await done());
    }

    [Fact]
    public async Task A_covered_page_goes_live_only_through_an_approved_submission_and_the_submitter_never_decides()
    {
        var t = await FreshAsync();
        var gulf = t.Unit("Gulf");
        // A route further up the tree with no named approver: the nearest route (Gulf's) is the one that applies.
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = t.Unit("Holding"), approverIds = Array.Empty<Guid>() }));
        var route = await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = gulf, approverIds = new[] { t["named"].Id } }));
        Assert.Equal([t["named"].Id], route.GetProperty("approverIds").EnumerateArray().Select(a => a.GetGuid()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageRouteChanged, route.GetProperty("id").GetString()!);

        // A page of a unit below Gulf is covered; publishing it directly is refused, even for a publisher.
        var page = await PageAsync(t, t.Unit("Plant"), "editor", "intro");
        var id = page["id"]!.GetValue<string>();
        var slug = page["slug"]!.GetValue<string>();
        var direct = await t["publisher"].Client.PostAsJsonAsync($"{Pages}/{id}/publish", new { reason = "now" });
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        Assert.Equal("conflict.page.approval_required", await KeyAsync(direct));
        Assert.True((await OkAsync(t["editor"].Client.GetAsync($"{Pages}/{id}/approval"))).GetProperty("required").GetBoolean());

        var submitted = await OkAsync(t["editor"].Client.PostAsJsonAsync($"{Pages}/{id}/submit", new { reason = "Safety rules" }));
        Assert.Equal("pending", submitted.GetProperty("status").GetString());
        Assert.False(submitted.GetProperty("decide").GetBoolean());
        var change = Assert.Single(submitted.GetProperty("diff").GetProperty("changes").EnumerateArray());
        Assert.Equal("added", change.GetProperty("change").GetString());
        Assert.Equal("intro", change.GetProperty("placementId").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageSubmitted, id);
        var again = await t["editor"].Client.PostAsJsonAsync($"{Pages}/{id}/submit", new { });
        Assert.Equal("conflict.page.in_approval", await KeyAsync(again));

        // The publisher at Gulf and the named approver get the task; the editor and Africa's publisher do not.
        Assert.Empty(await TasksAsync(t, "editor"));
        Assert.Empty(await TasksAsync(t, "far"));
        var task = Assert.Single(await TasksAsync(t, "publisher"));
        Assert.Equal($"/page-approvals/{id}?task={task.GetProperty("taskId").GetString()}", task.GetProperty("link").GetString());
        Assert.Single(await TasksAsync(t, "named"));
        await DispatchUntilAsync(async () => await NotificationsAsync(t, "named", PageApprovals.WaitingTemplate) == 1);
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.GetAsync($"{Pages}/{id}/approval")).StatusCode);
        var state = await OkAsync(t["named"].Client.GetAsync($"{Pages}/{id}/approval"));
        Assert.True(state.GetProperty("pending").GetProperty("decide").GetBoolean());
        var preview = await OkAsync(t["named"].Client.GetAsync($"{Pages}/{id}/approval/preview"));
        Assert.Equal("intro", preview.GetProperty("regions")[0].GetProperty("placements")[0].GetProperty("id").GetString());

        // Editing the draft after submitting leaves the submission as it was sent; the publisher returns it with a comment.
        await SaveAsync(t, (await OkAsync(t["editor"].Client.GetAsync($"{Pages}/{id}"))).Deserialize<JsonNode>()!, "editor", "intro", "more");
        Assert.Single((await OkAsync(t["publisher"].Client.GetAsync($"{Pages}/{id}/approval"))).GetProperty("pending").GetProperty("diff").GetProperty("changes").EnumerateArray());
        await OkAsync(t["publisher"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetString()}/decision", new { outcome = "return", comment = "Add the hotline" }));
        Assert.Equal(1, await NotificationsAsync(t, "editor", PageApprovals.ReturnedTemplate));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageReturned, id);
        var returned = await OkAsync(t["editor"].Client.GetAsync($"{Pages}/{id}/approval"));
        Assert.Equal(JsonValueKind.Null, returned.GetProperty("pending").ValueKind);
        Assert.Equal("returned", returned.GetProperty("last").GetProperty("status").GetString());
        Assert.Equal("Add the hotline", returned.GetProperty("last").GetProperty("comment").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["named"].Client.GetAsync($"/api/v1/experience/render?slug={slug}")).StatusCode);

        // Sent again with both placements, the named approver approves and it goes live as release 1.
        await OkAsync(t["editor"].Client.PostAsJsonAsync($"{Pages}/{id}/submit", new { }));
        var second = Assert.Single(await TasksAsync(t, "named"));
        await OkAsync(t["named"].Client.PostAsJsonAsync($"/api/v1/tasks/{second.GetProperty("taskId").GetString()}/decision", new { outcome = "approve" }));
        Assert.Empty(await TasksAsync(t, "publisher"));
        var approved = (await OkAsync(t["editor"].Client.GetAsync($"{Pages}/{id}/approval"))).GetProperty("last");
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal(1, approved.GetProperty("release").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageApproved, id);
        Assert.Equal(1, await NotificationsAsync(t, "editor", PageApprovals.ApprovedTemplate));
        var live = await OkAsync(t["named"].Client.GetAsync($"/api/v1/experience/render?slug={slug}"));
        Assert.Equal(["intro", "more"], live.GetProperty("regions")[0].GetProperty("placements").EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        // The next change is compared with that release.
        await SaveAsync(t, (await OkAsync(t["editor"].Client.GetAsync($"{Pages}/{id}"))).Deserialize<JsonNode>()!, "editor", "more");
        var diff = (await OkAsync(t["editor"].Client.PostAsJsonAsync($"{Pages}/{id}/submit", new { }))).GetProperty("diff");
        Assert.Equal(1, diff.GetProperty("baseRelease").GetInt32());
        Assert.Equal("removed", Assert.Single(diff.GetProperty("changes").EnumerateArray()).GetProperty("change").GetString());
        await OkAsync(t["editor"].Client.PostAsync($"{Pages}/{id}/withdraw", null), HttpStatusCode.NoContent);
        Assert.Empty(await TasksAsync(t, "named"));
    }

    [Fact]
    public async Task Uncovered_pages_publish_directly_and_routes_need_the_package_scope_and_tenant()
    {
        var t = await FreshAsync();
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = t.Unit("Gulf") }));
        var africa = await PageAsync(t, t.Unit("Africa"), "admin", "a");
        await OkAsync(t["far"].Client.PostAsJsonAsync($"{Pages}/{africa["id"]}/publish", new { reason = "go" }));
        Assert.False((await OkAsync(t["far"].Client.GetAsync($"{Pages}/{africa["id"]}/approval"))).GetProperty("required").GetBoolean());
        Assert.Equal("conflict.page.no_route", await KeyAsync(await t["far"].Client.PostAsJsonAsync($"{Pages}/{africa["id"]}/submit", new { })));

        // Only someone who publishes at a scope sets its route; approvers must be active members; the editor sees no routes.
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = t.Unit("Gulf") })).StatusCode);
        Assert.Equal("validation.page.route_approvers",
            await KeyAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = t.Unit("Africa"), approverIds = new[] { Guid.NewGuid() } })));
        Assert.Empty((await OkAsync(t["editor"].Client.GetAsync("/api/v1/experience/page-routes"))).EnumerateArray());
        Assert.Single((await OkAsync(t["publisher"].Client.GetAsync("/api/v1/experience/page-routes"))).EnumerateArray());

        // A tenant-wide route covers every page once the Gulf route is removed.
        var routes = await OkAsync(t["admin"].Client.GetAsync("/api/v1/experience/page-routes"));
        await OkAsync(t["admin"].Client.DeleteAsync($"/api/v1/experience/page-routes/{routes[0].GetProperty("id").GetString()}"), HttpStatusCode.NoContent);
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = (Guid?)null }));
        Assert.Equal(HttpStatusCode.Conflict, (await t["far"].Client.PostAsJsonAsync($"{Pages}/{africa["id"]}/publish", new { })).StatusCode);

        // Another tenant reaches none of it.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Pages}/{africa["id"]}/approval")).StatusCode);
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync("/api/v1/experience/page-routes"))).EnumerateArray());

        // Without experience.approval_routes there are no routes, and pages publish in one step.
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.PutAsJsonAsync("/api/v1/experience/page-routes", new { orgUnitId = (Guid?)null })).StatusCode);
        var page = await PageAsync(basic, basic.Unit("Gulf"), "admin", "x");
        await OkAsync(basic["admin"].Client.PostAsJsonAsync($"{Pages}/{page["id"]}/publish", new { }));
    }
}
