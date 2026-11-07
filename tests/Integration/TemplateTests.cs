using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 US5 (T010, T011): tenant templates copied from platform templates, with their own names, variants and region
/// locks, versions that never change existing pages, deprecation, distribution to org subtrees, the
/// <c>experience.max_custom_templates</c> limit, and tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class TemplateTests(TenantWorld world)
{
    private const string Templates = "/api/v1/experience/templates";

    private static string NewId() => "tpl-" + Guid.NewGuid().ToString("N")[..10];

    private static string Slug() => "p-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<JsonNode> CreateTemplateAsync(TestTenant t, string id, string basedOn, string user = "admin")
    {
        var response = await t[user].Client.PostAsJsonAsync(Templates, new
        {
            id, basedOn,
            names = new Dictionary<string, string> { ["en"] = "Gulf news", ["ar"] = "أخبار الخليج" },
            descriptions = new Dictionary<string, string> { ["en"] = "News pages for the Gulf companies." },
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode> PostAsync(TestTenant t, string path, object? body = null, string user = "admin")
    {
        var response = await t[user].Client.PostAsJsonAsync(path, body ?? new { });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static Task<HttpResponseMessage> CreatePageAsync(TestTenant t, string template, Guid? unit = null, string user = "admin") =>
        t[user].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = template, slug = Slug(), orgUnitId = unit });

    private static async Task<string> ErrorAsync(HttpResponseMessage response, string field)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("errors").GetProperty(field)[0].GetProperty("key").GetString()!;
    }

    private static async Task<IReadOnlyList<string>> ChooserAsync(TestTenant t, string user) =>
        (await t[user].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/catalogue")).GetProperty("templates").EnumerateArray()
            .Select(x => x.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Template_distributed_to_the_gulf_is_offered_there_and_can_be_neither_seen_nor_used_elsewhere()
    {
        var t = world.A;
        var id = NewId();
        var created = await CreateTemplateAsync(t, id, "T02");
        Assert.Equal("T02", created["basedOn"]!.GetValue<string>());
        Assert.Equal("draft", created["versions"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("L01", created["latest"]!["layout"]!.GetValue<string>());

        // A draft is not offered to editors; a released version is.
        Assert.DoesNotContain(id, await ChooserAsync(t, "admin"));
        await PostAsync(t, $"{Templates}/{id}/release");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TemplateReleased, id);
        var chooser = (await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/catalogue")).GetProperty("templates")
            .EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
        Assert.Equal("Gulf news", chooser.GetProperty("name").GetString());
        Assert.True(chooser.GetProperty("custom").GetBoolean());

        var distributed = await t["admin"].Client.PutAsJsonAsync($"{Templates}/{id}/distribution", new { orgUnitIds = new[] { t.Unit("Gulf") } });
        Assert.True(distributed.IsSuccessStatusCode, await distributed.Content.ReadAsStringAsync());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TemplateDistributed, id);

        await world.SeedUserAsync(t, "tpl_gulf", "employee");
        await world.AssignAsync(t, "tpl_gulf", "page_publisher", "org_subtree", t.Unit("Gulf"));
        await world.SeedUserAsync(t, "tpl_africa", "employee");
        await world.AssignAsync(t, "tpl_africa", "page_publisher", "org_subtree", t.Unit("Africa"));

        Assert.Contains(id, await ChooserAsync(t, "tpl_gulf"));
        Assert.DoesNotContain(id, await ChooserAsync(t, "tpl_africa"));

        var gulfPage = await CreatePageAsync(t, id, t.Unit("GulfFinance"), "tpl_gulf");
        Assert.True(gulfPage.StatusCode == HttpStatusCode.Created, await gulfPage.Content.ReadAsStringAsync());
        var page = JsonNode.Parse(await gulfPage.Content.ReadAsStringAsync())!;
        Assert.Equal(id, page["templateId"]!.GetValue<string>());
        Assert.Equal("1", page["templateVersion"]!.GetValue<string>());

        // Outside the distribution the template answers as unknown, for the Africa editor and for the tenant administrator alike.
        Assert.Equal("validation.page.template_unknown", await ErrorAsync(await CreatePageAsync(t, id, t.Unit("Africa"), "tpl_africa"), "templateId"));
        Assert.Equal("validation.page.template_unknown", await ErrorAsync(await CreatePageAsync(t, id, t.Unit("Africa")), "templateId"));
        Assert.Equal("validation.page.template_unknown", await ErrorAsync(await CreatePageAsync(t, id), "templateId"));

        // Page editors are not template designers.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["tpl_gulf"].Client.GetAsync(Templates)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["tpl_gulf"].Client.PostAsJsonAsync($"{Templates}/{id}/release", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await t["tpl_gulf"].Client.PutAsJsonAsync($"{Templates}/{id}/distribution", new { orgUnitIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task Version_two_leaves_existing_pages_alone_until_applied_and_a_deprecated_version_keeps_pages_working_but_blocks_new_ones()
    {
        var t = await world.CreateTenantAsync("tv-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var id = NewId();
        var template = await CreateTemplateAsync(t, id, "T01");
        await PostAsync(t, $"{Templates}/{id}/release");

        var created = await CreatePageAsync(t, id);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var pageId = page["id"]!.GetValue<string>();
        var slug = page["slug"]!.GetValue<string>();
        Assert.True((await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{pageId}/publish", new { reason = "first" })).IsSuccessStatusCode);

        // Version 2: fewer variants and the main region locked. Saving after a release starts a new draft version.
        var regions = template["latest"]!["regions"]!.DeepClone().AsArray();
        foreach (var region in regions)
        {
            region!["policy"]!["protection"] = "protected";
        }
        var draft = await t["admin"].Client.PutAsJsonAsync($"{Templates}/{id}/draft", new
        {
            names = new Dictionary<string, string> { ["en"] = "Gulf news v2" },
            variants = new[] { "balanced", "compact" },
            regions,
        });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());
        var saved = JsonNode.Parse(await draft.Content.ReadAsStringAsync())!;
        Assert.Equal([2, 1], saved["versions"]!.AsArray().Select(v => v!["version"]!.GetValue<int>()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.RegionLockChanged, id);

        var before = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{pageId}");
        Assert.Equal("1", before.GetProperty("templateVersion").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("newerTemplateVersion").ValueKind);

        await PostAsync(t, $"{Templates}/{id}/release");
        var told = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{pageId}");
        Assert.Equal("1", told.GetProperty("templateVersion").GetString());
        Assert.Equal("2", told.GetProperty("newerTemplateVersion").GetString());
        Assert.Equal("1", told.GetProperty("draft").GetProperty("template").GetProperty("version").GetString());

        // The owner applies version 2 through a template change; the page then records it.
        var change = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/experience/pages/{pageId}/change-template")
        {
            Content = JsonContent.Create(new { templateId = id, variant = "balanced", apply = true }),
        };
        change.Headers.TryAddWithoutValidation("If-Match", told.GetProperty("eTag").GetString());
        var applied = await t["admin"].Client.SendAsync(change);
        Assert.True(applied.IsSuccessStatusCode, await applied.Content.ReadAsStringAsync());
        var moved = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{pageId}");
        Assert.Equal("2", moved.GetProperty("templateVersion").GetString());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("newerTemplateVersion").ValueKind);

        // A region the template protects cannot be removed through the API (US5 scenario 5).
        var withoutRegions = JsonNode.Parse(moved.GetProperty("draft").GetRawText())!;
        withoutRegions["regions"] = new JsonArray();
        var strip = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{pageId}/draft")
        {
            Content = JsonContent.Create(new { definition = withoutRegions }),
        };
        strip.Headers.TryAddWithoutValidation("If-Match", moved.GetProperty("eTag").GetString());
        var stripped = await t["admin"].Client.SendAsync(strip);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, stripped.StatusCode);
        Assert.Contains((await stripped.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("findings").EnumerateArray(),
            f => f.GetProperty("code").GetString() == "validation.page.region_missing");

        // Deprecated: the published page still renders, its owner is told, and no new page can use the template.
        await PostAsync(t, $"{Templates}/{id}/deprecate");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.TemplateDeprecated, id);
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.GetAsync($"/api/v1/experience/render?slug={slug}")).StatusCode);
        Assert.True((await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{pageId}")).GetProperty("templateDeprecated").GetBoolean());
        Assert.Equal("validation.page.template_unknown", await ErrorAsync(await CreatePageAsync(t, id), "templateId"));
        Assert.DoesNotContain(id, await ChooserAsync(t, "admin"));
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync($"{Templates}/{id}/deprecate", new { })).StatusCode);
    }

    [Fact]
    public async Task Template_input_is_validated_and_variants_and_locks_stay_within_the_base_template()
    {
        var t = await world.CreateTenantAsync("ti-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var bad = await t["admin"].Client.PostAsJsonAsync(Templates, new { id = "X", basedOn = "T99", names = new Dictionary<string, string>() });
        var problem = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");
        Assert.Equal("validation.template.id_invalid", problem.GetProperty("id")[0].GetProperty("key").GetString());
        Assert.Equal("validation.page.template_unknown", problem.GetProperty("basedOn")[0].GetProperty("key").GetString());
        Assert.Equal("validation.template.names_invalid", problem.GetProperty("names")[0].GetProperty("key").GetString());

        var id = NewId();
        var template = await CreateTemplateAsync(t, id, "T01");
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            id, basedOn = "T01", names = new Dictionary<string, string> { ["en"] = "Again" },
        })).StatusCode);

        var regions = template["latest"]!["regions"]!.DeepClone().AsArray();
        regions[0]!["policy"]!["protection"] = "sealed";
        var draft = await t["admin"].Client.PutAsJsonAsync($"{Templates}/{id}/draft", new
        {
            names = new Dictionary<string, string> { ["en"] = "Ok" },
            variants = new[] { "neon" },
            regions,
        });
        var errors = JsonDocument.Parse(await draft.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");
        Assert.Equal("validation.template.variants_invalid", errors.GetProperty("variants")[0].GetProperty("key").GetString());
        Assert.True(errors.EnumerateObject().Any(e => e.Name.StartsWith("regions.", StringComparison.Ordinal)), errors.ToString());

        var missing = await t["admin"].Client.PutAsJsonAsync($"{Templates}/{id}/draft", new
        {
            names = new Dictionary<string, string> { ["en"] = "Ok" }, variants = new[] { "balanced" }, regions = Array.Empty<object>(),
        });
        Assert.Equal("validation.template.regions_mismatch", await ErrorAsync(missing, "regions"));
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync($"{Templates}/{id}/deprecate", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["admin"].Client.PostAsJsonAsync($"{Templates}/no-such-one/release", new { })).StatusCode);
    }

    [Fact]
    public async Task Custom_templates_stop_at_the_plan_limit_and_another_tenant_never_finds_them()
    {
        // Workplace allows five custom templates (spec 032 section 9).
        var t = await world.CreateTenantAsync("tl-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var id = NewId();
            await CreateTemplateAsync(t, id, "T01");
            ids.Add(id);
        }
        var sixth = await t["admin"].Client.PostAsJsonAsync(Templates, new
        {
            id = NewId(), basedOn = "T01", names = new Dictionary<string, string> { ["en"] = "Sixth" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, sixth.StatusCode);
        var problem = await sixth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement.quota_exhausted", problem.GetProperty("code").GetString());
        Assert.Equal("experience.max_custom_templates", problem.GetProperty("capability").GetString());
        Assert.Equal(5, (await t["admin"].Client.GetFromJsonAsync<JsonElement>(Templates)).GetArrayLength());

        await PostAsync(t, $"{Templates}/{ids[0]}/release");
        var other = world.B;
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Templates}/{ids[0]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsJsonAsync($"{Templates}/{ids[0]}/release", new { })).StatusCode);
        Assert.Equal("validation.page.template_unknown", await ErrorAsync(await CreatePageAsync(other, ids[0]), "templateId"));
        Assert.DoesNotContain(ids[0], await ChooserAsync(other, "admin"));
    }
}
