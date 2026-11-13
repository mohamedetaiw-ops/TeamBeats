using System.Diagnostics;
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
/// Spec 035 T020 and section 15: token validation and authorization overhead under 10 ms at p95, MCP gateway overhead
/// under 100 ms at p95, and the token endpoint under 300 ms at p95. Each measure leaves the operation out by using a
/// call that ends before it: a token without the operation's scope (validation, app and tenant checks, limits, the
/// call log and the scope check, then 403, less the median time of the same request without a token), the MCP <c>tools/list</c> request (validation, external AI client policy, tool catalogue),
/// and the client credentials grant (no identity provider round trip). The MCP and token times include the in-process
/// HTTP pipeline, so they are an upper bound. Counts stay under the per app limits of a minute.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiPerformanceTests(TenantWorld world)
{
    private const int Warmup = 5;

    private async Task<TestTenant> TenantAsync()
    {
        var t = await world.CreateTenantAsync("aperf-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        await world.SeedUserAsync(t, "gia", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "aip", MembershipTypes.Employee);
        await world.AssignAsync(t, "aip", BuiltInRoles.AiPolicyAdministrator, "tenant", null);
        await world.SetCapabilityAsync(t, Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return t;
    }

    private static double P95(List<double> timings)
    {
        timings.Sort();
        return timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
    }

    private static double Median(List<double> timings) => timings.Order().ElementAt(timings.Count / 2);

    private static async Task<List<double>> TimeAsync(int calls, Func<int, Task> call)
    {
        var timings = new List<double>(calls);
        for (var i = 0; i < Warmup + calls; i++)
        {
            var watch = Stopwatch.StartNew();
            await call(i);
            watch.Stop();
            if (i >= Warmup)
            {
                timings.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
        return timings;
    }

    [Fact]
    public async Task Token_validation_and_authorization_stay_under_10_ms_at_p95()
    {
        const int calls = 200;
        var t = await TenantAsync();
        var token = await AppAsync(world, t, ["content:read"], "tenant", null);
        var client = world.Factory.TenantClient(t.Key);
        // The in-process transport, routing and problem answer, without a token: refused before any token work.
        var transport = await TimeAsync(calls, async _ =>
        {
            using var response = await client.GetAsync("/api/public/v1/people");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        });
        var timings = await TimeAsync(calls, async _ =>
        {
            using var response = await SendAsync(world, t, token, HttpMethod.Get, "/api/public/v1/people");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        });
        var baseline = Median(transport);
        var overhead = timings.Select(x => x - baseline).ToList();
        var p95 = P95(overhead);
        Assert.True(p95 < 10, $"p95 {p95:F1} ms over a transport of {baseline:F1} ms, median call {Median(timings):F1} ms");
    }

    [Fact]
    public async Task Mcp_gateway_stays_under_100_ms_at_p95()
    {
        var t = await TenantAsync();
        var (token, _, _) = await DelegatedAsync(world, t, "gia", ["content:read", "people:read"], "mcp_client",
            id => AllowExternalAsync(t, "aip", (id, "internal")));
        var client = world.Factory.TenantClient(t.Key);
        var timings = await TimeAsync(100, async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = i, method = "tools/list", @params = new { } }),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
            Assert.Contains("\"tools\"", text, StringComparison.Ordinal);
        });
        var p95 = P95(timings);
        Assert.True(p95 < 100, $"p95 {p95:F1} ms, median {timings[timings.Count / 2]:F1} ms");
    }

    [Fact]
    public async Task Token_endpoint_stays_under_300_ms_at_p95()
    {
        var t = await TenantAsync();
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Perf", kind = "application", scopes = new[] { "content:read" } }), HttpStatusCode.Created);
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{app.GetProperty("id").GetString()}/credentials", new { }),
            HttpStatusCode.Created)).GetProperty("secret").GetString()!;
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.GetProperty("clientId").GetString()!, ["client_secret"] = secret, ["scope"] = "content:read",
        };
        var client = world.Factory.TenantClient(t.Key);
        // 60 token requests a minute per app: stay under it.
        var timings = await TimeAsync(50, async _ =>
        {
            using var response = await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
            var body = await JsonAsync(response);
            Assert.Equal(JsonValueKind.String, body.GetProperty("access_token").ValueKind);
        });
        var p95 = P95(timings);
        Assert.True(p95 < 300, $"p95 {p95:F1} ms, median {timings[timings.Count / 2]:F1} ms");
    }
}
