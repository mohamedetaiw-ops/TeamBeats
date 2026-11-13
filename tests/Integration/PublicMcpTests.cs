using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T013 and T014, US3: the tenant's MCP server for external AI clients. A client on the organization's list,
/// acting for an employee, sees only the tools its scopes cover and calls them through the agent tool gateway: what goes
/// back carries its sources and classification, nothing above the client's cap leaves, and every call leaves an
/// execution record. A client off the list is refused at sign in, the kill switch empties the server, other tokens and
/// other tenants are refused, and the call limits hold.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicMcpTests(TenantWorld world)
{
    private sealed record Mcp(string Token, Guid AppId, string ClientId);

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("mcp-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        foreach (var name in new[] { "gia", "aip", "sec" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private static Task AllowAsync(TestTenant t, params (string ClientId, string Cap)[] clients) => AllowExternalAsync(t, "aip", clients);

    private async Task<Mcp> ClientAsync(TestTenant t, string user, string[] scopes, string cap = "internal")
    {
        var (token, appId, clientId) = await DelegatedAsync(world, t, user, scopes, "mcp_client", id => AllowAsync(t, (id, cap)));
        return new Mcp(token, appId, clientId);
    }

    private Task<HttpResponseMessage> PostMcpAsync(TestTenant t, string? token, string method, object? parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method, @params = parameters ?? new { } }),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    /// <summary>The JSON-RPC result of one MCP request, read from a JSON body or the message event of a stream.</summary>
    private async Task<JsonElement> RpcAsync(TestTenant t, string token, string method, object? parameters = null)
    {
        var response = await PostMcpAsync(t, token, method, parameters);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        var json = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? string.Join('\n', text.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l[5..].Trim()))
            : text;
        var message = JsonDocument.Parse(json).RootElement.Clone();
        Assert.False(message.TryGetProperty("error", out var error), error.ToString());
        return message.GetProperty("result");
    }

    private async Task<IReadOnlyList<string>> ToolNamesAsync(TestTenant t, string token) =>
        (await RpcAsync(t, token, "tools/list")).GetProperty("tools").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList();

    /// <summary>The structured content of a tool call: ok, error, classification, content and sources.</summary>
    private async Task<JsonElement> CallAsync(TestTenant t, string token, string tool, object? arguments = null)
    {
        var result = await RpcAsync(t, token, "tools/call", new { name = tool, arguments = arguments ?? new { } });
        var structured = result.GetProperty("structuredContent");
        Assert.Equal(!structured.GetProperty("ok").GetBoolean(), result.TryGetProperty("isError", out var e) && e.GetBoolean());
        return structured;
    }

    private static string? Error(JsonElement call) => call.GetProperty("error").ValueKind == JsonValueKind.Null ? null : call.GetProperty("error").GetString();

    private static async Task<Guid> NewsAsync(TestTenant t, string title, string classification = "internal")
    {
        var item = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/items", new
        {
            type = "news", classification,
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title, summary = "Summary", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title + " body" } } } } } },
            },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/content/items/{item}/publish", new { material = true }));
        return item;
    }

    private Task<long> RecordsAsync(TestTenant t, Mcp client, string tool, string decision) =>
        world.Database.CountInTenantAsync(t.Id, "select count(*) from agents.external_call where app_id = @a and tool = @tool and decision = @d",
            ("a", client.AppId), ("tool", tool), ("d", decision));

    [Fact]
    public async Task A_listed_client_sees_and_calls_only_the_tools_its_scopes_cover_and_nothing_above_its_cap_goes_back()
    {
        var t = await FreshAsync();
        var open = await NewsAsync(t, "Canteen opens");
        var secret = await NewsAsync(t, "Merger plans", "confidential");
        var client = await ClientAsync(t, "admin", ["profile:read", "content:read"]);

        Assert.Equal(["get_page", "list_news", "whoami"], await ToolNamesAsync(t, client.Token));
        var listed = (await RpcAsync(t, client.Token, "tools/list")).GetProperty("tools").EnumerateArray().First();
        Assert.True(listed.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        Assert.Equal("object", listed.GetProperty("inputSchema").GetProperty("type").GetString());

        var me = await CallAsync(t, client.Token, "whoami");
        Assert.Equal(t["admin"].Id, me.GetProperty("content").GetProperty("id").GetGuid());

        // The feed leaves out what is above the cap; the page comes back as data with its absolute source link.
        var news = await CallAsync(t, client.Token, "list_news");
        var ids = news.GetProperty("content").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(open, ids);
        Assert.DoesNotContain(secret, ids);
        var page = await CallAsync(t, client.Token, "get_page", new { id = open });
        Assert.Equal("Canteen opens", page.GetProperty("content").GetProperty("title").GetString());
        Assert.Equal("internal", page.GetProperty("classification").GetString());
        var source = page.GetProperty("sources").EnumerateArray().Single();
        Assert.Equal(open, source.GetProperty("id").GetGuid());
        Assert.StartsWith("http", source.GetProperty("link").GetString(), StringComparison.Ordinal);
        Assert.EndsWith($"/content/read/{open}", source.GetProperty("link").GetString(), StringComparison.Ordinal);

        // A Confidential page is blocked under an Internal cap, and is the client's to read once the cap allows it.
        var blocked = await CallAsync(t, client.Token, "get_page", new { id = secret });
        Assert.Equal("tool.classification_blocked", Error(blocked));
        Assert.Equal(JsonValueKind.Null, blocked.GetProperty("content").ValueKind);

        // A tool outside the scopes is unknown; arguments outside the schema are refused before the tool runs.
        Assert.Equal("tool.unknown", Error(await CallAsync(t, client.Token, "get_document", new { id = open })));
        Assert.Equal("tool.unknown", Error(await CallAsync(t, client.Token, "draft_news", new { title = "x" })));
        Assert.Equal("tool.invalid_arguments", Error(await CallAsync(t, client.Token, "get_page", new { id = "short" })));
        Assert.Equal("tool.invalid_arguments", Error(await CallAsync(t, client.Token, "get_page", new { id = open, extra = 1 })));

        // A page that does not exist is denied, as the employee could not read it either.
        Assert.Equal("tool.denied", Error(await CallAsync(t, client.Token, "get_page", new { id = Guid.NewGuid() })));

        await AllowAsync(t, (client.ClientId, "confidential"));
        var allowed = await CallAsync(t, client.Token, "get_page", new { id = secret });
        Assert.Null(Error(allowed));
        Assert.Equal("confidential", allowed.GetProperty("classification").GetString());

        // Each call left an execution record; what went back Confidential has its parameters masked.
        Assert.Equal(2, await RecordsAsync(t, client, "get_page", "executed"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from agents.external_call where app_id = @a and tool = 'get_page' and classification = 'confidential' and parameters->>'id' = '***'",
            ("a", client.AppId)));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from agents.external_call where app_id = @a and reason = 'tool.classification_blocked' and parameters->>'id' = @id and user_id = @u",
            ("a", client.AppId), ("id", secret.ToString()), ("u", t["admin"].Id)));
        Assert.Equal(1, await RecordsAsync(t, client, "get_document", "refused"));
        Assert.Equal(1, await RecordsAsync(t, client, "draft_news", "refused"));
    }

    [Fact]
    public async Task Every_read_tool_runs_through_its_owning_module()
    {
        var t = await FreshAsync();
        await NewsAsync(t, "Forklift safety week");
        string[] scopes = ["profile:read", "content:read", "documents:read", "people:read", "policies:read", "search:query", "services:read", "requests:read", "tasks:read"];
        var client = await ClientAsync(t, "gia", scopes);
        Assert.Equal(["find_person", "get_document", "get_page", "get_policy", "list_my_requests", "list_my_tasks", "list_news", "list_services", "search", "whoami"],
            await ToolNamesAsync(t, client.Token));

        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        var found = await CallAsync(t, client.Token, "search", new { query = "forklift" });
        Assert.Null(Error(found));
        Assert.Contains(found.GetProperty("sources").EnumerateArray(), s => s.GetProperty("title").GetString() == "Forklift safety week");
        var person = await CallAsync(t, client.Token, "find_person", new { query = "sec" });
        Assert.Null(Error(person));
        foreach (var tool in new[] { "list_services", "list_my_requests", "list_my_tasks" })
        {
            Assert.Null(Error(await CallAsync(t, client.Token, tool)));
        }
        Assert.Equal("tool.denied", Error(await CallAsync(t, client.Token, "get_document", new { id = Guid.NewGuid() })));
        Assert.Equal("tool.denied", Error(await CallAsync(t, client.Token, "get_policy", new { id = Guid.NewGuid() })));
    }

    [Fact]
    public async Task The_employees_own_rights_bound_what_the_client_reads()
    {
        var t = await FreshAsync();
        var secret = await NewsAsync(t, "Board minutes", "confidential");
        var client = await ClientAsync(t, "gia", ["content:read"], "confidential");
        // Gia may not read the Confidential page in TeamBeats, so the client may not read it for them.
        Assert.Equal("tool.denied", Error(await CallAsync(t, client.Token, "get_page", new { id = secret })));
        Assert.Equal(1, await RecordsAsync(t, client, "get_page", "refused"));
    }

    [Fact]
    public async Task The_kill_switch_and_the_external_policy_empty_the_server()
    {
        var t = await FreshAsync();
        var item = await NewsAsync(t, "Parking update");
        var client = await ClientAsync(t, "gia", ["content:read"]);
        Assert.Null(Error(await CallAsync(t, client.Token, "get_page", new { id = item })));

        // Taking the tool off the external tools list hides it.
        var current = await JsonAsync(await t["aip"].Client.GetAsync("/api/v1/agents/policy"));
        var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.GetProperty("policy").GetRawText())!;
        body["external"] = new { enabled = true, clients = new[] { new { clientId = client.ClientId, maxClassification = "internal" } }, allowedTools = new[] { "list_news" } };
        await JsonAsync(await t["aip"].Client.PutAsJsonAsync("/api/v1/agents/policy", new { version = current.GetProperty("version").GetInt32(), policy = body }));
        Assert.Equal(["list_news"], await ToolNamesAsync(t, client.Token));
        Assert.Equal("tool.unknown", Error(await CallAsync(t, client.Token, "get_page", new { id = item })));
        await AllowAsync(t, (client.ClientId, "internal"));

        await JsonAsync(await t["aip"].Client.PutAsJsonAsync("/api/v1/agents/kill-switch", new { active = true, confirmation = $"{t.Key} holding", reason = "Leak" }));
        Assert.Empty(await ToolNamesAsync(t, client.Token));
        Assert.Equal("external.kill_switch", Error(await CallAsync(t, client.Token, "get_page", new { id = item })));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a",
            ("a", AuditActions.AgentExternalCallRefused)));

        await JsonAsync(await t["aip"].Client.PutAsJsonAsync("/api/v1/agents/kill-switch", new { active = false }));
        Assert.Equal(["get_page", "list_news"], await ToolNamesAsync(t, client.Token));

        // Turning external AI access off refuses every client.
        current = await JsonAsync(await t["aip"].Client.GetAsync("/api/v1/agents/policy"));
        body = JsonSerializer.Deserialize<Dictionary<string, object?>>(current.GetProperty("policy").GetRawText())!;
        body["external"] = new { enabled = false, clients = new[] { new { clientId = client.ClientId, maxClassification = "internal" } } };
        await JsonAsync(await t["aip"].Client.PutAsJsonAsync("/api/v1/agents/policy", new { version = current.GetProperty("version").GetInt32(), policy = body }));
        Assert.Equal("external.access_off", Error(await CallAsync(t, client.Token, "get_page", new { id = item })));
    }

    [Fact]
    public async Task A_client_off_the_list_is_refused_at_sign_in()
    {
        var t = await FreshAsync();
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Desktop AI", kind = "mcp_client", scopes = new[] { "content:read" }, redirectUris = new[] { Redirect }, publisher = "Vendor",
        }), HttpStatusCode.Created);
        var clientId = app.GetProperty("clientId").GetString()!;
        await JsonAsync(await t["gia"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes = new[] { "content:read" } }));
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var refused = await AuthorizeAsync(t, "gia", clientId, ["content:read"], verifier);
        var query = refused.StatusCode == HttpStatusCode.Redirect
            ? QueryHelpers.ParseQuery(refused.Headers.Location!.Query)
            : QueryHelpers.ParseQuery("?" + await refused.Content.ReadAsStringAsync());
        Assert.True(query.ContainsKey("error"), $"{(int)refused.StatusCode} {refused.Headers.Location}");
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.False(query.ContainsKey("code"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from audit.audit_event where action = @a and target_id = @id", ("a", AuditActions.AgentExternalCallRefused), ("id", app.GetProperty("id").GetString()!)));
    }

    [Fact]
    public async Task Other_tokens_other_tenants_and_a_closed_server_are_refused()
    {
        var t = await FreshAsync();
        var client = await ClientAsync(t, "gia", ["content:read"]);

        // No token: 401 naming the protected resource metadata, which names the tenant's authorization server.
        var anonymous = await PostMcpAsync(t, null, "tools/list", null);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("/.well-known/oauth-protected-resource/mcp", anonymous.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
        var metadata = await JsonAsync(await world.Factory.TenantClient(t.Key).GetAsync("/.well-known/oauth-protected-resource/mcp"));
        Assert.EndsWith("/mcp", metadata.GetProperty("resource").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/oauth", metadata.GetProperty("authorization_servers")[0].GetString(), StringComparison.Ordinal);
        var supported = metadata.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()).ToList();
        Assert.Contains("content:read", supported);
        Assert.DoesNotContain("org:read", supported);

        // A REST API token, delegated or an application's, is not for this resource.
        var person = await PersonAsync(world, t, "gia", ["content:read"]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(t, person, "tools/list", null)).StatusCode);
        var application = await AppAsync(world, t, ["content:read"], "tenant", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(t, application, "tools/list", null)).StatusCode);
        // And the MCP token is not for the REST API.
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(world, t, client.Token, HttpMethod.Get, "/api/public/v1/news")).StatusCode);

        // Another tenant does not accept the token.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostMcpAsync(other, client.Token, "tools/list", null)).StatusCode);

        // Without the MCP capability the server and its metadata are gone.
        await world.SetCapabilityAsync(t, Capabilities.ApiMcp, new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.TenantClient(t.Key).GetAsync("/.well-known/oauth-protected-resource/mcp")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await PostMcpAsync(t, client.Token, "tools/list", null)).StatusCode);
    }

    [Fact]
    public async Task The_call_limits_hold_per_employee_and_client()
    {
        var t = await FreshAsync();
        var item = await NewsAsync(t, "Gym hours");
        var client = await ClientAsync(t, "gia", ["content:read"]);
        // Sixty calls in the last minute, as the execution records count them.
        await world.Database.OwnerExecAsync(
            """
            select set_config('app.tenant_id', @t::text, false);
            insert into agents.external_call (tenant_id, id, app_id, client_id, user_id, tool, decision, at)
            select @t, gen_random_uuid(), @a, @c, @u, 'list_news', 'executed', now() - interval '10 seconds' from generate_series(1, 60)
            """, ("t", t.Id), ("a", client.AppId), ("c", client.ClientId), ("u", t["gia"].Id));
        Assert.Equal("tool.rate_limited", Error(await CallAsync(t, client.Token, "get_page", new { id = item })));
        Assert.Equal(1, await RecordsAsync(t, client, "get_page", "refused"));

        // Another employee with the same client has their own limit.
        await JsonAsync(await t["sec"].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId = client.ClientId, redirectUri = Redirect, scopes = new[] { "content:read" } }));
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = await AuthorizeAsync(t, "sec", client.ClientId, ["content:read"], verifier);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = client.ClientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        })));
        Assert.Null(Error(await CallAsync(t, tokens.GetProperty("access_token").GetString()!, "get_page", new { id = item })));
    }
}
