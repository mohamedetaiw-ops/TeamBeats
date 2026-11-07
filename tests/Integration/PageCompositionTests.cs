using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 032 (R1 backend): pages from templates, the component picker, the validator, publish and rollback, rendering for
/// viewers, and tenant and scope isolation. Components come from the two reference packs of the development host.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PageCompositionTests(TenantWorld world)
{
    private static string Slug() => "p-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<JsonNode> CreateAsync(TestTenant t, string template, Guid? unit = null, string user = "admin", string? slug = null)
    {
        var response = await t[user].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = template, slug = slug ?? Slug(), orgUnitId = unit });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static JsonObject P(string id, string component, object? props = null, int? span = null)
    {
        var node = new JsonObject { ["id"] = id, ["componentRef"] = component };
        if (props is not null)
        {
            node["props"] = JsonSerializer.SerializeToNode(props);
        }
        if (span is not null)
        {
            node["span"] = span;
        }
        return node;
    }

    private static JsonNode WithPlacements(JsonNode page, string region, params JsonObject[] placements)
    {
        var draft = page["draft"]!.DeepClone();
        var target = draft["regions"]!.AsArray().First(r => r!["id"]!.GetValue<string>() == region)!;
        target["placements"] = new JsonArray(placements.Select(p => (JsonNode)p).ToArray());
        return draft;
    }

    private static async Task<HttpResponseMessage> SaveAsync(TestTenant t, JsonNode page, JsonNode draft, string user = "admin", string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{page["id"]}/draft")
        {
            Content = JsonContent.Create(new { definition = draft }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag ?? page["eTag"]!.GetValue<string>());
        return await t[user].Client.SendAsync(request);
    }

    private static async Task<JsonNode> SavedAsync(TestTenant t, JsonNode page, JsonNode draft, string user = "admin")
    {
        var response = await SaveAsync(t, page, draft, user);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static Task<HttpResponseMessage> PublishAsync(TestTenant t, JsonNode page, string user = "admin") =>
        t[user].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/publish", new { reason = "first version" });

    private static async Task<JsonElement> FindingsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("findings");
    }

    private static bool Has(JsonElement findings, string code, string? placement = null) => findings.EnumerateArray().Any(f =>
        f.GetProperty("code").GetString() == "validation.page." + code
        && (placement is null || (f.TryGetProperty("placement", out var p) && p.GetString() == placement)));

    private static readonly object Text = new { body = new { en = "Wear a helmet on the plant floor.", ar = "ارتد الخوذة في أرض المصنع." } };

    [Fact]
    public async Task Catalogue_has_ten_layouts_the_twelve_templates_and_the_home_template_and_every_template_makes_an_empty_page()
    {
        var t = world.A;
        var catalogue = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/catalogue");
        Assert.Equal(10, catalogue.GetProperty("layouts").GetArrayLength());
        // Tenant templates other tests release in this tenant follow the platform ones and are left out here.
        var templates = catalogue.GetProperty("templates").EnumerateArray().Where(x => !x.GetProperty("custom").GetBoolean()).ToList();
        Assert.Equal(13, templates.Count);
        Assert.Contains(templates, t => t.GetProperty("id").GetString() == "H01");
        foreach (var template in templates)
        {
            Assert.Equal(["spacious", "balanced", "compact"], template.GetProperty("variants").EnumerateArray().Select(v => v.GetString()));
            var page = await CreateAsync(t, template.GetProperty("id").GetString()!);
            Assert.Equal(template.GetProperty("id").GetString(), page["templateId"]!.GetValue<string>());
            Assert.Equal(template.GetProperty("layout").GetString(), page["draft"]!["layout"]!["id"]!.GetValue<string>());
            Assert.All(page["draft"]!["regions"]!.AsArray(), r => Assert.Empty(r!["placements"]!.AsArray()));
            Assert.DoesNotContain(page["findings"]!.AsArray(), f => f!["severity"]!.GetValue<string>() == "blocking");
        }
    }

    [Fact]
    public async Task Catalogue_names_layouts_and_templates_in_the_editor_locale()
    {
        var t = await world.CreateTenantAsync("pn-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var english = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/catalogue");
        var canvas = english.GetProperty("templates").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "T01");
        Assert.Equal("Canvas", canvas.GetProperty("name").GetString());
        Assert.Equal("One region you fill as you like.", canvas.GetProperty("description").GetString());
        Assert.All(english.GetProperty("layouts").EnumerateArray(), l => Assert.False(string.IsNullOrWhiteSpace(l.GetProperty("name").GetString())));
        Assert.All(english.GetProperty("templates").EnumerateArray(), x => Assert.False(string.IsNullOrWhiteSpace(x.GetProperty("description").GetString())));

        (await t["admin"].Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();
        var arabic = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/catalogue");
        Assert.Equal("لوحة فارغة", arabic.GetProperty("templates").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "T01").GetProperty("name").GetString());
        Assert.Equal("عمود واحد", arabic.GetProperty("layouts").EnumerateArray().Single(x => x.GetProperty("id").GetString() == "L01").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Page_list_shows_only_pages_the_editor_may_edit_in_their_own_tenant()
    {
        var t = world.A;
        await world.SeedUserAsync(t, "list_editor", "employee");
        await world.AssignAsync(t, "list_editor", "page_publisher", "org_subtree", t.Unit("Gulf"));
        var gulf = await CreateAsync(t, "T01", t.Unit("Gulf"), user: "list_editor");
        var africa = await CreateAsync(t, "T02", t.Unit("Africa"));
        var other = await CreateAsync(world.B, "T01");

        static async Task<HashSet<string>> Ids(HttpClient client)
        {
            var list = await client.GetFromJsonAsync<JsonElement>("/api/v1/experience/pages");
            return list.EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToHashSet();
        }

        var admin = await Ids(t["admin"].Client);
        Assert.Contains(gulf["id"]!.GetValue<string>(), admin);
        Assert.Contains(africa["id"]!.GetValue<string>(), admin);
        Assert.DoesNotContain(other["id"]!.GetValue<string>(), admin);

        var editor = await Ids(t["list_editor"].Client);
        Assert.Contains(gulf["id"]!.GetValue<string>(), editor);
        Assert.DoesNotContain(africa["id"]!.GetValue<string>(), editor);

        var list = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/pages");
        var row = list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == africa["id"]!.GetValue<string>());
        Assert.Equal("T02", row.GetProperty("templateId").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("publishedAt").ValueKind);

        // Someone who may edit nothing gets an empty list, never another unit's pages.
        Assert.Empty(await Ids(t["employee_gulf"].Client));
    }

    [Fact]
    public async Task Preview_draws_the_saved_draft_for_its_editors_only_in_the_chosen_language()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T01");
        await SavedAsync(t, page, WithPlacements(page, "main", P("a", "reference.alpha.text@1", Text)));

        var preview = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}/preview?locale=ar");
        var placement = preview.GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "main")
            .GetProperty("placements").EnumerateArray().Single();
        Assert.Equal("ready", placement.GetProperty("state").GetString());
        Assert.Equal("ارتد الخوذة في أرض المصنع.", placement.GetProperty("props").GetProperty("body").GetProperty("ar").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("provenance").ValueKind);

        // The draft is not published: viewers and other tenants get nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee_gulf"].Client.GetAsync($"/api/v1/experience/pages/{page["id"]}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"/api/v1/experience/pages/{page["id"]}/preview")).StatusCode);
    }

    [Fact]
    public async Task Text_missing_in_one_enabled_language_is_a_translation_warning_that_does_not_block()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T01");
        var englishOnly = new { body = new { en = "Wear a helmet on the plant floor." } };
        var saved = await SavedAsync(t, page, WithPlacements(page, "main", P("a", "reference.alpha.text@1", englishOnly)));
        var warning = saved["findings"]!.AsArray().Single(f => f!["code"]!.GetValue<string>() == "validation.page.translation_missing");
        Assert.Equal("warning", warning!["severity"]!.GetValue<string>());
        Assert.Equal("a", warning["placement"]!.GetValue<string>());
        Assert.Equal("body", warning["property"]!.GetValue<string>());
        Assert.Equal("ar", warning["locale"]!.GetValue<string>());
        Assert.True((await PublishAsync(t, saved)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Page_is_created_from_configuration_with_provenance_and_audited()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T04");
        Assert.Equal("T04", page["draft"]!["template"]!["id"]!.GetValue<string>());
        Assert.Equal("L06", page["draft"]!["layout"]!["id"]!.GetValue<string>());
        Assert.Equal(["hero", "grid"], page["draft"]!["regions"]!.AsArray().Select(r => r!["id"]!.GetValue<string>()));
        Assert.Null(page["currentRelease"]);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageCreated, page["id"]!.GetValue<string>());

        var taken = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T04", slug = page["slug"]!.GetValue<string>() });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        var bad = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T99", slug = "Bad Slug" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Picker_lists_registered_components_and_explains_ineligible_ones()
    {
        var page = await CreateAsync(world.B, "T04");
        var items = await world.B["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}/components?region=hero");
        var byRef = items.EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);

        Assert.True(byRef["reference.alpha.text@1"].GetProperty("eligible").GetBoolean());
        Assert.True(byRef["reference.beta.banner@1"].GetProperty("eligible").GetBoolean());
        // The builder draws names and property forms from the picker answer, never from component code.
        var text = byRef["reference.alpha.text@1"];
        Assert.False(string.IsNullOrWhiteSpace(text.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Object, text.GetProperty("properties").ValueKind);
        Assert.Equal(JsonValueKind.Object, text.GetProperty("placement").ValueKind);
        Assert.Equal("Heading", text.GetProperty("labels").GetProperty("heading").GetString());
        Assert.Equal("Percent", byRef["reference.alpha.kpi@1"].GetProperty("labels").GetProperty("unit.percent").GetString());
        Assert.False(string.IsNullOrWhiteSpace(byRef["reference.alpha.links@1"].GetProperty("reason").GetString()));
        // The hero region allows content, media and data only.
        Assert.Equal("validation.page.category_not_allowed", byRef["reference.alpha.links@1"].GetProperty("reasonKey").GetString());
        // Workplace does not include Microsoft 365, which the counter declares.
        Assert.Equal("validation.page.component_not_entitled", byRef["reference.beta.counter@1"].GetProperty("reasonKey").GetString());
        // Retired and invalid manifests never reach the picker.
        Assert.False(byRef.ContainsKey("reference.beta.legacy@1"));
        Assert.False(byRef.ContainsKey("reference.beta.invalid@1"));
    }

    [Fact]
    public async Task Picker_refuses_components_whose_permission_the_editor_lacks()
    {
        var t = await world.CreateTenantAsync("pc-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "editor", "employee");
        await world.AssignAsync(t, "editor", "page_editor", "tenant", null);
        var page = await CreateAsync(t, "T01", user: "editor");
        var items = await t["editor"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}/components?region=main");
        var restricted = items.EnumerateArray().Single(i => i.GetProperty("componentRef").GetString() == "reference.beta.restricted@1");
        Assert.Equal("validation.page.component_not_permitted", restricted.GetProperty("reasonKey").GetString());

        var refused = await SaveAsync(t, page, WithPlacements(page, "main", P("tool", "reference.beta.restricted@1")), "editor");
        Assert.True(Has(await FindingsAsync(refused), "component_not_permitted", "tool"));
        // The tenant admin holds the permission and may place it.
        var adminPage = await CreateAsync(t, "T01");
        await SavedAsync(t, adminPage, WithPlacements(adminPage, "main", P("tool", "reference.beta.restricted@1")));
    }

    [Fact]
    public async Task Validator_blocks_bad_placements_and_names_each_one()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T04");
        var draft = WithPlacements(page, "hero",
            P("links", "reference.alpha.links@1"),
            P("kpi", "reference.alpha.kpi@1", new { label = new { en = "Days safe" } }, span: 9),
            P("ghost", "reference.unknown.thing@1"),
            P("script", "reference.alpha.text@1", new { body = new { en = "<script>alert(1)</script>" } }));
        var findings = await FindingsAsync(await SaveAsync(t, page, draft));
        Assert.True(Has(findings, "category_not_allowed", "links"));
        Assert.True(Has(findings, "span_invalid", "kpi"));
        Assert.True(Has(findings, "component_unknown", "ghost"));
        Assert.True(Has(findings, "executable_content", "script"));
        // Each finding carries its text in the editor's language.
        Assert.Contains(findings.EnumerateArray(), f => f.GetProperty("message").GetString() == "Remove markup or script from this field");

        var twice = WithPlacements(page, "grid", P("f1", "reference.alpha.filter@1"), P("f2", "reference.alpha.filter@1"), P("f3", "reference.alpha.text@1"));
        twice["regions"]!.AsArray().RemoveAt(0);
        findings = await FindingsAsync(await SaveAsync(t, page, twice));
        Assert.True(Has(findings, "singleton_duplicate", "f2"));
        Assert.True(Has(findings, "property_invalid", "f3"));
        Assert.True(Has(findings, "region_missing"));

        var unsafeUrl = WithPlacements(page, "hero", P("banner", "reference.beta.banner@1",
            new { image = "asset:" + Guid.NewGuid(), alt = new { en = "Plant" }, link = "javascript:alert(1)" }));
        Assert.True(Has(await FindingsAsync(await SaveAsync(t, page, unsafeUrl)), "property_invalid", "banner"));

        var rule = WithPlacements(page, "hero", P("x", "reference.alpha.text@1", Text));
        rule["regions"]![0]!["placements"]![0]!["visibilityRuleRef"] = "plant-operators";
        Assert.True(Has(await FindingsAsync(await SaveAsync(t, page, rule)), "visibility_rule_unknown", "x"));
    }

    [Fact]
    public async Task Stale_draft_save_is_a_conflict_that_keeps_both_versions()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T01");
        var first = await SavedAsync(t, page, WithPlacements(page, "main", P("a", "reference.alpha.text@1", Text)));
        Assert.NotEqual(page["eTag"]!.GetValue<string>(), first["eTag"]!.GetValue<string>());

        var stale = await SaveAsync(t, page, WithPlacements(page, "main", P("b", "reference.alpha.text@1", Text)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(first["eTag"]!.GetValue<string>(), body.GetProperty("etag").GetString());
        Assert.Equal("a", body.GetProperty("current").GetProperty("regions")[0].GetProperty("placements")[0].GetProperty("id").GetString());
        var missing = await SaveAsync(t, page, page["draft"]!, etag: "");
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
    }

    [Fact]
    public async Task Draft_is_never_visible_and_publish_makes_an_immutable_release()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T01");
        var slug = page["slug"]!.GetValue<string>();
        var saved = await SavedAsync(t, page, WithPlacements(page, "main", P("intro", "reference.alpha.text@1", Text)));
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee_gulf"].Client.GetAsync($"/api/v1/experience/render?slug={slug}")).StatusCode);

        var published = await PublishAsync(t, saved);
        Assert.Equal(1, (await published.ReadAsync<JsonElement>()).GetProperty("release").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PagePublished, page["id"]!.GetValue<string>());

        // A later draft does not change what employees see until it is published.
        var reloaded = JsonNode.Parse(await t["admin"].Client.GetStringAsync($"/api/v1/experience/pages/{page["id"]}"))!;
        await SavedAsync(t, reloaded, WithPlacements(reloaded, "main", P("other", "reference.alpha.links@1", new { title = "Links" })));
        var view = await t["employee_gulf"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}");
        Assert.Equal(1, view.GetProperty("release").GetInt32());
        Assert.Equal("intro", view.GetProperty("regions")[0].GetProperty("placements")[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, view.GetProperty("provenance").ValueKind);

        // The application role cannot change or delete a release.
        await using var conn = new NpgsqlConnection(world.Database.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select string_agg(privilege_type, ',') from information_schema.role_table_grants where table_schema = 'experience' and table_name = 'page_release' and grantee = 'tb_app'", conn);
        var privileges = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("UPDATE", privileges, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", privileges, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollback_restores_an_earlier_release_as_a_new_release()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T01");
        var slug = page["slug"]!.GetValue<string>();
        var v1 = await SavedAsync(t, page, WithPlacements(page, "main", P("first", "reference.alpha.text@1", Text)));
        await PublishAsync(t, v1);
        var v2 = await SavedAsync(t, v1, WithPlacements(v1, "main", P("second", "reference.alpha.text@1", Text)));
        await PublishAsync(t, v2);

        var rolled = await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/rollback", new { release = 1, reason = "broken banner" });
        var release = await rolled.ReadAsync<JsonElement>();
        Assert.Equal(3, release.GetProperty("release").GetInt32());
        Assert.Equal(1, release.GetProperty("rollbackOf").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageRolledBack, page["id"]!.GetValue<string>());

        var view = await t["employee_gulf"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}");
        Assert.Equal(3, view.GetProperty("release").GetInt32());
        Assert.Equal("first", view.GetProperty("regions")[0].GetProperty("placements")[0].GetProperty("id").GetString());
        var releases = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}/releases");
        Assert.Equal([3, 2, 1], releases.EnumerateArray().Select(r => r.GetProperty("release").GetInt32()));
    }

    [Fact]
    public async Task Publish_needs_required_placements()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T09");
        Assert.Contains(page["findings"]!.AsArray(), f => f!["code"]!.GetValue<string>() == "validation.page.too_few_placements"
            && f["severity"]!.GetValue<string>() == "warning");
        Assert.True(Has(await FindingsAsync(await PublishAsync(t, page)), "too_few_placements"));
    }

    [Fact]
    public async Task Delegated_editor_works_only_inside_their_scope_and_outside_is_not_found_and_audited()
    {
        var t = world.A;
        await world.AssignAsync(t, "editor_unit", "page_publisher", "org_subtree", t.Unit("Gulf"));
        var gulf = await CreateAsync(t, "T01", t.Unit("Gulf"), user: "editor_unit");
        var saved = await SavedAsync(t, gulf, WithPlacements(gulf, "main", P("a", "reference.alpha.text@1", Text)), "editor_unit");
        Assert.True((await PublishAsync(t, saved, "editor_unit")).IsSuccessStatusCode);

        var africa = await CreateAsync(t, "T01", t.Unit("Africa"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["editor_unit"].Client.GetAsync($"/api/v1/experience/pages/{africa["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublishAsync(t, africa, "editor_unit")).StatusCode);
        var denied = await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id and outcome = 'denied'",
            ("a", AuditActions.PagePublished), ("id", africa["id"]!.GetValue<string>()));
        Assert.Equal(1, denied);
        var create = await t["editor_unit"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T01", slug = Slug(), orgUnitId = t.Unit("Africa") });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        // An employee can view pages but not edit them.
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee_gulf"].Client.GetAsync($"/api/v1/experience/pages/{gulf["id"]}")).StatusCode);
    }

    private static async Task<HttpResponseMessage> ChangeTemplateAsync(TestTenant t, JsonNode page, object body, string user = "admin",
        string? etag = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/experience/pages/{page["id"]}/change-template") { Content = JsonContent.Create(body) };
        if (etag is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", etag);
        }
        return await t[user].Client.SendAsync(request);
    }

    private static string[] PlacementsIn(JsonNode draft, string region) =>
        draft["regions"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == region)!["placements"]!.AsArray()
            .Select(p => p!["id"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Layout_change_keeps_placements_in_compatible_regions_and_lists_the_rest_for_the_editor()
    {
        var t = world.A;
        var page = await CreateAsync(t, "T04");
        var kpi = new { label = new { en = "Visits", ar = "الزيارات" }, value = 5 };
        var draft = WithPlacements(page, "hero", P("h", "reference.alpha.text@1", Text));
        draft["regions"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == "grid")!["placements"] = new JsonArray(
            P("g", "reference.alpha.text@1", Text, span: 12), P("k", "reference.alpha.kpi@1", kpi), P("x", "reference.alpha.text@1", Text));
        var saved = await SavedAsync(t, page, draft);

        // A dry run works the change out and saves nothing. T05 has no full-width section, so the hero text has no matching region.
        var dry = await ChangeTemplateAsync(t, saved, new { templateId = "T05" });
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        var plan = JsonNode.Parse(await dry.Content.ReadAsStringAsync())!;
        var unplaced = plan["unplaced"]!.AsArray().Single()!;
        Assert.Equal("h", unplaced["placementId"]!.GetValue<string>());
        Assert.Equal("hero", unplaced["fromRegion"]!.GetValue<string>());
        Assert.Equal(["canvas"], unplaced["options"]!.AsArray().Select(o => o!.GetValue<string>()));
        Assert.Empty(PlacementsIn(plan["draft"]!, "sidebar"));
        Assert.Equal(["g", "k", "x"], PlacementsIn(plan["draft"]!, "canvas"));
        Assert.Equal("balanced", plan["draft"]!["variant"]!.GetValue<string>());
        Assert.Contains(plan["findings"]!.AsArray(), f => f!["code"]!.GetValue<string>() == "validation.page.placement_unplaced"
            && f["placement"]!.GetValue<string>() == "h" && f["severity"]!.GetValue<string>() == "blocking");
        Assert.Null(plan["page"]);
        var unchanged = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}");
        Assert.Equal("T04", unchanged.GetProperty("templateId").GetString());
        var etag = unchanged.GetProperty("eTag").GetString()!;

        // Applying needs the draft version, and refuses while a placement has no region.
        Assert.Equal(HttpStatusCode.Conflict, (await ChangeTemplateAsync(t, saved, new { templateId = "T05", apply = true })).StatusCode);
        Assert.True(Has(await FindingsAsync(await ChangeTemplateAsync(t, saved, new { templateId = "T05", apply = true }, etag: etag)),
            "placement_unplaced", "h"));

        // The editor places the hero text in the canvas and removes another; the change is saved and audited.
        var applied = await ChangeTemplateAsync(t, saved,
            new { templateId = "T05", variant = "compact", apply = true, assignments = new Dictionary<string, string> { ["h"] = "canvas", ["x"] = "" } },
            etag: etag);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var result = JsonNode.Parse(await applied.Content.ReadAsStringAsync())!;
        Assert.Empty(result["unplaced"]!.AsArray());
        // Placements keep their order on the page, so the hero text comes first.
        Assert.Equal(["h", "g", "k"], PlacementsIn(result["draft"]!, "canvas"));
        var after = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}");
        Assert.Equal("T05", after.GetProperty("templateId").GetString());
        Assert.Equal("L05", after.GetProperty("draft").GetProperty("layout").GetProperty("id").GetString());
        Assert.Equal("compact", after.GetProperty("draft").GetProperty("variant").GetString());
        Assert.NotEqual(etag, after.GetProperty("eTag").GetString());
        // A width wider than the new region falls back to the component default.
        var moved = after.GetProperty("draft").GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "canvas")
            .GetProperty("placements").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "g");
        Assert.True(!moved.TryGetProperty("span", out var span) || span.ValueKind == JsonValueKind.Null);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PageTemplateChanged, page["id"]!.GetValue<string>());
        Assert.True((await PublishAsync(t, JsonNode.Parse(after.GetRawText())!)).IsSuccessStatusCode);

        // The old version is stale now.
        Assert.Equal(HttpStatusCode.Conflict, (await ChangeTemplateAsync(t, saved, new { templateId = "T01", apply = true }, etag: etag)).StatusCode);
    }

    [Fact]
    public async Task Layout_change_refuses_unknown_templates_and_is_not_found_outside_scope_and_tenant()
    {
        var page = await CreateAsync(world.A, "T01");
        var etag = page["eTag"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeTemplateAsync(world.A, page, new { templateId = "T99" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeTemplateAsync(world.A, page, new { templateId = "T03", variant = "loud" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await ChangeTemplateAsync(world.A, page, new { templateId = "T03", apply = true }, user: "employee_gulf", etag: etag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.PostAsJsonAsync(
            $"/api/v1/experience/pages/{page["id"]}/change-template", new { templateId = "T03", apply = true })).StatusCode);
        var still = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page["id"]}");
        Assert.Equal("T01", still.GetProperty("templateId").GetString());
        Assert.Equal(etag, still.GetProperty("eTag").GetString());
    }

    [Fact]
    public async Task Pages_of_another_tenant_are_not_found()
    {
        var page = await CreateAsync(world.A, "T01");
        var saved = await SavedAsync(world.A, page, WithPlacements(page, "main", P("a", "reference.alpha.text@1", Text)));
        await PublishAsync(world.A, saved);
        var b = world.B["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/experience/pages/{page["id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/publish", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/v1/experience/render?slug={page["slug"]}")).StatusCode);
        // The same address can exist in each tenant.
        await CreateAsync(world.B, "T01", slug: page["slug"]!.GetValue<string>());
    }

    [Fact]
    public async Task Render_passes_only_declared_context_applies_defaults_and_shows_unavailable_components()
    {
        var t = await world.CreateTenantAsync("pr-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "employee", "employee");
        var page = await CreateAsync(t, "T01");
        var saved = await SavedAsync(t, page, WithPlacements(page, "main",
            P("intro", "reference.alpha.text@1", Text),
            P("figure", "reference.alpha.kpi@1", new { label = new { en = "Days safe" }, value = 120 }, span: 4),
            P("list", "reference.beta.list@1")));
        await PublishAsync(t, saved);
        var slug = page["slug"]!.GetValue<string>();

        var view = await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}");
        var placements = view.GetProperty("regions")[0].GetProperty("placements").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal(["direction", "locale"], placements["intro"].GetProperty("context").EnumerateObject().Select(c => c.Name).Order());
        Assert.Equal(page["id"]!.GetValue<string>(), placements["list"].GetProperty("context").GetProperty("pageId").GetString());
        Assert.False(placements["list"].GetProperty("context").TryGetProperty("tenantId", out _));
        Assert.Equal("count", placements["figure"].GetProperty("props").GetProperty("unit").GetString());
        Assert.Equal(10, placements["list"].GetProperty("props").GetProperty("pageSize").GetInt32());
        Assert.Equal(["filterChanged"], placements["list"].GetProperty("subscribes").EnumerateArray().Select(e => e.GetString()));

        // The admin sees where the configuration came from.
        var admin = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}");
        Assert.Equal("T01@1.0.0", admin.GetProperty("provenance").GetProperty("template").GetString());

        // Turning a component off: viewers get "unavailable" with no configuration, and editors cannot place it.
        var off = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.kpi/enablement", new { enabled = false });
        Assert.True(off.IsSuccessStatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComponentDisabled, "reference.alpha.kpi");
        view = await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}");
        var figure = view.GetProperty("regions")[0].GetProperty("placements").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "figure");
        Assert.Equal("unavailable", figure.GetProperty("state").GetString());
        Assert.Empty(figure.GetProperty("props").EnumerateObject());
        var reloaded = JsonNode.Parse(await t["admin"].Client.GetStringAsync($"/api/v1/experience/pages/{page["id"]}"))!;
        Assert.Contains(reloaded["findings"]!.AsArray(), f => f!["code"]!.GetValue<string>() == "validation.page.component_disabled");
        var on = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.kpi/enablement", new { enabled = true });
        Assert.True(on.IsSuccessStatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ComponentEnabled, "reference.alpha.kpi");

        // Enablement needs its own permission.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.kpi/enablement", new { enabled = false })).StatusCode);
    }
}
