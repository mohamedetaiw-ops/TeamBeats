using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Api;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.DevFakes;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.AiGateway.OpenRouter;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 R3 T031 (FR-020, US6 scenario 6): a tenant's own OpenRouter key in dedicated and on-premises installs. The
/// key is a secret manager reference, never returned; calls keep the platform key until a test passes, then use the
/// tenant's key, and usage records say which key paid. Removal goes back to the platform key, another tenant never uses
/// it, and a shared install does not offer the setting. The fake OpenRouter records the key of each request.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiProviderKeyTests(TenantWorld world)
{
    private const string Keys = "/api/v1/ai/provider-keys/openrouter";

    private FakeOpenRouter Provider => world.Factory.OpenRouter;

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pk-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static AiRequest Ask() => new(AiUseCases.Assistant, [new AiMessage(AiRoles.User, "What is the leave policy?")], DataClassification.Internal);

    private async Task<string?> KeyOfNextCallAsync(TestTenant t)
    {
        var result = await world.Factory.AsUserAsync(t.Id, t["admin"].Id, sp => sp.GetRequiredService<IAiGateway>().ChatAsync(Ask()));
        Assert.True(result.IsOk, result.Denial);
        return Provider.LastKey;
    }

    private Task<string?> LastKeyOwnerAsync(TestTenant t) =>
        world.Database.TenantScalarAsync<string>(t.Id, "select key_owner from ai.usage_record where decision = 'served' order by at desc limit 1");

    private Task<long> AuditAsync(TestTenant t, string action) =>
        world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from audit.audit_event where action = @action", ("action", action));

    private string NewKey(TestTenant t, string name, bool accepted = true)
    {
        var key = "fictional-tenant-key-" + Guid.NewGuid().ToString("N");
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, name), key);
        if (accepted)
        {
            Provider.TenantKeys[key] = true;
        }
        return key;
    }

    [Fact]
    public async Task A_tenant_key_is_used_only_after_its_test_passes_is_never_shown_and_stays_with_its_tenant()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var layla = t["layla"].Client;

        var empty = await OkAsync(layla.GetAsync(Keys));
        Assert.True(empty.GetProperty("offered").GetBoolean());
        Assert.True(empty.GetProperty("editable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("secretRef").ValueKind);

        // Only the AI Policy Administrator sets it, and only to a reference of the tenant's own secrets.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(t.Id, "openrouter") })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await layla.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(other.Id, "openrouter") })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await layla.PutAsJsonAsync("/api/v1/ai/provider-keys/anthropic", new { secretRef = SecretReferences.For(t.Id, "openrouter") })).StatusCode);

        var key = NewKey(t, "openrouter");
        var saved = await OkAsync(layla.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(t.Id, "openrouter") }));
        Assert.False(saved.GetProperty("inUse").GetBoolean());
        Assert.DoesNotContain(key, saved.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, await AuditAsync(t, AuditActions.AiProviderKeySet));

        // Untested: the platform key still pays.
        Assert.Equal(FakeOpenRouter.ApiKey, await KeyOfNextCallAsync(t));
        Assert.Equal("platform", await LastKeyOwnerAsync(t));

        var tested = await OkAsync(layla.PostAsync($"{Keys}/test", null));
        Assert.True(tested.GetProperty("lastTestPassed").GetBoolean());
        Assert.True(tested.GetProperty("inUse").GetBoolean());
        Assert.DoesNotContain(key, tested.ToString(), StringComparison.Ordinal);
        Assert.Equal(key, await KeyOfNextCallAsync(t));
        Assert.Equal("tenant", await LastKeyOwnerAsync(t));

        // Another tenant's calls never use it, and it cannot read or change it.
        Assert.Equal(FakeOpenRouter.ApiKey, await KeyOfNextCallAsync(other));
        Assert.Equal(JsonValueKind.Null, (await OkAsync(other["admin"].Client.GetAsync(Keys))).GetProperty("secretRef").ValueKind);

        // A changed reference is untested again; a key OpenRouter refuses fails its test and is not used.
        NewKey(t, "openrouter-2", accepted: false);
        await OkAsync(layla.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(t.Id, "openrouter-2") }));
        var refused = await OkAsync(layla.PostAsync($"{Keys}/test", null));
        Assert.False(refused.GetProperty("lastTestPassed").GetBoolean());
        Assert.Equal(FakeOpenRouter.ApiKey, await KeyOfNextCallAsync(t));
        Assert.Equal(2, await AuditAsync(t, AuditActions.AiProviderKeyTested));

        // Removal goes back to the platform key.
        await OkAsync(layla.DeleteAsync(Keys), HttpStatusCode.NoContent);
        Assert.Equal(1, await AuditAsync(t, AuditActions.AiProviderKeyRemoved));
        Assert.Equal(FakeOpenRouter.ApiKey, await KeyOfNextCallAsync(t));
        Assert.Equal(JsonValueKind.Null, (await OkAsync(layla.GetAsync(Keys))).GetProperty("secretRef").ValueKind);
    }

    [Fact]
    public async Task A_shared_install_does_not_offer_tenant_keys_and_ignores_any_left_behind()
    {
        var t = await FreshAsync();
        NewKey(t, "openrouter");
        await OkAsync(t["layla"].Client.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(t.Id, "openrouter") }));
        await OkAsync(t["layla"].Client.PostAsync($"{Keys}/test", null));

        using var shared = world.Factory.WithWebHostBuilder(b => b.UseSetting(ProviderKeys.AllowedSetting, "false"));
        var client = shared.CreateDefaultClient(new Uri($"https://{t.Key}.{ApiFactory.BaseDomain}"));
        client.DefaultRequestHeaders.Add("Cookie", $"tb_session={t["layla"].Token}");
        var view = await OkAsync(client.GetAsync(Keys));
        Assert.False(view.GetProperty("offered").GetBoolean());
        Assert.False(view.GetProperty("editable").GetBoolean());
        var put = await client.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(t.Id, "openrouter") });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("conflict.ai.provider_keys_not_offered", (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detailKey").GetString());

        await using var scope = shared.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        ctx.SetTenant(t.Id);
        ctx.SetActor(ActorType.User, t["admin"].Id);
        Assert.True((await scope.ServiceProvider.GetRequiredService<IAiGateway>().ChatAsync(Ask())).IsOk);
        Assert.Equal(FakeOpenRouter.ApiKey, Provider.LastKey);
    }

    [Fact]
    public async Task Without_a_platform_key_a_tested_tenant_key_still_reaches_openrouter_unless_egress_is_blocked()
    {
        var t = await FreshAsync();
        var none = await FreshAsync();
        var key = NewKey(t, "openrouter");
        await OkAsync(t["layla"].Client.PutAsJsonAsync(Keys, new { secretRef = SecretReferences.For(t.Id, "openrouter") }));
        await OkAsync(t["layla"].Client.PostAsync($"{Keys}/test", null));

        async Task<AiResult> AskAsync(WebApplicationFactory<ApiProgram> factory, TestTenant tenant)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
            ctx.SetTenant(tenant.Id);
            ctx.SetActor(ActorType.User, tenant["admin"].Id);
            return await scope.ServiceProvider.GetRequiredService<IAiGateway>().ChatAsync(Ask());
        }

        using var keyless = world.Factory.WithWebHostBuilder(b => b.UseSetting(OpenRouterProvider.ApiKeySetting, ""));
        Assert.True((await AskAsync(keyless, t)).IsOk);
        Assert.Equal(key, Provider.LastKey);
        Assert.Equal(AiDenials.ProviderUnavailable, (await AskAsync(keyless, none)).Denial);

        using var blocked = world.Factory.WithWebHostBuilder(b => b.UseSetting(OpenRouterProvider.ApiKeySetting, "").UseSetting(OpenRouterProvider.EgressBlockedSetting, "true"));
        var before = Provider.Requests;
        Assert.Equal(AiDenials.ProviderUnavailable, (await AskAsync(blocked, t)).Denial);
        Assert.Equal(before, Provider.Requests);
    }
}
