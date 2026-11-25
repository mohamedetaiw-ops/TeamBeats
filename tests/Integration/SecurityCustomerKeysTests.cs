using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 026 US8, FR-007 customer-managed keys (defaults in the R3 plan): an Enterprise and Government tenant connects a
/// key in Azure Key Vault or a Vault transit engine, its data key is wrapped with it and the page shows the status; a
/// revoked key makes the tenant unavailable with a clear answer and it comes back when the key is restored, with nothing
/// deleted; a new key version is wrapped onto without downtime; a change of key is a four-eyes kind; other packages,
/// people without the permission and other tenants get nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SecurityCustomerKeysTests(TenantWorld world)
{
    private const string Keys = "/api/v1/security/keys";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("ck-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "second", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.AssignAsync(t, "second", BuiltInRoles.TenantAdministrator, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> SendAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var body = await SendAsync(call, expected);
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private (FakeKeyVault Vault, object Body) Azure(TestTenant t, string secretName = "kms-app")
    {
        var vault = FakeKeyVault.Azure(world.Factory.Rest);
        var secretRef = SecretReferences.For(t.Id, secretName);
        world.Factory.Secrets.Set(secretRef, "fictional-client-secret");
        return (vault, new
        {
            provider = "azure_key_vault", keyUri = vault.KeyUri, secretRef,
            directoryId = "3f1c2b8e-4a6d-4c2e-9b1a-6d5e4f3a2b10", clientId = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d",
        });
    }

    private Task<string?> CheckAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var status = await sp.GetRequiredService<TenantKeyService>().CheckAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return status;
        });

    private Task<long> AuditedAsync(TestTenant t, string action) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a", ("a", action));

    private Task<TenantDataKey?> DataKeyAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            try
            {
                return await sp.GetRequiredService<ITenantDataKeys>().CurrentAsync();
            }
            catch (AppException ex) when (ex.Code == ProblemCodes.TenantKeyUnavailable)
            {
                return null;
            }
        });

    [Fact]
    public async Task A_connected_key_wraps_the_data_key_and_a_revocation_makes_the_tenant_unavailable_until_it_is_restored()
    {
        var t = await FreshAsync();
        var empty = await SendAsync(t["admin"].Client.GetAsync(Keys), HttpStatusCode.OK);
        Assert.False(empty.GetProperty("connected").GetBoolean());
        Assert.Null(await DataKeyAsync(t));

        var (vault, body) = Azure(t);
        var connected = await SendAsync(t["admin"].Client.PutAsJsonAsync(Keys, body), HttpStatusCode.OK);
        Assert.Equal("active", connected.GetProperty("status").GetString());
        Assert.Equal(vault.LatestVersion, connected.GetProperty("keyVersion").GetString());
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityKeyConnected));
        Assert.True(vault.Wraps >= 1 && vault.Unwraps >= 1);

        // The data key is the tenant's own; only its wrapped form is stored.
        var key = await DataKeyAsync(t);
        Assert.NotNull(key);
        Assert.Equal(32, key!.Key.Length);
        var stored = await world.Database.TenantScalarAsync<byte[]>(t.Id, "select wrapped_data_key from security.tenant_key");
        Assert.False(stored.AsSpan().IndexOf(key.Key) >= 0);
        Assert.DoesNotContain(Convert.ToBase64String(key.Key), string.Join("\n", world.Factory.Rest.SentTo(vault.Host).Select(s => s.Body ?? "").Where(b => b.Contains("wrapkey", StringComparison.Ordinal))));

        // The customer revokes the key: the next check (every 5 minutes) makes the tenant unavailable, with a clear answer.
        vault.Disable();
        Assert.Equal(KeyStatuses.Revoked, await CheckAsync(t));
        Assert.Equal(ProblemCodes.TenantKeyUnavailable, await ProblemAsync(t["emp"].Client.GetAsync("/api/v1/me"), HttpStatusCode.ServiceUnavailable));
        Assert.Equal(ProblemCodes.TenantKeyUnavailable, await ProblemAsync(t["admin"].Client.GetAsync(Keys), HttpStatusCode.ServiceUnavailable));
        var notice = await SendAsync(t["emp"].Client.GetAsync("/api/v1/site/availability"), HttpStatusCode.OK);
        Assert.False(notice.GetProperty("available").GetBoolean());
        Assert.Equal("key_unavailable", notice.GetProperty("reason").GetString());
        Assert.Null(await DataKeyAsync(t));
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityKeyRevoked));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.alert where kind = @k", ("k", AlertKinds.KeyRevoked)));
        // A second check while it stays revoked records nothing new.
        Assert.Equal(KeyStatuses.Revoked, await CheckAsync(t));
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityKeyRevoked));

        // Another tenant is not affected.
        var other = await FreshAsync();
        await SendAsync(other["emp"].Client.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        Assert.True((await SendAsync(other["emp"].Client.GetAsync("/api/v1/site/availability"), HttpStatusCode.OK)).GetProperty("available").GetBoolean());

        // Restored: the tenant comes back with the same data key, and nothing was deleted.
        vault.Enable();
        Assert.Equal(KeyStatuses.Active, await CheckAsync(t));
        await SendAsync(t["emp"].Client.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        Assert.Equal(key.Key, (await DataKeyAsync(t))!.Key);
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityKeyRestored));
    }

    [Fact]
    public async Task A_new_key_version_is_wrapped_onto_without_downtime_and_an_outage_is_not_a_revocation()
    {
        var t = await FreshAsync();
        var (vault, body) = Azure(t);
        await SendAsync(t["admin"].Client.PutAsJsonAsync(Keys, body), HttpStatusCode.OK);
        var first = vault.LatestVersion;
        var key = (await DataKeyAsync(t))!.Key;

        vault.Rotate();
        Assert.Equal(KeyStatuses.Active, await CheckAsync(t));
        var page = await SendAsync(t["admin"].Client.GetAsync(Keys), HttpStatusCode.OK);
        Assert.Equal(vault.LatestVersion, page.GetProperty("keyVersion").GetString());
        Assert.NotEqual(first, vault.LatestVersion);
        Assert.Equal(JsonValueKind.String, page.GetProperty("rotatedAt").ValueKind);
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityKeyRotated));
        Assert.Equal(key, (await DataKeyAsync(t))!.Key);

        // The key service does not answer: the tenant stays available, the status says so, and an alert is raised once.
        vault.Down();
        Assert.Equal(KeyStatuses.Unreachable, await CheckAsync(t));
        Assert.Equal(KeyStatuses.Unreachable, await CheckAsync(t));
        await SendAsync(t["emp"].Client.GetAsync("/api/v1/me"), HttpStatusCode.OK);
        Assert.Equal(1L, await AuditedAsync(t, AuditActions.SecurityKeyUnreachable));
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from security.alert where kind = @k", ("k", AlertKinds.KeyUnreachable)));

        // After 24 hours without an answer it counts as revoked.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update security.tenant_key set last_ok_at = now() - interval '25 hours'");
        Assert.Equal(KeyStatuses.Revoked, await CheckAsync(t));
        await SendAsync(t["emp"].Client.GetAsync("/api/v1/me"), HttpStatusCode.ServiceUnavailable);
        vault.Down(false);
        Assert.Equal(KeyStatuses.Active, await CheckAsync(t));
        await SendAsync(t["emp"].Client.GetAsync("/api/v1/me"), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Changing_the_key_keeps_the_data_key_and_is_held_for_a_second_administrator_when_four_eyes_is_on()
    {
        var t = await FreshAsync();
        var (_, body) = Azure(t);
        await SendAsync(t["admin"].Client.PutAsJsonAsync(Keys, body), HttpStatusCode.OK);
        var key = (await DataKeyAsync(t))!;

        await SendAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/security/four-eyes", new { enabled = true }), HttpStatusCode.OK);
        var (next, nextBody) = Azure(t, "kms-app-2");
        var held = await SendAsync(t["admin"].Client.PutAsJsonAsync(Keys, nextBody), HttpStatusCode.Accepted);
        var change = held.GetProperty("pendingChange");
        Assert.Equal(FourEyesKinds.Keys, change.GetProperty("kind").GetString());
        Assert.Contains("keyUri", change.GetProperty("fields").EnumerateArray().Select(f => f.GetString()));
        Assert.NotEqual(next.KeyUri, held.GetProperty("keyUri").GetString());
        Assert.Equal(0, next.Wraps);

        await SendAsync(t["second"].Client.PostAsync($"/api/v1/security/changes/{change.GetProperty("id").GetGuid()}:approve", null), HttpStatusCode.OK);
        var page = await SendAsync(t["admin"].Client.GetAsync(Keys), HttpStatusCode.OK);
        Assert.Equal(next.KeyUri, page.GetProperty("keyUri").GetString());
        Assert.Equal(2L, await AuditedAsync(t, AuditActions.SecurityKeyConnected));
        var moved = (await DataKeyAsync(t))!;
        Assert.Equal(key.Id, moved.Id);
        Assert.Equal(key.Key, moved.Key);

        // While the current key does not answer, the data key cannot move.
        await SendAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/security/four-eyes", new { enabled = false }), HttpStatusCode.Accepted);
        var pending = await SendAsync(t["second"].Client.GetAsync("/api/v1/security/changes"), HttpStatusCode.OK);
        var off = pending.GetProperty("items").EnumerateArray().First(i => i.GetProperty("status").GetString() == "pending").GetProperty("id").GetGuid();
        await SendAsync(t["second"].Client.PostAsync($"/api/v1/security/changes/{off}:approve", null), HttpStatusCode.OK);
        next.Down();
        var (_, third) = Azure(t, "kms-app-3");
        Assert.Equal("conflict.keys.current_unavailable", await ProblemAsync(t["admin"].Client.PutAsJsonAsync(Keys, third), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task A_vault_transit_key_works_and_bad_connections_packages_and_people_are_refused()
    {
        var t = await FreshAsync();
        var token = SecretReferences.For(t.Id, "vault-token");
        world.Factory.Secrets.Set(token, "fictional-vault-token");
        var vault = FakeKeyVault.VaultTransit(world.Factory.Rest, "fictional-vault-token");
        var body = new { provider = "vault_transit", keyUri = vault.KeyUri, secretRef = token };

        // The Vault host must be allowed first.
        Assert.Equal("validation.custom_rest.host_not_allowed", await ProblemAsync(t["admin"].Client.PutAsJsonAsync(Keys, body), HttpStatusCode.BadRequest));
        await SendAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host = vault.Host }), HttpStatusCode.OK);
        var connected = await SendAsync(t["admin"].Client.PutAsJsonAsync(Keys, body), HttpStatusCode.OK);
        Assert.Equal("1", connected.GetProperty("keyVersion").GetString());
        vault.Rotate();
        Assert.Equal(KeyStatuses.Active, await CheckAsync(t));
        Assert.Equal("2", (await SendAsync(t["admin"].Client.GetAsync(Keys), HttpStatusCode.OK)).GetProperty("keyVersion").GetString());
        vault.Disable();
        Assert.Equal(KeyStatuses.Revoked, await CheckAsync(t));
        vault.Enable();
        Assert.Equal(KeyStatuses.Active, await CheckAsync(t));

        // Bad connections are refused before anything is stored.
        var other = await FreshAsync();
        Assert.Equal("validation.keys.key_uri", await ProblemAsync(other["admin"].Client.PutAsJsonAsync(Keys, new
        {
            provider = "azure_key_vault", keyUri = "https://keys.example.test/keys/teambeats", secretRef = SecretReferences.For(other.Id, "kms-app"),
            directoryId = "3f1c2b8e-4a6d-4c2e-9b1a-6d5e4f3a2b10", clientId = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d",
        }), HttpStatusCode.BadRequest));
        // A secret reference of another tenant is refused.
        var (otherVault, _) = Azure(other);
        Assert.Equal("validation.custom_rest.secret_ref", await ProblemAsync(other["admin"].Client.PutAsJsonAsync(Keys, new
        {
            provider = "azure_key_vault", keyUri = otherVault.KeyUri, secretRef = token,
            directoryId = "3f1c2b8e-4a6d-4c2e-9b1a-6d5e4f3a2b10", clientId = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d",
        }), HttpStatusCode.BadRequest));
        var unknown = FakeKeyVault.Azure(world.Factory.Rest);
        Assert.Equal("validation.keys.credential_missing", await ProblemAsync(other["admin"].Client.PutAsJsonAsync(Keys, new
        {
            provider = "azure_key_vault", keyUri = unknown.KeyUri, secretRef = SecretReferences.For(other.Id, "no-such-secret"),
            directoryId = "3f1c2b8e-4a6d-4c2e-9b1a-6d5e4f3a2b10", clientId = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d",
        }), HttpStatusCode.BadRequest));
        unknown.Disable();
        world.Factory.Secrets.Set(SecretReferences.For(other.Id, "kms-off"), "fictional-client-secret");
        Assert.Equal("validation.keys.key_disabled", await ProblemAsync(other["admin"].Client.PutAsJsonAsync(Keys, new
        {
            provider = "azure_key_vault", keyUri = unknown.KeyUri, secretRef = SecretReferences.For(other.Id, "kms-off"),
            directoryId = "3f1c2b8e-4a6d-4c2e-9b1a-6d5e4f3a2b10", clientId = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d",
        }), HttpStatusCode.BadRequest));
        Assert.False((await SendAsync(other["admin"].Client.GetAsync(Keys), HttpStatusCode.OK)).GetProperty("connected").GetBoolean());

        // Only Enterprise and Government, and only people who manage security.
        var plus = await FreshAsync("workplace_plus");
        Assert.Equal(ProblemCodes.EntitlementNotInPackage, await ProblemAsync(plus["admin"].Client.GetAsync(Keys), HttpStatusCode.Forbidden));
        await SendAsync(t["emp"].Client.GetAsync(Keys), HttpStatusCode.Forbidden);
        await SendAsync(t["emp"].Client.PutAsJsonAsync(Keys, body), HttpStatusCode.Forbidden);
        await SendAsync(t["emp"].Client.PostAsync($"{Keys}:check", null), HttpStatusCode.Forbidden);
        var checkedPage = await SendAsync(t["admin"].Client.PostAsync($"{Keys}:check", null), HttpStatusCode.OK);
        Assert.Equal("active", checkedPage.GetProperty("status").GetString());
    }
}
