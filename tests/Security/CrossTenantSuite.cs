using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Security.Tests;

/// <summary>
/// Enumerates every tenant API route (spec 004 SC, constitution I) and calls it as tenant A's administrator with
/// identifiers that belong to tenant B. No call may succeed. New routes are covered automatically.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class CrossTenantSuite(TenantWorld world, ITestOutputHelper output)
{
    /// <summary>Routes reachable without a session, by design.</summary>
    private static readonly HashSet<string> Anonymous =
    [
        "POST /api/v1/auth/signout",
        "GET /api/v1/tenant/branding/logo",
        "GET /api/v1/auth/signin/options",
        "GET /api/v1/auth/signin/sso",
        "GET /api/v1/auth/signin/sso/callback",
    ];

    private IReadOnlyList<(string Method, string Pattern)> TenantRoutes() =>
        world.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } p && p.StartsWith("/api/v1/", StringComparison.Ordinal) && !p.StartsWith("/api/v1/platform", StringComparison.Ordinal))
            .SelectMany(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (m, e.RoutePattern.RawText!)))
            .Distinct()
            .ToList();

    private async Task<Dictionary<string, Guid>> ForeignIdsAsync()
    {
        var b = world.B["admin"].Client;
        var memberships = await b.GetFromJsonAsync<JsonElement>($"/api/v1/org/units/{world.B.Unit("Gulf")}/memberships");
        var assignments = await b.GetFromJsonAsync<JsonElement>("/api/v1/authz/assignments");
        var group = await (await b.PostAsJsonAsync("/api/v1/authz/groups", new { key = "b-secret-" + Guid.NewGuid().ToString("N")[..6], name = "B" })).ReadAsync<JsonElement>();
        var export = await (await b.PostAsJsonAsync("/api/v1/audit/exports", new { format = "csv" })).ReadAsync<JsonElement>();
        return new Dictionary<string, Guid>
        {
            ["units"] = world.B.Unit("Gulf"),
            ["users"] = world.B["employee_gulf"].Id,
            ["memberships"] = memberships[0].GetProperty("id").GetGuid(),
            ["assignments"] = assignments[0].GetProperty("id").GetGuid(),
            ["groups"] = group.GetProperty("id").GetGuid(),
            ["exports"] = export.GetProperty("id").GetGuid(),
            ["resources"] = world.Factory.Documents.Add(world.B.Id, world.B.Unit("Gulf"), ResourceVisibility.Restricted, "b secret"),
        };
    }

    private static string Fill(string pattern, Dictionary<string, Guid> ids)
    {
        var segments = pattern.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            if (!s.StartsWith('{'))
            {
                continue;
            }
            var name = s.Trim('{', '}').Split(':')[0];
            var suffix = s.Contains("}:") ? s[(s.IndexOf("}:", StringComparison.Ordinal) + 1)..] : string.Empty;
            var collection = i > 0 ? segments[i - 1] : string.Empty;
            segments[i] = name switch
            {
                "type" => TestDocuments.ResourceType,
                "membershipType" => "contractor",
                _ when collection == TestDocuments.ResourceType => (ids.TryGetValue("resources", out var r) ? r : Guid.NewGuid()) + suffix,
                _ => (ids.TryGetValue(collection, out var id) ? id : Guid.NewGuid()) + suffix,
            };
        }
        return string.Join('/', segments);
    }

    [Fact]
    public async Task No_tenant_route_succeeds_with_identifiers_of_another_tenant()
    {
        var ids = await ForeignIdsAsync();
        var client = world.A["admin"].Client;
        var leaks = new List<string>();
        // Only routes that take an object identifier can address another tenant's data.
        var routes = TenantRoutes().Where(r => r.Pattern.Contains(":guid}", StringComparison.Ordinal)).ToList();
        Assert.True(routes.Count >= 15, $"Expected the route registry to be populated, found {routes.Count}.");

        foreach (var (method, pattern) in routes)
        {
            var url = Fill(pattern, ids);
            using var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (method is "POST" or "PUT" or "PATCH")
            {
                request.Content = new StringContent(pattern.EndsWith("/acl", StringComparison.Ordinal) ? "[]" : "{}", Encoding.UTF8, "application/json");
            }
            var response = await client.SendAsync(request);
            output.WriteLine($"{method} {url} -> {(int)response.StatusCode}");
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.InternalServerError)
            {
                leaks.Add($"{method} {pattern} -> {(int)response.StatusCode}");
            }
        }
        Assert.True(leaks.Count == 0, "Cross-tenant calls that did not fail safely:\n" + string.Join('\n', leaks));
    }

    [Fact]
    public async Task Every_tenant_route_requires_a_session()
    {
        var anonymous = world.Factory.TenantClient(world.A.Key);
        var open = new List<string>();
        foreach (var (method, pattern) in TenantRoutes())
        {
            if (Anonymous.Contains($"{method} {pattern}"))
            {
                continue;
            }
            using var request = new HttpRequestMessage(new HttpMethod(method), Fill(pattern, new Dictionary<string, Guid>()));
            if (method is "POST" or "PUT" or "PATCH")
            {
                request.Content = new StringContent(pattern.EndsWith("/acl", StringComparison.Ordinal) ? "[]" : "{}", Encoding.UTF8, "application/json");
            }
            var response = await anonymous.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                open.Add($"{method} {pattern} -> {(int)response.StatusCode}");
            }
        }
        Assert.True(open.Count == 0, "Routes reachable without a session:\n" + string.Join('\n', open));
    }

    [Fact]
    public async Task Platform_routes_are_invisible_on_tenant_hosts()
    {
        var response = await world.A["admin"].Client.GetAsync("/api/v1/platform/tenants");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_routes_are_invisible_on_the_platform_host()
    {
        var response = await world.Platform.GetAsync("/api/v1/tenant/settings");
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, response.StatusCode.ToString());
    }
}
