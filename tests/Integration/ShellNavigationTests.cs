using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 T004 to T006 (R1 backend): the shell configuration, global, organization and section menus, the information
/// architecture with old slug redirects, breadcrumbs, quick navigation, and the one recovery answer for every
/// destination the caller cannot open. Tests that change tenant wide menus use their own tenant.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ShellNavigationTests(TenantWorld world)
{
    private static string Slug(string prefix = "n") => prefix + "-" + Guid.NewGuid().ToString("N")[..10];

    private static readonly object Text = new { body = new { en = "Wear a helmet on the plant floor.", ar = "ارتد الخوذة في أرض المصنع." } };

    private async Task<TestTenant> FreshAsync() => await world.CreateTenantAsync("sn-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");

    /// <summary>A page from template T01 with one text placement, published unless asked otherwise.</summary>
    private static async Task<(Guid Id, string Slug)> PageAsync(TestTenant t, bool publish = true, Guid? unit = null)
    {
        var slug = Slug("p");
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug, orgUnitId = unit });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(
            new JsonObject { ["id"] = "intro", ["componentRef"] = "reference.alpha.text@1", ["props"] = JsonSerializer.SerializeToNode(Text) });
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        Assert.True((await t["admin"].Client.SendAsync(request)).IsSuccessStatusCode);
        if (publish)
        {
            Assert.True((await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/publish", new { reason = "first" })).IsSuccessStatusCode);
        }
        return (page["id"]!.GetValue<Guid>(), slug);
    }

    private static Task<HttpResponseMessage> PutNodeAsync(TestTenant t, Guid id, object body, string user = "admin") =>
        t[user].Client.PutAsJsonAsync($"/api/v1/experience/ia/{id}", body);

    private static async Task<Guid> NodeAsync(TestTenant t, string slug, Guid? page = null, string? url = null, Guid? parent = null, Guid? owner = null,
        string user = "admin", string label = "Entry")
    {
        var id = Guid.NewGuid();
        var response = await PutNodeAsync(t, id, new { parentId = parent, labels = new { en = label, ar = "عنصر" }, slug, pageId = page, url, ownerOrgUnitId = owner }, user);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return id;
    }

    private static Task<HttpResponseMessage> PutMenuAsync(TestTenant t, string kind, string scope, object items, string user = "admin") =>
        t[user].Client.PutAsJsonAsync($"/api/v1/experience/menus/{kind}/{scope}", new { items });

    private static async Task<JsonElement> ResolveAsync(TestTenant t, string path, string user = "admin") =>
        await t[user].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/resolve?path={Uri.EscapeDataString(path)}");

    private static IEnumerable<string> Hrefs(JsonElement links) => links.EnumerateArray().Select(l => l.GetProperty("href").GetString()!);

    [Fact]
    public async Task Shell_has_a_default_layout_and_publishing_it_changes_no_page()
    {
        var t = await FreshAsync();
        var (pageId, _) = await PageAsync(t);
        var shell = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        Assert.Equal(0, shell.GetProperty("version").GetInt32());
        Assert.Equal(["identity", "global_navigation"], shell.GetProperty("layout").GetProperty("start").EnumerateArray().Select(r => r.GetString()));
        // Every package has the assistant (spec 002), so the shell offers the Ask TeamBeats button (spec 008 FR-056).
        Assert.True(shell.GetProperty("assistant").GetBoolean());

        var releasesBefore = await t["admin"].Client.GetStringAsync($"/api/v1/experience/pages/{pageId}/releases");
        var definition = new
        {
            start = new[] { "identity", "global_navigation" },
            center = new[] { "search" },
            end = new[] { "notifications", "utilities", "settings" },
            below = new[] { "breadcrumbs" },
            utilities = new[] { "world_clock", "weather" },
        };
        var published = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/shell/definition", definition);
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        Assert.Equal(1, (await published.ReadAsync<JsonElement>()).GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ShellPublished);
        Assert.Equal(releasesBefore, await t["admin"].Client.GetStringAsync($"/api/v1/experience/pages/{pageId}/releases"));

        shell = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        Assert.Equal(1, shell.GetProperty("version").GetInt32());
        Assert.Equal(["world_clock", "weather"], shell.GetProperty("layout").GetProperty("utilities").EnumerateArray().Select(u => u.GetString()));
    }

    private static object HomeShell(Guid? home) => new
    {
        start = new[] { "identity", "global_navigation" },
        center = new[] { "search" },
        end = new[] { "settings" },
        below = new[] { "breadcrumbs" },
        utilities = Array.Empty<string>(),
        homePageId = home,
    };

    [Fact]
    public async Task Home_page_is_set_in_the_shell_and_shown_only_when_published_and_of_this_tenant()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", "employee");
        var (draftId, _) = await PageAsync(t, publish: false);
        var set = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/shell/definition", HomeShell(draftId));
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        // A home that is not published yet is not offered, so the root address keeps its plain welcome.
        var shell = await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        Assert.Equal(JsonValueKind.Null, shell.GetProperty("home").ValueKind);

        var (homeId, homeSlug) = await PageAsync(t);
        Assert.True((await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/shell/definition", HomeShell(homeId))).IsSuccessStatusCode);
        shell = await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        Assert.Equal(homeSlug, shell.GetProperty("home").GetString());
        Assert.Equal(homeId, shell.GetProperty("layout").GetProperty("homePageId").GetGuid());

        // A page of another tenant, or no page at all, cannot be the home.
        var (otherPage, _) = await PageAsync(world.A);
        foreach (var id in new[] { otherPage, Guid.NewGuid() })
        {
            var refused = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/shell/definition", HomeShell(id));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("validation.shell.home_unknown", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Shell_definition_is_validated_and_needs_shell_edit()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "employee", "employee");
        var bad = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/shell/definition", new
        {
            start = new[] { "global_navigation", "banner" },
            center = new[] { "search", "search" },
            end = Array.Empty<string>(),
            below = Array.Empty<string>(),
            utilities = new[] { "lottery" },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        foreach (var key in new[] { "region_unknown", "region_twice", "region_required", "utility_unknown", "utilities_unplaced" })
        {
            Assert.Contains("validation.shell." + key, body, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync("/api/v1/experience/shell/definition")).StatusCode);
        var denied = await t["employee"].Client.PutAsJsonAsync("/api/v1/experience/shell/definition",
            new { start = new[] { "identity" }, center = Array.Empty<string>(), end = new[] { "settings" }, below = Array.Empty<string>(), utilities = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        // Every member reads the shell.
        Assert.True((await t["employee"].Client.GetAsync("/api/v1/experience/shell")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Each_menu_publishes_on_its_own_and_lists_only_pages_the_caller_may_open()
    {
        var t = await FreshAsync();
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/membership-policies/contractor", new { defaultEmployeeRole = false })).EnsureSuccessStatusCode();
        await world.SeedUserAsync(t, "employee", "employee");
        await world.SeedUserAsync(t, "contractor", "contractor");
        var (live, _) = await PageAsync(t);
        var (draft, _) = await PageAsync(t, publish: false);
        var newsSlug = Slug();
        var news = await NodeAsync(t, newsSlug, page: live);
        var hidden = await NodeAsync(t, Slug(), page: draft);
        var under = await NodeAsync(t, Slug(), url: "/directory", parent: hidden);
        var external = await NodeAsync(t, Slug(), url: "https://example.org/help");

        var global = await PutMenuAsync(t, "global", "tenant", new object[]
        {
            new { nodeId = news, children = new object[] { new { nodeId = external } } },
            new { nodeId = hidden, children = new object[] { new { nodeId = under } } },
        });
        Assert.True(global.IsSuccessStatusCode, await global.Content.ReadAsStringAsync());
        Assert.Equal(1, (await global.ReadAsync<JsonElement>()).GetProperty("version").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.NavigationPublished);
        var again = await PutMenuAsync(t, "global", "tenant", new object[] { new { nodeId = news }, new { nodeId = hidden } });
        Assert.Equal(2, (await again.ReadAsync<JsonElement>()).GetProperty("version").GetInt32());

        // A section menu publishes alone and leaves the global menu as it was.
        Assert.True((await PutMenuAsync(t, "section", news.ToString(), new object[] { new { nodeId = external } })).IsSuccessStatusCode);
        var menus = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/menus");
        Assert.Equal(2, menus.EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "global").GetProperty("version").GetInt32());
        Assert.Equal(1, menus.EnumerateArray().Single(m => m.GetProperty("kind").GetString() == "section").GetProperty("version").GetInt32());

        // The unpublished page and everything under it are left out; the contractor without page.view sees no page at all.
        var employee = (await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell")).GetProperty("globalMenu");
        Assert.Single(employee.EnumerateArray());
        Assert.Equal(news, employee[0].GetProperty("nodeId").GetGuid());
        var contractor = (await t["contractor"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell")).GetProperty("globalMenu");
        Assert.Empty(contractor.EnumerateArray());

        // Quick navigation follows the same rule.
        var quick = await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/quick-nav?q=n-");
        var quickHrefs = Hrefs(quick).ToList();
        Assert.Contains("/" + newsSlug, quickHrefs);
        Assert.Contains("https://example.org/help", quickHrefs);
        Assert.DoesNotContain("/directory", quickHrefs);
        var contractorQuick = await t["contractor"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/quick-nav?q=p-");
        Assert.Empty(contractorQuick.EnumerateArray());
    }

    [Fact]
    public async Task Menu_items_are_limited_and_must_be_known_entries()
    {
        var t = await FreshAsync();
        var node = await NodeAsync(t, Slug(), url: "/a");
        var deep = await PutMenuAsync(t, "global", "tenant", new object[]
        {
            new { nodeId = node, children = new object[] { new { nodeId = node, children = new object[] { new { nodeId = node } } } } },
        });
        Assert.Contains("validation.navigation.too_deep", await deep.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var many = await PutMenuAsync(t, "global", "tenant", Enumerable.Range(0, 51).Select(_ => new { nodeId = node }).ToArray());
        Assert.Contains("validation.navigation.too_many", await many.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var unknown = await PutMenuAsync(t, "global", "tenant", new object[] { new { nodeId = Guid.NewGuid() } });
        Assert.Contains("validation.navigation.node_unknown", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await PutMenuAsync(t, "sidebar", "tenant", Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutMenuAsync(t, "global", "elsewhere", Array.Empty<object>())).StatusCode);
    }

    [Fact]
    public async Task Missing_unpublished_and_not_permitted_destinations_give_the_same_recovery_answer()
    {
        var t = await FreshAsync();
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/membership-policies/contractor", new { defaultEmployeeRole = false })).EnsureSuccessStatusCode();
        await world.SeedUserAsync(t, "employee", "employee");
        await world.SeedUserAsync(t, "contractor", "contractor");
        var (live, liveSlug) = await PageAsync(t);
        var (_, draftSlug) = await PageAsync(t, publish: false);
        var liveNode = Slug();
        await NodeAsync(t, liveNode, page: live);

        var missing = await t["employee"].Client.GetStringAsync("/api/v1/experience/resolve?path=/" + Slug());
        var unpublished = await t["employee"].Client.GetStringAsync("/api/v1/experience/resolve?path=/" + draftSlug);
        var notPermitted = await t["contractor"].Client.GetStringAsync("/api/v1/experience/resolve?path=/" + liveNode);
        var notPermittedPage = await t["contractor"].Client.GetStringAsync("/api/v1/experience/resolve?path=/" + liveSlug);
        Assert.Equal(missing, unpublished);
        Assert.Equal(missing, notPermitted);
        Assert.Equal(missing, notPermittedPage);
        var recovery = JsonDocument.Parse(missing).RootElement;
        Assert.Equal("recovery", recovery.GetProperty("kind").GetString());
        Assert.Equal(["/", "/search"], Hrefs(recovery.GetProperty("recovery")));

        // The employee may open both the node and the page outside the IA.
        Assert.Equal(liveSlug, (await ResolveAsync(t, "/" + liveNode, "employee")).GetProperty("pageSlug").GetString());
        Assert.Equal("page", (await ResolveAsync(t, "/" + liveSlug, "employee")).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Old_slugs_redirect_and_breadcrumbs_and_section_menus_follow_the_ia()
    {
        var t = await FreshAsync();
        var (page, pageSlug) = await PageAsync(t);
        var root = Slug("about");
        var rootId = await NodeAsync(t, root, url: "/about", label: "About");
        var leafSlug = root + "/" + Slug("team");
        var leafId = await NodeAsync(t, leafSlug, page: page, parent: rootId, label: "Team");
        var sibling = await NodeAsync(t, root + "/" + Slug("history"), url: "https://example.org/history", parent: rootId, label: "History");
        Assert.True((await PutMenuAsync(t, "section", rootId.ToString(), new object[] { new { nodeId = leafId }, new { nodeId = sibling } })).IsSuccessStatusCode);

        var resolved = await ResolveAsync(t, "/" + leafSlug);
        Assert.Equal("page", resolved.GetProperty("kind").GetString());
        Assert.Equal(pageSlug, resolved.GetProperty("pageSlug").GetString());
        Assert.Equal(["About", "Team"], resolved.GetProperty("breadcrumbs").EnumerateArray().Select(c => c.GetProperty("label").GetString()));
        Assert.Equal(["/" + leafSlug, "https://example.org/history"], Hrefs(resolved.GetProperty("sectionMenu")));
        // Labels follow the reader's own language.
        await world.SeedUserAsync(t, "reader", "employee");
        (await t["reader"].Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var ar = await ResolveAsync(t, "/" + leafSlug, "reader");
        Assert.Equal("عنصر", ar.GetProperty("breadcrumbs")[0].GetProperty("label").GetString());

        // Renaming keeps the old address working as a redirect, audited.
        var renamed = root + "/" + Slug("people");
        var moved = await PutNodeAsync(t, leafId, new { parentId = rootId, labels = new { en = "People" }, slug = renamed, pageId = page });
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        Assert.Contains(leafSlug, (await moved.ReadAsync<JsonElement>()).GetProperty("oldSlugs").EnumerateArray().Select(s => s.GetString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.NavigationNodeChanged, leafId.ToString());
        var redirect = await ResolveAsync(t, "/" + leafSlug);
        Assert.Equal("redirect", redirect.GetProperty("kind").GetString());
        Assert.Equal("/" + renamed, redirect.GetProperty("redirectTo").GetString());

        // A new entry that takes the old address ends the redirect.
        await NodeAsync(t, leafSlug, url: "/elsewhere", parent: rootId);
        Assert.Equal("/elsewhere", (await ResolveAsync(t, "/" + leafSlug)).GetProperty("redirectTo").GetString());
    }

    [Fact]
    public async Task Ia_entries_are_validated()
    {
        var t = await FreshAsync();
        var taken = Slug();
        var id = await NodeAsync(t, taken, url: "/a");
        var child = await NodeAsync(t, taken + "/child", url: "/b", parent: id);
        async Task<string> Errors(object body, Guid? node = null) => await (await PutNodeAsync(t, node ?? Guid.NewGuid(), body)).Content.ReadAsStringAsync();

        Assert.Contains("validation.navigation.label", await Errors(new { labels = new { }, slug = Slug(), url = "/a" }), StringComparison.Ordinal);
        Assert.Contains("validation.navigation.slug", await Errors(new { labels = new { en = "x" }, slug = "Not A Slug", url = "/a" }), StringComparison.Ordinal);
        Assert.Contains("validation.navigation.slug_taken", await Errors(new { labels = new { en = "x" }, slug = taken, url = "/a" }), StringComparison.Ordinal);
        Assert.Contains("validation.navigation.target", await Errors(new { labels = new { en = "x" }, slug = Slug() }), StringComparison.Ordinal);
        Assert.Contains("validation.navigation.url", await Errors(new { labels = new { en = "x" }, slug = Slug(), url = "javascript:alert(1)" }), StringComparison.Ordinal);
        Assert.Contains("validation.navigation.url", await Errors(new { labels = new { en = "x" }, slug = Slug(), url = "//evil.example" }), StringComparison.Ordinal);
        Assert.Contains("validation.navigation.page_unknown", await Errors(new { labels = new { en = "x" }, slug = Slug(), pageId = Guid.NewGuid() }), StringComparison.Ordinal);
        // A node cannot move under itself or its own child.
        Assert.Contains("validation.navigation.parent", await Errors(new { labels = new { en = "x" }, slug = taken, url = "/a", parentId = child }, id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Organization_admin_edits_only_their_own_organization_menus_and_entries()
    {
        var t = world.A;
        var gulf = t.Unit("Gulf").ToString();
        var africa = t.Unit("Africa").ToString();
        var gulfNode = await NodeAsync(t, Slug(), url: "/gulf", owner: t.Unit("Gulf"), user: "orgadmin");
        Assert.True((await PutMenuAsync(t, "organization", gulf, new object[] { new { nodeId = gulfNode } }, "orgadmin")).IsSuccessStatusCode);
        Assert.True((await PutMenuAsync(t, "section", gulfNode.ToString(), new object[] { new { nodeId = gulfNode } }, "orgadmin")).IsSuccessStatusCode);

        // A sibling organization, the tenant menu and tenant entries are out of reach.
        Assert.Equal(HttpStatusCode.NotFound, (await PutMenuAsync(t, "organization", africa, new object[] { new { nodeId = gulfNode } }, "orgadmin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutMenuAsync(t, "global", "tenant", new object[] { new { nodeId = gulfNode } }, "orgadmin")).StatusCode);
        var africaNode = await NodeAsync(t, Slug(), url: "/africa", owner: t.Unit("Africa"));
        Assert.Equal(HttpStatusCode.NotFound, (await PutNodeAsync(t, africaNode, new { labels = new { en = "Mine" }, slug = Slug(), url = "/x", ownerOrgUnitId = t.Unit("Gulf") }, "orgadmin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutNodeAsync(t, Guid.NewGuid(), new { labels = new { en = "x" }, slug = Slug(), url = "/x", ownerOrgUnitId = t.Unit("Africa") }, "orgadmin")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await PutNodeAsync(t, Guid.NewGuid(), new { labels = new { en = "x" }, slug = Slug(), url = "/x" }, "orgadmin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutMenuAsync(t, "section", africaNode.ToString(), new object[] { new { nodeId = africaNode } }, "orgadmin")).StatusCode);

        // The IA list shows only entries the caller may edit.
        var listed = await t["orgadmin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/ia");
        Assert.Contains(listed.EnumerateArray(), n => n.GetProperty("id").GetGuid() == gulfNode);
        Assert.DoesNotContain(listed.EnumerateArray(), n => n.GetProperty("id").GetGuid() == africaNode);

        // Members of Gulf see the organization menu with its name; members of Africa do not.
        var gulfShell = await t["employee_gulf"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        var orgMenu = gulfShell.GetProperty("organizationMenus").EnumerateArray().Single(m => m.GetProperty("orgUnitId").GetString() == gulf);
        Assert.Equal("Gulf", orgMenu.GetProperty("name").GetString());
        var africaShell = await t["employee_africa"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        Assert.DoesNotContain(africaShell.GetProperty("organizationMenus").EnumerateArray(), m => m.GetProperty("orgUnitId").GetString() == gulf);
        Assert.Contains(gulfShell.GetProperty("organizations").EnumerateArray(), o => o.GetProperty("id").GetString() == t.Unit("GulfFinance").ToString());
    }

    [Fact]
    public async Task Another_tenants_menus_entries_and_scopes_are_not_found()
    {
        var a = world.A;
        var b = world.B;
        var slug = Slug();
        var node = await NodeAsync(a, slug, url: "/only-a", owner: a.Unit("Gulf"));
        Assert.Equal(HttpStatusCode.NotFound, (await PutMenuAsync(b, "organization", a.Unit("Gulf").ToString(), Array.Empty<object>())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PutMenuAsync(b, "section", node.ToString(), Array.Empty<object>())).StatusCode);
        var unknownInB = await PutMenuAsync(b, "global", "tenant", new object[] { new { nodeId = node } });
        Assert.Contains("validation.navigation.node_unknown", await unknownInB.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("recovery", (await ResolveAsync(b, "/" + slug)).GetProperty("kind").GetString());
        Assert.DoesNotContain((await b["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/ia")).EnumerateArray(), n => n.GetProperty("id").GetGuid() == node);
        // The same id cannot be written over from another tenant either: it is a separate entry there.
        Assert.True((await PutNodeAsync(b, node, new { labels = new { en = "B" }, slug, url = "/only-b" })).IsSuccessStatusCode);
        Assert.Equal("/only-a", (await ResolveAsync(a, "/" + slug)).GetProperty("redirectTo").GetString());
    }
}
