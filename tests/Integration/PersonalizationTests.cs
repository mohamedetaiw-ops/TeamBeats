using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 008 T007 to T010 (R1 backend): the preference profile, personal layouts with protection enforced by the API,
/// reset one and reset all, skipped operations, the personalization policy, audience rules with a default for people
/// who match none, the targeting preview, and the precedence of FR-031. Pages use template T07: a protected sidebar and
/// an optional canvas.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PersonalizationTests(TenantWorld world)
{
    private static string Slug() => "pz-" + Guid.NewGuid().ToString("N")[..10];

    private static readonly object Text = new { body = new { en = "Wear a helmet on the plant floor.", ar = "ارتد الخوذة في أرض المصنع." } };

    private static readonly object Kpi = new { label = new { en = "Days safe" }, value = 120 };

    private static object? PropsFor(string component) => component switch
    {
        "reference.alpha.text@1" => Text,
        "reference.alpha.kpi@1" => Kpi,
        _ => null,
    };

    private static JsonObject P(string id, string component = "reference.alpha.text@1", string? rule = null)
    {
        var node = new JsonObject { ["id"] = id, ["componentRef"] = component };
        if (PropsFor(component) is { } props)
        {
            node["props"] = JsonSerializer.SerializeToNode(props);
        }
        if (rule is not null)
        {
            node["visibilityRuleRef"] = rule;
        }
        return node;
    }

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pz-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "employee", "employee");
        await world.SeedUserAsync(t, "coworker", "employee");
        return t;
    }

    private static async Task<HttpResponseMessage> SaveDraftAsync(TestTenant t, JsonNode page, JsonObject[] sidebar, JsonObject[] canvas)
    {
        var draft = page["draft"]!.DeepClone();
        foreach (var region in draft["regions"]!.AsArray())
        {
            var items = region!["id"]!.GetValue<string>() == "sidebar" ? sidebar : canvas;
            region["placements"] = new JsonArray(items.Select(p => (JsonNode)p.DeepClone()).ToArray());
        }
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/experience/pages/{page["id"]}/draft") { Content = JsonContent.Create(new { definition = draft }) };
        request.Headers.TryAddWithoutValidation("If-Match", page["eTag"]!.GetValue<string>());
        return await t["admin"].Client.SendAsync(request);
    }

    /// <summary>A published T07 page: a notice in the protected sidebar, and intro and links in the canvas unless given.</summary>
    private static async Task<(Guid Id, string Slug)> PageAsync(TestTenant t, JsonObject[]? canvas = null, Guid? unit = null)
    {
        var slug = Slug();
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T07", slug, orgUnitId = unit });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var page = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var saved = await SaveDraftAsync(t, page, [P("notice")], canvas ?? [P("intro"), P("links", "reference.alpha.links@1")]);
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var published = await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/pages/{page["id"]}/publish", new { reason = "first" });
        Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        return (page["id"]!.GetValue<Guid>(), slug);
    }

    private static async Task DelegateAsync(TestTenant t, Guid page, object? canvas = null, string user = "admin")
    {
        var response = await t[user].Client.PutAsJsonAsync($"/api/v1/experience/pages/{page}/personalization-policy",
            new { regions = new[] { canvas ?? new { regionId = "canvas", rights = new[] { "add", "hide", "remove", "reorder" } } } });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> LayoutAsync(TestTenant t, Guid page, string user, params object[] operations)
    {
        var current = await t[user].Client.GetAsync($"/api/v1/me/pages/{page}/layout");
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/pages/{page}/layout") { Content = JsonContent.Create(new { operations }) };
        request.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        return await t[user].Client.SendAsync(request);
    }

    private static async Task<IReadOnlyList<string>> RefusedAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("refused").EnumerateArray().Select(r => r.GetProperty("reasonKey").GetString()!).ToList();
    }

    private static async Task<Dictionary<string, List<string>>> RenderAsync(TestTenant t, string slug, string user) =>
        (await t[user].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}"))
            .GetProperty("regions").EnumerateArray()
            .ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("placements").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToList());

    private static object AddOp(string id, string component = "reference.alpha.text@1", string region = "canvas", int? position = null) =>
        new { op = "add", placementId = id, componentRef = component, regionId = region, position, props = PropsFor(component) };

    // T008, US2

    [Fact]
    public async Task Personal_placements_go_only_into_delegated_regions_with_eligible_components_and_coworkers_are_unaffected()
    {
        var t = await FreshAsync();
        var (page, slug) = await PageAsync(t);
        // Not delegated yet: T07 has no personal region, so the default policy delegates nothing.
        var policy = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/pages/{page}/personalization-policy");
        Assert.All(policy.EnumerateArray(), r => Assert.Empty(r.GetProperty("rights").EnumerateArray()));
        Assert.Contains("validation.personalization.region_not_delegated", await RefusedAsync(await LayoutAsync(t, page, "employee", AddOp("my-note"))));

        await DelegateAsync(t, page);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PersonalizationPolicyChanged, page.ToString());
        var saved = await LayoutAsync(t, page, "employee", AddOp("my-note", position: 0));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var mine = await RenderAsync(t, slug, "employee");
        Assert.Equal(["my-note", "intro", "links"], mine["canvas"]);
        var personal = (await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}"))
            .GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "canvas");
        Assert.True(personal.GetProperty("placements")[0].GetProperty("personal").GetBoolean());
        Assert.Equal(["add", "hide", "remove", "reorder"], personal.GetProperty("rights").EnumerateArray().Select(r => r.GetString()));
        Assert.Equal(["intro", "links"], (await RenderAsync(t, slug, "coworker"))["canvas"]);

        // The protected sidebar, a component the user may not use, and a disabled component are all refused.
        Assert.Contains("validation.personalization.region_not_delegated", await RefusedAsync(await LayoutAsync(t, page, "employee", AddOp("my-side", region: "sidebar"))));
        Assert.Contains("validation.page.component_not_permitted", await RefusedAsync(await LayoutAsync(t, page, "employee", AddOp("my-secret", "reference.beta.restricted@1"))));
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.kpi/enablement", new { enabled = false })).EnsureSuccessStatusCode();
        Assert.Contains("validation.page.component_disabled", await RefusedAsync(await LayoutAsync(t, page, "employee", AddOp("my-kpi", "reference.alpha.kpi@1"))));
        Assert.Contains("validation.page.component_unknown", await RefusedAsync(await LayoutAsync(t, page, "employee", AddOp("my-old", "reference.beta.legacy@1"))));

        // The add panel gives the same answers with reasons.
        var panel = (await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/me/pages/{page}/add-panel?region=canvas"))
            .EnumerateArray().ToDictionary(i => i.GetProperty("componentRef").GetString()!);
        Assert.True(panel["reference.alpha.text@1"].GetProperty("eligible").GetBoolean());
        Assert.Equal("validation.page.component_not_permitted", panel["reference.beta.restricted@1"].GetProperty("reasonKey").GetString());
        Assert.Equal("validation.page.component_disabled", panel["reference.alpha.kpi@1"].GetProperty("reasonKey").GetString());
        Assert.False(panel.ContainsKey("reference.beta.legacy@1"));
        var sidebar = await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/me/pages/{page}/add-panel?region=sidebar");
        Assert.All(sidebar.EnumerateArray(), i => Assert.Equal("validation.personalization.region_not_delegated", i.GetProperty("reasonKey").GetString()));
    }

    [Fact]
    public async Task Protected_placements_cannot_be_hidden_removed_or_moved_through_the_api()
    {
        var t = await FreshAsync();
        var (page, slug) = await PageAsync(t);
        await DelegateAsync(t, page);
        Assert.Equal(["validation.personalization.protected"], await RefusedAsync(await LayoutAsync(t, page, "employee", new { op = "hide", placementId = "notice" })));
        Assert.Equal(["validation.personalization.protected"], await RefusedAsync(await LayoutAsync(t, page, "employee", new { op = "remove", placementId = "notice" })));
        Assert.Equal(["validation.personalization.protected"], await RefusedAsync(await LayoutAsync(t, page, "employee",
            new { op = "move", placementId = "notice", regionId = "canvas", position = 0 })));
        // Nor can the policy unlock the protected region.
        var locked = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/pages/{page}/personalization-policy",
            new { regions = new[] { new { regionId = "sidebar", rights = new[] { "hide" } } } });
        Assert.Contains("validation.personalization.region_locked", await locked.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(["notice"], (await RenderAsync(t, slug, "employee"))["sidebar"]);
        // The whole save is refused, so nothing of it is kept.
        Assert.Equal(0, (await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/me/pages/{page}/layout")).GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task Hide_move_and_region_maximum_follow_the_policy_and_survive_reload()
    {
        var t = await FreshAsync();
        var (page, slug) = await PageAsync(t);
        await DelegateAsync(t, page, new { regionId = "canvas", rights = new[] { "add", "hide", "remove", "reorder" }, maxPersonalPlacements = 2 });
        var saved = await LayoutAsync(t, page, "employee",
            new { op = "move", placementId = "links", regionId = "canvas", position = 0 },
            AddOp("my-a"), AddOp("my-b"),
            new { op = "remove", placementId = "my-a" },
            new { op = "hide", placementId = "intro" });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal(["links", "my-b"], (await RenderAsync(t, slug, "employee"))["canvas"]);
        Assert.Equal(1, (await saved.ReadAsync<JsonElement>()).GetProperty("revision").GetInt32());

        Assert.Equal(["validation.personalization.region_full"], await RefusedAsync(await LayoutAsync(t, page, "employee", AddOp("my-a"), AddOp("my-b"), AddOp("my-c"))));
        var badId = await LayoutAsync(t, page, "employee", AddOp("intro-2"));
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        Assert.Contains("validation.personalization.placement_id", await badId.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // A stale layout is refused instead of overwriting a newer one.
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/me/pages/{page}/layout") { Content = JsonContent.Create(new { operations = Array.Empty<object>() }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"l0\"");
        Assert.Equal(HttpStatusCode.Conflict, (await t["employee"].Client.SendAsync(stale)).StatusCode);
    }

    [Fact]
    public async Task Reset_one_and_reset_all_keep_the_preference_profile()
    {
        var t = await FreshAsync();
        var (first, firstSlug) = await PageAsync(t);
        var (second, secondSlug) = await PageAsync(t);
        await DelegateAsync(t, first);
        await DelegateAsync(t, second);
        (await t["employee"].Client.PutAsJsonAsync("/api/v1/me/preference-profile", new { values = new { density = "compact", mode = "dark" } })).EnsureSuccessStatusCode();
        Assert.True((await LayoutAsync(t, first, "employee", new { op = "hide", placementId = "links" })).IsSuccessStatusCode);
        Assert.True((await LayoutAsync(t, second, "employee", new { op = "hide", placementId = "links" })).IsSuccessStatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await t["employee"].Client.DeleteAsync($"/api/v1/me/pages/{first}/layout")).StatusCode);
        Assert.Equal(["intro", "links"], (await RenderAsync(t, firstSlug, "employee"))["canvas"]);
        Assert.Equal(["intro"], (await RenderAsync(t, secondSlug, "employee"))["canvas"]);

        Assert.Equal(HttpStatusCode.NoContent, (await t["employee"].Client.DeleteAsync("/api/v1/me/layouts")).StatusCode);
        Assert.Equal(["intro", "links"], (await RenderAsync(t, secondSlug, "employee"))["canvas"]);
        var profile = await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/preference-profile");
        Assert.Equal("compact", profile.GetProperty("values").GetProperty("density").GetString());
        Assert.Equal("dark", profile.GetProperty("values").GetProperty("mode").GetString());
        Assert.Equal("system", profile.GetProperty("values").GetProperty("motion").GetString());
    }

    [Fact]
    public async Task Operations_that_no_longer_apply_are_skipped_and_reported_and_return_with_the_policy()
    {
        var t = await FreshAsync();
        var (page, slug) = await PageAsync(t);
        await DelegateAsync(t, page);
        Assert.True((await LayoutAsync(t, page, "employee", new { op = "hide", placementId = "links" }, AddOp("my-note"))).IsSuccessStatusCode);
        Assert.Equal(["intro", "my-note"], (await RenderAsync(t, slug, "employee"))["canvas"]);

        // The admin turns the region off: the personal changes stop applying but are kept.
        await DelegateAsync(t, page, new { regionId = "canvas", rights = Array.Empty<string>() });
        Assert.Equal(["intro", "links"], (await RenderAsync(t, slug, "employee"))["canvas"]);
        var layout = await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/me/pages/{page}/layout");
        Assert.Equal(2, layout.GetProperty("operations").GetArrayLength());
        Assert.Equal(["hide", "add"], layout.GetProperty("skipped").EnumerateArray().Select(s => s.GetProperty("op").GetString()));
        var render = await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={slug}");
        Assert.Equal(2, render.GetProperty("skipped").GetArrayLength());

        await DelegateAsync(t, page);
        Assert.Equal(["intro", "my-note"], (await RenderAsync(t, slug, "employee"))["canvas"]);
    }

    [Fact]
    public async Task Layouts_are_per_user_and_per_tenant_and_need_a_viewable_published_page()
    {
        var t = await FreshAsync();
        var (page, _) = await PageAsync(t);
        await DelegateAsync(t, page);
        Assert.True((await LayoutAsync(t, page, "employee", new { op = "hide", placementId = "links" })).IsSuccessStatusCode);
        var coworker = await t["coworker"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/me/pages/{page}/layout");
        Assert.Equal(0, coworker.GetProperty("revision").GetInt32());
        Assert.Empty(coworker.GetProperty("operations").EnumerateArray());

        var other = world.B["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/me/pages/{page}/layout")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/v1/me/pages/{page}/layout", new { operations = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/v1/me/pages/{page}/layout")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/v1/experience/pages/{page}/personalization-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.GetAsync($"/api/v1/me/pages/{Guid.NewGuid()}/layout")).StatusCode);

        // A page that is not published has no personal layout.
        var draft = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T07", slug = Slug() });
        var draftId = (await draft.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.GetAsync($"/api/v1/me/pages/{draftId}/layout")).StatusCode);
    }

    [Fact]
    public async Task Preference_profile_accepts_only_schema_keys_and_values()
    {
        var t = await FreshAsync();
        var profile = await t["employee"].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/preference-profile");
        Assert.Equal(1, profile.GetProperty("schemaVersion").GetInt32());
        Assert.All(profile.GetProperty("schema").EnumerateArray(), s => Assert.Equal("cross_device", s.GetProperty("scope").GetString()));
        var unknown = await t["employee"].Client.PutAsJsonAsync("/api/v1/me/preference-profile", new { values = new { favouriteColour = "red" } });
        Assert.Contains("validation.preferences.unknown", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var bad = await t["employee"].Client.PutAsJsonAsync("/api/v1/me/preference-profile", new { values = new { density = "tiny" } });
        Assert.Contains("validation.preferences.value", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // Each user's profile is their own.
        (await t["employee"].Client.PutAsJsonAsync("/api/v1/me/preference-profile", new { values = new { text_scale = "150" } })).EnsureSuccessStatusCode();
        Assert.Equal("100", (await t["coworker"].Client.GetFromJsonAsync<JsonElement>("/api/v1/me/preference-profile")).GetProperty("values").GetProperty("text_scale").GetString());
    }

    [Fact]
    public async Task Policy_needs_the_package_for_pages_beyond_home_and_stays_in_the_admins_scope()
    {
        var workplace = await FreshAsync("workplace");
        var (page, _) = await PageAsync(workplace);
        var refused = await workplace["admin"].Client.PutAsJsonAsync($"/api/v1/experience/pages/{page}/personalization-policy",
            new { regions = new[] { new { regionId = "canvas", rights = new[] { "add" } } } });
        Assert.Contains("validation.personalization.not_entitled", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await workplace["admin"].Client.PutAsJsonAsync($"/api/v1/experience/audience-rules/{Guid.NewGuid()}",
                new { labels = new { en = "All" }, conditions = new { locales = new[] { "en" } } })).StatusCode);

        var a = world.A;
        var (africa, _) = await PageAsync(a, unit: a.Unit("Africa"));
        var (gulf, _) = await PageAsync(a, unit: a.Unit("Gulf"));
        await DelegateAsync(a, gulf, user: "orgadmin");
        Assert.Equal(HttpStatusCode.NotFound, (await a["orgadmin"].Client.GetAsync($"/api/v1/experience/pages/{africa}/personalization-policy")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a["employee_gulf"].Client.GetAsync($"/api/v1/experience/pages/{gulf}/personalization-policy")).StatusCode);
    }

    // T009, US3

    private async Task<Guid> RuleAsync(TestTenant t, object conditions)
    {
        var id = Guid.NewGuid();
        var response = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/audience-rules/{id}", new { labels = new { en = "Audience", ar = "جمهور" }, conditions });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AudienceRuleChanged, id.ToString());
        return id;
    }

    [Fact]
    public async Task People_who_match_no_audience_get_the_default_and_hiding_neither_grants_nor_removes_access()
    {
        var a = world.A;
        var gulf = await RuleAsync(a, new { orgUnits = new[] { a.Unit("Gulf") } });
        var (page, slug) = await PageAsync(a, [P("gulf-news", rule: gulf.ToString()), P("general")]);
        Assert.Equal(["gulf-news", "general"], (await RenderAsync(a, slug, "employee_gulf"))["canvas"]);
        Assert.Equal(["general"], (await RenderAsync(a, slug, "employee_africa"))["canvas"]);

        // A region with only targeted items has no default and cannot be saved.
        var created = await a["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T07", slug = Slug() });
        var draft = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var noDefault = await SaveDraftAsync(a, draft, [P("notice")], [P("only", rule: gulf.ToString())]);
        Assert.Contains("validation.page.audience_default_missing", await noDefault.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // Targeting is not access: the hidden viewer still reads the page, the targeted viewer still cannot edit it.
        Assert.Equal(HttpStatusCode.OK, (await a["employee_africa"].Client.GetAsync($"/api/v1/experience/render?slug={slug}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a["employee_gulf"].Client.GetAsync($"/api/v1/experience/pages/{page}")).StatusCode);

        // Audience rules need the package capability on placements too.
        var workplace = await FreshAsync("workplace");
        var w = await workplace["admin"].Client.PostAsJsonAsync("/api/v1/experience/pages", new { templateId = "T07", slug = Slug() });
        var wDraft = JsonNode.Parse(await w.Content.ReadAsStringAsync())!;
        var targeted = await SaveDraftAsync(workplace, wDraft, [P("notice")], [P("t", rule: gulf.ToString()), P("d")]);
        Assert.Contains("validation.page.visibility_rule_not_entitled", await targeted.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Precedence_locks_and_package_then_audience_then_admin_default_then_user_choice()
    {
        var a = world.A;
        var gulf = await RuleAsync(a, new { orgUnits = new[] { a.Unit("Gulf") } });
        var africa = await RuleAsync(a, new { orgUnits = new[] { a.Unit("Africa") }, membershipTypes = new[] { "employee" } });
        var (page, slug) = await PageAsync(a, [P("gulf-news", rule: gulf.ToString()), P("general"), P("links", "reference.alpha.links@1")]);
        await DelegateAsync(a, page);

        // User choice over the admin default: the user's order wins in a delegated region.
        Assert.True((await LayoutAsync(a, page, "employee_gulf", new { op = "move", placementId = "links", regionId = "canvas", position = 0 })).IsSuccessStatusCode);
        Assert.Equal(["links", "gulf-news", "general"], (await RenderAsync(a, slug, "employee_gulf"))["canvas"]);
        Assert.Equal(["general", "links"], (await RenderAsync(a, slug, "employee_africa"))["canvas"]);

        // Audience rule over user choice: a placement outside the user's audience cannot be reached by an operation.
        Assert.Equal(["validation.personalization.placement_unknown"],
            await RefusedAsync(await LayoutAsync(a, page, "employee_africa", new { op = "move", placementId = "gulf-news", regionId = "canvas", position = 0 })));

        // Parent lock over everything below it: the protected sidebar ignores the policy and the user.
        Assert.Equal(["validation.personalization.protected"], await RefusedAsync(await LayoutAsync(a, page, "employee_gulf", new { op = "hide", placementId = "notice" })));

        // Audience on the policy: only the audience may personalize.
        await DelegateAsync(a, page, new { regionId = "canvas", rights = new[] { "add", "hide", "remove", "reorder" }, audienceRuleId = africa });
        Assert.Equal(["general", "links"], (await RenderAsync(a, slug, "employee_gulf"))["canvas"].Where(p => p != "gulf-news").ToList());
        Assert.True((await LayoutAsync(a, page, "employee_africa", AddOp("my-note"))).IsSuccessStatusCode);
        Assert.Contains("validation.personalization.region_not_delegated", await RefusedAsync(await LayoutAsync(a, page, "employee_gulf", AddOp("my-note"))));

        // Legal and package constraints over the user and the admin: a disabled component shows as unavailable wherever it is.
        var t = await FreshAsync();
        var (own, ownSlug) = await PageAsync(t, [P("intro"), P("figure", "reference.alpha.kpi@1")]);
        await DelegateAsync(t, own);
        Assert.True((await LayoutAsync(t, own, "employee", AddOp("my-kpi", "reference.alpha.kpi@1"))).IsSuccessStatusCode);
        (await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/components/reference.alpha.kpi/enablement", new { enabled = false })).EnsureSuccessStatusCode();
        var render = await t["employee"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/render?slug={ownSlug}");
        var states = render.GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "canvas").GetProperty("placements")
            .EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!, p => p.GetProperty("state").GetString());
        Assert.Equal("unavailable", states["figure"]);
        Assert.Equal("unavailable", states["my-kpi"]);
        Assert.Equal("ready", states["intro"]);
    }

    [Fact]
    public async Task Targeting_preview_shows_who_matches_and_what_each_persona_sees()
    {
        var a = world.A;
        var gulf = await RuleAsync(a, new { orgUnits = new[] { a.Unit("Gulf") }, locales = new[] { "ar" } });
        var (page, _) = await PageAsync(a, [P("gulf-news", rule: gulf.ToString()), P("general")]);
        var preview = await a["admin"].Client.PostAsJsonAsync("/api/v1/experience/audience-rules/preview", new
        {
            pageId = page,
            personas = new object[]
            {
                new { name = "Gulf finance in Arabic", orgUnitId = a.Unit("GulfFinance"), locale = "ar" },
                new { name = "Gulf finance in English", orgUnitId = a.Unit("GulfFinance"), locale = "en" },
                new { name = "Africa", orgUnitId = a.Unit("Africa"), locale = "ar" },
            },
        });
        Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
        var personas = (await preview.ReadAsync<JsonElement>()).EnumerateArray().ToList();
        Assert.Contains(gulf, personas[0].GetProperty("matchedRules").EnumerateArray().Select(r => r.GetGuid()));
        Assert.DoesNotContain(gulf, personas[1].GetProperty("matchedRules").EnumerateArray().Select(r => r.GetGuid()));
        IEnumerable<string?> Canvas(JsonElement persona) => persona.GetProperty("regions").EnumerateArray()
            .Single(r => r.GetProperty("regionId").GetString() == "canvas").GetProperty("placements").EnumerateArray().Select(p => p.GetString());
        Assert.Equal(["gulf-news", "general"], Canvas(personas[0]));
        Assert.Equal(["general"], Canvas(personas[1]));
        Assert.Equal(["general"], Canvas(personas[2]));

        // Rules are validated, employees cannot preview, and another tenant cannot see the page.
        var bad = await a["admin"].Client.PutAsJsonAsync($"/api/v1/experience/audience-rules/{Guid.NewGuid()}", new { labels = new { en = "x" }, conditions = new { } });
        Assert.Contains("validation.audience.empty", await bad.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var badAttribute = await a["admin"].Client.PutAsJsonAsync($"/api/v1/experience/audience-rules/{Guid.NewGuid()}",
            new { labels = new { en = "x" }, conditions = new { attributes = new Dictionary<string, string[]> { ["mfa_performed"] = ["true"] } } });
        Assert.Contains("validation.audience.attribute", await badAttribute.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Forbidden, (await a["employee_gulf"].Client.PostAsJsonAsync("/api/v1/experience/audience-rules/preview",
            new { personas = new[] { new { name = "x" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await a["employee_gulf"].Client.PutAsJsonAsync($"/api/v1/experience/audience-rules/{Guid.NewGuid()}",
            new { labels = new { en = "x" }, conditions = new { locales = new[] { "en" } } })).StatusCode);
        var other = await FreshAsync();
        var b = await other["admin"].Client.PostAsJsonAsync("/api/v1/experience/audience-rules/preview", new { pageId = page, personas = new[] { new { name = "x" } } });
        Assert.Equal(HttpStatusCode.NotFound, b.StatusCode);
    }
}
