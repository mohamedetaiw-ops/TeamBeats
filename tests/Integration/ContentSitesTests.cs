using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R2 US6 (T025): a site made from a template gets its home page in the page builder at its own unit and a
/// default audience of that subtree; readers find the sites that target them, or are shared with them; a site owner
/// holds <c>content.admin</c> at the site's unit only, so other sites and other units stay out of reach; archiving
/// makes the items read-only and takes them out of feeds and navigation; other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentSitesTests(TenantWorld world)
{
    private const string Sites = "/api/v1/content/sites";
    private const string Items = "/api/v1/content/items";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("cs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", holding, "subsidiary");
        foreach (var name in new[] { "olga", "gus", "eli" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "gus", "Gulf");
        await world.AddMembershipAsync(t, "eli", "Egypt");
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "site_owner", names = new Dictionary<string, string> { ["en"] = "Site owner" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentAdmin, Permissions.ContentPublish, Permissions.ContentEdit }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "olga", "site_owner", "org_subtree", gulf);
        return new Org(t, gulf, egypt);
    }

    private static Dictionary<string, string> Names(string en) => new() { ["en"] = en, ["ar"] = en };

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

    private static IEnumerable<Guid> Ids(JsonElement list) => list.EnumerateArray().Select(Id);

    [Fact]
    public async Task A_site_from_a_template_gets_its_home_page_and_reaches_its_own_subtree_and_whom_it_is_shared_with()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var templates = await OkAsync(admin.GetAsync($"{Sites}/templates"));
        Assert.Equal(["subsidiary", "location", "brand"], templates.EnumerateArray().Select(t => t.GetProperty("key").GetString()!));

        var site = await OkAsync(admin.PostAsJsonAsync(Sites, new { names = Names("Gulf"), orgUnitId = o.Gulf, template = "subsidiary", homeSlug = "gulf" }),
            HttpStatusCode.Created);
        var id = Id(site);
        Assert.Equal("subsidiary", site.GetProperty("templateKey").GetString());
        Assert.Equal("gulf", site.GetProperty("homeSlug").GetString());
        Assert.True(site.GetProperty("routes").GetProperty("news").GetProperty("approval").GetBoolean());
        var unit = site.GetProperty("defaultAudience").GetProperty("anyOf")[0].GetProperty("orgUnits")[0];
        Assert.Equal(o.Gulf, unit.GetProperty("id").GetGuid());
        Assert.True(unit.GetProperty("subtree").GetBoolean());

        // The home page is an ordinary page of the builder, owned by the Gulf unit, so the site's theme applies to it.
        var page = await OkAsync(admin.GetAsync($"/api/v1/experience/pages/{site.GetProperty("homePageId").GetGuid()}"));
        Assert.Equal(o.Gulf, page.GetProperty("orgUnitId").GetGuid());
        Assert.Equal("T05", page.GetProperty("templateId").GetString());

        // Gulf staff find the site; Egypt staff do not, until it is shared with them. The home has no release yet.
        var mine = await OkAsync(o.T["gus"].Client.GetAsync($"{Sites}/mine"));
        Assert.Equal([id], Ids(mine));
        Assert.Equal(JsonValueKind.Null, mine[0].GetProperty("homeSlug").ValueKind);
        Assert.Empty(Ids(await OkAsync(o.T["eli"].Client.GetAsync($"{Sites}/mine"))));
        var egypt = new { anyOf = new[] { new { orgUnits = new[] { new { id = o.Egypt, subtree = true } } } } };
        await OkAsync(admin.PutAsJsonAsync($"{Sites}/{id}", new { sharedAudience = egypt }));
        Assert.Equal([id], Ids(await OkAsync(o.T["eli"].Client.GetAsync($"{Sites}/mine"))));
        await OkAsync(admin.PutAsJsonAsync($"{Sites}/{id}", new { unshare = true }));
        Assert.Empty(Ids(await OkAsync(o.T["eli"].Client.GetAsync($"{Sites}/mine"))));

        // A home page must belong to the site's own unit, and a template must exist.
        var tenantPage = await OkAsync(admin.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = "elsewhere" }), HttpStatusCode.Created);
        Assert.Equal("validation.content.site_home",
            await ProblemKeyAsync(await admin.PutAsJsonAsync($"{Sites}/{id}", new { homePageId = Id(tenantPage) }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.content.site_template",
            await ProblemKeyAsync(await admin.PostAsJsonAsync(Sites, new { names = Names("X"), template = "castle" }), HttpStatusCode.BadRequest));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.SiteCreated, id.ToString());
    }

    [Fact]
    public async Task A_site_owner_manages_their_own_site_only()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var gulf = Id(await OkAsync(admin.PostAsJsonAsync(Sites, new { names = Names("Gulf"), orgUnitId = o.Gulf }), HttpStatusCode.Created));
        var egypt = Id(await OkAsync(admin.PostAsJsonAsync(Sites, new { names = Names("Egypt"), orgUnitId = o.Egypt }), HttpStatusCode.Created));
        var olga = o.T["olga"].Client;

        var listed = await OkAsync(olga.GetAsync(Sites));
        Assert.Contains(gulf, Ids(listed));
        Assert.DoesNotContain(egypt, Ids(listed));
        Assert.True(listed.EnumerateArray().Single(s => Id(s) == gulf).GetProperty("canManage").GetBoolean());

        var renamed = await OkAsync(olga.PutAsJsonAsync($"{Sites}/{gulf}", new { names = Names("Gulf people"), moderation = true }));
        Assert.Equal("Gulf people", renamed.GetProperty("names").GetProperty("en").GetString());

        // Another site, a move out of her unit, a new site and the tenant's settings are out of her reach.
        Assert.Equal(HttpStatusCode.NotFound, (await olga.PutAsJsonAsync($"{Sites}/{egypt}", new { names = Names("Mine now") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await olga.PostAsync($"{Sites}/{egypt}/archive", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.PutAsJsonAsync($"{Sites}/{gulf}", new { orgUnitId = o.Egypt })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.PostAsJsonAsync(Sites, new { names = Names("New"), orgUnitId = o.Gulf })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await olga.PutAsJsonAsync("/api/v1/content/settings", new { embedHosts = new[] { "example.com" } })).StatusCode);

        // Another tenant learns nothing about either site.
        var other = await world.CreateTenantAsync("cs-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Sites}/{gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PutAsJsonAsync($"{Sites}/{gulf}", new { names = Names("Taken") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"{Sites}/{gulf}/archive", null)).StatusCode);
    }

    [Fact]
    public async Task An_archived_site_is_read_only_and_leaves_feeds_and_navigation()
    {
        var o = await FreshAsync();
        var admin = o.T["admin"].Client;
        var site = Id(await OkAsync(admin.PostAsJsonAsync(Sites, new { names = Names("Gulf"), orgUnitId = o.Gulf, template = "location", homeSlug = "gulf-office" }),
            HttpStatusCode.Created));
        var olga = o.T["olga"].Client;
        var item = Id(await OkAsync(olga.PostAsJsonAsync(Items, new
        {
            siteId = site, type = "news",
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "Gulf picnic", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "Friday." } } } } } },
            },
        }), HttpStatusCode.Created));
        await OkAsync(olga.PostAsJsonAsync($"{Items}/{item}/publish", new { }));
        var gus = o.T["gus"].Client;
        Assert.Contains(item, Ids((await OkAsync(gus.GetAsync("/api/v1/content/feed"))).GetProperty("items")));
        Assert.Contains(site, Ids(await OkAsync(gus.GetAsync($"{Sites}/mine"))));

        var archived = await OkAsync(olga.PostAsync($"{Sites}/{site}/archive", null));
        Assert.Equal("archived", archived.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, archived.GetProperty("archivedAt").ValueKind);
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.SiteArchived, site.ToString());

        Assert.DoesNotContain(item, Ids((await OkAsync(gus.GetAsync("/api/v1/content/feed"))).GetProperty("items")));
        Assert.DoesNotContain(site, Ids(await OkAsync(gus.GetAsync($"{Sites}/mine"))));
        var read = await OkAsync(gus.GetAsync($"{Items}/{item}/view"));
        Assert.True(read.GetProperty("siteArchived").GetBoolean());
        Assert.Equal("conflict.content.site_archived",
            await ProblemKeyAsync(await olga.PutAsJsonAsync($"{Items}/{item}", new { pinned = true }), HttpStatusCode.Conflict));
        Assert.Equal("conflict.content.site_archived",
            await ProblemKeyAsync(await olga.PutAsJsonAsync($"{Sites}/{site}", new { names = Names("Still Gulf") }), HttpStatusCode.Conflict));
        // The owner still sees the archived site, so it can come back.
        Assert.Contains(site, Ids(await OkAsync(olga.GetAsync(Sites))));

        await OkAsync(olga.PostAsync($"{Sites}/{site}/restore", null));
        await world.Database.AssertAuditedAsync(o.T.Id, AuditActions.SiteRestored, site.ToString());
        Assert.Contains(item, Ids((await OkAsync(gus.GetAsync("/api/v1/content/feed"))).GetProperty("items")));

        // The tenant's default site stays.
        var defaultSite = (await OkAsync(admin.GetAsync(Sites))).EnumerateArray().Single(s => s.GetProperty("isDefault").GetBoolean());
        Assert.Equal("conflict.content.site_default", await ProblemKeyAsync(await admin.PostAsync($"{Sites}/{Id(defaultSite)}/archive", null), HttpStatusCode.Conflict));
    }
}
