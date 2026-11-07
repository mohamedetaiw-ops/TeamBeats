using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>Spec 026 T014 (FR-017): the posture checks follow the tenant policy and plan, for security readers only, per tenant.</summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityPostureTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync(string package)
    {
        var t = await world.CreateTenantAsync("pst-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "officer", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        await world.AssignAsync(t, "officer", "security_officer", "tenant", null);
        return t;
    }

    private static async Task<JsonElement> PostureAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/v1/security/posture");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string State(JsonElement posture, string key) =>
        posture.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("key").GetString() == key).GetProperty("state").GetString()!;

    [Fact]
    public async Task A_new_tenant_shows_the_controls_it_has_not_set_with_links_to_fix_them()
    {
        var t = await FreshAsync("workplace_plus");
        var posture = await PostureAsync(t["admin"].Client);

        Assert.Equal("failed", State(posture, "mfa_required"));
        Assert.Equal("failed", State(posture, "ip_rules"));
        Assert.Equal("failed", State(posture, "admin_area_rule"));
        Assert.Equal("failed", State(posture, "sso_configured"));
        Assert.Equal("passed", State(posture, "step_up_window"));
        Assert.Equal("passed", State(posture, "idle_timeout"));
        Assert.Equal("passed", State(posture, "absolute_timeout"));
        var checks = posture.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Equal(checks.Length, posture.GetProperty("total").GetInt32());
        Assert.Equal(checks.Count(c => c.GetProperty("state").GetString() == "passed"), posture.GetProperty("passed").GetInt32());
        Assert.Equal("/admin/security", checks.Single(c => c.GetProperty("key").GetString() == "ip_rules").GetProperty("link").GetString());
        Assert.Equal("/admin/identity", checks.Single(c => c.GetProperty("key").GetString() == "sso_configured").GetProperty("link").GetString());
    }

    [Fact]
    public async Task Weakening_a_setting_shows_on_the_posture_of_that_tenant_only()
    {
        var t = await FreshAsync("workplace_plus");
        var other = await FreshAsync("workplace_plus");
        var before = await PostureAsync(t["admin"].Client);

        var update = await t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { idleMinutes = 120, stepUpMinutes = 30 });
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

        var after = await PostureAsync(t["officer"].Client);
        Assert.Equal("failed", State(after, "idle_timeout"));
        Assert.Equal("failed", State(after, "step_up_window"));
        Assert.Equal(before.GetProperty("passed").GetInt32() - 2, after.GetProperty("passed").GetInt32());

        var untouched = await PostureAsync(other["admin"].Client);
        Assert.Equal("passed", State(untouched, "idle_timeout"));
        Assert.Equal("passed", State(untouched, "step_up_window"));
    }

    [Fact]
    public async Task The_admin_area_check_does_not_count_on_a_plan_without_scoped_rules()
    {
        var t = await FreshAsync("workplace");
        var posture = await PostureAsync(t["admin"].Client);
        Assert.Equal("unavailable", State(posture, "admin_area_rule"));
        Assert.Equal(posture.GetProperty("checks").GetArrayLength() - 1, posture.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Members_without_security_permissions_are_refused()
    {
        var t = await FreshAsync("workplace");
        var response = await t["employee"].Client.GetAsync("/api/v1/security/posture");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
