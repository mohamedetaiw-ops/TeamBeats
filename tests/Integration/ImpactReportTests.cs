using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 R2 T024 (US6 scenario 2, FR-054): before a page, a template or a theme assignment is published, the publisher
/// sees the organizations, audiences, pages and personal layouts it reaches, and how many personal layouts the change
/// touches. Scoped editors see reports only for what they may publish; packages without approval routes have none; other
/// tenants reach nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ImpactReportTests(TenantWorld world)
{
    private const string Experience = "/api/v1/experience";

    private static readonly object Text = new { body = new { en = "Shift times change on Sunday.", ar = "تتغير مواعيد الورديات يوم الأحد." } };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("imp-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "editor", "far", "reader" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, name == "far" ? "Africa" : "Gulf");
        }
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

    private static async Task<JsonNode> SaveAsync(TestTenant t, JsonNode page, string user, string? rule, params string[] placements)
    {
        var draft = page["draft"]!.DeepClone();
        draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == "main")!["placements"] = new JsonArray(placements
            .Select(id =>
            {
                var node = new JsonObject { ["id"] = id, ["componentRef"] = "reference.alpha.text@1", ["props"] = JsonSerializer.SerializeToNode(Text) };
                if (rule is not null && id == placements[^1])
                {
                    node["visibilityRuleRef"] = rule;
                }
                return (JsonNode)node;
            }).ToArray());
        var request = new HttpRequestMessage(HttpMethod.Put, $"{Experience}/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        var response = await t[user].Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private Task LayoutAsync(TestTenant t, string user, Guid page, string operations) =>
        world.Database.TenantScalarAsync<int>(t.Id,
            """
            insert into experience.personal_layout (tenant_id, user_id, page_id, schema_version, revision, operations)
            values (platform.current_tenant_id(), @u, @p, 1, 1, @ops::jsonb) returning 1
            """, ("u", t[user].Id), ("p", page), ("ops", operations));

    private static Guid[] Units(JsonElement report) => report.GetProperty("orgUnits").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToArray();

    [Fact]
    public async Task Publishing_a_page_reports_its_scope_audiences_and_the_personal_layouts_it_touches()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var rule = Guid.NewGuid();
        await OkAsync(admin.PutAsJsonAsync($"{Experience}/audience-rules/{rule}",
            new { labels = new { en = "Arabic readers", ar = "قراء العربية" }, conditions = new { locales = new[] { "ar" } } }));

        var created = await admin.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T01", slug = "imp-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = t.Unit("Gulf") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = await SaveAsync(t, JsonNode.Parse(await created.Content.ReadAsStringAsync())!, "admin", null, "intro", "shifts");
        var id = page["id"]!.GetValue<Guid>();
        await OkAsync(admin.PostAsJsonAsync($"{Experience}/pages/{id}/publish", new { reason = "first" }));

        // Three people arranged the page: one hid "shifts", one moved "intro", one added their own placement.
        await LayoutAsync(t, "editor", id, """[{"op":"hide","placementId":"shifts"}]""");
        await LayoutAsync(t, "reader", id, """[{"op":"move","placementId":"intro","regionId":"main","position":1}]""");
        await LayoutAsync(t, "far", id, """[{"op":"add","placementId":"mine","componentRef":"reference.alpha.text@1","regionId":"main"}]""");

        // The next draft drops "shifts" and adds a notice for an audience.
        page = JsonNode.Parse(await (await admin.GetAsync($"{Experience}/pages/{id}")).Content.ReadAsStringAsync())!;
        await SaveAsync(t, page, "admin", rule.ToString(), "intro", "notice");

        var report = await OkAsync(admin.GetAsync($"{Experience}/pages/{id}/impact"));
        Assert.Equal("page", report.GetProperty("artifact").GetString());
        Assert.False(report.GetProperty("tenantWide").GetBoolean());
        Assert.Equal(2, report.GetProperty("orgUnitCount").GetInt32());
        Assert.Equal([t.Unit("Gulf"), t.Unit("Plant")], Units(report));
        Assert.Contains("Gulf", report.GetProperty("orgUnits")[0].GetProperty("names").GetProperty("en").GetString());
        Assert.Equal([rule], report.GetProperty("audiences").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()));
        Assert.Equal("Arabic readers", report.GetProperty("audiences")[0].GetProperty("labels").GetProperty("en").GetString());
        Assert.Equal(1, report.GetProperty("pageCount").GetInt32());
        Assert.Equal(id, report.GetProperty("pages")[0].GetProperty("id").GetGuid());
        Assert.Equal(3, report.GetProperty("personalLayouts").GetInt32());
        Assert.Equal(1, report.GetProperty("personalLayoutsTouched").GetInt32());

        // An editor of the scope sees it before submitting; a publisher of another unit and a reader find no page.
        Assert.Equal(1, (await OkAsync(t["editor"].Client.GetAsync($"{Experience}/pages/{id}/impact"))).GetProperty("personalLayoutsTouched").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.GetAsync($"{Experience}/pages/{id}/impact")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["reader"].Client.GetAsync($"{Experience}/pages/{id}/impact")).StatusCode);

        // A page that was never published touches nobody.
        var fresh = await admin.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T01", slug = "imp-" + Guid.NewGuid().ToString("N")[..8] });
        var freshId = JsonNode.Parse(await fresh.Content.ReadAsStringAsync())!["id"]!.GetValue<Guid>();
        var tenantWide = await OkAsync(admin.GetAsync($"{Experience}/pages/{freshId}/impact"));
        Assert.True(tenantWide.GetProperty("tenantWide").GetBoolean());
        Assert.Equal(0, tenantWide.GetProperty("personalLayouts").GetInt32());

        // Another tenant reaches nothing.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Experience}/pages/{id}/impact")).StatusCode);
    }

    [Fact]
    public async Task Template_releases_and_theme_assignments_report_the_pages_they_reach_and_need_the_package()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var template = "tpl-" + Guid.NewGuid().ToString("N")[..10];
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync($"{Experience}/templates",
            new { id = template, basedOn = "T01", names = new Dictionary<string, string> { ["en"] = "Plant pages", ["ar"] = "صفحات المصنع" } })).StatusCode);
        await OkAsync(admin.PostAsJsonAsync($"{Experience}/templates/{template}/release", new { }));
        await OkAsync(admin.PutAsJsonAsync($"{Experience}/templates/{template}/distribution", new { orgUnitIds = new[] { t.Unit("Gulf") } }));
        var pages = new List<Guid>();
        foreach (var unit in new[] { "Gulf", "Plant" })
        {
            var created = await admin.PostAsJsonAsync($"{Experience}/pages", new { templateId = template, slug = "tp-" + Guid.NewGuid().ToString("N")[..8], orgUnitId = t.Unit(unit) });
            Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
            pages.Add(JsonNode.Parse(await created.Content.ReadAsStringAsync())!["id"]!.GetValue<Guid>());
        }
        await LayoutAsync(t, "reader", pages[1], """[{"op":"hide","placementId":"x"}]""");

        // A new draft version of the template is offered to both pages; their layouts stay as they are until applied.
        var report = await OkAsync(admin.GetAsync($"{Experience}/templates/{template}/impact"));
        Assert.Equal("template", report.GetProperty("artifact").GetString());
        Assert.Equal([t.Unit("Gulf"), t.Unit("Plant")], Units(report));
        Assert.Equal(2, report.GetProperty("pageCount").GetInt32());
        Assert.Equal(pages.ToHashSet(), report.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToHashSet());
        Assert.Equal(1, report.GetProperty("personalLayouts").GetInt32());
        Assert.Equal(0, report.GetProperty("personalLayoutsTouched").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["editor"].Client.GetAsync($"{Experience}/templates/{template}/impact")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"{Experience}/templates/tpl-unknown00/impact")).StatusCode);

        // A theme at the Plant reaches the Plant page; at the tenant, every page.
        var plant = await OkAsync(admin.GetAsync($"{Experience}/brand-assignments/{t.Unit("Plant")}/impact"));
        Assert.Equal("theme", plant.GetProperty("artifact").GetString());
        Assert.Equal([t.Unit("Plant")], Units(plant));
        Assert.Equal([pages[1]], plant.GetProperty("pages").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        Assert.Equal(0, plant.GetProperty("personalLayoutsTouched").GetInt32());
        var tenant = await OkAsync(admin.GetAsync($"{Experience}/brand-assignments/tenant/impact"));
        Assert.True(tenant.GetProperty("tenantWide").GetBoolean());
        Assert.Equal(2, tenant.GetProperty("pageCount").GetInt32());
        // A page publisher of Africa may not publish themes at the Plant, and the tenant scope is denied outright.
        Assert.Equal(HttpStatusCode.NotFound, (await t["far"].Client.GetAsync($"{Experience}/brand-assignments/{t.Unit("Plant")}/impact")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["far"].Client.GetAsync($"{Experience}/brand-assignments/tenant/impact")).StatusCode);
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Experience}/brand-assignments/{t.Unit("Plant")}/impact")).StatusCode);

        // Without approval routes in the package there are no impact reports; publishing stays one step.
        var basic = await FreshAsync("workplace");
        var page = await basic["admin"].Client.PostAsJsonAsync($"{Experience}/pages", new { templateId = "T01", slug = "b-" + Guid.NewGuid().ToString("N")[..8] });
        var basicId = JsonNode.Parse(await page.Content.ReadAsStringAsync())!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Experience}/pages/{basicId}/impact")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Experience}/brand-assignments/tenant/impact")).StatusCode);
    }
}
