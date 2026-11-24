using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity.Directory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// A tenant with its own AD DS connection read by an enrolled sync agent (as in OnPremDirectorySyncTests), for the
/// sign-in tests that link on objectGUID: AD FS over OIDC and over SAML.
/// </summary>
internal sealed record OnPremAd(TestTenant Tenant, Guid Connection, Guid AgentId, ECDsa Key)
{
    private const string Packs = "/api/v1/integrations/pack-connections";

    public static async Task<OnPremAd> SetupAsync(TenantWorld world, string package)
    {
        var t = await world.CreateTenantAsync("fs-" + Guid.NewGuid().ToString("N")[..8], package);
        var admin = t["admin"].Client;
        var created = await admin.PostAsJsonAsync(Packs, new { kind = "onprem_ad", displayName = "Head office AD", settings = new { host = "dc01.corp.example", baseDn = "DC=corp,DC=example" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var connection = (await created.ReadAsync<JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"{Packs}/{connection}:enable", null)).StatusCode);
        var code = (await (await admin.PostAsync($"{Packs}/{connection}/agents/enrolment-code", null)).ReadAsync<JsonElement>()).GetProperty("code").GetString();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var enrolled = await world.Factory.TenantClient(t.Key).PostAsJsonAsync("/api/v1/agent/enrol",
            new { code, name = "AGENT-01", publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
        var agentId = (await enrolled.ReadAsync<JsonElement>()).GetProperty("agentId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/tenant/directory",
            new { connectionId = connection, userScope = new { mode = "all", groups = Array.Empty<string>() }, deletionThresholdPct = 100 })).StatusCode);
        return new OnPremAd(t, connection, agentId, key);
    }

    public Task<HttpResponseMessage> AgentAsync(TenantWorld world, string path, object? payload = null)
    {
        var body = payload is null ? "" : JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation(AgentProtocol.AgentHeader, AgentId.ToString());
        request.Headers.TryAddWithoutValidation(AgentProtocol.TimestampHeader, ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(AgentProtocol.SignatureHeader, AgentProtocol.Sign(Key, "POST", path, ts, Encoding.UTF8.GetBytes(body)));
        return world.Factory.TenantClient(Tenant.Key).SendAsync(request);
    }

    /// <summary>One directory sync run: the agent reads these users and Identity applies them.</summary>
    public async Task SyncAsync(TenantWorld world, params (string Guid, string Name)[] users)
    {
        await world.Factory.InScopeAsync(Tenant.Id, async sp =>
        {
            var run = await sp.GetRequiredService<DirectorySyncService>().RunAsync(Connection, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });
        var job = await (await AgentAsync(world, "/api/v1/agent/jobs:pull")).ReadAsync<JsonElement>();
        var jobId = job.GetProperty("id").GetGuid();
        var batch = new
        {
            users = users.Select(u => new
            {
                externalId = u.Guid, removed = false, accountEnabled = true, displayName = u.Name,
                email = $"{u.Name.ToLowerInvariant()}@{Tenant.Key}.example", userPrincipalName = $"{u.Name.ToLowerInvariant()}@{Tenant.Key}.example",
            }).ToArray(),
        };
        Assert.Equal(HttpStatusCode.OK, (await AgentAsync(world, $"/api/v1/agent/jobs/{jobId}/records", batch)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await AgentAsync(world, $"/api/v1/agent/jobs/{jobId}:complete", new { ok = true, usersCursor = "usn-1", groupsCursor = "usn-2" })).StatusCode);
        var applied = await world.Factory.InScopeAsync(Tenant.Id, async sp =>
        {
            var run = await sp.GetRequiredService<DirectorySyncService>().ApplyAgentReadAsync(new AgentJobFinished(jobId, Connection, "directory_sync", true, null, 0, 0), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return run;
        });
        Assert.Equal("succeeded", applied!.Outcome);
    }
}
