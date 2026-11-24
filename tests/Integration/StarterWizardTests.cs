using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T020 (US6, FR-027): the starter wizard for a new organization makes a brand with the chosen design and
/// navigation, pages from templates and their menu entries, and hands the organization to its administrator. Nothing of it
/// reaches people until it is published; every part stays editable; one starter per organization; refused without the
/// package, for an employee and for another tenant's organization.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class StarterWizardTests(TenantWorld world)
{
    private const string Starters = "/api/v1/experience/starters";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("stw-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        foreach (var name in new[] { "lead", "staff" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        return t;
    }

    private static object Body(Guid unit, string slug, Guid? admin, string design = "atlas") => new
    {
        orgUnitId = unit,
        design,
        navigation = "sidebar",
        language = "ar",
        adminUserId = admin,
        pages = new[] { new { templateId = "T01", slug, title = "الرئيسية" } },
    };

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    [Fact]
    public async Task The_starter_is_an_editable_draft_owned_by_the_organization_admin_until_published()
    {
        var t = await FreshAsync();
        var gulf = t.Unit("Gulf");
        var slug = "gulf-" + Guid.NewGuid().ToString("N")[..8];

        var created = await t["admin"].Client.PostAsJsonAsync(Starters, Body(gulf, slug, t["lead"].Id));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var starter = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("draft", starter.GetProperty("state").GetString());
        var id = starter.GetProperty("id").GetGuid();
        var brandId = starter.GetProperty("brandId").GetGuid();
        var page = starter.GetProperty("pages")[0];
        Assert.False(page.GetProperty("published").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperienceStarterCreated, id.ToString());

        // The brand carries the design, navigation pattern and the organization's title in the starter's language.
        var brand = await (await t["admin"].Client.GetAsync($"/api/v1/experience/brands/{brandId}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("atlas", brand.GetProperty("tokens").GetProperty("design.preset").GetProperty("$value").GetString());
        Assert.Equal("sidebar", brand.GetProperty("tokens").GetProperty("layout.navigation").GetProperty("$value").GetString());
        Assert.True(brand.GetProperty("assets").TryGetProperty("title.ar", out _));

        // A draft: people of Gulf do not see the page, and Gulf still has no brand of its own.
        Assert.Equal(HttpStatusCode.NotFound, (await t["staff"].Client.GetAsync($"/api/v1/experience/render?slug={slug}")).StatusCode);
        var assignment = await (await t["admin"].Client.GetAsync($"/api/v1/experience/brand-assignments/{gulf}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("brandId").ValueKind);

        // The organization's administrator owns it: they edit the page and the brand at Gulf, and the starter is listed for them.
        var pageId = page.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["lead"].Client.GetAsync($"/api/v1/experience/pages/{pageId}")).StatusCode);
        var delegated = await (await t["admin"].Client.GetAsync("/api/v1/experience/delegations")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(delegated.EnumerateArray(), d => d.GetProperty("userId").GetGuid() == t["lead"].Id
            && d.GetProperty("areas").EnumerateArray().Select(a => a.GetString()).SequenceEqual(["brand", "navigation", "pages"]));
        Assert.Equal(HttpStatusCode.OK, (await t["lead"].Client.GetAsync($"{Starters}/for/{gulf}")).StatusCode);
        var publish = await t["lead"].Client.PostAsync($"{Starters}/{id}/publish", null);
        Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());

        var published = await publish.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("published", published.GetProperty("state").GetString());
        Assert.True(published.GetProperty("pages")[0].GetProperty("published").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await t["staff"].Client.GetAsync($"/api/v1/experience/render?slug={slug}")).StatusCode);
        assignment = await (await t["admin"].Client.GetAsync($"/api/v1/experience/brand-assignments/{gulf}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(brandId, assignment.GetProperty("brandId").GetGuid());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperienceStarterPublished, id.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await t["lead"].Client.PostAsync($"{Starters}/{id}/publish", null)).StatusCode);
    }

    [Fact]
    public async Task One_starter_per_organization_with_valid_choices_for_those_allowed_only()
    {
        var t = await FreshAsync();
        var gulf = t.Unit("Gulf");
        Assert.Equal("validation.experience.starter_design",
            await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Starters, Body(gulf, "bad-" + Guid.NewGuid().ToString("N")[..8], null, "neon"))));
        // An employee is refused without learning the organization exists; so is another tenant's administrator.
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["staff"].Client.PostAsJsonAsync(Starters, Body(gulf, "st-" + Guid.NewGuid().ToString("N")[..8], null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await world.B["admin"].Client.PostAsJsonAsync(Starters, Body(gulf, "st-" + Guid.NewGuid().ToString("N")[..8], null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"{Starters}/for/{gulf}")).StatusCode);

        Assert.Equal(HttpStatusCode.Created,
            (await t["admin"].Client.PostAsJsonAsync(Starters, Body(gulf, "one-" + Guid.NewGuid().ToString("N")[..8], null))).StatusCode);
        Assert.Equal("conflict.experience.starter_exists",
            await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Starters, Body(gulf, "two-" + Guid.NewGuid().ToString("N")[..8], null))));

        // A failed run leaves nothing behind: a taken address rolls back the brand it had made.
        var other = await world.CreateUnitAsync(t, "Levant", t.Unit("Holding"), "subsidiary");
        var brands = (await (await t["admin"].Client.GetAsync("/api/v1/experience/brands")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength();
        var taken = (await t["admin"].Client.GetFromJsonAsync<JsonElement>($"{Starters}/for/{gulf}")).GetProperty("pages")[0].GetProperty("slug").GetString()!;
        Assert.Equal("conflict.page.slug_taken", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Starters, Body(other, taken, null))));
        Assert.Equal(brands, (await (await t["admin"].Client.GetAsync("/api/v1/experience/brands")).Content.ReadFromJsonAsync<JsonElement>()).GetArrayLength());
    }

    [Fact]
    public async Task The_wizard_needs_organization_branding_in_the_package()
    {
        var t = await world.CreateTenantAsync("stp-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var unit = await world.CreateUnitAsync(t, "Gulf", null, "organization");
        var refused = await t["admin"].Client.PostAsJsonAsync(Starters, Body(unit, "wp-" + Guid.NewGuid().ToString("N")[..8], null));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
