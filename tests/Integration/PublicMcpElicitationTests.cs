using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 R3 (T026): confirmation inside verified AI clients through MCP elicitation. When the tenant's external AI
/// client policy allows it, a verified client that can ask the employee does so during the write tool call, and the
/// answer counts as if given on the TeamBeats card: accept runs the action once, decline rejects it, cancel leaves it
/// waiting in TeamBeats. With the setting off, for a client the tenant registered itself, or for a client that cannot
/// ask, the action waits in TeamBeats as before. An answer is bound to the pending action's employee, app, tool and
/// arguments.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicMcpElicitationTests(TenantWorld world)
{
    private static readonly Dictionary<string, object?> Canteen = new() { ["title"] = "Canteen menu", ["text"] = "Soup on Monday." };

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("mcpe-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "aip", MembershipTypes.Employee);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private static async Task PolicyAsync(TestTenant t, string clientId, bool confirmInClient)
    {
        var current = await JsonAsync(await t["aip"].Client.GetAsync("/api/v1/agents/policy"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.GetProperty("policy").GetRawText())!;
        body["external"] = new { enabled = true, clients = new[] { new { clientId, maxClassification = "internal" } }, writeTools = true, confirmInClient };
        await JsonAsync(await t["aip"].Client.PutAsJsonAsync("/api/v1/agents/policy", new { version = current.GetProperty("version").GetInt32(), policy = body }));
    }

    private static string Verifier() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A verified client from the platform list, connected by the administrator with read and draft scopes; its token.</summary>
    private async Task<(string Token, string ClientId)> VerifiedAsync(TestTenant t, bool confirmInClient)
    {
        var clientId = $"https://ai.example.test/{Guid.NewGuid():N}/client.json";
        string[] scopes = ["content:read", "content:draft"];
        await JsonAsync(await world.Platform.PutAsJsonAsync("/api/v1/platform/api/verified-mcp-clients", new
        {
            clientId, name = "Desk Assistant", publisher = "Assistant Labs", redirectUris = new[] { Redirect }, scopes,
        }));
        await PolicyAsync(t, clientId, confirmInClient);
        // The first connection registers the client; the administrator then consents to drafting for the tenant.
        await AuthorizeAsync(t, "admin", clientId, scopes, Verifier());
        var app = (await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/apps"))).EnumerateArray().Single(a => a.GetProperty("clientId").GetString() == clientId);
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.GetProperty("id").GetString()}/admin-consent", new { scopes = new[] { "content:draft" } }));
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes }));
        var verifier = Verifier();
        var authorize = await AuthorizeAsync(t, "admin", clientId, scopes, verifier);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), authorize.Headers.Location!.ToString());
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        })));
        return (tokens.GetProperty("access_token").GetString()!, clientId);
    }

    /// <summary>An MCP client of the official SDK, with an elicitation handler when one is given.</summary>
    private async Task<McpClient> ClientAsync(TestTenant t, string token, Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>>? ask)
    {
        var http = world.Factory.TenantClient(t.Key);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        var options = new McpClientOptions();
        if (ask is not null)
        {
            options.Handlers = new McpClientHandlers { ElicitationHandler = ask };
        }
        return await McpClient.CreateAsync(transport, options);
    }

    private static Func<ElicitRequestParams?, CancellationToken, ValueTask<ElicitResult>> Answer(List<string> asked, string action, bool confirm = true) =>
        (request, _) =>
        {
            asked.Add(request?.Message ?? "");
            return ValueTask.FromResult(new ElicitResult
            {
                Action = action,
                Content = action == "accept" ? new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(confirm) } : null,
            });
        };

    private static JsonElement Structured(CallToolResult result) =>
        result.StructuredContent ?? throw new Xunit.Sdk.XunitException(string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));

    private Task<long> DraftsAsync(TestTenant t, string title) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from content.content_version where variants::text like @title", ("title", $"%\"{title}\"%"));

    private Task<long> ActionsAsync(TestTenant t, string status, string? decidedIn = null) =>
        world.Database.CountInTenantAsync(t.Id,
            "select count(*) from agents.external_action where status = @s and (@d::text is null or decided_in = @d)", ("s", status), ("d", (object?)decidedIn ?? DBNull.Value));

    [Fact]
    public async Task A_verified_client_asks_the_employee_and_their_answer_counts_as_on_the_card()
    {
        var t = await FreshAsync();
        var (token, _) = await VerifiedAsync(t, confirmInClient: true);

        // Accept: the client shows the question with the values it sent, and the action runs once, here and now.
        var asked = new List<string>();
        await using (var client = await ClientAsync(t, token, Answer(asked, "accept")))
        {
            var done = Structured(await client.CallToolAsync("draft_news", Canteen));
            Assert.True(done.GetProperty("ok").GetBoolean(), done.ToString());
            Assert.Equal("draft", done.GetProperty("content").GetProperty("status").GetString());
        }
        var question = Assert.Single(asked);
        Assert.Contains("Desk Assistant", question, StringComparison.Ordinal);
        Assert.Contains("draft_news", question, StringComparison.Ordinal);
        Assert.Contains("Canteen menu", question, StringComparison.Ordinal);
        Assert.Equal(1, await DraftsAsync(t, "Canteen menu"));
        Assert.Equal(1, await ActionsAsync(t, "confirmed", "client"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where template_key = 'agents.external_action' and read_at is null"));

        // Decline: rejected, nothing done, and the client is told.
        await using (var client = await ClientAsync(t, token, Answer([], "decline")))
        {
            var refused = Structured(await client.CallToolAsync("draft_news", new Dictionary<string, object?> { ["title"] = "Parking", ["text"] = "Level 2." }));
            Assert.Equal("tool.rejected", refused.GetProperty("error").GetString());
        }
        Assert.Equal(1, await ActionsAsync(t, "rejected", "client"));
        // Accept with the box left off is a no as well.
        await using (var client = await ClientAsync(t, token, Answer([], "accept", confirm: false)))
        {
            Assert.Equal("tool.rejected", Structured(await client.CallToolAsync("draft_news", new Dictionary<string, object?> { ["title"] = "Gym", ["text"] = "Closed." }))
                .GetProperty("error").GetString());
        }
        Assert.Equal(0, await DraftsAsync(t, "Parking") + await DraftsAsync(t, "Gym"));

        // Cancel: the action keeps waiting on the TeamBeats card, as without elicitation.
        await using (var client = await ClientAsync(t, token, Answer([], "cancel")))
        {
            var waiting = Structured(await client.CallToolAsync("draft_news", new Dictionary<string, object?> { ["title"] = "Lift", ["text"] = "Fixed." }));
            Assert.Equal("pending_confirmation", waiting.GetProperty("content").GetProperty("status").GetString());
        }
        Assert.Equal(1, await ActionsAsync(t, "pending"));
        Assert.Equal(0, await DraftsAsync(t, "Lift"));
    }

    [Fact]
    public async Task Without_the_setting_a_verified_client_or_a_client_that_can_ask_the_action_waits_in_TeamBeats()
    {
        var t = await FreshAsync();
        var (token, clientId) = await VerifiedAsync(t, confirmInClient: false);
        var asked = new List<string>();
        await using (var client = await ClientAsync(t, token, Answer(asked, "accept")))
        {
            Assert.Equal("pending_confirmation", Structured(await client.CallToolAsync("draft_news", Canteen)).GetProperty("content").GetProperty("status").GetString());
        }
        Assert.Empty(asked);

        // With the setting on, a client without an elicitation handler is not asked either.
        await PolicyAsync(t, clientId, confirmInClient: true);
        await using (var client = await ClientAsync(t, token, null))
        {
            var other = new Dictionary<string, object?> { ["title"] = "Parking", ["text"] = "Level 2." };
            Assert.Equal("pending_confirmation", Structured(await client.CallToolAsync("draft_news", other)).GetProperty("content").GetProperty("status").GetString());
        }
        Assert.Equal(2, await ActionsAsync(t, "pending"));
        Assert.Equal(0, await DraftsAsync(t, "Canteen menu"));

        // A client the tenant registered itself is never asked, even with the setting on.
        var (own, _, ownId) = await DelegatedAsync(world, t, "admin", ["content:read", "content:draft"], "mcp_client", id => PolicyAsync(t, id, true));
        await using (var client = await ClientAsync(t, own, Answer(asked, "accept")))
        {
            Assert.Equal("pending_confirmation", Structured(await client.CallToolAsync("draft_news", new Dictionary<string, object?> { ["title"] = "Gym", ["text"] = "Closed." }))
                .GetProperty("content").GetProperty("status").GetString());
        }
        Assert.Empty(asked);
        Assert.Equal(0, await DraftsAsync(t, "Gym"));
    }

    [Fact]
    public async Task An_answer_covers_only_the_pending_action_it_was_asked_for()
    {
        var t = await FreshAsync();
        var (token, _) = await VerifiedAsync(t, confirmInClient: true);
        await using (var client = await ClientAsync(t, token, Answer([], "cancel")))
        {
            await client.CallToolAsync("draft_news", Canteen);
        }
        var action = await world.Database.ScalarInTenantAsync<Guid>(t.Id, "select id from agents.external_action where status = 'pending'");

        // An answer sent with other arguments, or for an unknown action, decides nothing.
        async Task SendAsync(object arguments, string state)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0", id = 1, method = "tools/call",
                    @params = new { name = "draft_news", arguments, requestState = state, inputResponses = new { confirm = new { action = "accept", content = new { confirm = true } } } },
                }),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await world.Factory.TenantClient(t.Key).SendAsync(request)).StatusCode);
        }
        await SendAsync(new { title = "Canteen menu", text = "Changed." }, action.ToString("N"));
        await SendAsync(Canteen, Guid.NewGuid().ToString("N"));
        Assert.Equal("pending", await world.Database.ScalarInTenantAsync<string>(t.Id, "select status from agents.external_action where id = @id", ("id", action)));
        Assert.Equal(0, await DraftsAsync(t, "Canteen menu"));

        // Another tenant's server does not take the token at all.
        var other = await FreshAsync();
        var refused = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list", @params = new { } }) };
        refused.Headers.Accept.ParseAdd("application/json");
        refused.Headers.Accept.ParseAdd("text/event-stream");
        refused.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(other.Key).SendAsync(refused)).StatusCode);
    }
}
