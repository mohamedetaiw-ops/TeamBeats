using System.Net;
using System.Text.Json;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T019, US7 (slice 5c): the tenant's developer page lists where the public OpenAPI document, the authorization
/// server metadata and the MCP server are, and the operations, scopes, events, MCP tools and resources the package
/// includes, with any deprecations. API viewers and administrators only.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiDeveloperTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync(string package)
    {
        var t = await world.CreateTenantAsync("dev-" + Guid.NewGuid().ToString("N")[..6], package);
        await world.SeedUserAsync(t, "gia", MembershipTypes.Employee);
        return t;
    }

    private static async Task<JsonElement> PageAsync(TestTenant t) => await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/developer"));

    private static IEnumerable<string> Names(JsonElement page, string list, string field) =>
        page.GetProperty(list).EnumerateArray().Select(e => e.GetProperty(field).GetString()!);

    [Fact]
    public async Task The_developer_page_shows_the_addresses_and_what_the_package_includes()
    {
        var plus = await PageAsync(await FreshAsync("workplace_plus"));
        Assert.EndsWith("/openapi/public-v1.json", plus.GetProperty("openApiUrl").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/.well-known/oauth-authorization-server/oauth", plus.GetProperty("authorizationServerMetadataUrl").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/mcp", plus.GetProperty("mcpUrl").GetString(), StringComparison.Ordinal);
        var operations = plus.GetProperty("operations").EnumerateArray().Select(o => $"{o.GetProperty("method").GetString()} {o.GetProperty("path").GetString()}").ToList();
        Assert.Contains("GET /api/public/v1/org-units/{id:guid}", operations);
        Assert.Contains("POST /api/public/v1/requests", operations);
        Assert.Contains("GET /api/public/v1/events", operations);
        Assert.All(plus.GetProperty("operations").EnumerateArray(), o => Assert.NotNull(ScopeCatalogue.Find(o.GetProperty("scope").GetString()!)));
        Assert.Contains(ScopeCatalogue.EventsSubscribe, Names(plus, "scopes", "key"));
        Assert.Equal(PublicEventCatalogue.All.Select(e => e.Type), Names(plus, "events", "type"));
        var mcp = Names(plus, "mcp", "name").ToList();
        Assert.Contains("get_page", mcp);
        Assert.Contains("submit_service_request", mcp);
        Assert.Contains("teambeats://pages/{id}", mcp);
        // No public operation is deprecated yet.
        Assert.Empty(plus.GetProperty("deprecations").EnumerateArray());

        // Workplace has no write scopes, event subscriptions or MCP write tools, so the page leaves them out.
        var basic = await PageAsync(await FreshAsync("workplace"));
        var basicOperations = basic.GetProperty("operations").EnumerateArray().Select(o => $"{o.GetProperty("method").GetString()} {o.GetProperty("path").GetString()}").ToList();
        Assert.Contains("GET /api/public/v1/org-units/{id:guid}", basicOperations);
        Assert.DoesNotContain("POST /api/public/v1/requests", basicOperations);
        Assert.DoesNotContain("GET /api/public/v1/events", basicOperations);
        Assert.DoesNotContain(ScopeCatalogue.EventsSubscribe, Names(basic, "scopes", "key"));
        Assert.Empty(basic.GetProperty("events").EnumerateArray());
        Assert.DoesNotContain("submit_service_request", Names(basic, "mcp", "name"));
        Assert.Contains("get_page", Names(basic, "mcp", "name"));
    }

    [Fact]
    public async Task Only_api_viewers_open_the_developer_page_and_the_public_document_is_on_the_tenant_host()
    {
        var t = await FreshAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gia"].Client.GetAsync("/api/v1/api-access/developer")).StatusCode);

        var document = await world.Factory.TenantClient(t.Key).GetAsync("/openapi/public-v1.json");
        var json = await JsonAsync(document);
        Assert.True(json.GetProperty("paths").TryGetProperty("/api/public/v1/org-units/{id}", out _));
        Assert.DoesNotContain(json.GetProperty("paths").EnumerateObject(), p => !p.Name.StartsWith("/api/public/v1/", StringComparison.Ordinal));
    }
}
