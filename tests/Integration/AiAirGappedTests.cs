using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Api;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.AiGateway.OpenAiCompatible;
using TeamBeats.Modules.AiGateway.OpenRouter;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 R3 T033 (FR-021, US6 scenario 5): an air-gapped install. Shared providers are unreachable by design and
/// left off the catalogue; models the install hosts in its own network (provider <c>local</c>) route, meter and may take
/// Restricted content; a hosted server that is down ends the call with <c>provider_unavailable</c> and nothing reaches
/// OpenRouter; the policy page says the install is air-gapped. Each test plays the install with its own settings on the
/// shared test host.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiAirGappedTests(TenantWorld world)
{
    private const string Chat = "local/llama-local";
    private const string Embed = "local/bge-local";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        Assert.DoesNotContain(ApiFactory.LocalModelKey, text, StringComparison.Ordinal);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private WebApplicationFactory<ApiProgram> AirGapped() => world.Factory.WithWebHostBuilder(b => b
        .UseSetting(OpenRouterProvider.AirGappedSetting, "true")
        .UseSetting(LocalModelProvider.BaseUrlSetting, ApiFactory.LocalModelUrl)
        .UseSetting(LocalModelProvider.ApiKeySetting, ApiFactory.LocalModelKey));

    private async Task HostedModelsAsync()
    {
        await OkAsync(world.Platform.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new
        {
            model = Chat, provider = "local", capabilities = new[] { "chat", "tools" }, contextTokens = 128_000, regions = new[] { "onprem" },
            zeroRetention = true, noTraining = true, maxClassification = "restricted", inputPrice = 0, outputPrice = 0,
        }));
        await OkAsync(world.Platform.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new
        {
            model = Embed, provider = "local", capabilities = new[] { "embeddings" }, contextTokens = 8_192, regions = new[] { "onprem" },
            zeroRetention = true, noTraining = true, maxClassification = "restricted", inputPrice = 0, outputPrice = 0,
        }));
    }

    private async Task<TestTenant> TenantAsync()
    {
        var t = await world.CreateTenantAsync("gap-" + Guid.NewGuid().ToString("N")[..8], "enterprise_gov");
        await world.SeedUserAsync(t, "layla", MembershipTypes.Employee);
        await world.AssignAsync(t, "layla", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        return t;
    }

    private static HttpClient Client(WebApplicationFactory<ApiProgram> factory, TestTenant t, string user)
    {
        var client = factory.CreateDefaultClient(new Uri($"https://{t.Key}.{ApiFactory.BaseDomain}"));
        client.DefaultRequestHeaders.Add("Cookie", $"tb_session={t[user].Token}");
        return client;
    }

    private static object Policy(string[]? sovereign = null) => new
    {
        // The assistant and agent routes stay as the platform has them, so the change applies without the release gate.
        allowlist = new[] { Chat, Embed, "openai/gpt-4o-mini", "openai/gpt-4o" },
        routes = AiUseCases.All.Where(u => u is not (AiUseCases.Transcribe or AiUseCases.Speak)).ToDictionary(u => u, u => u == AiUseCases.Embed ? new[] { Embed }
            : AiUseCases.Gated.Contains(u) ? new[] { "openai/gpt-4o-mini", "openai/gpt-4o" } : new[] { Chat }),
        confidential = new[] { Chat, Embed },
        sovereign,
    };

    private static async Task<JsonElement> SaveAsync(HttpClient client, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var current = await client.GetAsync("/api/v1/ai/policy");
        using var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/ai/policy") { Content = JsonContent.Create(body) };
        put.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag?.ToString());
        return await OkAsync(client.SendAsync(put), expected);
    }

    private static async Task<T> AsAsync<T>(WebApplicationFactory<ApiProgram> factory, TestTenant t, Func<IAiGateway, Task<T>> call)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        ctx.SetTenant(t.Id);
        ctx.SetActor(ActorType.User, t["layla"].Id);
        return await call(scope.ServiceProvider.GetRequiredService<IAiGateway>());
    }

    private static Task<AiResult> AskAsync(WebApplicationFactory<ApiProgram> factory, TestTenant t, DataClassification level) =>
        AsAsync(factory, t, g => g.ChatAsync(new AiRequest(AiUseCases.Summarize, [new AiMessage(AiRoles.User, "Summarize the cabinet note")], level)));

    [Fact]
    public async Task Only_hosted_models_serve_an_air_gapped_install_even_for_Restricted_content_and_nothing_reaches_openrouter()
    {
        await HostedModelsAsync();
        var t = await TenantAsync();
        using var install = AirGapped();
        var layla = Client(install, t, "layla");

        // Shared models are left off the catalogue; hosted ones are there.
        var catalogue = await OkAsync(layla.GetAsync("/api/v1/ai/catalogue"));
        var providers = catalogue.EnumerateArray().Select(m => m.GetProperty("provider").GetString()).Distinct().ToList();
        Assert.DoesNotContain("openrouter", providers);
        Assert.Contains("local", providers);
        Assert.True((await OkAsync(layla.GetAsync("/api/v1/ai/policy"))).GetProperty("airGapped").GetBoolean());

        await SaveAsync(layla, Policy(["confidential", "restricted"]));
        var before = world.Factory.OpenRouter.Requests;
        foreach (var level in new[] { DataClassification.Internal, DataClassification.Confidential, DataClassification.Restricted })
        {
            var served = await AskAsync(install, t, level);
            Assert.True(served.IsOk, served.Denial);
            Assert.Equal(Chat, served.Completion!.Model);
            Assert.Equal("local", served.Completion.Provider);
        }
        Assert.Equal("llama-local", world.Factory.LocalModels.LastModel);
        var vectors = await AsAsync(install, t, g => g.EmbedAsync(new EmbeddingRequest(["cabinet note"], DataClassification.Restricted)));
        Assert.Equal(Embed, vectors.Model);
        Assert.Equal(before, world.Factory.OpenRouter.Requests);
        Assert.Equal(4L, await world.Database.TenantScalarAsync<long>(t.Id, "select count(*) from ai.usage_record where provider = 'local' and decision = 'served'"));

        // The hosted server is down: the call ends, and there is nothing to fall back to.
        world.Factory.LocalModels.Unavailable = true;
        try
        {
            Assert.Equal(AiDenials.ProviderUnavailable, (await AskAsync(install, t, DataClassification.Internal)).Denial);
            Assert.Equal(before, world.Factory.OpenRouter.Requests);
        }
        finally
        {
            world.Factory.LocalModels.Unavailable = false;
        }
    }

    [Fact]
    public async Task Restricted_is_only_for_hosted_models_and_a_normal_install_never_reaches_a_hosted_server()
    {
        // The catalogue refuses Restricted for a shared provider.
        var shared = await world.Platform.PutAsJsonAsync("/api/v1/platform/ai/catalogue", new
        {
            model = "openai/restricted-try", provider = "openrouter", capabilities = new[] { "chat" }, contextTokens = 8_000, regions = new[] { "us" },
            zeroRetention = true, noTraining = true, maxClassification = "restricted",
        });
        Assert.Equal(HttpStatusCode.BadRequest, shared.StatusCode);

        // Without a hosted server configured, hosted models are unreachable and left off the catalogue, and the page says nothing.
        await HostedModelsAsync();
        var t = await TenantAsync();
        var catalogue = await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/catalogue"));
        Assert.DoesNotContain(catalogue.EnumerateArray(), m => m.GetProperty("provider").GetString() == "local");
        Assert.False((await OkAsync(t["layla"].Client.GetAsync("/api/v1/ai/policy"))).GetProperty("airGapped").GetBoolean());

        // Another tenant of an air-gapped install has hosted models too, but only by its own policy.
        using var install = AirGapped();
        var other = await TenantAsync();
        Assert.NotEqual(Chat, (await AskAsync(install, other, DataClassification.Internal)).Completion?.Model);
    }
}
