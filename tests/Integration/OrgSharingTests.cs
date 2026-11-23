using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T019 (US4 scenarios 4 and 5, FR-021, FR-022): an organization's pages and menu reach its own branch; another
/// branch sees them only through a share made with the owner's publish right, with the owner named and its brand kept,
/// and a share never gives a permission. Tenant pages, other tenants and unknown kinds cannot be shared.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OrgSharingTests(TenantWorld world)
{
    private const string Shares = "/api/v1/experience/shares";
    private static readonly object Text = new { body = new { en = "Wear a helmet on the plant floor.", ar = "ارتد الخوذة في أرض المصنع." } };

    /// <summary>
    /// Holding with Gulf (and its Plant) and Africa below it; an employee in each, Gulf's page publisher, and Africa's.
    /// </summary>
    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("osh-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var (name, unit) in new[] { ("group", "Holding"), ("gulfer", "Gulf"), ("planter", "Plant"), ("african", "Africa"), ("publisher", "Gulf"), ("farpub", "Africa") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "publisher", "page_publisher", "org_subtree", gulf);
        await world.AssignAsync(t, "farpub", "page_publisher", "org_subtree", africa);
        return t;
    }

    private static async Task<(Guid Id, string Slug)> PageAsync(TestTenant t, Guid? unit)
    {
        var slug = "sh-" + Guid.NewGuid().ToString("N")[..10];
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug, orgUnitId = unit });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(
            new JsonObject { ["id"] = "intro", ["componentRef"] = "reference.alpha.text@1", ["props"] = JsonSerializer.SerializeToNode(Text) });
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        Assert.True((await t["admin"].Client.SendAsync(request)).IsSuccessStatusCode);
        var published = await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/publish", new { reason = "first" });
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        return (page["id"]!.GetValue<Guid>(), slug);
    }

    private static Task<HttpResponseMessage> RenderAsync(TestTenant t, string user, string slug) =>
        t[user].Client.GetAsync($"/api/v1/experience/render?slug={slug}");

    private static async Task<string?> SharedByAsync(TestTenant t, string user, string slug)
    {
        var response = await RenderAsync(t, user, slug);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        return page.TryGetProperty("sharedBy", out var by) && by.ValueKind == JsonValueKind.String ? by.GetString() : null;
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()! : body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task An_organizations_page_reaches_its_branch_and_another_branch_only_through_a_share()
    {
        var t = await FreshAsync();
        var (id, slug) = await PageAsync(t, t.Unit("Gulf"));

        // Gulf, the Plant below it and the Holding above it see the page; Africa, on another branch, does not.
        foreach (var user in new[] { "gulfer", "planter", "group" })
        {
            Assert.Null(await SharedByAsync(t, user, slug));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await RenderAsync(t, "african", slug)).StatusCode);
        var resolved = await t["african"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/resolve?path={Uri.EscapeDataString(slug)}");
        Assert.Equal("recovery", resolved.GetProperty("kind").GetString());

        // Africa's publisher cannot share Gulf's page; Gulf's publisher shares it with Africa.
        Assert.Equal(HttpStatusCode.NotFound, (await t["farpub"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Africa")}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["publisher"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Africa")}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["publisher"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Africa")}", null)).StatusCode);
        var list = await t["publisher"].Client.GetFromJsonAsync<JsonElement>($"{Shares}/page/{id}");
        Assert.Equal(t.Unit("Africa"), Assert.Single(list.EnumerateArray()).GetProperty("orgUnitId").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperienceShareChanged, id.ToString());

        // Africa now sees it, named as Gulf's and in Gulf's brand; Gulf's own people still see no owner label.
        Assert.Equal("Gulf", await SharedByAsync(t, "african", slug));
        var page = await (await RenderAsync(t, "african", slug)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(t.Unit("Gulf").ToString(), page.GetProperty("themeScope").GetString());
        Assert.Null(await SharedByAsync(t, "gulfer", slug));
        // The tenant administrator sees it through their editing rights, and nothing was shared with them, so no label.
        Assert.Null(await SharedByAsync(t, "admin", slug));

        // Seeing is not editing: the share merges no permission, and Africa's publisher still cannot open the page to edit.
        Assert.Equal(HttpStatusCode.NotFound, (await t["farpub"].Client.GetAsync($"/api/v1/experience/pages/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["african"].Client.GetAsync($"{Shares}/page/{id}")).StatusCode);

        // Removing the share hides it again.
        Assert.Equal(HttpStatusCode.NoContent, (await t["publisher"].Client.DeleteAsync($"{Shares}/page/{id}/{t.Unit("Africa")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await RenderAsync(t, "african", slug)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["publisher"].Client.DeleteAsync($"{Shares}/page/{id}/{t.Unit("Africa")}")).StatusCode);
    }

    [Fact]
    public async Task Shares_are_checked_and_stay_in_the_tenant()
    {
        var t = await FreshAsync();
        var (id, _) = await PageAsync(t, t.Unit("Gulf"));
        var (tenantPage, tenantSlug) = await PageAsync(t, null);

        // The owner's own branch sees it already, so a share there is refused; so is an unknown organization.
        Assert.Equal("validation.experience.share_own_line", await KeyAsync(await t["publisher"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Plant")}", null)));
        Assert.Equal("validation.experience.share_own_line", await KeyAsync(await t["admin"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Holding")}", null)));
        Assert.Equal("validation.experience.share_unit", await KeyAsync(await t["publisher"].Client.PutAsync($"{Shares}/page/{id}/{Guid.NewGuid()}", null)));
        // Tenant pages reach everyone and are not shared; unknown kinds and pages are not found.
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PutAsync($"{Shares}/page/{tenantPage}/{t.Unit("Africa")}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RenderAsync(t, "african", tenantSlug)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PutAsync($"{Shares}/brand/{id}/{t.Unit("Africa")}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"{Shares}/page/{Guid.NewGuid()}")).StatusCode);
        // Another tenant can neither read nor change this tenant's shares, nor share into it.
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"{Shares}/page/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Africa")}", null)).StatusCode);
        // An employee holds no publish right, so the page's sharing is hidden from them too.
        Assert.Equal(HttpStatusCode.NotFound, (await t["gulfer"].Client.PutAsync($"{Shares}/page/{id}/{t.Unit("Africa")}", null)).StatusCode);
    }

    [Fact]
    public async Task An_organization_menu_shared_with_another_branch_shows_there_with_its_owner()
    {
        var t = await FreshAsync();
        var gulf = t.Unit("Gulf");
        var (pageId, slug) = await PageAsync(t, gulf);
        var node = Guid.NewGuid();
        Assert.True((await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/ia/{node}",
            new { parentId = (Guid?)null, labels = new { en = "Gulf safety", ar = "السلامة" }, slug = "gs-" + Guid.NewGuid().ToString("N")[..8], pageId, ownerOrgUnitId = gulf })).IsSuccessStatusCode);
        Assert.True((await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/menus/organization/{gulf}", new { items = new object[] { new { nodeId = node } } })).IsSuccessStatusCode);

        static async Task<JsonElement[]> MenusAsync(TestTenant t, string user) =>
            (await t[user].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell")).GetProperty("organizationMenus").EnumerateArray().ToArray();

        Assert.DoesNotContain(await MenusAsync(t, "african"), m => m.GetProperty("orgUnitId").GetGuid() == gulf);
        Assert.False(Assert.Single(await MenusAsync(t, "gulfer"), m => m.GetProperty("orgUnitId").GetGuid() == gulf).GetProperty("shared").GetBoolean());

        // The menu is shared with Africa, and the page behind its link too, so the link shows.
        Assert.Equal(HttpStatusCode.NotFound, (await t["publisher"].Client.PutAsync($"{Shares}/menu/{gulf}/{t.Unit("Africa")}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.PutAsync($"{Shares}/menu/{gulf}/{t.Unit("Africa")}", null)).StatusCode);
        var shared = Assert.Single(await MenusAsync(t, "african"), m => m.GetProperty("orgUnitId").GetGuid() == gulf);
        Assert.True(shared.GetProperty("shared").GetBoolean());
        Assert.Equal("Gulf", shared.GetProperty("name").GetString());
        Assert.Empty(shared.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await t["publisher"].Client.PutAsync($"{Shares}/page/{pageId}/{t.Unit("Africa")}", null)).StatusCode);
        shared = Assert.Single(await MenusAsync(t, "african"), m => m.GetProperty("orgUnitId").GetGuid() == gulf);
        Assert.Single(shared.GetProperty("items").EnumerateArray());
        // A menu of an organization with none is not found.
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PutAsync($"{Shares}/menu/{t.Unit("Africa")}/{gulf}", null)).StatusCode);
    }
}
