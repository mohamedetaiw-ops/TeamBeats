using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 T025 (FR-035): safe suggestions. Each says why it is offered, changes nothing until accepted, can be undone,
/// and is never offered again once answered. A component suggestion appears only where the page's personalization policy
/// lets the person add, and only when enough colleagues in their unit added it. Another tenant sees none of it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SuggestionTests(TenantWorld world)
{
    private const string Suggestions = "/api/v1/me/suggestions";
    private const string Clock = "toolbox.world_clock@1";

    private static string Slug(string prefix = "s") => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<TestTenant> FreshAsync(params string[] users)
    {
        var t = await world.CreateTenantAsync("sug-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        foreach (var user in users.Prepend("employee"))
        {
            await world.SeedUserAsync(t, user, MembershipTypes.Employee);
        }
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A published page reachable at <c>/slug</c> through the information architecture.</summary>
    private static async Task<(Guid Page, string Slug)> ShellPageAsync(TestTenant t)
    {
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = Slug("p") }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{id}/publish", new { reason = "first" }));
        var slug = Slug();
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/ia/{Guid.NewGuid()}",
            new { parentId = (Guid?)null, labels = new { en = "Safety", ar = "السلامة" }, slug, pageId = id }));
        return (id, slug);
    }

    /// <summary>A published T07 page whose canvas region people may add to.</summary>
    private static async Task<Guid> DelegatedPageAsync(TestTenant t)
    {
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T07", slug = Slug("pz") });
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var draft = page["draft"]!.DeepClone();
        var text = new JsonObject { ["body"] = new JsonObject { ["en"] = "Wear a helmet." } };
        foreach (var region in draft["regions"]!.AsArray())
        {
            var id = region!["id"]!.GetValue<string>();
            region["placements"] = new JsonArray(new JsonObject { ["id"] = id + "-text", ["componentRef"] = "reference.alpha.text@1", ["props"] = text.DeepClone() });
        }
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        await OkAsync(t["admin"].Client.SendAsync(request));
        var pageId = page["id"]!.GetValue<Guid>();
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{pageId}/publish", new { reason = "first" }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/pages/{pageId}/personalization-policy",
            new { regions = new[] { new { regionId = "canvas", rights = new[] { "add", "hide", "remove", "reorder" } } } }));
        return pageId;
    }

    private static async Task AddClockAsync(TestTenant t, string user, Guid page)
    {
        var current = await t[user].Client.GetAsync($"/api/v1/me/pages/{page}/layout");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/pages/{page}/layout")
        {
            Content = JsonContent.Create(new { operations = new[] { new { op = "add", placementId = "my-clock", componentRef = Clock, regionId = "canvas" } } }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        await OkAsync(t[user].Client.SendAsync(request));
    }

    private static async Task VisitAsync(TestTenant t, string user, string path, int times)
    {
        for (var i = 0; i < times; i++)
        {
            await OkAsync(t[user].Client.PostAsJsonAsync("/api/v1/me/recents", new { path, label = "Safety" }), HttpStatusCode.NoContent);
        }
    }

    private static async Task<JsonElement[]> ListAsync(TestTenant t, string user, Guid? page = null) =>
        (await OkAsync(t[user].Client.GetAsync(page is null ? Suggestions : $"{Suggestions}?pageId={page}"))).EnumerateArray().ToArray();

    [Fact]
    public async Task Pinning_a_page_opened_often_is_offered_with_its_reason_and_changes_nothing_until_accepted_then_undo_removes_it()
    {
        var t = await FreshAsync();
        var (_, slug) = await ShellPageAsync(t);
        var path = "/" + slug;

        await VisitAsync(t, "employee", path, 4);
        Assert.Empty(await ListAsync(t, "employee"));

        await VisitAsync(t, "employee", path, 1);
        var offered = Assert.Single(await ListAsync(t, "employee"));
        Assert.Equal("pin_page", offered.GetProperty("kind").GetString());
        Assert.Equal(5, offered.GetProperty("count").GetInt32());
        Assert.Equal(path, offered.GetProperty("href").GetString());
        Assert.False(string.IsNullOrEmpty(offered.GetProperty("reasonKey").GetString()));
        // Offering it changed nothing.
        Assert.Equal(0, (await OkAsync(t["employee"].Client.GetAsync("/api/v1/me/favorites"))).GetArrayLength());

        var key = offered.GetProperty("key").GetString();
        var accepted = await OkAsync(t["employee"].Client.PostAsJsonAsync($"{Suggestions}/accept", new { key }));
        Assert.Equal("accepted", accepted.GetProperty("state").GetString());
        Assert.True(accepted.GetProperty("canUndo").GetBoolean());
        var favorites = await OkAsync(t["employee"].Client.GetAsync("/api/v1/me/favorites"));
        Assert.Equal(path, Assert.Single(favorites.EnumerateArray()).GetProperty("href").GetString());
        Assert.Empty(await ListAsync(t, "employee"));

        await OkAsync(t["employee"].Client.PostAsJsonAsync($"{Suggestions}/undo", new { key }));
        Assert.Equal(0, (await OkAsync(t["employee"].Client.GetAsync("/api/v1/me/favorites"))).GetArrayLength());
        // Answered once, it is not offered again, and it cannot be undone twice.
        await VisitAsync(t, "employee", path, 3);
        Assert.Empty(await ListAsync(t, "employee"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.PostAsJsonAsync($"{Suggestions}/undo", new { key })).StatusCode);
    }

    [Fact]
    public async Task A_declined_or_made_up_suggestion_changes_nothing_and_another_tenant_sees_none()
    {
        var t = await FreshAsync("other");
        var (_, slug) = await ShellPageAsync(t);
        var path = "/" + slug;
        await VisitAsync(t, "employee", path, 5);
        var key = Assert.Single(await ListAsync(t, "employee")).GetProperty("key").GetString();

        // Only the person's own visits count.
        Assert.Empty(await ListAsync(t, "other"));
        Assert.Equal(HttpStatusCode.BadRequest, (await t["other"].Client.PostAsJsonAsync($"{Suggestions}/accept", new { key })).StatusCode);

        // A key that is not offered now is refused and nothing is added.
        var madeUp = await t["employee"].Client.PostAsJsonAsync($"{Suggestions}/accept", new { key = "pin:/p/" + Slug() });
        Assert.Equal(HttpStatusCode.BadRequest, madeUp.StatusCode);
        Assert.Contains("validation.personalization.suggestion", await madeUp.Content.ReadAsStringAsync());
        Assert.Equal(0, (await OkAsync(t["employee"].Client.GetAsync("/api/v1/me/favorites"))).GetArrayLength());

        var declined = await OkAsync(t["employee"].Client.PostAsJsonAsync($"{Suggestions}/decline", new { key }));
        Assert.Equal("declined", declined.GetProperty("state").GetString());
        Assert.Empty(await ListAsync(t, "employee"));
        Assert.Equal(0, (await OkAsync(t["employee"].Client.GetAsync("/api/v1/me/favorites"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.PostAsJsonAsync($"{Suggestions}/undo", new { key })).StatusCode);

        // Another tenant has no such visits, page or decision.
        var b = await FreshAsync();
        Assert.Empty(await ListAsync(b, "employee"));
        Assert.Equal(HttpStatusCode.NotFound, (await b["employee"].Client.PostAsJsonAsync($"{Suggestions}/undo", new { key })).StatusCode);
    }

    [Fact]
    public async Task A_component_colleagues_in_the_unit_added_is_offered_where_the_policy_lets_the_person_add_and_undo_removes_it()
    {
        var t = await FreshAsync("c1", "c2", "c3", "outsider");
        await world.CreateUnitAsync(t, "Ops", null, "organization");
        await world.CreateUnitAsync(t, "Sales", null, "organization");
        foreach (var user in new[] { "employee", "c1", "c2", "c3" })
        {
            await world.AddMembershipAsync(t, user, "Ops");
        }
        await world.AddMembershipAsync(t, "outsider", "Sales");
        var page = await DelegatedPageAsync(t);

        // Two colleagues are not enough: it could point at one person.
        await AddClockAsync(t, "c1", page);
        await AddClockAsync(t, "c2", page);
        Assert.Empty(await ListAsync(t, "employee", page));

        await AddClockAsync(t, "c3", page);
        var offered = Assert.Single(await ListAsync(t, "employee", page));
        Assert.Equal("add_component", offered.GetProperty("kind").GetString());
        Assert.Equal(3, offered.GetProperty("count").GetInt32());
        Assert.Equal(Clock, offered.GetProperty("componentRef").GetString());
        // Someone in another unit is not offered it.
        Assert.Empty(await ListAsync(t, "outsider", page));
        // Nothing changed for the person yet.
        Assert.Equal(0, (await OkAsync(t["employee"].Client.GetAsync($"/api/v1/me/pages/{page}/layout"))).GetProperty("operations").GetArrayLength());

        var key = offered.GetProperty("key").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await t["employee"].Client.PostAsJsonAsync($"{Suggestions}/accept", new { key })).StatusCode);
        await OkAsync(t["employee"].Client.PostAsJsonAsync($"{Suggestions}/accept", new { key, pageId = page }));
        var layout = await OkAsync(t["employee"].Client.GetAsync($"/api/v1/me/pages/{page}/layout"));
        var added = Assert.Single(layout.GetProperty("operations").EnumerateArray());
        Assert.Equal(Clock, added.GetProperty("componentRef").GetString());
        Assert.Empty(await ListAsync(t, "employee", page));

        await OkAsync(t["employee"].Client.PostAsJsonAsync($"{Suggestions}/undo", new { key }));
        layout = await OkAsync(t["employee"].Client.GetAsync($"/api/v1/me/pages/{page}/layout"));
        Assert.Equal(0, layout.GetProperty("operations").GetArrayLength());
        Assert.Empty(await ListAsync(t, "employee", page));
    }

    [Fact]
    public async Task No_component_is_suggested_where_the_policy_does_not_let_the_person_add()
    {
        var t = await FreshAsync("c1", "c2", "c3");
        await world.CreateUnitAsync(t, "Ops", null, "organization");
        foreach (var user in new[] { "employee", "c1", "c2", "c3" })
        {
            await world.AddMembershipAsync(t, user, "Ops");
        }
        var page = await DelegatedPageAsync(t);
        foreach (var user in new[] { "c1", "c2", "c3" })
        {
            await AddClockAsync(t, user, page);
        }
        Assert.Single(await ListAsync(t, "employee", page));

        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/pages/{page}/personalization-policy",
            new { regions = new[] { new { regionId = "canvas", rights = new[] { "hide" } } } }));
        Assert.Empty(await ListAsync(t, "employee", page));
    }
}
