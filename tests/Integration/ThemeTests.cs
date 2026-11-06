using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 (R1 backend): brand profiles, assignments and overrides per org unit, locks, the effective theme with provenance,
/// the compiled theme CSS, seeding from spec 001 settings, package limits, and tenant and scope isolation. Tests use their own
/// org units so they do not depend on each other.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ThemeTests(TenantWorld world)
{
    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private static object Token(string value, string? dark = null) =>
        dark is null ? new Dictionary<string, object> { ["$value"] = value }
            : new Dictionary<string, object> { ["$value"] = value, ["$modes"] = new Dictionary<string, string> { ["dark"] = dark } };

    private static async Task<JsonElement> CreateBrandAsync(TestTenant t, string name, Dictionary<string, object> tokens, string user = "admin")
    {
        var response = await t[user].Client.PostAsJsonAsync("/api/v1/experience/brands", new { name, tokens });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> AssignAsync(TestTenant t, string scope, Guid? brandId, Dictionary<string, object>? overrides = null,
        string user = "admin") =>
        t[user].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{scope}", new { brandId, overrides });

    private static async Task<JsonElement> ThemeAsync(TestTenant t, string scope, string user = "admin") =>
        await t[user].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/theme?scope={scope}");

    private static string Value(JsonElement theme, string token, string mode = "light", string density = "comfortable") =>
        theme.GetProperty("variants").EnumerateArray()
            .Single(v => v.GetProperty("mode").GetString() == mode && v.GetProperty("density").GetString() == density)
            .GetProperty("tokens").GetProperty(token).GetString()!;

    private static void AssertOk(HttpResponseMessage response, string body)
    {
        Assert.True(response.IsSuccessStatusCode, body);
    }

    [Fact]
    public async Task Two_sibling_units_render_their_own_brand_at_the_same_time()
    {
        var t = world.A;
        var gulf = await world.CreateUnitAsync(t, Code("BG"), t.Unit("Holding"), "subsidiary");
        var africa = await world.CreateUnitAsync(t, Code("BA"), t.Unit("Holding"), "subsidiary");
        var teal = await CreateBrandAsync(t, "Acme Gulf", new()
        {
            ["color.accent.base"] = Token("#0f766e", "#5eead4"),
            ["font.family.arabic"] = Token("IBM Plex Sans Arabic"),
        });
        var orange = await CreateBrandAsync(t, "Acme Africa", new() { ["color.accent.base"] = Token("#9a3412", "#fdba74") });
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ThemePublished, teal.GetProperty("id").GetString());

        var assigned = await AssignAsync(t, gulf.ToString(), teal.GetProperty("id").GetGuid());
        AssertOk(assigned, await assigned.Content.ReadAsStringAsync());
        assigned = await AssignAsync(t, africa.ToString(), orange.GetProperty("id").GetGuid());
        AssertOk(assigned, await assigned.Content.ReadAsStringAsync());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.BrandAssigned, africa.ToString());

        var gulfTheme = await ThemeAsync(t, gulf.ToString(), "employee_gulf");
        var africaTheme = await ThemeAsync(t, africa.ToString(), "employee_africa");
        Assert.Equal("#0f766e", Value(gulfTheme, "color.accent.base"));
        Assert.Equal("#5eead4", Value(gulfTheme, "color.accent.base", "dark"));
        Assert.Equal("IBM Plex Sans Arabic", Value(gulfTheme, "font.family.arabic"));
        Assert.Equal("#9a3412", Value(africaTheme, "color.accent.base"));
        Assert.NotEqual(Value(africaTheme, "font.family.arabic"), Value(gulfTheme, "font.family.arabic"));
        Assert.Equal(teal.GetProperty("id").GetGuid(), gulfTheme.GetProperty("brandId").GetGuid());

        // A unit below inherits its parent's brand; employees get no provenance, config_history.view holders do (FR-018).
        var below = await world.CreateUnitAsync(t, Code("BGD"), gulf, "department");
        Assert.Equal("#0f766e", Value(await ThemeAsync(t, below.ToString(), "employee_gulf"), "color.accent.base"));
        Assert.Equal(JsonValueKind.Null, gulfTheme.GetProperty("provenance").ValueKind);
        var traced = await ThemeAsync(t, below.ToString(), "security");
        var source = traced.GetProperty("provenance").GetProperty("color.accent.base");
        Assert.Equal("brand", source.GetProperty("layer").GetString());
        Assert.Equal(gulf, source.GetProperty("scopeOrgUnitId").GetGuid());

        // Switching the unit's brand changes its pages without code changes (US1 scenario 1).
        assigned = await AssignAsync(t, gulf.ToString(), orange.GetProperty("id").GetGuid());
        AssertOk(assigned, await assigned.Content.ReadAsStringAsync());
        Assert.Equal("#9a3412", Value(await ThemeAsync(t, gulf.ToString(), "employee_gulf"), "color.accent.base"));

        assigned = await AssignAsync(t, gulf.ToString(), null);
        AssertOk(assigned, await assigned.Content.ReadAsStringAsync());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.BrandUnassigned, gulf.ToString());
    }

    [Fact]
    public async Task Parent_locks_refuse_child_overrides_and_permanent_locks_cannot_be_removed()
    {
        var t = world.A;
        var parent = await world.CreateUnitAsync(t, Code("LP"), t.Unit("Holding"), "subsidiary");
        var child = await world.CreateUnitAsync(t, Code("LC"), parent, "department");

        var locked = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{parent}", new { settings = new[] { "color.accent.base", "legal_footer" } });
        AssertOk(locked, await locked.Content.ReadAsStringAsync());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ExperienceLockChanged, parent.ToString());

        // The scope that locked a value may still set it; its children may not, through the API (US1 scenario 5).
        var own = await AssignAsync(t, parent.ToString(), null, new() { ["color.accent.base"] = Token("#115e59") });
        AssertOk(own, await own.Content.ReadAsStringAsync());
        var refused = await AssignAsync(t, child.ToString(), null, new() { ["color.accent.base"] = Token("#9a3412"), ["radius.md"] = Token("2px") });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var errors = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("validation.theme.token_locked", errors.GetProperty("overrides.color.accent.base")[0].GetProperty("key").GetString());

        // Older data that overrides a value locked later is ignored by the resolver, and the trace says so.
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @ts, false);
            insert into experience.brand_assignment (tenant_id, scope_org_unit_id, overrides)
            values (@t, @u, '{"color.accent.base": {"$value": "#000000"}}'::jsonb);
            update experience.theme_state set version = version + 1 where tenant_id = @t;
            """,
            ("ts", t.Id.ToString()), ("t", t.Id), ("u", child));
        var theme = await ThemeAsync(t, child.ToString(), "security");
        Assert.Equal("#115e59", Value(theme, "color.accent.base"));
        Assert.True(theme.GetProperty("provenance").GetProperty("color.accent.base").GetProperty("lockIgnoredOverride").GetBoolean());

        var inherited = await t["admin"].Client.GetFromJsonAsync<JsonElement>($"/api/v1/experience/locks/{child}");
        var accentLock = inherited.EnumerateArray().Single(l => l.GetProperty("setting").GetString() == "color.accent.base");
        Assert.True(accentLock.GetProperty("inherited").GetBoolean());
        Assert.Contains(inherited.EnumerateArray(), l => l.GetProperty("setting").GetString() == "security_banner" && l.GetProperty("permanent").GetBoolean());

        // Clearing the child's locks leaves the parent's in place.
        var cleared = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{child}", new { settings = Array.Empty<string>() });
        AssertOk(cleared, await cleared.Content.ReadAsStringAsync());
        Assert.Contains((await cleared.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray(), l => l.GetProperty("setting").GetString() == "color.accent.base");

        var permanent = await t["admin"].Client.PutAsJsonAsync("/api/v1/experience/locks/tenant", new { settings = new[] { "legal_footer" } });
        Assert.Equal(HttpStatusCode.BadRequest, permanent.StatusCode);
        Assert.Contains("validation.theme.lock_required", await permanent.Content.ReadAsStringAsync());
        var unknown = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{child}", new { settings = new[] { "body.background-image" } });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Delegated_admins_cannot_reach_neighbouring_or_child_scopes_they_hold_no_grant_for()
    {
        var t = world.A;
        // orgadmin holds organization_administrator on the Gulf subtree (theme.edit, theme.publish).
        var mine = await AssignAsync(t, t.Unit("GulfHR").ToString(), null, new() { ["radius.md"] = Token("6px") }, "orgadmin");
        AssertOk(mine, await mine.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(t, t.Unit("Africa").ToString(), null, new() { ["radius.md"] = Token("6px") }, "orgadmin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["orgadmin"].Client.GetAsync($"/api/v1/experience/brand-assignments/{t.Unit("AfricaFinance")}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssignAsync(t, "tenant", Guid.NewGuid(), null, "orgadmin")).StatusCode);
        // No config_lock.manage for the organization administrator.
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["orgadmin"].Client.PutAsJsonAsync($"/api/v1/experience/locks/{t.Unit("GulfHR")}", new { settings = new[] { "navigation" } })).StatusCode);

        // A parent admin without subtree scope cannot edit a child's override (spec 033 section 7).
        var unitAdmin = await world.SeedUserAsync(t, Code("unitadmin"), MembershipTypes.Employee);
        await world.AssignAsync(t, unitAdmin.Name, "organization_administrator", "org_unit", t.Unit("Gulf"));
        Assert.Equal(HttpStatusCode.NotFound,
            (await AssignAsync(t, t.Unit("GulfFinance").ToString(), null, new() { ["radius.md"] = Token("6px") }, unitAdmin.Name)).StatusCode);

        // Employees cannot manage brands or assignments at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee_gulf"].Client.GetAsync("/api/v1/experience/brands")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(t, t.Unit("GulfFinance").ToString(), null, new() { ["radius.md"] = Token("6px") }, "employee_gulf")).StatusCode);
    }

    [Fact]
    public async Task Token_values_are_typed_and_contrast_failures_block_on_the_shell_and_warn_on_accents()
    {
        var t = world.A;
        var injected = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands", new
        {
            name = "Injected",
            tokens = new Dictionary<string, object>
            {
                ["color.accent.base"] = Token("url(https://evil.example/x)"),
                ["font.family.latin"] = Token("Comic Sans MS"),
                ["space.md"] = Token("expression(alert(1))"),
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        var errors = (await injected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.Equal("validation.theme.color_invalid", errors.GetProperty("tokens.color.accent.base")[0].GetProperty("key").GetString());
        Assert.Equal("validation.theme.font_not_approved", errors.GetProperty("tokens.font.family.latin")[0].GetProperty("key").GetString());
        Assert.True(errors.TryGetProperty("tokens.space.md", out _));

        var cycle = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands", new
        {
            name = "Cycle",
            tokens = new Dictionary<string, object> { ["color.link"] = Token("{color.accent.base}"), ["color.accent.base"] = Token("{color.link}") },
        });
        Assert.Equal(HttpStatusCode.BadRequest, cycle.StatusCode);
        Assert.Contains("validation.theme.alias_cycle", await cycle.Content.ReadAsStringAsync());

        var shell = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands", new
        {
            name = "Low contrast shell",
            tokens = new Dictionary<string, object> { ["color.shell.text"] = Token("#3b3f8f") },
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, shell.StatusCode);
        var finding = (await shell.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("findings").EnumerateArray()
            .First(f => f.GetProperty("token").GetString() == "color.shell.text");
        Assert.Equal("blocking", finding.GetProperty("severity").GetString());
        Assert.Equal("color.shell.background", finding.GetProperty("against").GetString());
        Assert.True(finding.GetProperty("ratio").GetDouble() < 4.5);
        Assert.Equal(4.5, finding.GetProperty("required").GetDouble());

        var anyBrand = (await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/brands"))[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound,
            (await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/brands/{Guid.NewGuid()}/check", new { name = "x" })).StatusCode);
        var check = await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/brands/{anyBrand}/check", new
        {
            name = "Pale accent",
            tokens = new Dictionary<string, object> { ["color.accent.base"] = Token("#fde68a", "#fde68a") },
        });
        var findings = (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("findings").EnumerateArray().ToList();
        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("warning", f.GetProperty("severity").GetString()));
    }

    [Fact]
    public async Task Brand_manager_edits_brands_but_publishing_a_brand_in_use_needs_theme_publish()
    {
        var t = world.A;
        var manager = await world.SeedUserAsync(t, Code("brandmgr"), MembershipTypes.Employee);
        await world.AssignAsync(t, manager.Name, "brand_manager", "tenant", null);

        var brand = await CreateBrandAsync(t, "Campaign", new() { ["radius.md"] = Token("12px") }, manager.Name);
        var id = brand.GetProperty("id").GetGuid();
        var updated = await t[manager.Name].Client.PutAsJsonAsync($"/api/v1/experience/brands/{id}",
            new { name = "Campaign", tokens = new Dictionary<string, object> { ["radius.md"] = Token("10px") } });
        AssertOk(updated, await updated.Content.ReadAsStringAsync());
        Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetInt32());

        var unit = await world.CreateUnitAsync(t, Code("BM"), t.Unit("Holding"), "subsidiary");
        Assert.Equal(HttpStatusCode.NotFound, (await AssignAsync(t, unit.ToString(), id, null, manager.Name)).StatusCode);
        AssertOk(await AssignAsync(t, unit.ToString(), id), "admin assigns");
        var inUse = await t[manager.Name].Client.PutAsJsonAsync($"/api/v1/experience/brands/{id}",
            new { name = "Campaign", tokens = new Dictionary<string, object> { ["radius.md"] = Token("8px") } });
        Assert.Equal(HttpStatusCode.Forbidden, inUse.StatusCode);

        // Versions are immutable: the application role can add versions but never change or remove one.
        Assert.False(await world.Database.OwnerScalarAsync<bool>("select has_table_privilege('tb_app', 'experience.brand_version', 'UPDATE')"));
        Assert.False(await world.Database.OwnerScalarAsync<bool>("select has_table_privilege('tb_app', 'experience.brand_version', 'DELETE')"));
        Assert.Equal("10px", Value(await ThemeAsync(t, unit.ToString()), "radius.md"));
    }

    [Fact]
    public async Task Theme_css_is_compiled_per_scope_mode_and_density_and_cached_by_hash()
    {
        var t = world.A;
        var theme = await ThemeAsync(t, "tenant", "employee_holding");
        Assert.Equal(4, theme.GetProperty("variants").GetArrayLength());
        var dark = theme.GetProperty("variants").EnumerateArray().Single(v => v.GetProperty("mode").GetString() == "dark"
            && v.GetProperty("density").GetString() == "compact");
        var response = await t["employee_holding"].Client.GetAsync(dark.GetProperty("cssUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.CacheControl.Private);
        var css = await response.Content.ReadAsStringAsync();
        Assert.Contains($"--tb-space-md:{Value(theme, "space.md", "dark", "compact")};", css);
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(css));
        }
        Assert.True(buffer.Length < 30 * 1024);

        var stale = await t["employee_holding"].Client.GetAsync("/api/v1/experience/theme.css?scope=tenant&mode=light&density=comfortable&v=old");
        Assert.Contains("max-age=60", stale.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee_holding"].Client.GetAsync("/api/v1/experience/theme.css?mode=sepia")).StatusCode);
    }

    [Fact]
    public async Task Another_tenants_theme_css_assignments_and_brands_are_not_found()
    {
        var a = world.A;
        var b = world.B;
        var unitOfA = a.Unit("Gulf");
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"/api/v1/experience/theme.css?scope={unitOfA}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"/api/v1/experience/theme?scope={unitOfA}")).StatusCode);
        var brandOfA = (await a["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/brands"))[0].GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync($"/api/v1/experience/brands/{brandOfA}")).StatusCode);
        var assign = await AssignAsync(b, "tenant", brandOfA);
        Assert.Equal(HttpStatusCode.BadRequest, assign.StatusCode);
        Assert.Contains("validation.theme.brand_unknown", await assign.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task First_use_seeds_the_default_brand_from_tenant_settings_and_package_limits_apply()
    {
        var t = await world.CreateTenantAsync(Code("seed"), "workplace");
        var settings = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/settings");
        var saved = await t["admin"].Client.PutAsJsonAsync("/api/v1/tenant/settings", new
        {
            defaultLocale = settings.GetProperty("defaultLocale").GetString(),
            enabledLocales = settings.GetProperty("enabledLocales"),
            timeZone = settings.GetProperty("timeZone").GetString(),
            primaryColor = "#0f766e",
            rowVersion = settings.GetProperty("rowVersion").GetInt32(),
        });
        AssertOk(saved, await saved.Content.ReadAsStringAsync());

        var theme = await ThemeAsync(t, "tenant");
        Assert.Equal("#0f766e", Value(theme, "color.accent.base"));
        Assert.NotEqual("#0f766e", Value(theme, "color.accent.base", "dark"));
        Assert.Equal(["accessibility_minimums", "legal_footer", "security_banner", "sign_in_presentation"],
            theme.GetProperty("locks").EnumerateArray().Select(l => l.GetString()));
        var brands = await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/brands");
        var seeded = Assert.Single(brands.EnumerateArray());
        Assert.True(seeded.GetProperty("seeded").GetBoolean());
        Assert.Equal(["tenant"], seeded.GetProperty("assignedScopes").EnumerateArray().Select(s => s.GetString()));

        // Workplace: one brand (experience.max_brands) and no organization overrides (experience.org_branding).
        var second = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands", new { name = "Second" });
        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("entitlement.quota_exhausted", problem.GetProperty("code").GetString());
        Assert.Equal("experience.max_brands", problem.GetProperty("capability").GetString());

        var unit = await world.CreateUnitAsync(t, "Seeded", null, "organization");
        var orgOverride = await AssignAsync(t, unit.ToString(), null, new() { ["radius.md"] = Token("6px") });
        Assert.Equal(HttpStatusCode.Forbidden, orgOverride.StatusCode);
        Assert.Equal("entitlement.not_in_package", (await orgOverride.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());

        // The tenant brand itself can still be edited in every package.
        var edit = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brands/{seeded.GetProperty("id").GetGuid()}",
            new { name = "Default", tokens = new Dictionary<string, object> { ["color.accent.base"] = Token("#115e59", "#5eead4") } });
        AssertOk(edit, await edit.Content.ReadAsStringAsync());
        Assert.Equal("#5eead4", Value(await ThemeAsync(t, "tenant"), "color.accent.base", "dark"));
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static Task<HttpResponseMessage> UploadAsync(TestTenant t, string user, byte[] data, string contentType)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return t[user].Client.PostAsync("/api/v1/experience/assets", content);
    }

    [Fact]
    public async Task Brand_assets_are_validated_stored_per_tenant_and_readable_by_members_only()
    {
        var a = world.A;
        var b = world.B;
        var uploaded = await UploadAsync(a, "admin", Png, "image/png");
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        var asset = await uploaded.Content.ReadFromJsonAsync<JsonElement>();
        var reference = asset.GetProperty("reference").GetString()!;
        var url = asset.GetProperty("url").GetString()!;
        Assert.StartsWith("asset:", reference);
        Assert.Equal("image/png", asset.GetProperty("contentType").GetString());

        // Members see the asset, because the shell shows it; the URL is useless in another tenant.
        var read = await a["employee_holding"].Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(Png, await read.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", read.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await b["admin"].Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a["admin"].Client.GetAsync($"/api/v1/experience/assets/{Guid.NewGuid()}")).StatusCode);

        var brand = await CreateBrandAsync(a, Code("Logo"), new(), "admin");
        var saved = await a["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brands/{brand.GetProperty("id").GetGuid()}",
            new { name = "Logo", tokens = new Dictionary<string, object>(), assets = new Dictionary<string, string> { ["logo"] = reference } });
        AssertOk(saved, await saved.Content.ReadAsStringAsync());
        var view = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(reference, view.GetProperty("assets").GetProperty("logo").GetString());
        Assert.Equal(url, view.GetProperty("assetUrls").GetProperty("logo").GetString());

        // Another tenant cannot point its brand at this asset, and unknown assets are refused too.
        var foreign = await b["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands",
            new { name = Code("Foreign"), tokens = new Dictionary<string, object>(), assets = new Dictionary<string, string> { ["logo"] = reference } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("validation.theme.asset_reference_invalid", await foreign.Content.ReadAsStringAsync());

        await world.Database.AssertAuditedAsync(a.Id, AuditActions.BrandAssetUploaded, reference["asset:".Length..]);
    }

    [Fact]
    public async Task Asset_upload_needs_theme_edit_and_refuses_unsafe_or_large_files()
    {
        var t = world.A;
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(t, "employee_holding", Png, "image/png")).StatusCode);

        var script = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
        var unsafeSvg = await UploadAsync(t, "admin", script, "image/svg+xml");
        Assert.Equal(HttpStatusCode.BadRequest, unsafeSvg.StatusCode);
        Assert.Contains("validation.asset.unsafe_svg", await unsafeSvg.Content.ReadAsStringAsync());

        var disguised = await UploadAsync(t, "admin", Encoding.UTF8.GetBytes("<svg/>"), "image/png");
        Assert.Contains("validation.asset.type", await disguised.Content.ReadAsStringAsync());

        var large = await UploadAsync(t, "admin", [.. Png, .. new byte[1024 * 1024]], "image/png");
        Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);
        Assert.Contains("validation.asset.size", await large.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Check_returns_the_preview_properties_of_both_modes()
    {
        var t = world.A;
        var brand = (await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/brands"))[0].GetProperty("id").GetGuid();
        var check = await t["admin"].Client.PostAsJsonAsync($"/api/v1/experience/brands/{brand}/check", new
        {
            name = "Preview",
            tokens = new Dictionary<string, object> { ["color.accent.base"] = Token("#0f766e", "#5eead4"), ["color.link"] = Token("{color.accent.base}") },
        });
        AssertOk(check, await check.Content.ReadAsStringAsync());
        var preview = (await check.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("preview");
        Assert.Equal("#0f766e", preview.GetProperty("light").GetProperty("--tb-color-accent-base").GetString());
        Assert.Equal("#5eead4", preview.GetProperty("dark").GetProperty("--tb-color-link").GetString());
        Assert.StartsWith("\"Inter\"", preview.GetProperty("light").GetProperty("--tb-font-family-latin").GetString());

        var schema = await t["employee_holding"].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/tokens");
        var tokens = schema.GetProperty("tokens").EnumerateArray().ToList();
        Assert.Equal(preview.GetProperty("light").EnumerateObject().Count(), tokens.Count);
        var accent = tokens.Single(x => x.GetProperty("name").GetString() == "color.accent.base");
        Assert.Equal("color", accent.GetProperty("group").GetString());
        Assert.Equal(["dark"], accent.GetProperty("modes").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains("logo", schema.GetProperty("assetSlots").EnumerateArray().Select(m => m.GetString()));
    }
}
