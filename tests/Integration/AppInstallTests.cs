using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 030 US1 (FR-001): the installable app. The manifest and the app icon carry the tenant's name and the brand of the
/// viewer's organization (spec 033), come from the session and never from the request, and need the mobile.pwa capability.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AppInstallTests(TenantWorld world)
{
    private const string Manifest = "/api/v1/experience/manifest.webmanifest";
    private const string Icon = "/api/v1/experience/app-icon.svg";

    private Task<TestTenant> FreshAsync() => world.CreateTenantAsync("pw-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");

    private static async Task<JsonElement> ManifestAsync(TestTenant t, string user)
    {
        var response = await t[user].Client.GetAsync(Manifest);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(text).RootElement;
    }

    [Fact]
    public async Task Each_organization_installs_its_own_brand_under_the_tenant_name()
    {
        var t = await FreshAsync();
        var root = await world.CreateUnitAsync(t, "PR" + Guid.NewGuid().ToString("N")[..6], null, "organization");
        var unit = await world.CreateUnitAsync(t, "PW" + Guid.NewGuid().ToString("N")[..6], root, "subsidiary");
        var brand = await t["admin"].Client.PostAsJsonAsync("/api/v1/experience/brands", new
        {
            name = "Acme teal",
            tokens = new Dictionary<string, object>
            {
                ["color.accent.base"] = new Dictionary<string, object> { ["$value"] = "#0f766e" },
                ["color.surface.base"] = new Dictionary<string, object> { ["$value"] = "#f0fdfa", ["$modes"] = new Dictionary<string, string> { ["dark"] = "#0b1220" } },
            },
        });
        Assert.True(brand.StatusCode == HttpStatusCode.Created, await brand.Content.ReadAsStringAsync());
        var brandId = (await brand.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.True((await t["admin"].Client.PutAsJsonAsync($"/api/v1/experience/brand-assignments/{unit}", new { brandId })).IsSuccessStatusCode);
        var name = "pwa_" + Guid.NewGuid().ToString("N")[..6];
        await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        (await t["admin"].Client.PostAsJsonAsync($"/api/v1/org/units/{unit}/memberships", new { userId = t[name].Id, type = "primary" })).EnsureSuccessStatusCode();

        var other = "pwo_" + Guid.NewGuid().ToString("N")[..6];
        await world.SeedUserAsync(t, other, MembershipTypes.Employee);
        (await t["admin"].Client.PostAsJsonAsync($"/api/v1/org/units/{root}/memberships", new { userId = t[other].Id, type = "primary" })).EnsureSuccessStatusCode();

        var teal = await ManifestAsync(t, name);
        Assert.Equal($"{t.Key} holding", teal.GetProperty("name").GetString());
        // The short name under the icon keeps to twelve characters.
        Assert.Equal($"{t.Key} holding"[..AppManifest.ShortNameLength].TrimEnd(), teal.GetProperty("short_name").GetString());
        Assert.Equal("#0f766e", teal.GetProperty("theme_color").GetString());
        Assert.Equal("#f0fdfa", teal.GetProperty("background_color").GetString());
        Assert.Equal("standalone", teal.GetProperty("display").GetString());
        Assert.Equal("/", teal.GetProperty("scope").GetString());
        Assert.Equal("/?source=app", teal.GetProperty("start_url").GetString());
        var icons = teal.GetProperty("icons").EnumerateArray().ToList();
        Assert.Contains(icons, i => i.GetProperty("src").GetString() == "/icons/icon-512.png" && i.GetProperty("sizes").GetString() == "512x512");
        Assert.Contains(icons, i => i.GetProperty("purpose").GetString() == "maskable");
        // Nulls are left out, so the browser never reads "type": null.
        Assert.All(icons, i => Assert.NotEqual(JsonValueKind.Null, i.TryGetProperty("type", out var type) ? type.ValueKind : JsonValueKind.String));

        // Someone in another organization of the same tenant installs the tenant's default brand.
        var holding = await ManifestAsync(t, other);
        Assert.Equal($"{t.Key} holding", holding.GetProperty("name").GetString());
        Assert.NotEqual("#0f766e", holding.GetProperty("theme_color").GetString());

        // Another tenant installs its own name, whatever host or header the request claims.
        var b = await ManifestAsync(world.B, "admin");
        Assert.NotEqual($"{t.Key} holding", b.GetProperty("name").GetString());
        var cross = await world.Factory.TenantClient(t.Key, world.B["admin"].Token).GetAsync(Manifest);
        Assert.Equal(HttpStatusCode.Unauthorized, cross.StatusCode);
    }

    [Fact]
    public async Task The_app_icon_is_the_tenant_initials_on_the_brand_colour()
    {
        var t = await FreshAsync();
        var response = await t["admin"].Client.GetAsync(Icon);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var svg = await response.Content.ReadAsStringAsync();
        Assert.Contains($">{char.ToUpperInvariant(t.Key[0])}H</text>", svg, StringComparison.Ordinal);
        Assert.Contains($"aria-label=\"{t.Key} holding\"", svg, StringComparison.Ordinal);
        Assert.Contains("rx=\"112\"", svg, StringComparison.Ordinal);
        // The maskable icon fills the square so the platform mask never cuts the corners twice.
        var maskable = await (await t["admin"].Client.GetAsync(Icon + "?maskable=true")).Content.ReadAsStringAsync();
        Assert.Contains("rx=\"0\"", maskable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_out_callers_and_tenants_without_the_app_get_nothing()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(world.A.Key).GetAsync(Manifest)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(world.A.Key).GetAsync(Icon)).StatusCode);

        var tenant = await world.CreateTenantAsync("pw-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        await world.PinPackageVersionAsync(tenant, "workplace", new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
            [Capabilities.OrgHierarchy] = new { enabled = true },
        });
        foreach (var path in new[] { Manifest, Icon })
        {
            var response = await tenant["admin"].Client.GetAsync(path);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, text);
            Assert.Contains("entitlement.not_in_package", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task An_install_is_counted_by_platform_family_without_the_person()
    {
        var t = await FreshAsync();
        async Task<int> SendAsync(string platform)
        {
            var response = await t["admin"].Client.PostAsJsonAsync("/api/v1/analytics/events",
                new { events = new[] { new { name = "mobile.install", fields = new { platform } } } });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accepted").GetInt32();
        }

        Assert.Equal(1, await SendAsync("android"));
        // Only the coarse family is kept: anything that could name a device is dropped.
        Assert.Equal(0, await SendAsync("Pixel 8 Pro 4f2a"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from analytics.event where name = 'mobile.install' and user_key is null and fields->>'platform' = 'android'"));
    }

    [Theory]
    [InlineData("Acme Holding", "AH")]
    [InlineData("acme", "A")]
    [InlineData("  ", "T")]
    [InlineData("شركة النور للتقنية", "شا")]
    public void Initials_take_the_first_letter_of_the_first_two_words(string name, string expected) =>
        Assert.Equal(expected, AppManifest.Initials(name));
}
