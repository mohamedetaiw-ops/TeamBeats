using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.DevFakes;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T023 (US6): a tenant's private OpenAI-compatible endpoint is tested before it can be enabled and the test
/// stays valid for 24 hours; classifications kept on private endpoints never fall back to a shared provider; Restricted
/// content goes only to a private endpoint whose ceiling is Restricted; Confidential embeddings come only from the private
/// endpoint; the key is never returned; and endpoints never cross tenants. The fake private server shares the test host,
/// so the tests that take it down run in the world collection, one at a time.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiPrivateEndpointTests(TenantWorld world)
{
    private const string Small = "openai/gpt-4o-mini";
    private const string Large = "openai/gpt-4o";
    private const string Embedding = "openai/text-embedding-3-small";
    private const string Endpoints = "/api/v1/ai/private-endpoints";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        Assert.DoesNotContain(FakeOpenRouter.PrivateKey, text, StringComparison.Ordinal);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static object Registration(TestTenant t, string secret = "llm-key", string level = "restricted", string name = "Riyadh cluster", int? version = null) => new
    {
        name,
        version,
        baseUrl = FakeOpenRouter.PrivateBaseUrl,
        secretRef = SecretReferences.For(t.Id, secret),
        models = new object[]
        {
            new { name = FakeOpenRouter.PrivateChatModel, capabilities = new[] { "chat", "tools" }, contextTokens = 128_000 },
            new { name = FakeOpenRouter.PrivateEmbedModel, capabilities = new[] { "embeddings" }, contextTokens = 8_192 },
        },
        region = "sa",
        maxClassification = level,
    };

    private async Task<TestTenant> TenantAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("aip-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "llm-key"), FakeOpenRouter.PrivateKey);
        return t;
    }

    /// <summary>Registers, tests and enables an endpoint, and returns its id and catalogue names (chat, embeddings).</summary>
    private static async Task<(Guid Id, string Chat, string Embed)> EnabledAsync(TestTenant t, string level = "restricted")
    {
        var created = await OkAsync(t["layla"].Client.PostAsJsonAsync(Endpoints, Registration(t, level: level)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{id}/test", null));
        await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{id}/enable", null));
        return (id, $"private/{id:N}/{FakeOpenRouter.PrivateChatModel}", $"private/{id:N}/{FakeOpenRouter.PrivateEmbedModel}");
    }

    private static async Task<JsonElement> PolicyAsync(TestTenant t, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var current = await t["layla"].Client.GetAsync("/api/v1/ai/policy");
        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy") { Content = JsonContent.Create(body) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        return await OkAsync(t["layla"].Client.SendAsync(put), expected);
    }

    private static object SovereignPolicy(string chat, string embed, string[]? sovereign) => new
    {
        allowlist = new[] { Small, Large, Embedding, chat, embed },
        routes = AiUseCases.All.ToDictionary(u => u, u => u == AiUseCases.Embed ? new[] { Embedding, embed }
            : AiUseCases.Gated.Contains(u) ? new[] { Small, Large } : new[] { Small, chat, Large }),
        confidential = new[] { chat, embed },
        sovereign,
    };

    private Task<AiResult> ChatAsync(TestTenant t, DataClassification classification, string? model = null) =>
        world.Factory.AsUserAsync(t.Id, t["layla"].Id, sp => sp.GetRequiredService<IAiGateway>().ChatAsync(
            new AiRequest(AiUseCases.Summarize, [new AiMessage(AiRoles.User, "Summarize the cabinet note")], classification, Model: model)));

    private Task<EmbeddingResult> EmbedAsync(TestTenant t, DataClassification classification) =>
        world.Factory.AsUserAsync(t.Id, t["layla"].Id, sp => sp.GetRequiredService<IAiGateway>().EmbedAsync(new EmbeddingRequest(["cabinet note"], classification)));

    [Fact]
    public async Task An_endpoint_is_tested_before_it_can_be_enabled_and_its_key_is_never_returned()
    {
        var t = await TenantAsync();
        var created = await OkAsync(t["layla"].Client.PostAsJsonAsync(Endpoints, Registration(t)), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("disabled", created.GetProperty("status").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiEndpointRegistered);

        var untested = await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{id}/enable", null), HttpStatusCode.Conflict);
        Assert.Contains("conflict.ai.endpoint_untested", untested.GetRawText());

        var tested = await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{id}/test", null));
        Assert.True(tested.GetProperty("lastTestPassed").GetBoolean());
        Assert.True(tested.GetProperty("testCurrent").GetBoolean());
        var checks = tested.GetProperty("checks").EnumerateArray().Select(c => (c.GetProperty("check").GetString(), c.GetProperty("passed").GetBoolean())).ToList();
        Assert.Contains(("connection", true), checks);
        Assert.Contains(("chat", true), checks);
        Assert.Contains(("embeddings", true), checks);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AiEndpointTested);

        var enabled = await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{id}/enable", null));
        Assert.Equal("enabled", enabled.GetProperty("status").GetString());
        Assert.Equal(2, enabled.GetProperty("catalogueModels").GetArrayLength());

        // An enabled endpoint cannot be removed; any change disables it until it is tested again.
        await OkAsync(t["layla"].Client.DeleteAsync($"{Endpoints}/{id}"), HttpStatusCode.Conflict);
        var changed = await OkAsync(t["layla"].Client.PutAsJsonAsync($"{Endpoints}/{id}", Registration(t, name: "Riyadh cluster 2", version: enabled.GetProperty("version").GetInt32())));
        Assert.Equal("disabled", changed.GetProperty("status").GetString());
        Assert.False(changed.GetProperty("testCurrent").GetBoolean());
        await OkAsync(t["layla"].Client.DeleteAsync($"{Endpoints}/{id}"), HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_failing_test_names_what_failed_and_keeps_the_endpoint_off()
    {
        var t = await TenantAsync();
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "wrong-key"), "fictional-wrong-value");
        var wrong = await OkAsync(t["layla"].Client.PostAsJsonAsync(Endpoints, Registration(t, secret: "wrong-key")), HttpStatusCode.Created);
        var test = await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{wrong.GetProperty("id").GetGuid()}/test", null));
        Assert.False(test.GetProperty("lastTestPassed").GetBoolean());
        Assert.Equal("ai.endpoints.checks.rejected", test.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("check").GetString() == "connection").GetProperty("detail").GetString());
        await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{wrong.GetProperty("id").GetGuid()}/enable", null), HttpStatusCode.Conflict);

        // A secret that is not in the secret manager fails before any call.
        var missing = await OkAsync(t["layla"].Client.PostAsJsonAsync(Endpoints, Registration(t, secret: "absent")), HttpStatusCode.Created);
        var none = await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{missing.GetProperty("id").GetGuid()}/test", null));
        Assert.Equal("ai.endpoints.checks.secret_missing", none.GetProperty("checks")[0].GetProperty("detail").GetString());

        // Only HTTPS, and a secret reference of the tenant itself.
        var bad = await OkAsync(t["layla"].Client.PostAsJsonAsync(Endpoints, new
        {
            name = "Plain", baseUrl = "http://llm.private.example/v1", secretRef = SecretReferences.For(Guid.NewGuid(), "llm-key"),
            models = new[] { new { name = "m", capabilities = new[] { "chat" }, contextTokens = 1000 } }, region = "sa", maxClassification = "internal",
        }), HttpStatusCode.BadRequest);
        Assert.True(bad.GetProperty("errors").TryGetProperty("baseUrl", out _));
        Assert.True(bad.GetProperty("errors").TryGetProperty("secretRef", out _));
    }

    [Fact]
    public async Task Sovereign_classifications_go_only_to_the_private_endpoint_and_never_fall_back()
    {
        var t = await TenantAsync();
        var (_, chat, embed) = await EnabledAsync(t);
        await PolicyAsync(t, SovereignPolicy(chat, embed, ["confidential", "restricted"]));

        var before = world.Factory.OpenRouter.Requests;
        var confidential = await ChatAsync(t, DataClassification.Confidential);
        Assert.Equal(chat, confidential.Completion!.Model);
        Assert.Equal(FakeOpenRouter.PrivateChatModel, world.Factory.PrivateModels.LastModel);
        Assert.Equal(chat, (await ChatAsync(t, DataClassification.Restricted)).Completion!.Model);
        Assert.Equal(before, world.Factory.OpenRouter.Requests);

        // Internal content still prefers the shared model at the head of the route.
        Assert.Equal(Small, (await ChatAsync(t, DataClassification.Internal)).Completion!.Model);

        // Confidential embeddings come only from the private endpoint (scenario 4).
        var vectors = await EmbedAsync(t, DataClassification.Confidential);
        Assert.Equal(embed, vectors.Model);
        Assert.Equal(Embedding, (await EmbedAsync(t, DataClassification.Internal)).Model);

        // The endpoint is down: the call fails instead of falling back to OpenRouter (scenario 2).
        world.Factory.PrivateModels.Unavailable = true;
        try
        {
            before = world.Factory.OpenRouter.Requests;
            Assert.Equal(AiDenials.ProviderUnavailable, (await ChatAsync(t, DataClassification.Confidential)).Denial);
            Assert.Equal(AiDenials.ProviderUnavailable, (await EmbedAsync(t, DataClassification.Confidential)).Denial);
            Assert.Equal(before, world.Factory.OpenRouter.Requests);
        }
        finally
        {
            world.Factory.PrivateModels.Unavailable = false;
        }
    }

    [Fact]
    public async Task Restricted_needs_a_private_endpoint_whose_ceiling_is_Restricted()
    {
        var t = await TenantAsync();
        var (id, chat, embed) = await EnabledAsync(t, level: "confidential");
        await PolicyAsync(t, SovereignPolicy(chat, embed, null));
        Assert.Equal(chat, (await ChatAsync(t, DataClassification.Confidential)).Completion!.Model);
        Assert.Equal(AiDenials.ClassificationBlocked, (await ChatAsync(t, DataClassification.Restricted)).Denial);

        // Keeping Restricted on private endpoints needs an enabled, tested endpoint that may take it.
        var refused = await PolicyAsync(t, SovereignPolicy(chat, embed, ["restricted"]), HttpStatusCode.BadRequest);
        Assert.Equal("validation.ai.sovereign_untested", refused.GetProperty("errors").GetProperty("sovereign")[0].GetProperty("key").GetString());

        // Disabling the endpoint takes its models out of the tenant catalogue at once.
        await OkAsync(t["layla"].Client.PostAsync($"{Endpoints}/{id}/disable", null));
        Assert.Equal(AiDenials.ModelNotAllowed, (await ChatAsync(t, DataClassification.Internal, model: chat)).Denial);
    }

    [Fact]
    public async Task Private_endpoints_need_the_capability_and_the_policy_permission()
    {
        var plain = await TenantAsync("workplace");
        var refused = await plain["layla"].Client.PostAsJsonAsync(Endpoints, Registration(plain));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("not_in_package", await refused.Content.ReadAsStringAsync());
        var view = await OkAsync(plain["layla"].Client.GetAsync(Endpoints));
        Assert.False(view.GetProperty("editable").GetBoolean());
        Assert.Equal("ai.endpoints.read_only_package", view.GetProperty("readOnlyReason").GetString());

        var t = await TenantAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.GetAsync(Endpoints)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["emp"].Client.PostAsJsonAsync(Endpoints, Registration(t))).StatusCode);
    }

    [Fact]
    public async Task Private_endpoints_never_cross_tenants()
    {
        var a = await TenantAsync();
        var b = await TenantAsync();
        var (id, chat, _) = await EnabledAsync(a);
        Assert.Equal(HttpStatusCode.NotFound, (await b["layla"].Client.GetAsync($"{Endpoints}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b["layla"].Client.PostAsync($"{Endpoints}/{id}/test", null)).StatusCode);
        Assert.Equal(0, (await OkAsync(b["layla"].Client.GetAsync(Endpoints))).GetProperty("endpoints").GetArrayLength());

        // Tenant B can neither name A's model nor register an endpoint with A's secret.
        Assert.Equal(AiDenials.ModelNotAllowed, (await ChatAsync(b, DataClassification.Internal, model: chat)).Denial);
        var stolen = await OkAsync(b["layla"].Client.PostAsJsonAsync(Endpoints, Registration(a)), HttpStatusCode.BadRequest);
        Assert.True(stolen.GetProperty("errors").TryGetProperty("secretRef", out _));
    }
}
