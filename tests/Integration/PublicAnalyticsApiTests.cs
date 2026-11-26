using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 021 R3 (T025): the analytics API for BI tools. A person acting through a delegated app reads the metric catalogue
/// and runs the workspace query with their own analytics rights, so the answer is the workspace's own; the scope needs an
/// administrator's consent, a person without analytics rights gets nothing, the package must have the analytics API, and
/// another tenant refuses the token.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicAnalyticsApiTests(TenantWorld world)
{
    private const string Scope = "analytics:read";
    private const string Metrics = "/api/public/v1/analytics/metrics";
    private const string Query = "/api/public/v1/analytics/query";

    private async Task<TestTenant> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("pan-" + Guid.NewGuid().ToString("N")[..6], package);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        return t;
    }

    private async Task<string> TokenAsync(TestTenant t, string user)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "BI desk " + Guid.NewGuid().ToString("N")[..4], kind = "delegated_public", scopes = new[] { Scope }, redirectUris = new[] { Redirect }, publisher = "Acme BI",
        }), HttpStatusCode.Created);
        var clientId = app.GetProperty("clientId").GetString()!;
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.GetProperty("id").GetString()}/admin-consent", new { scopes = new[] { Scope } }));
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var authorize = await AuthorizeAsync(t, user, clientId, [Scope], verifier);
        Assert.True(authorize.StatusCode == HttpStatusCode.Redirect, $"{(int)authorize.StatusCode}: {await authorize.Content.ReadAsStringAsync()}");
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var token = await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        }));
        return (await JsonAsync(token)).GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> CallAsync(TestTenant t, string token, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    [Fact]
    public async Task An_analyst_reads_the_catalogue_and_the_same_answer_as_the_workspace()
    {
        var t = await FreshAsync();
        var token = await TokenAsync(t, "admin");

        var catalogue = await JsonAsync(await CallAsync(t, token, HttpMethod.Get, Metrics));
        Assert.True(catalogue.GetProperty("minGroupSize").GetInt32() >= 1);
        Assert.Contains(catalogue.GetProperty("metrics").EnumerateArray(), m => m.GetProperty("key").GetString() == Modules.Analytics.Workspace.ActivePeople);

        var query = new { metric = Modules.Analytics.Workspace.ActivePeople, groupBy = "channel" };
        var viaApi = await JsonAsync(await CallAsync(t, token, HttpMethod.Post, Query, query));
        var inApp = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/analytics/query", query));
        Assert.Equal(inApp.GetRawText(), viaApi.GetRawText());

        // A metric outside the catalogue is refused as in the workspace.
        Assert.Equal(HttpStatusCode.BadRequest, (await CallAsync(t, token, HttpMethod.Post, Query, new { metric = "raw_events" })).StatusCode);
    }

    [Fact]
    public async Task Without_analytics_rights_the_scope_the_package_or_the_tenant_the_api_refuses()
    {
        var t = await FreshAsync();

        // A person without analytics rights gets nothing, even with a consented app.
        var employee = await TokenAsync(t, "emp");
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, employee, HttpMethod.Get, Metrics)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, employee, HttpMethod.Post, Query, new { metric = Modules.Analytics.Workspace.ActivePeople })).StatusCode);

        // Without the scope the token reads nothing here, and an application app cannot hold the scope.
        var other = await PersonAsync(world, t, "admin", ["profile:read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, other, HttpMethod.Get, Metrics)).StatusCode);
        var application = await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new { name = "Robot", kind = "application", scopes = new[] { Scope } });
        Assert.Equal(HttpStatusCode.BadRequest, application.StatusCode);

        // An employee cannot consent to it alone.
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "BI", kind = "delegated_public", scopes = new[] { Scope }, redirectUris = new[] { Redirect },
        }), HttpStatusCode.Created);
        var consent = await t["emp"].Client.PostAsJsonAsync("/api/v1/me/app-consent",
            new { clientId = app.GetProperty("clientId").GetString(), redirectUri = Redirect, scopes = new[] { Scope } });
        Assert.False(consent.IsSuccessStatusCode);

        // Another tenant refuses the token.
        var g = await FreshAsync();
        var token = await TokenAsync(t, "admin");
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(g, token, HttpMethod.Get, Metrics)).StatusCode);

        // A package without the analytics API refuses the scope.
        var plus = await FreshAsync("workplace_plus");
        var refused = await plus["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "BI", kind = "delegated_public", scopes = new[] { Scope }, redirectUris = new[] { Redirect },
        });
        Assert.False(refused.IsSuccessStatusCode, await refused.Content.ReadAsStringAsync());
    }
}
