using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 US5 (FR-025): before an organization moves, the administrator sees what changes for it: the brand in effect,
/// the locks it gains and loses, the tokens that change, the pages and menus of the old parents that stop reaching it and
/// those of the new ones that start. A page shared with it on purpose keeps reaching it. Only someone who may make the
/// move sees the report, and nothing changes until the move is made.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class MoveImpactTests(TenantWorld world)
{
    private static object Token(string value) => new Dictionary<string, object> { ["$value"] = value };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> BrandAsync(TestTenant t, string name, string accent) =>
        (await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands",
            new { name, tokens = new Dictionary<string, object> { ["color.accent.base"] = Token(accent) } }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task<Guid> PageAsync(TestTenant t, Guid unit) =>
        (await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages",
            new { templateId = "T01", slug = "mv-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = unit }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private static async Task MenuAsync(TestTenant t, Guid unit, Guid page)
    {
        var node = Guid.NewGuid();
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/ia/{node}",
            new { parentId = (Guid?)null, labels = new { en = "Menu", ar = "قائمة" }, slug = "mn-" + Guid.NewGuid().ToString("N")[..8], pageId = page, ownerOrgUnitId = unit }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/menus/organization/{unit}", new { items = new object[] { new { nodeId = node } } }));
    }

    [Fact]
    public async Task The_report_lists_the_theme_pages_and_menus_that_change_before_a_move()
    {
        var t = await world.CreateTenantAsync("mvi-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var plant = await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.SeedUserAsync(t, "gulfadmin", MembershipTypes.Employee);
        await world.AssignAsync(t, "gulfadmin", "organization_administrator", "org_subtree", gulf);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);

        var gulfBrand = await BrandAsync(t, "Gulf brand", "#0f766e");
        var africaBrand = await BrandAsync(t, "Africa brand", "#b45309");
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{gulf}", new { brandId = gulfBrand }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{africa}", new { brandId = africaBrand }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{africa}", new { settings = new[] { "radius.md" } }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{gulf}", new { settings = new[] { "radius.lg" } }));

        var gulfPage = await PageAsync(t, gulf);
        var sharedGulfPage = await PageAsync(t, gulf);
        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.PutAsync($"/api/v1/experience/shares/page/{sharedGulfPage}/{africa}", null)).StatusCode);
        await PageAsync(t, africa);
        await MenuAsync(t, gulf, gulfPage);
        await MenuAsync(t, africa, gulfPage);

        var report = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={africa}"));
        Assert.Equal(1, report.GetProperty("units").GetInt32());
        var theme = report.GetProperty("theme");
        Assert.Equal("Gulf brand", theme.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal("Africa brand", theme.GetProperty("after").GetProperty("name").GetString());
        Assert.Equal(["radius.md"], theme.GetProperty("locksGained").EnumerateArray().Select(l => l.GetString()));
        Assert.Equal(["radius.lg"], theme.GetProperty("locksLost").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("color.accent.base", theme.GetProperty("tokens").EnumerateArray().Select(x => x.GetString()));
        // The Gulf page stops reaching the plant; the one Gulf shared with Africa still reaches it there; the Africa page starts.
        Assert.Equal(1, report.GetProperty("pagesLost").GetInt32());
        Assert.Equal(1, report.GetProperty("pagesGained").GetInt32());
        Assert.Equal(gulf, Assert.Single(report.GetProperty("menusLost").EnumerateArray()).GetProperty("id").GetGuid());
        var gained = Assert.Single(report.GetProperty("menusGained").EnumerateArray());
        Assert.Equal("Africa", gained.GetProperty("names").GetProperty("en").GetString());

        // Moving the whole of Gulf takes the plant with it, and Gulf's own lock moves along.
        var whole = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{gulf}?parentId={africa}"));
        Assert.Equal(2, whole.GetProperty("units").GetInt32());
        Assert.Empty(whole.GetProperty("theme").GetProperty("locksLost").EnumerateArray());
        // To the top of the tree.
        var top = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}"));
        Assert.Equal("Gulf brand", top.GetProperty("theme").GetProperty("before").GetProperty("name").GetString());

        // Nothing changed: the plant still has the Gulf brand.
        var still = await OkAsync(t["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={africa}"));
        Assert.Equal("Gulf brand", still.GetProperty("theme").GetProperty("before").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Only_someone_who_may_make_the_move_sees_its_impact()
    {
        var t = await world.CreateTenantAsync("mvd-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var plant = await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.SeedUserAsync(t, "gulfadmin", MembershipTypes.Employee);
        await world.AssignAsync(t, "gulfadmin", "organization_administrator", "org_subtree", gulf);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);

        // Inside Gulf the organization administrator may move the plant; to Africa they may not.
        Assert.Equal(HttpStatusCode.OK, (await t["gulfadmin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gulfadmin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={africa}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["gulfadmin"].Client.GetAsync($"/api/v1/experience/move-impact/{africa}?parentId={gulf}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={africa}")).StatusCode);
        // A unit cannot go under itself, and another tenant reaches nothing.
        var cycle = await t["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{gulf}?parentId={plant}");
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);
        Assert.Contains("validation.org.cycle", await cycle.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={africa}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.GetAsync($"/api/v1/experience/move-impact/{plant}?parentId={Guid.NewGuid()}")).StatusCode);
    }
}
