using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T015 (US2 scenario 4, FR-024): before a brand's draft is published, a theme publisher sees what it reaches:
/// the inheriting scopes, the pages and their audience variants, the units below that use another brand, the values child
/// scopes override among the changed tokens, and a preview of a representative child scope with the draft applied. It
/// needs theme.publish and experience.approval_routes; other tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ThemeImpactTests(TenantWorld world)
{
    private const string Brands = "/api/v1/experience/brands";

    private static object Token(string value) => new Dictionary<string, object> { ["$value"] = value };

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

    private static async Task<Guid> BrandAsync(TestTenant t, string name, string radius)
    {
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync(Brands, new { name, tokens = new Dictionary<string, object> { ["radius.md"] = Token(radius) } }),
            HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    private static async Task<string> PageAsync(TestTenant t, Guid unit)
    {
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages",
            new { templateId = "T01", slug = "p-" + Guid.NewGuid().ToString("N")[..10], orgUnitId = unit }), HttpStatusCode.Created);
        return created.GetProperty("id").GetString()!;
    }

    private async Task<(TestTenant T, Guid Group, Guid Africa, string PlantPage, string AfricaPage)> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("thi-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var plant = await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.SeedUserAsync(t, "manager", MembershipTypes.Employee);
        await world.AssignAsync(t, "manager", "brand_manager", "tenant", null);

        var group = await BrandAsync(t, "Group", "12px");
        var africaBrand = await BrandAsync(t, "Africa", "20px");
        var client = t["admin"].Client;
        await OkAsync(client.PutAsJsonAsync("/api/v1/experience/brand-assignments/tenant", new { brandId = group }));
        await OkAsync(client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{africa}", new { brandId = africaBrand }));
        // Gulf keeps the group brand but overrides the radius.
        await OkAsync(client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{gulf}",
            new { brandId = (Guid?)null, overrides = new Dictionary<string, object> { ["radius.md"] = Token("9px") } }));
        var plantPage = await PageAsync(t, plant);
        var africaPage = await PageAsync(t, africa);
        await OkAsync(client.PutAsJsonAsync($"{Brands}/{group}/draft", new
        {
            name = "Group",
            tokens = new Dictionary<string, object> { ["radius.md"] = Token("4px"), ["color.accent.base"] = Token("#0f766e") },
        }));
        return (t, group, africaBrand, plantPage, africaPage);
    }

    [Fact]
    public async Task The_report_lists_what_inherits_what_stops_it_and_what_overrides_it_with_a_child_preview()
    {
        var (t, group, africaBrand, plantPage, africaPage) = await FreshAsync();
        var impact = await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{group}/draft/impact"));
        var report = impact.GetProperty("report");
        Assert.Equal("theme", report.GetProperty("artifact").GetString());
        Assert.True(report.GetProperty("tenantWide").GetBoolean());
        var pages = report.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains(plantPage, pages);
        Assert.DoesNotContain(africaPage, pages);
        Assert.Equal(["color.accent.base", "radius.md"], impact.GetProperty("changes").EnumerateArray().Select(c => c.GetString()));
        var excluded = Assert.Single(impact.GetProperty("excluded").EnumerateArray());
        Assert.Equal(t.Unit("Africa"), excluded.GetProperty("id").GetGuid());
        var overridden = Assert.Single(impact.GetProperty("overridden").EnumerateArray());
        Assert.Equal(t.Unit("Gulf").ToString(), overridden.GetProperty("scope").GetString());
        Assert.Equal("radius.md", overridden.GetProperty("token").GetString());

        // The default preview is the scope that overrides a changed value: its own radius wins, the new accent reaches it.
        Assert.Equal(t.Unit("Gulf").ToString(), impact.GetProperty("previewScope").GetString());
        var light = impact.GetProperty("preview").GetProperty("light");
        Assert.Equal("9px", light.GetProperty("--tb-radius-md").GetString());
        Assert.Equal("#0f766e", light.GetProperty("--tb-color-accent-base").GetString());
        var tenant = await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{group}/draft/impact?scope=tenant"));
        Assert.Equal("4px", tenant.GetProperty("preview").GetProperty("light").GetProperty("--tb-radius-md").GetString());
        var outside = await t["admin"].Client.GetAsync($"{Brands}/{group}/draft/impact?scope={t.Unit("Africa")}");
        Assert.Equal("validation.theme.preview_scope", await KeyAsync(outside));

        // A brand assigned to one unit reaches that unit and the pages in it, not the tenant.
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Brands}/{africaBrand}/draft", new { name = "Africa", tokens = new Dictionary<string, object> { ["radius.md"] = Token("2px") } }));
        var africa = await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{africaBrand}/draft/impact"));
        Assert.False(africa.GetProperty("report").GetProperty("tenantWide").GetBoolean());
        Assert.Equal([t.Unit("Africa")], africa.GetProperty("report").GetProperty("orgUnits").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()));
        Assert.Equal([africaPage], africa.GetProperty("report").GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(t.Unit("Africa").ToString(), africa.GetProperty("previewScope").GetString());
        Assert.Equal("2px", africa.GetProperty("preview").GetProperty("light").GetProperty("--tb-radius-md").GetString());

        // Reading the impact changes nothing: employees still get the version in force.
        var theme = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/theme?scope={t.Unit("Plant")}");
        var comfortable = theme.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("mode").GetString() == "light" && v.GetProperty("density").GetString() == "comfortable");
        Assert.Equal("9px", comfortable.GetProperty("tokens").GetProperty("radius.md").GetString());
    }

    [Fact]
    public async Task The_report_needs_a_draft_theme_publish_the_package_and_the_tenant()
    {
        var (t, group, _, _, _) = await FreshAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t["manager"].Client.GetAsync($"{Brands}/{group}/draft/impact")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"{Brands}/{group}/draft/impact")).StatusCode);
        await OkAsync(t["admin"].Client.DeleteAsync($"{Brands}/{group}/draft"), HttpStatusCode.NoContent);
        Assert.Equal("conflict.theme.no_draft", await KeyAsync(await t["admin"].Client.GetAsync($"{Brands}/{group}/draft/impact")));

        // Without experience.approval_routes there is no impact report; publishing stays one step.
        var plain = await world.CreateTenantAsync("thi-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var brand = (await OkAsync(plain["admin"].Client.GetAsync(Brands)))[0].GetProperty("id").GetGuid();
        await OkAsync(plain["admin"].Client.PutAsJsonAsync($"{Brands}/{brand}/draft", new { name = "Plain", tokens = new Dictionary<string, object> { ["radius.md"] = Token("3px") } }));
        Assert.Equal(HttpStatusCode.Forbidden, (await plain["admin"].Client.GetAsync($"{Brands}/{brand}/draft/impact")).StatusCode);
    }
}
