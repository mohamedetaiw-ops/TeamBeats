using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 033 R2 T018 (US3 scenario 3, FR-028): a brand gives its title, logo and lockup per language. The shell and the
/// installed app show the version of the viewer's language and fall back to the one for every language; titles are
/// checked, unknown language slots are refused, and nothing crosses to another tenant or organization.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class BrandLanguageTests(TenantWorld world)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52];

    private static string Code(string prefix) => prefix + Guid.NewGuid().ToString("N")[..6];

    private static async Task<(string Reference, string Url)> UploadAsync(TestTenant t)
    {
        var content = new ByteArrayContent(Png);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        var response = await t["admin"].Client.PostAsync("/api/v1/experience/assets", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var asset = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (asset.GetProperty("reference").GetString()!, asset.GetProperty("url").GetString()!);
    }

    private static Task<HttpResponseMessage> SaveAsync(TestTenant t, Guid brand, Dictionary<string, string> assets) =>
        t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brands/{brand}", new { name = "By language", tokens = new Dictionary<string, object>(), assets });

    private static async Task<JsonElement> ShellBrandAsync(TestTenant t, string user, string locale)
    {
        (await t[user].Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale })).EnsureSuccessStatusCode();
        var shell = await t[user].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/shell");
        return shell.GetProperty("brand");
    }

    /// <summary>
    /// A tenant of its own (packages limit the number of brands), a brand assigned to a new unit, an employee whose primary
    /// organization it is, and one in the parent organization.
    /// </summary>
    private async Task<(TestTenant Tenant, Guid Brand, string User, string Other)> SetUpAsync()
    {
        var t = await world.CreateTenantAsync("bl-" + Guid.NewGuid().ToString("N")[..6], "enterprise_gov");
        var root = await world.CreateUnitAsync(t, Code("BR"), null, "organization");
        var unit = await world.CreateUnitAsync(t, Code("BL"), root, "subsidiary");
        var created = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands", new { name = Code("Lang"), tokens = new Dictionary<string, object>() });
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var brand = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True((await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{unit}", new { brandId = brand })).IsSuccessStatusCode);
        var user = Code("bl_");
        await world.SeedUserAsync(t, user, MembershipTypes.Employee);
        (await t["admin"].Client.PostAsJsonAsync($"/api/v1/org/units/{unit}/memberships", new { userId = t[user].Id, type = "primary" })).EnsureSuccessStatusCode();
        var other = Code("bo_");
        await world.SeedUserAsync(t, other, MembershipTypes.Employee);
        (await t["admin"].Client.PostAsJsonAsync($"/api/v1/org/units/{root}/memberships", new { userId = t[other].Id, type = "primary" })).EnsureSuccessStatusCode();
        return (t, brand, user, other);
    }

    [Fact]
    public async Task The_shell_shows_the_title_logo_and_lockup_of_the_viewers_language_and_falls_back_to_every_language()
    {
        var (t, brand, user, other) = await SetUpAsync();
        var (all, allUrl) = await UploadAsync(t);
        var (arabic, arabicUrl) = await UploadAsync(t);
        var (lockup, lockupUrl) = await UploadAsync(t);
        var saved = await SaveAsync(t, brand, new()
        {
            ["title"] = "  Acme Gulf  ",
            ["title.ar"] = "أكمي الخليج",
            ["logo"] = all,
            ["logo.ar"] = arabic,
            ["lockup.ar"] = lockup,
        });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        var view = await saved.Content.ReadFromJsonAsync<JsonElement>();
        // The title is kept trimmed with the images, and only images get an address.
        Assert.Equal("Acme Gulf", view.GetProperty("assets").GetProperty("title").GetString());
        Assert.False(view.GetProperty("assetUrls").TryGetProperty("title.ar", out _));
        Assert.Equal(arabicUrl, view.GetProperty("assetUrls").GetProperty("logo.ar").GetString());

        var ar = await ShellBrandAsync(t, user, "ar");
        Assert.Equal("أكمي الخليج", ar.GetProperty("title").GetString());
        Assert.Equal(arabicUrl, ar.GetProperty("logoUrl").GetString());
        Assert.Equal(lockupUrl, ar.GetProperty("lockupUrl").GetString());
        var manifest = await t[user].Client.GetFromJsonAsync<JsonElement>("/api/v1/experience/manifest.webmanifest");
        Assert.Equal("أكمي الخليج", manifest.GetProperty("name").GetString());

        // Switching language switches them; English has no lockup of its own, and none for every language either.
        var en = await ShellBrandAsync(t, user, "en");
        Assert.Equal("Acme Gulf", en.GetProperty("title").GetString());
        Assert.Equal(allUrl, en.GetProperty("logoUrl").GetString());
        Assert.Equal(JsonValueKind.Null, en.GetProperty("lockupUrl").ValueKind);

        // Someone in another organization keeps the tenant's brand, which has no title.
        var parent = await ShellBrandAsync(t, other, "en");
        Assert.Equal(JsonValueKind.Null, parent.GetProperty("title").ValueKind);
    }

    [Fact]
    public async Task Titles_are_checked_and_unknown_language_slots_or_foreign_assets_are_refused()
    {
        var (t, brand, _, _) = await SetUpAsync();
        var (logo, _) = await UploadAsync(t);
        async Task<string> RefusedAsync(Dictionary<string, string> assets)
        {
            var response = await SaveAsync(t, brand, assets);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Assert.Contains("validation.theme.title", await RefusedAsync(new() { ["title.ar"] = new string('a', 61) }));
        Assert.Contains("validation.theme.title", await RefusedAsync(new() { ["title"] = "   " }));
        Assert.Contains("validation.theme.title", await RefusedAsync(new() { ["title"] = "Line\nbreak" }));
        // Only supported languages, and the browser icon stays one for every language.
        Assert.Contains("validation.theme.asset_slot_unknown", await RefusedAsync(new() { ["logo.xx"] = logo }));
        Assert.Contains("validation.theme.asset_slot_unknown", await RefusedAsync(new() { ["favicon.ar"] = logo }));
        Assert.Contains("validation.theme.asset_slot_unknown", await RefusedAsync(new() { ["title.xx"] = "Acme" }));
        // A language slot holds an image reference, never text.
        Assert.Contains("validation.theme.asset_reference_invalid", await RefusedAsync(new() { ["logo.ar"] = "Acme" }));

        // Another tenant cannot point its own language logo at this tenant's asset.
        var foreign = await world.B["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands",
            new { name = Code("Foreign"), tokens = new Dictionary<string, object>(), assets = new Dictionary<string, string> { ["logo.ar"] = logo } });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        Assert.Contains("validation.theme.asset_reference_invalid", await foreign.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Language_titles_go_live_only_with_the_release_and_stay_in_the_tenant()
    {
        var (t, brand, user, _) = await SetUpAsync();
        // A draft title is not shown until the draft is published (T014).
        var draft = await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brands/{brand}/draft",
            new { name = "By language", tokens = new Dictionary<string, object>(), assets = new Dictionary<string, string> { ["title.en"] = "Draft title" } });
        Assert.True(draft.IsSuccessStatusCode, await draft.Content.ReadAsStringAsync());
        Assert.NotEqual("Draft title", (await ShellBrandAsync(t, user, "en")).GetProperty("title").GetString());

        var saved = await SaveAsync(t, brand, new() { ["title.en"] = "Live title" });
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.Equal("Live title", (await ShellBrandAsync(t, user, "en")).GetProperty("title").GetString());
        Assert.NotEqual("Live title", (await ShellBrandAsync(world.B, "admin", "en")).GetProperty("title").GetString());
    }
}
