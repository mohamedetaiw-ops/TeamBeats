using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.DevFakes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 030 R3 MOB-021 and MOB-023 (T014, T015): the app lock is part of the tenant security policy, kept in range and
/// audited, and set only where the package has the store apps; the store app reads, before sign-in, whether this
/// workspace takes it, the lock, the sign-in hosts it may open and whether native push is there. Another tenant's
/// settings never show, and an unknown host gets nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class MobileShellTests(TenantWorld world)
{
    private const string Policy = "/api/v1/security/policy";
    private const string Shell = "/api/v1/mobile/shell";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private Task<JsonElement> ShellAsync(TestTenant t) => OkAsync(world.Factory.TenantClient(t.Key).GetAsync(Shell));

    [Fact]
    public async Task The_app_lock_is_in_the_policy_and_the_app_reads_what_this_workspace_allows()
    {
        var t = await world.CreateTenantAsync("ms-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        await world.SeedUserAsync(t, "employee", MembershipTypes.Employee);
        var admin = t["admin"].Client;

        // Defaults before anything is saved: optional, after 5 minutes.
        var shell = await ShellAsync(t);
        Assert.True(shell.GetProperty("available").GetBoolean());
        Assert.Equal("optional", shell.GetProperty("appLock").GetString());
        Assert.Equal(5, shell.GetProperty("appLockMinutes").GetInt32());
        Assert.True(shell.GetProperty("nativePush").GetBoolean());
        Assert.Empty(shell.GetProperty("signInHosts").EnumerateArray());
        var policy = await OkAsync(admin.GetAsync(Policy));
        Assert.True(policy.GetProperty("appLockAvailable").GetBoolean());

        foreach (var bad in new object[] { new { appLock = "sometimes" }, new { appLockMinutes = 61 }, new { appLockMinutes = -1 } })
        {
            var refused = await admin.PutAsJsonAsync(Policy, bad);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        }
        Assert.Contains("validation.security.app_lock", await (await admin.PutAsJsonAsync(Policy, new { appLock = "sometimes" })).Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var saved = await OkAsync(admin.PutAsJsonAsync(Policy, new { appLock = "required", appLockMinutes = 0 }));
        Assert.Equal("required", saved.GetProperty("appLock").GetString());
        Assert.Equal(0, saved.GetProperty("appLockMinutes").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityPolicyChanged, null);
        shell = await ShellAsync(t);
        Assert.Equal("required", shell.GetProperty("appLock").GetString());
        Assert.Equal(0, shell.GetProperty("appLockMinutes").GetInt32());

        // With single sign-on set up, the app may open the provider's host and nothing else of it.
        var secretRef = SecretReferences.For(t.Id, "entra-client");
        world.Factory.Secrets.Set(secretRef, "fictional-test-client-secret");
        await OkAsync(admin.PutAsJsonAsync("/api/v1/tenant/identity-provider", new
        {
            kind = "entra",
            issuer = FakeIdentityProvider.IssuerFor(Guid.NewGuid()),
            clientId = "7d0b4bb8-6d1e-4c0e-9a0f-2a8f5f0b1c01",
            secretRef,
            displayName = "Contoso",
            jitEnabled = false,
            jitDomains = Array.Empty<string>(),
            groupRoleMap = Array.Empty<object>(),
        }));
        var hosts = (await ShellAsync(t)).GetProperty("signInHosts").EnumerateArray().Select(h => h.GetString()).ToList();
        Assert.Equal([new Uri(FakeIdentityProvider.Authority).Host], hosts);

        // Only security administrators change it.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PutAsJsonAsync(Policy, new { appLock = "off" })).StatusCode);
    }

    [Fact]
    public async Task Without_the_store_apps_the_lock_cannot_be_set_and_the_app_is_told_no()
    {
        var plain = await world.CreateTenantAsync("ms-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var shell = await ShellAsync(plain);
        Assert.False(shell.GetProperty("available").GetBoolean());
        Assert.False(shell.GetProperty("nativePush").GetBoolean());
        Assert.False((await OkAsync(plain["admin"].Client.GetAsync(Policy))).GetProperty("appLockAvailable").GetBoolean());
        var refused = await plain["admin"].Client.PutAsJsonAsync(Policy, new { appLock = "required" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // Other settings still save there.
        await OkAsync(plain["admin"].Client.PutAsJsonAsync(Policy, new { offlineDays = 3 }));

        // One tenant's lock is never another's.
        var gov = await world.CreateTenantAsync("ms-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        await OkAsync(gov["admin"].Client.PutAsJsonAsync(Policy, new { appLock = "off", appLockMinutes = 30 }));
        var other = await world.CreateTenantAsync("ms-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        Assert.Equal("optional", (await ShellAsync(other)).GetProperty("appLock").GetString());
        Assert.Equal("off", (await ShellAsync(gov)).GetProperty("appLock").GetString());

        // Not on a tenant host: nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.CreateClient().GetAsync(Shell)).StatusCode);
    }
}
