using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>A tenant with a consented Microsoft 365 connection and directory sync set up (spec 003 US6), for mapping tests.</summary>
public sealed record SyncedTenant(TestTenant Tenant, Guid Directory, Guid Connection, FakeMicrosoftGraph.DirectoryData Data)
{
    public string Key => Tenant.Key;
    public Guid Id => Tenant.Id;
    public HttpClient Admin => Tenant["admin"].Client;
    public string Email(string name) => $"{name}@{Tenant.Key}.example";
}

public static class DirectoryTestSetup
{
    private static readonly string[] DirectoryRoles = ["User.Read.All", "Group.Read.All", "AdministrativeUnit.Read.All"];

    public static async Task<SyncedTenant> CreateAsync(TenantWorld world, string prefix)
    {
        var t = await world.CreateTenantAsync(prefix + "-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var directory = Guid.NewGuid();
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync("/api/v1/integrations/connections", new
        {
            kind = "microsoft365", externalTenantId = directory, displayName = "Contoso Microsoft 365", features = new[] { "directory_sync" },
        });
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        world.Factory.Graph.Consent(directory, DirectoryRoles);

        var start = await admin.GetAsync($"/api/v1/integrations/connections/{connection}/features/directory_sync/consent");
        var query = FakeIdentityProvider.ParseQuery(start.Headers.Location!.Query);
        var relay = await world.Factory.PlatformClient().GetAsync(
            $"/api/v1/integrations/consent/callback?admin_consent=True&tenant={directory:D}&state={Uri.EscapeDataString(query["state"])}");
        var client = world.Factory.TenantClient(t.Key, t["admin"].Token);
        client.DefaultRequestHeaders.Add("Cookie", start.Headers.GetValues("Set-Cookie").Select(v => v.Split(';')[0]).First(v => v.StartsWith("tb_consent=", StringComparison.Ordinal)));
        await client.GetAsync(relay.Headers.Location!.PathAndQuery);

        var saved = await admin.PutAsJsonAsync("/api/v1/tenant/directory", new { userScope = new { mode = "all", groups = Array.Empty<string>() }, deletionThresholdPct = 100 });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return new SyncedTenant(t, directory, connection, world.Factory.Graph.Data(directory));
    }

    /// <summary>Runs one directory sync and delivers its events, so an approved mapping is applied as in production.</summary>
    public static async Task SyncAsync(TenantWorld world, SyncedTenant s)
    {
        var run = await world.Factory.InScopeAsync(s.Id, async sp =>
        {
            var result = await sp.GetRequiredService<DirectorySyncService>().RunAsync(s.Connection, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result!;
        });
        Assert.Equal("succeeded", run.Outcome);
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
    }
}
