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
/// Spec 032 R2 T027 (FR-045): governed fragments. A publisher of a scope keeps a reusable set of components with immutable
/// versions; pages of that scope and below place it by reference and show its current version, so a new version reaches every
/// page at once. Pages outside the scope, other units' publishers, other tenants and packages without fragments reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class FragmentTests(TenantWorld world)
{
    private const string Experience = "/api/v1/experience";

    private static object Text(string en) => new { body = new { en, ar = "نص " + en } };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("frg-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "pub", "editor", "far" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "far" ? "Africa" : "Gulf");
        }
        await world.AssignAsync(t, "pub", "page_publisher", "org_subtree", gulf);
        await world.AssignAsync(t, "editor", "page_editor", "org_subtree", gulf);
        await world.AssignAsync(t, "far", "page_publisher", "org_subtree", africa);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Fragment(Guid? unit, string key, params (string Id, string Text)[] placements) => new
    {
        key,
        names = new { en = "Visitor desk", ar = "مكتب الزوار" },
        orgUnitId = unit,
        placements = placements.Select(p => new { id = p.Id, componentRef = "reference.alpha.text@1", props = Text(p.Text) }).ToArray(),
    };

    private static async Task<JsonNode> PageAsync(TestTenant t, string user, Guid unit, params JsonNode[] placements)
    {
        var created = await t[user].Client.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T01", slug = "f-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = unit });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(placements);
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Experience}/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        var saved = await t[user].Client.SendAsync(request);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        return JsonNode.Parse(await saved.Content.ReadAsStringAsync())!;
    }

    private static JsonNode Reference(string id, string key) => new JsonObject { ["id"] = id, ["componentRef"] = "fragment:" + key };

    private static JsonNode Plain(string id) => new JsonObject { ["id"] = id, ["componentRef"] = "reference.alpha.text@1", ["props"] = JsonSerializer.SerializeToNode(Text("Own text")) };

    private static IEnumerable<JsonElement> MainOf(JsonElement rendered) =>
        rendered.GetProperty("regions").EnumerateArray().First(r => r.GetProperty("id").GetString() == "main").GetProperty("placements").EnumerateArray();

    [Fact]
    public async Task A_page_places_a_fragment_by_reference_and_shows_each_new_version()
    {
        var t = await FreshAsync();
        var pub = t["pub"].Client;
        var fragment = await OkAsync(pub.PostAsJsonAsync($"{Experience}/fragments", Fragment(t.Unit("Gulf"), "visitor-desk", ("hours", "Open 8 to 4"), ("map", "Gate B"))));
        var id = fragment.GetProperty("id").GetGuid();
        Assert.Equal("fragment:visitor-desk", fragment.GetProperty("reference").GetString());
        Assert.Equal(1, fragment.GetProperty("currentVersion").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.FragmentChanged, id.ToString());

        // A page of a unit below places it; the picker offers it there.
        var page = await PageAsync(t, "editor", t.Unit("Plant"), Plain("intro"), Reference("desk-1", "visitor-desk"));
        var pageId = page["id"]!.GetValue<string>();
        Assert.Empty(page["findings"]!.AsArray());
        var picker = await OkAsync(t["editor"].Client.GetAsync($"{Experience}/pages/{pageId}/components?region=main"));
        var offered = picker.EnumerateArray().Single(i => i.GetProperty("componentRef").GetString() == "fragment:visitor-desk");
        Assert.Equal("fragments", offered.GetProperty("category").GetString());
        Assert.Equal("Visitor desk", offered.GetProperty("name").GetString());

        // Published, it shows the fragment's placements in its place.
        await OkAsync(pub.PostAsJsonAsync($"{Experience}/pages/{pageId}/publish", new { reason = "Visitor desk" }));
        var slug = page["slug"]!.GetValue<string>();
        var shown = await OkAsync(pub.GetAsync($"{Experience}/render?slug={slug}"));
        Assert.Equal(["intro", "desk-1-hours", "desk-1-map"], MainOf(shown).Select(p => p.GetProperty("id").GetString()));

        // A new version reaches the page without republishing it; the first version stays readable.
        await OkAsync(pub.PutAsJsonAsync($"{Experience}/fragments/{id}",
            new { placements = new[] { new { id = "hours", componentRef = "reference.alpha.text@1", props = Text("Open 7 to 3") } }, note = "Ramadan hours" }));
        shown = await OkAsync(pub.GetAsync($"{Experience}/render?slug={slug}"));
        var placements = MainOf(shown).ToList();
        Assert.Equal(["intro", "desk-1-hours"], placements.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal("Open 7 to 3", placements[1].GetProperty("props").GetProperty("body").GetProperty("en").GetString());
        var versions = (await OkAsync(pub.GetAsync($"{Experience}/fragments/{id}/versions"))).EnumerateArray().ToList();
        Assert.Equal([2, 1], versions.Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal("Ramadan hours", versions[0].GetProperty("note").GetString());
        Assert.Equal(2, versions[1].GetProperty("placements").GetArrayLength());

        // Its manager sees the pages it reaches.
        var impact = await OkAsync(pub.GetAsync($"{Experience}/fragments/{id}/impact"));
        Assert.Equal(1, impact.GetProperty("pageCount").GetInt32());
        Assert.Equal(pageId, impact.GetProperty("pages")[0].GetProperty("id").GetString());

        // A fragment can be made from a page region's draft, but not from one that already places a fragment.
        var plain = await PageAsync(t, "editor", t.Unit("Plant"), Plain("intro"));
        var copied = await OkAsync(pub.PostAsJsonAsync($"{Experience}/fragments",
            new { key = "intro-copy", names = new { en = "Intro" }, orgUnitId = t.Unit("Gulf"), fromPageId = plain["id"]!.GetValue<string>(), regionId = "main" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await pub.PostAsJsonAsync($"{Experience}/fragments",
            new { key = "desk-copy", names = new { en = "Desk" }, orgUnitId = t.Unit("Gulf"), fromPageId = pageId, regionId = "main" })).StatusCode);
        Assert.Equal(["intro"], copied.GetProperty("placements").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await pub.PostAsJsonAsync($"{Experience}/fragments",
            new { key = "nested", names = new { en = "Nested" }, orgUnitId = t.Unit("Gulf"), fromPageId = pageId, regionId = "main", placements = new[] { new { id = "x", componentRef = "fragment:visitor-desk" } } })).StatusCode);

        // Archived, it shows nothing; the draft still saves with a warning, and publishing is refused until it is removed.
        await OkAsync(pub.DeleteAsync($"{Experience}/fragments/{id}"));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.FragmentArchived, id.ToString());
        shown = await OkAsync(pub.GetAsync($"{Experience}/render?slug={slug}"));
        Assert.Equal(["intro"], MainOf(shown).Select(p => p.GetProperty("id").GetString()));
        var findings = await OkAsync(pub.PostAsync($"{Experience}/pages/{pageId}/validate", null));
        Assert.Contains(findings.GetProperty("findings").EnumerateArray(), f => f.GetProperty("code").GetString() == "validation.page.fragment_unknown"
            && f.GetProperty("placement").GetString() == "desk-1" && f.GetProperty("severity").GetString() == "blocking");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await pub.PostAsJsonAsync($"{Experience}/pages/{pageId}/publish", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task Only_publishers_of_the_scope_manage_fragments_and_pages_elsewhere_cannot_place_them()
    {
        var t = await FreshAsync();
        var fragment = await OkAsync(t["pub"].Client.PostAsJsonAsync($"{Experience}/fragments", Fragment(t.Unit("Gulf"), "gulf-desk", ("hours", "Open"))));
        var id = fragment.GetProperty("id").GetGuid();

        // Keys are unique; content is checked.
        Assert.Equal(HttpStatusCode.Conflict, (await t["pub"].Client.PostAsJsonAsync($"{Experience}/fragments", Fragment(t.Unit("Gulf"), "gulf-desk", ("a", "b")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["pub"].Client.PostAsJsonAsync($"{Experience}/fragments", Fragment(t.Unit("Gulf"), "Bad Key", ("a", "b")))).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await t["pub"].Client.PostAsJsonAsync($"{Experience}/fragments", Fragment(t.Unit("Gulf"), "long-ids", ("a-very-long-inner-id", "b")))).StatusCode);

        // An editor of the scope reads and places it but does not change it.
        Assert.Equal(id, (await OkAsync(t["editor"].Client.GetAsync($"{Experience}/fragments/{id}"))).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["editor"].Client.PutAsJsonAsync($"{Experience}/fragments/{id}", new { placements = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["editor"].Client.DeleteAsync($"{Experience}/fragments/{id}")).StatusCode);
        Assert.False((await t["editor"].Client.PostAsJsonAsync($"{Experience}/fragments", Fragment(t.Unit("Gulf"), "editor-desk", ("a", "b")))).IsSuccessStatusCode);

        // A publisher of another unit finds nothing, cannot list it, and a page of theirs cannot place it.
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.GetAsync($"{Experience}/fragments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.PutAsJsonAsync($"{Experience}/fragments/{id}", new { placements = Array.Empty<object>() })).StatusCode);
        Assert.DoesNotContain((await OkAsync(t["far"].Client.GetAsync($"{Experience}/fragments"))).EnumerateArray(), f => f.GetProperty("id").GetGuid() == id);
        var elsewhere = await PageAsync(t, "far", t.Unit("Africa"), Reference("desk-1", "gulf-desk"));
        Assert.Contains(elsewhere["findings"]!.AsArray(), f => f!["code"]!.GetValue<string>() == "validation.page.fragment_out_of_scope");
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await t["far"].Client.PostAsJsonAsync($"{Experience}/pages/{elsewhere["id"]}/publish", new { reason = "x" })).StatusCode);
        var picker = await OkAsync(t["far"].Client.GetAsync($"{Experience}/pages/{elsewhere["id"]}/components?region=main"));
        Assert.DoesNotContain(picker.EnumerateArray(), i => i.GetProperty("componentRef").GetString() == "fragment:gulf-desk");

        // Another tenant reaches nothing; a package without fragments has none.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Experience}/fragments/{id}")).StatusCode);
        Assert.Empty((await OkAsync(other["admin"].Client.GetAsync($"{Experience}/fragments"))).EnumerateArray());
        var basic = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Experience}/fragments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.PostAsJsonAsync($"{Experience}/fragments", Fragment(null, "basic-desk", ("a", "b")))).StatusCode);
    }
}
