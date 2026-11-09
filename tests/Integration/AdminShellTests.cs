using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 007 admin shell API and localized responses (spec 007, LOC requirements).</summary>
[Collection(WorldCollection.Name)]
public sealed class AdminShellTests(TenantWorld world)
{
    private async Task<HashSet<string>> Sections(TestUser user)
    {
        var nav = await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/navigation");
        return nav.EnumerateArray().Select(s => s.GetProperty("key").GetString()!).ToHashSet();
    }

    [Fact]
    public async Task Navigation_is_server_driven_per_permission()
    {
        var admin = await Sections(world.A["admin"]);
        Assert.Superset(new HashSet<string> { "tenant_settings", "organization", "users", "roles", "plan", "audit", "pages", "templates", "notifications", "services", "ai" }, admin);

        var orgadmin = await Sections(world.A["orgadmin"]);
        Assert.Contains("organization", orgadmin);
        Assert.Contains("services", orgadmin); // Service owners work inside their units (spec 016 US7).
        Assert.DoesNotContain("tenant_settings", orgadmin);
        Assert.DoesNotContain("plan", orgadmin);
        // The org admin holds template.edit for the Gulf only; template designers work tenant-wide in R1.
        Assert.DoesNotContain("templates", orgadmin);
        Assert.DoesNotContain("notifications", orgadmin);

        var security = await Sections(world.A["security"]);
        Assert.Contains("audit", security);
        Assert.Contains("ai", security); // Security officers read the AI pages with ai.policy_view (spec 013).
        Assert.DoesNotContain("services", security);
        Assert.DoesNotContain("organization", security);

        Assert.Empty(await Sections(world.A["employee_gulf"]));
    }

    [Fact]
    public async Task Navigation_requires_a_session()
    {
        var response = await world.Factory.TenantClient(world.A.Key).GetAsync("/api/v1/admin/navigation");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task User_list_is_tenant_scoped()
    {
        var users = await world.A["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/admin/users");
        var ids = users.EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Contains(world.A["employee_gulf"].Id, ids);
        Assert.DoesNotContain(world.B["employee_gulf"].Id, ids);

        var cross = await world.A["admin"].Client.GetAsync($"/api/v1/admin/users/{world.B["employee_gulf"].Id}");
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
    }

    [Fact]
    public async Task Problems_are_localized_to_the_user_locale()
    {
        var user = await world.SeedUserAsync(world.A, "arabic_reader", "employee");
        (await user.Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "ar" })).EnsureSuccessStatusCode();

        var me = await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("rtl", me.GetProperty("dir").GetString());

        var denied = await user.Client.GetAsync("/api/v1/tenant/entitlements");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var problem = await denied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("urn:teambeats:problem:authz.denied", problem.GetProperty("type").GetString());
        Assert.Equal("ليست لديك صلاحية للقيام بهذا الإجراء", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Locale_not_enabled_by_tenant_falls_back_to_tenant_default()
    {
        var user = await world.SeedUserAsync(world.A, "french_reader", "employee");
        (await user.Client.PutAsJsonAsync("/api/v1/me/preferences", new { locale = "fr" })).EnsureSuccessStatusCode();
        var me = await user.Client.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal("en", me.GetProperty("locale").GetString());
    }
}
