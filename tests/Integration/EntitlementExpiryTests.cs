using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 005 R2 US4 scenario 3: after the contract end the tenant has 30 days of grace, then is read only: writes are
/// refused with <c>entitlement.expired</c> while reads, sign-out and other tenants keep working.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class EntitlementExpiryTests(TenantWorld world)
{
    private async Task<TestTenant> TenantAsync() => await world.CreateTenantAsync("ex-" + Guid.NewGuid().ToString("N")[..8], "workplace");

    private Task<HttpResponseMessage> ContractAsync(TestTenant t, object body) => world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/contract", body);

    private async Task<JsonElement> StateAsync(TestTenant t) =>
        (await t["admin"].Client.GetFromJsonAsync<JsonElement>("/api/v1/tenant/entitlements")).GetProperty("contract");

    private Task<HttpResponseMessage> WriteAsync(TestTenant t) => t["admin"].Client.PostAsJsonAsync("/api/v1/org/units", new
    {
        parentId = (Guid?)null, typeKey = "organization", code = "U" + Guid.NewGuid().ToString("N")[..6],
        names = new Dictionary<string, string> { ["en"] = "Unit", ["ar"] = "وحدة" },
    });

    [Fact]
    public async Task A_contract_runs_into_grace_then_read_only_where_writes_are_refused_and_reads_work()
    {
        var t = await TenantAsync();
        var other = await TenantAsync();
        Assert.Equal("active", (await StateAsync(t)).GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await ContractAsync(t, new { endsAt = DateTimeOffset.UtcNow.AddMonths(3) })).StatusCode);
        Assert.Equal("active", (await StateAsync(t)).GetProperty("state").GetString());

        // Ended five days ago: grace, and work goes on.
        (await ContractAsync(t, new { endsAt = DateTimeOffset.UtcNow.AddDays(-5) })).EnsureSuccessStatusCode();
        var grace = await StateAsync(t);
        Assert.Equal("grace", grace.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.String, grace.GetProperty("graceEndsAt").ValueKind);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await WriteAsync(t)).StatusCode);

        // Past the 30 days of grace: read only.
        (await ContractAsync(t, new { endsAt = DateTimeOffset.UtcNow.AddDays(-31) })).EnsureSuccessStatusCode();
        Assert.Equal("read_only", (await StateAsync(t)).GetProperty("state").GetString());
        var refused = await WriteAsync(t);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("entitlement.expired", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.GetAsync("/api/v1/org/units")).StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, (await WriteAsync(other)).StatusCode);

        // A longer grace set by the platform reopens it; moving the package keeps the contract.
        (await ContractAsync(t, new { endsAt = DateTimeOffset.UtcNow.AddDays(-31), graceDays = 60 })).EnsureSuccessStatusCode();
        Assert.Equal("grace", (await StateAsync(t)).GetProperty("state").GetString());
        (await world.Platform.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/subscription", new { packageKey = "workplace_plus" })).EnsureSuccessStatusCode();
        Assert.Equal("grace", (await StateAsync(t)).GetProperty("state").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await world.Factory.TenantClient(t.Key, t["admin"].Token).PostAsync("/api/v1/auth/signout", null)).StatusCode);
    }

    [Fact]
    public async Task Only_the_platform_sets_a_contract_and_grace_is_bounded()
    {
        var t = await TenantAsync();
        Assert.NotEqual(HttpStatusCode.NoContent, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/platform/tenants/{t.Id}/contract",
            new { endsAt = DateTimeOffset.UtcNow.AddDays(-100) })).StatusCode);
        Assert.Equal("active", (await StateAsync(t)).GetProperty("state").GetString());
        var invalid = await ContractAsync(t, new { endsAt = DateTimeOffset.UtcNow, graceDays = 400 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }
}
