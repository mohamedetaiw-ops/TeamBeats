using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T022 (US8, FR-013 to FR-015): governance reports and portable tokens. A brand's tokens export as a DTCG
/// 2025.10 file and import back into its draft with aliases and modes intact; the drift report lists missing and
/// out-of-policy assets, overrides of removed tokens, overrides a parent lock overrules and inaccessible values, each with
/// its scope and token; a reviewed token extension applies only in its scope once someone else approves it, never as free
/// CSS. Other tenants, employees and smaller packages reach none of it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ThemeGovernanceTests(TenantWorld world)
{
    private const string Brands = "/api/v1/experience/brands";
    private const string Extensions = "/api/v1/experience/token-extensions";

    private static object Token(string value, string? dark = null) => dark is null
        ? new Dictionary<string, object> { ["$value"] = value }
        : new Dictionary<string, object> { ["$value"] = value, ["$modes"] = new Dictionary<string, string> { ["dark"] = dark } };

    private async Task<TestTenant> FreshAsync(string package)
    {
        var t = await world.CreateTenantAsync("thg-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Plant", gulf, "department");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "manager", MembershipTypes.Employee);
        await world.AssignAsync(t, "manager", "brand_manager", "tenant", null);
        await world.SeedUserAsync(t, "gulfpub", MembershipTypes.Employee);
        await world.AssignAsync(t, "gulfpub", "brand_publisher", "org_subtree", gulf);
        return t;
    }

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
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static async Task<Guid> BrandAsync(TestTenant t, object tokens)
    {
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync(Brands, new { name = "Campaign", tokens }), HttpStatusCode.Created);
        return created.GetProperty("id").GetGuid();
    }

    private static Task<string> CssAsync(TestTenant t, string scope, string mode = "light") =>
        t["employee"].Client.GetStringAsync($"/api/v1/experience/theme.css?scope={scope}&mode={mode}&density=comfortable");

    [Fact]
    public async Task A_brand_exports_as_a_dtcg_file_and_imports_back_into_its_draft_with_aliases_and_modes()
    {
        var t = await FreshAsync("workplace_plus");
        var brand = await BrandAsync(t, new Dictionary<string, object>
        {
            ["color.accent.base"] = Token("#0f766e", "#5eead4"),
            ["radius.lg"] = Token("{radius.md}"),
            ["motion.duration.normal"] = Token("250ms"),
        });

        var exported = await t["admin"].Client.GetAsync($"{Brands}/{brand}/tokens");
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Equal("application/design-tokens+json", exported.Content.Headers.ContentType!.MediaType);
        Assert.Contains(".tokens.json", exported.Content.Headers.ContentDisposition!.ToString());
        var file = JsonNode.Parse(await exported.Content.ReadAsStringAsync())!;
        Assert.Equal("#0f766e", file["color"]!["accent"]!["base"]!["$value"]!["hex"]!.GetValue<string>());
        Assert.Equal("{radius.md}", file["radius"]!["lg"]!["$value"]!.GetValue<string>());

        // Another tool changes the accent, and the file comes back into the draft only.
        file["color"]!["accent"]!["base"]!["$value"] = new JsonObject { ["colorSpace"] = "srgb", ["components"] = new JsonArray(0, 0.4, 0.8) };
        var draft = await OkAsync(t["manager"].Client.PutAsync($"{Brands}/{brand}/tokens", new StringContent(file.ToJsonString(), Encoding.UTF8, "application/json")));
        Assert.True(draft.GetProperty("hasDraft").GetBoolean());
        var tokens = draft.GetProperty("tokens");
        Assert.Equal("#0066cc", tokens.GetProperty("color.accent.base").GetProperty("$value").GetString());
        Assert.Equal("#5eead4", tokens.GetProperty("color.accent.base").GetProperty("$modes").GetProperty("dark").GetString());
        Assert.Equal("{radius.md}", tokens.GetProperty("radius.lg").GetProperty("$value").GetString());
        Assert.Equal("250ms", tokens.GetProperty("motion.duration.normal").GetProperty("$value").GetString());
        var live = await OkAsync(t["admin"].Client.GetAsync($"{Brands}/{brand}"));
        Assert.Equal("#0f766e", live.GetProperty("tokens").GetProperty("color.accent.base").GetProperty("$value").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeTokensImported, brand.ToString());

        // Values outside the token types, unknown tokens and an unapproved font are refused, and the draft keeps what it had.
        var css = new JsonObject { ["layout"] = new JsonObject { ["css"] = new JsonObject { ["$value"] = "body{display:none}" } } };
        Assert.Equal("validation.theme.token_unknown",
            await KeyAsync(await t["admin"].Client.PutAsync($"{Brands}/{brand}/tokens", new StringContent(css.ToJsonString(), Encoding.UTF8, "application/json"))));
        var font = new JsonObject { ["font"] = new JsonObject { ["family"] = new JsonObject { ["latin"] = new JsonObject { ["$type"] = "fontFamily", ["$value"] = "Comic Sans MS" } } } };
        Assert.Equal("validation.theme.font_not_approved",
            await KeyAsync(await t["admin"].Client.PutAsync($"{Brands}/{brand}/tokens", new StringContent(font.ToJsonString(), Encoding.UTF8, "application/json"))));
        Assert.Equal("validation.theme.dtcg_invalid",
            await KeyAsync(await t["admin"].Client.PutAsync($"{Brands}/{brand}/tokens", new StringContent("[]", Encoding.UTF8, "application/json"))));

        // Employees, and other tenants, reach neither.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync($"{Brands}/{brand}/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.GetAsync($"{Brands}/{brand}/tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await world.B["admin"].Client.PutAsync($"{Brands}/{brand}/tokens", new StringContent(file.ToJsonString(), Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task The_drift_report_lists_assets_overrides_and_inaccessible_values_with_scope_and_token()
    {
        var t = await FreshAsync("workplace_plus");
        var (gulf, plant, holding) = (t.Unit("Gulf"), t.Unit("Plant"), t.Unit("Holding"));
        var empty = await OkAsync(t["admin"].Client.GetAsync("/api/v1/experience/drift"));
        Assert.DoesNotContain(empty.GetProperty("findings").EnumerateArray(), f => f.GetProperty("kind").GetString() is "token_unknown" or "override_locked");

        // A brand whose logo asset is later deleted.
        var upload = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52]);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var asset = await OkAsync(t["admin"].Client.PostAsync("/api/v1/experience/assets", upload), HttpStatusCode.Created);
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync(Brands,
            new { name = "Logo", tokens = new Dictionary<string, object>(), assets = new Dictionary<string, string> { ["logo"] = asset.GetProperty("reference").GetString()! } }),
            HttpStatusCode.Created);
        var brand = created.GetProperty("id").GetGuid();
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            "with gone as (delete from experience.brand_asset where id = @id returning 1) select count(*) from gone", ("id", Guid.Parse(asset.GetProperty("reference").GetString()!["asset:".Length..]))));

        // Gulf overrides a value Holding then locks, and an override names a token the schema no longer has.
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{gulf}",
            new { brandId = (Guid?)null, overrides = new Dictionary<string, object> { ["color.accent.base"] = Token("#0f766e") } }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{holding}", new { settings = new[] { "color.accent.base" } }));
        await OkAsync(t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{plant}",
            new { brandId = (Guid?)null, overrides = new Dictionary<string, object> { ["radius.md"] = Token("10px") } }));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            """
            with changed as (
              update experience.brand_assignment set overrides = overrides || '{"color.legacy.tint": {"$value": "#123456"}, "color.text.secondary": {"$value": "#f4f4f4"}}'::jsonb
              where scope_org_unit_id = @plant returning 1)
            select count(*) from changed
            """, ("plant", plant)));

        var report = await OkAsync(t["admin"].Client.GetAsync("/api/v1/experience/drift"));
        var findings = report.GetProperty("findings").EnumerateArray().ToList();
        bool Has(string kind, string? scope, string? token = null, Guid? brandId = null) => findings.Any(f =>
            f.GetProperty("kind").GetString() == kind
            && (scope is null || f.GetProperty("scope").GetString() == scope)
            && (token is null || f.GetProperty("token").GetString() == token)
            && (brandId is null || f.GetProperty("brandId").GetGuid() == brandId));
        Assert.True(Has("asset_missing", null, brandId: brand), report.ToString());
        Assert.Contains(findings, f => f.GetProperty("kind").GetString() == "asset_missing" && f.GetProperty("slot").GetString() == "logo");
        Assert.True(Has("override_locked", gulf.ToString(), "color.accent.base"), report.ToString());
        Assert.True(Has("token_unknown", plant.ToString(), "color.legacy.tint"), report.ToString());
        // A near white secondary text on the light surface fails contrast where it applies.
        var inaccessible = findings.First(f => f.GetProperty("kind").GetString() == "inaccessible" && f.GetProperty("scope").GetString() == plant.ToString()
            && f.GetProperty("token").GetString() == "color.text.secondary");
        Assert.Equal("light", inaccessible.GetProperty("mode").GetString());
        Assert.True(inaccessible.GetProperty("ratio").GetDouble() < inaccessible.GetProperty("required").GetDouble());
        Assert.Equal("Plant", inaccessible.GetProperty("scopeNames").GetProperty("en").GetString());

        // An organization's brand publisher sees their organizations only, and no tenant brands.
        var gulfReport = await OkAsync(t["gulfpub"].Client.GetAsync("/api/v1/experience/drift"));
        var scopes = gulfReport.GetProperty("findings").EnumerateArray().Select(f => f.GetProperty("scope").GetString()).Distinct().ToList();
        Assert.All(scopes, s => Assert.Contains(s, new[] { gulf.ToString(), plant.ToString() }));
        Assert.DoesNotContain(gulfReport.GetProperty("findings").EnumerateArray(), f => f.GetProperty("kind").GetString() == "asset_missing");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.GetAsync("/api/v1/experience/drift")).StatusCode);
        // Another tenant's report holds only its own scopes.
        var other = await OkAsync(world.B["admin"].Client.GetAsync("/api/v1/experience/drift"));
        Assert.DoesNotContain(other.GetProperty("findings").EnumerateArray(), f => f.GetProperty("scope").GetString() == plant.ToString());
    }

    [Fact]
    public async Task A_token_extension_applies_in_its_scope_only_after_someone_else_approves_it()
    {
        var t = await FreshAsync("enterprise_gov");
        var (gulf, plant, africa) = (t.Unit("Gulf"), t.Unit("Plant"), t.Unit("Africa"));
        var proposed = await OkAsync(t["gulfpub"].Client.PostAsJsonAsync(Extensions,
            new { scope = gulf.ToString(), key = "campaign_accent", type = "color", value = Token("#be185d", "#f9a8d4"), description = "Ramadan campaign" }),
            HttpStatusCode.Created);
        var id = proposed.GetProperty("id").GetGuid();
        Assert.Equal("proposed", proposed.GetProperty("state").GetString());
        Assert.Equal("--tb-ext-campaign-accent", proposed.GetProperty("cssName").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeExtensionProposed, id.ToString());
        Assert.DoesNotContain("--tb-ext-campaign-accent", await CssAsync(t, gulf.ToString()));

        // The person who proposed it cannot approve it.
        var own = await t["gulfpub"].Client.PostAsJsonAsync($"{Extensions}/{id}/review", new { approve = true });
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.Equal("authz.denied.extension_own_proposal", await KeyAsync(own));
        // Nor can someone who does not publish themes in Gulf, who is not even told the extension is there.
        Assert.Equal(HttpStatusCode.NotFound, (await t["manager"].Client.PostAsJsonAsync($"{Extensions}/{id}/review", new { approve = true })).StatusCode);

        var approved = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Extensions}/{id}/review", new { approve = true, note = "Fine for March" }));
        Assert.Equal("approved", approved.GetProperty("state").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeExtensionApproved, id.ToString());

        // Gulf and the plant below get it, in each mode; Africa and the tenant do not.
        Assert.Contains("--tb-ext-campaign-accent:#be185d;", await CssAsync(t, gulf.ToString()));
        Assert.Contains("--tb-ext-campaign-accent:#f9a8d4;", await CssAsync(t, plant.ToString(), "dark"));
        Assert.DoesNotContain("--tb-ext-campaign-accent", await CssAsync(t, africa.ToString()));
        Assert.DoesNotContain("--tb-ext-campaign-accent", await CssAsync(t, "tenant"));
        var plantList = await OkAsync(t["gulfpub"].Client.GetAsync($"{Extensions}?scope={plant}"));
        Assert.True(plantList.GetProperty("available").GetBoolean());
        Assert.True(Assert.Single(plantList.GetProperty("items").EnumerateArray()).GetProperty("inherited").GetBoolean());
        // An approved extension is not edited in place.
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["gulfpub"].Client.PutAsJsonAsync($"{Extensions}/{id}", new { type = "color", value = Token("#000000") })).StatusCode);

        // An alias takes the platform value of the scope; free CSS, platform names and other types are refused.
        var alias = await OkAsync(t["gulfpub"].Client.PostAsJsonAsync(Extensions,
            new { scope = gulf.ToString(), key = "hero_radius", type = "dimension", value = Token("{radius.lg}") }), HttpStatusCode.Created);
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Extensions}/{alias.GetProperty("id").GetGuid()}/review", new { approve = true }));
        var gulfCss = await CssAsync(t, gulf.ToString());
        var radius = System.Text.RegularExpressions.Regex.Match(gulfCss, "--tb-radius-lg:([^;]+);").Groups[1].Value;
        Assert.Contains($"--tb-ext-hero-radius:{radius};", gulfCss);
        // A rejected proposal is not served; changing it sends it back for review.
        var spare = await OkAsync(t["gulfpub"].Client.PostAsJsonAsync(Extensions,
            new { scope = gulf.ToString(), key = "spare_accent", type = "color", value = Token("#0000ff") }), HttpStatusCode.Created);
        var spareId = spare.GetProperty("id").GetGuid();
        var rejected = await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Extensions}/{spareId}/review", new { approve = false, note = "Off brand" }));
        Assert.Equal("rejected", rejected.GetProperty("state").GetString());
        Assert.Equal("Off brand", rejected.GetProperty("reviewNote").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeExtensionRejected, spareId.ToString());
        Assert.DoesNotContain("--tb-ext-spare-accent", await CssAsync(t, gulf.ToString()));
        var resent = await OkAsync(t["gulfpub"].Client.PutAsJsonAsync($"{Extensions}/{spareId}", new { type = "color", value = Token("#1d4ed8") }));
        Assert.Equal("proposed", resent.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict,
            (await t["admin"].Client.PostAsJsonAsync($"{Extensions}/{id}/review", new { approve = true })).StatusCode);
        var tenantLevel = await OkAsync(t["admin"].Client.PostAsJsonAsync(Extensions, new { scope = "tenant", key = "banner_gap", type = "dimension", value = Token("24px") }),
            HttpStatusCode.Created);
        var tenantId = tenantLevel.GetProperty("id").GetGuid();
        Assert.Equal("validation.theme.color_invalid", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Extensions,
            new { scope = "tenant", key = "evil", type = "color", value = Token("red;}body{display:none") })));
        Assert.Equal("validation.theme.extension_key", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Extensions,
            new { scope = "tenant", key = "color.accent.base", type = "color", value = Token("#000000") })));
        Assert.Equal("validation.theme.extension_type", await KeyAsync(await t["admin"].Client.PostAsJsonAsync(Extensions,
            new { scope = "tenant", key = "font_hack", type = "font_family", value = Token("Inter") })));
        Assert.Equal(HttpStatusCode.Conflict, (await t["admin"].Client.PostAsJsonAsync(Extensions,
            new { scope = gulf.ToString(), key = "campaign_accent", type = "color", value = Token("#000000") })).StatusCode);
        // Gulf's publisher reaches neither the tenant nor a neighbour.
        Assert.Equal(HttpStatusCode.NotFound, (await t["gulfpub"].Client.PostAsJsonAsync(Extensions,
            new { scope = africa.ToString(), key = "accent", type = "color", value = Token("#000000") })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gulfpub"].Client.DeleteAsync($"{Extensions}/{tenantId}")).StatusCode);

        // Removing it stops it at once.
        Assert.Equal(HttpStatusCode.NoContent, (await t["gulfpub"].Client.DeleteAsync($"{Extensions}/{id}")).StatusCode);
        Assert.DoesNotContain("--tb-ext-campaign-accent", await CssAsync(t, gulf.ToString()));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemeExtensionRemoved, id.ToString());
    }

    [Fact]
    public async Task Token_extensions_need_the_package_and_stay_in_the_tenant()
    {
        var t = await FreshAsync("workplace_plus");
        var refused = await t["admin"].Client.PostAsJsonAsync(Extensions, new { scope = "tenant", key = "campaign_accent", type = "color", value = Token("#be185d") });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("experience.token_extensions", await refused.Content.ReadAsStringAsync());

        var e = await FreshAsync("enterprise_gov");
        var proposed = await OkAsync(e["admin"].Client.PostAsJsonAsync(Extensions,
            new { scope = "tenant", key = "campaign_accent", type = "color", value = Token("#be185d") }), HttpStatusCode.Created);
        var id = proposed.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.PostAsJsonAsync($"{Extensions}/{id}/review", new { approve = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.DeleteAsync($"{Extensions}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await world.B["admin"].Client.PostAsJsonAsync(Extensions,
            new { scope = e.Unit("Gulf").ToString(), key = "x1", type = "color", value = Token("#000000") })).StatusCode);
        Assert.DoesNotContain((await OkAsync(world.B["admin"].Client.GetAsync(Extensions))).GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id);
        Assert.False((await OkAsync(t["admin"].Client.GetAsync(Extensions))).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await e["employee"].Client.GetAsync(Extensions)).StatusCode);
    }
}
