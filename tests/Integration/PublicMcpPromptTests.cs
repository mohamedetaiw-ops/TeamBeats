using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 R3 (T022): MCP prompts. A client sees only the prompts whose every tool it is offered, so prompts follow the
/// scopes and the external AI client policy as tools do; titles and texts follow the request's language; arguments are
/// required where marked, short, and go into the text as quoted words; other tenants are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicMcpPromptTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("mcpp-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "gia", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "aip", MembershipTypes.Employee);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private async Task<(string Token, string ClientId)> ClientAsync(TestTenant t, string[] scopes, bool writeTools = false)
    {
        var (token, _, clientId) = await DelegatedAsync(world, t, "gia", scopes, "mcp_client", id => ExternalPolicyAsync(t, "aip", writeTools, (id, "internal")));
        return (token, clientId);
    }

    private async Task<(HttpStatusCode Status, JsonElement Message)> RpcAsync(TestTenant t, string token, string method, object? parameters = null, string? language = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (language is not null)
        {
            request.Headers.AcceptLanguage.ParseAdd(language);
        }
        var response = await world.Factory.TenantClient(t.Key).SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return (response.StatusCode, default);
        }
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? string.Join('\n', text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()))
            : text;
        return (response.StatusCode, JsonDocument.Parse(json).RootElement.Clone());
    }

    private async Task<List<string>> NamesAsync(TestTenant t, string token)
    {
        var (_, message) = await RpcAsync(t, token, "prompts/list");
        return message.GetProperty("result").GetProperty("prompts").EnumerateArray().Select(p => p.GetProperty("name").GetString()!).ToList();
    }

    private async Task AssertRefusedAsync(TestTenant t, string token, object parameters)
    {
        var (status, message) = await RpcAsync(t, token, "prompts/get", parameters);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(message.TryGetProperty("error", out var error), message.ToString());
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_client_sees_only_the_prompts_its_tools_allow_in_the_callers_language()
    {
        var t = await FreshAsync();
        var (token, _) = await ClientAsync(t, ["content:read", "policies:read", "search:query"]);

        var names = await NamesAsync(t, token);
        Assert.Equal(["catch_up_on_news", "find_a_policy"], names.Order(StringComparer.Ordinal).ToList());

        var (_, listed) = await RpcAsync(t, token, "prompts/list");
        var policy = listed.GetProperty("result").GetProperty("prompts").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "find_a_policy");
        Assert.Equal("Find a policy", policy.GetProperty("title").GetString());
        var argument = policy.GetProperty("arguments").EnumerateArray().Single();
        Assert.Equal("topic", argument.GetProperty("name").GetString());
        Assert.True(argument.GetProperty("required").GetBoolean());

        var (_, got) = await RpcAsync(t, token, "prompts/get", new { name = "find_a_policy", arguments = new { topic = "travel \"expenses\"\nnow" } });
        var text = got.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString()!;
        Assert.Contains("\"travel 'expenses' now\"", text, StringComparison.Ordinal);
        Assert.Contains("get_policy", text, StringComparison.Ordinal);
        Assert.Equal("user", got.GetProperty("result").GetProperty("messages")[0].GetProperty("role").GetString());

        var (_, arabic) = await RpcAsync(t, token, "prompts/get", new { name = "find_a_policy", arguments = new { topic = "السفر" } }, "ar");
        Assert.Contains("اعثر على سياسة", arabic.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString(), StringComparison.Ordinal);
        var (_, arabicList) = await RpcAsync(t, token, "prompts/list", language: "ar");
        Assert.Contains(arabicList.GetProperty("result").GetProperty("prompts").EnumerateArray(), p => p.GetProperty("title").GetString() == "البحث عن سياسة");

        // A prompt takes its own arguments only: required, short and named.
        await AssertRefusedAsync(t, token, new { name = "find_a_policy" });
        await AssertRefusedAsync(t, token, new { name = "find_a_policy", arguments = new { topic = "  " } });
        await AssertRefusedAsync(t, token, new { name = "find_a_policy", arguments = new { topic = new string('a', 301) } });
        await AssertRefusedAsync(t, token, new { name = "find_a_policy", arguments = new { topic = "travel", other = "x" } });
        // A prompt whose tools the client is not offered is not served, nor is an unknown one.
        await AssertRefusedAsync(t, token, new { name = "who_can_help", arguments = new { need = "payroll" } });
        await AssertRefusedAsync(t, token, new { name = "no_such_prompt" });
    }

    [Fact]
    public async Task Prompts_follow_the_external_policy_and_stay_in_their_tenant()
    {
        var t = await FreshAsync();
        string[] scopes = ["services:read", "requests:submit", "requests:read", "tasks:read"];
        var (token, clientId) = await ClientAsync(t, scopes);

        // Write tools are off for external AI clients, so the prompt that submits a request is not offered.
        Assert.Equal(["my_open_items"], await NamesAsync(t, token));
        await AssertRefusedAsync(t, token, new { name = "request_a_service", arguments = new { need = "a salary letter" } });

        await ExternalPolicyAsync(t, "aip", true, (clientId, "internal"));
        Assert.Equal(["my_open_items", "request_a_service"], (await NamesAsync(t, token)).Order(StringComparer.Ordinal).ToList());
        var (_, got) = await RpcAsync(t, token, "prompts/get", new { name = "request_a_service", arguments = new { need = "a salary letter" } });
        Assert.Contains("\"a salary letter\"", got.GetProperty("result").GetProperty("messages")[0].GetProperty("content").GetProperty("text").GetString(), StringComparison.Ordinal);

        // Another tenant's server does not take the token.
        var other = await FreshAsync();
        var (status, _) = await RpcAsync(other, token, "prompts/list");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }
}
