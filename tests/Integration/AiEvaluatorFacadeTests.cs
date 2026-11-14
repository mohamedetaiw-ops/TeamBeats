using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 013 T028 (FR-018, ADR-0015): the internal OpenAI-compatible facade for evaluator workers answers only on the
/// internal host name, only with a token for one tenant, one run and the evaluation use case; expired, tampered and
/// wrong-kind tokens are refused; the call runs through the whole gateway pipeline in the token's tenant (classification,
/// allowlist, metering); and the facade does not exist unless the installation turns it on.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AiEvaluatorFacadeTests(TenantWorld world)
{
    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private Task<string?> MintAsync(Guid tenant, Guid run, DataClassification level = DataClassification.Internal) =>
        world.Factory.InScopeAsync(tenant, sp => Task.FromResult(sp.GetRequiredService<IAiEvaluatorTokens>().Mint(run, level, null)));

    private static HttpRequestMessage Call(string? token, object body, string host = EvaluatorTokens.DefaultHost)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, EvaluatorFacade.Path) { Content = JsonContent.Create(body) };
        request.Headers.Host = host;
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    private static object Judge(string text = "Is the answer grounded? Reply pass or fail.") =>
        new { messages = new[] { new { role = "system", content = "You are a strict judge." }, new { role = "user", content = text } }, temperature = 0 };

    private Task<long> EvaluationCallsAsync(Guid tenant) =>
        world.Database.TenantScalarAsync<long>(tenant, "select count(*) from ai.usage_record where use_case = 'evaluation'");

    [Fact]
    public async Task A_run_token_gets_an_answer_through_the_gateway_in_its_own_tenant()
    {
        var a = await world.CreateTenantAsync("aie-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var b = await world.CreateTenantAsync("aie-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var token = await MintAsync(a.Id, Guid.NewGuid());
        Assert.NotNull(token);

        var client = world.Factory.CreateClient();
        var response = await client.SendAsync(Call(token, Judge()));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);
        var json = JsonDocument.Parse(text).RootElement;
        Assert.Equal("chat.completion", json.GetProperty("object").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()));
        Assert.True(json.GetProperty("usage").GetProperty("total_tokens").GetInt32() > 0);

        // Metered in the token's tenant only (FR-002 step 7).
        Assert.Equal(1, await EvaluationCallsAsync(a.Id));
        Assert.Equal(0, await EvaluationCallsAsync(b.Id));

        // The run's classification travels in the token: Confidential has no allowed model under the default policy.
        var confidential = await client.SendAsync(Call(await MintAsync(a.Id, Guid.NewGuid(), DataClassification.Confidential), Judge()));
        Assert.Equal(HttpStatusCode.Forbidden, confidential.StatusCode);
        Assert.Contains(AiDenials.ClassificationBlocked, await confidential.Content.ReadAsStringAsync());

        // A model outside the tenant's allowlist is refused as for any judge call.
        var named = await client.SendAsync(Call(token, new { model = "unknown/model", messages = new[] { new { role = "user", content = "Hi" } } }));
        Assert.Equal(HttpStatusCode.Forbidden, named.StatusCode);

        // Tools and streaming are not part of the facade.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Call(token, new { messages = new[] { new { role = "user", content = "Hi" } }, stream = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Call(token, new { messages = Array.Empty<object>() }))).StatusCode);
    }

    [Fact]
    public async Task Missing_expired_tampered_and_foreign_tokens_are_refused()
    {
        var t = await world.CreateTenantAsync("aie-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var client = world.Factory.CreateClient();
        var token = (await MintAsync(t.Id, Guid.NewGuid()))!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Call(null, Judge()))).StatusCode);
        var tampered = token[..^2] + (token[^2] == 'A' ? "B" : "A") + token[^1];
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Call(tampered, Judge()))).StatusCode);

        // A token whose payload was swapped for another tenant's keeps the old signature and fails.
        var other = await world.CreateTenantAsync("aie-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var otherToken = (await MintAsync(other.Id, Guid.NewGuid()))!;
        var swapped = $"tbev1.{otherToken.Split('.')[1]}.{token.Split('.')[2]}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Call(swapped, Judge()))).StatusCode);

        // Expired: minted 16 minutes ago.
        var expired = world.Factory.InScopeAsync(t.Id, sp => Task.FromResult(new EvaluatorTokens(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ITenantContext>(),
            new FixedClock(DateTimeOffset.UtcNow.AddMinutes(-16))).Mint(Guid.NewGuid(), DataClassification.Internal, null)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Call(await expired, Judge()))).StatusCode);

        // A tenant session or a tenant host never reaches the facade.
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Call(token, Judge(), host: $"{t.Key}.{ApiFactory.BaseDomain}"))).StatusCode);
        Assert.Equal(0, await EvaluationCallsAsync(t.Id));
    }

    [Fact]
    public async Task The_facade_does_not_exist_unless_turned_on()
    {
        using var off = world.Factory.WithWebHostBuilder(b => b.UseSetting(EvaluatorTokens.EnabledSetting, "false"));
        var client = off.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Call("tbev1.x.y", Judge()))).StatusCode);
        var t = world.A;
        await using var scope = off.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContextAccessor>().SetTenant(t.Id);
        Assert.Null(scope.ServiceProvider.GetRequiredService<IAiEvaluatorTokens>().Mint(Guid.NewGuid(), DataClassification.Internal, null));
    }
}
