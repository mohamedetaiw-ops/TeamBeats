using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 phase 3 (T008, T009): the request pipeline of the public API. Calls over an app's limit get 429 with
/// Retry-After and rate limit headers and are counted, not processed; a tenant may only lower limits; the monthly quota
/// stops API calls, not the web app, and API administrators are told at 80% and 100%; the tenant switch closes the API
/// at once; an app limited to address ranges is refused anywhere else; and the usage page shows each tenant only its own.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicApiPipelineTests(TenantWorld world)
{
    private const string Office = "203.0.113.5";
    private const string Outside = "198.51.100.7";

    private Task<TestTenant> FreshAsync() => world.CreateTenantAsync("pl-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var errors = (await JsonAsync(response, HttpStatusCode.BadRequest)).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
    }

    private sealed record App(string Id, string ClientId, string Secret);

    private static async Task<App> AppAsync(TestTenant t, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["name"] = "Finance sync", ["kind"] = "application", ["scopes"] = new[] { "content:read" } };
        foreach (var p in extra?.GetType().GetProperties() ?? [])
        {
            body[p.Name] = p.GetValue(extra);
        }
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", body), HttpStatusCode.Created);
        var id = app.GetProperty("id").GetString()!;
        var issued = await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created);
        return new App(id, app.GetProperty("clientId").GetString()!, issued.GetProperty("secret").GetString()!);
    }

    private HttpClient From(TestTenant t, string? address)
    {
        var client = world.Factory.TenantClient(t.Key);
        if (address is not null)
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", address);
        }
        return client;
    }

    private Task<HttpResponseMessage> TokenResponseAsync(TestTenant t, App app, string? address = null) =>
        From(t, address).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.ClientId, ["client_secret"] = app.Secret, ["scope"] = "content:read",
        }));

    private async Task<string> TokenAsync(TestTenant t, App app, string? address = null) =>
        (await JsonAsync(await TokenResponseAsync(t, app, address))).GetProperty("access_token").GetString()!;

    private Task<HttpResponseMessage> CallAsync(TestTenant t, string token, string? address = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return From(t, address).SendAsync(request);
    }

    private Task<long> LoggedAsync(TestTenant t, string appId, string errorClass) => world.Database.CountInTenantAsync(t.Id,
        "select count(*) from publicapi.call_log where app_id = @a::uuid and error_class = @e", ("a", appId), ("e", errorClass));

    [Fact]
    public async Task Calls_over_the_app_limit_get_429_with_headers_and_are_counted_not_processed()
    {
        var t = await FreshAsync();
        var app = await AppAsync(t, new { callsPerMinute = 2 });
        var other = await AppAsync(t);
        var token = await TokenAsync(t, app);

        // /me needs profile:read, which an application app never holds: the call is refused, but processed and logged.
        var first = await CallAsync(t, token);
        Assert.Equal(HttpStatusCode.Forbidden, first.StatusCode);
        Assert.Equal("2", first.Headers.GetValues("RateLimit-Limit").Single());
        Assert.Equal("1", first.Headers.GetValues("RateLimit-Remaining").Single());
        Assert.InRange(int.Parse(first.Headers.GetValues("RateLimit-Reset").Single()), 1, 60);
        // The third call in a minute is over the limit; should the minute turn between calls, the next window fills up too.
        var third = await CallAsync(t, token);
        for (var i = 0; i < 3 && third.StatusCode != HttpStatusCode.TooManyRequests; i++)
        {
            third = await CallAsync(t, token);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.InRange(int.Parse(third.Headers.RetryAfter!.ToString()), 1, 60);
        Assert.Equal("0", third.Headers.GetValues("RateLimit-Remaining").Single());
        var problem = await JsonAsync(third, HttpStatusCode.TooManyRequests);
        Assert.Equal("api.rate_limited", problem.GetProperty("code").GetString());
        Assert.Equal("app", problem.GetProperty("limit").GetString());
        Assert.True(await LoggedAsync(t, app.Id, "rate_limited") >= 1);
        Assert.True(await LoggedAsync(t, app.Id, "denied") >= 2);
        // Only processed calls count for the month.
        Assert.Equal(await LoggedAsync(t, app.Id, "denied"),
            await world.Database.CountInTenantAsync(t.Id, "select coalesce(sum(calls), 0) from publicapi.usage_month"));

        // Another app has its own limit.
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, await TokenAsync(t, other))).StatusCode);
        // The call log keeps no body and no query string, only the operation's route.
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from publicapi.call_log where operation <> 'GET /api/public/v1/me'"));
    }

    [Fact]
    public async Task A_tenant_may_lower_limits_but_never_raise_them_above_the_platform()
    {
        var t = await FreshAsync();
        Assert.Contains("validation.api.calls_per_minute", await ErrorKeysAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Too fast", kind = "application", scopes = new[] { "content:read" }, callsPerMinute = 601 })));
        var app = await AppAsync(t, new { callsPerMinute = 100 });
        var view = await JsonAsync(await t["admin"].Client.GetAsync($"/api/v1/api-access/apps/{app.Id}"));
        Assert.Equal(100, view.GetProperty("app").GetProperty("callsPerMinute").GetInt32());
        Assert.True(view.GetProperty("app").GetProperty("callsPerMinuteLowered").GetBoolean());
        Assert.Contains("validation.api.calls_per_minute", await ErrorKeysAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.Id}",
            new { callsPerMinute = 3000 })));
        // 0 returns the app to the platform default.
        var reset = await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.Id}", new { callsPerMinute = 0 }));
        Assert.Equal(600, reset.GetProperty("callsPerMinute").GetInt32());
        Assert.False(reset.GetProperty("callsPerMinuteLowered").GetBoolean());

        Assert.Contains("validation.api.tenant_calls_per_minute", await ErrorKeysAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings",
            new { tenantCallsPerMinute = 6001 })));
        var settings = await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { tenantCallsPerMinute = 1 }));
        Assert.Equal(1, settings.GetProperty("tenantCallsPerMinute").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiSettingsChanged, null);

        // The tenant's own limit covers all its apps together.
        var token = await TokenAsync(t, app);
        var calls = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
        {
            calls.Add(await CallAsync(t, token));
        }
        var limited = calls.First(c => c.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("tenant", (await JsonAsync(limited, HttpStatusCode.TooManyRequests)).GetProperty("limit").GetString());
    }

    [Fact]
    public async Task The_monthly_quota_stops_api_calls_only_and_api_administrators_are_told_at_80_and_100_percent()
    {
        var t = await FreshAsync();
        await world.SeedUserAsync(t, "integrator", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        await world.AssignAsync(t, "integrator", "integration_administrator", "tenant", null);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.ApiMonthlyCalls, new { enabled = true, limit = 5 });
        var app = await AppAsync(t);
        var token = await TokenAsync(t, app);

        long Notified(string template) => world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = @k and link = '/admin/api?tab=usage'",
            ("u", t["integrator"].Id), ("k", template)).GetAwaiter().GetResult();
        for (var i = 1; i <= 3; i++)
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await CallAsync(t, token)).StatusCode);
        }
        Assert.Equal(0, Notified(CallLog.Quota80Template));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await CallAsync(t, token)).StatusCode);
        Assert.Equal(1, Notified(CallLog.Quota80Template));
        Assert.Equal(0, Notified(CallLog.Quota100Template));
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await CallAsync(t, token)).StatusCode);
        Assert.Equal(1, Notified(CallLog.Quota100Template));

        var refused = await CallAsync(t, token);
        var problem = await JsonAsync(refused, HttpStatusCode.TooManyRequests);
        Assert.Equal("entitlement.quota_exhausted", problem.GetProperty("code").GetString());
        Assert.Equal("api.monthly_calls", problem.GetProperty("capability").GetString());
        Assert.True(long.Parse(refused.Headers.RetryAfter!.ToString()) > 0);
        Assert.Equal(1, await LoggedAsync(t, app.Id, "quota"));
        // Each alert is sent once a month, and the web app keeps working.
        await CallAsync(t, token);
        Assert.Equal(1, Notified(CallLog.Quota100Template));
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.GetAsync("/api/v1/api-access/apps")).StatusCode);

        var usage = await JsonAsync(await t["integrator"].Client.GetAsync("/api/v1/api-access/usage"));
        Assert.Equal(5, usage.GetProperty("monthCalls").GetInt64());
        Assert.Equal(5, usage.GetProperty("monthlyLimit").GetInt64());
    }

    [Fact]
    public async Task The_tenant_switch_closes_the_api_at_once()
    {
        var t = await FreshAsync();
        var app = await AppAsync(t);
        var token = await TokenAsync(t, app);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(t, token)).StatusCode);
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { apiEnabled = false, reason = "Leaked secret under review" }));
        var closed = await CallAsync(t, token);
        Assert.Equal("entitlement.not_in_package", (await JsonAsync(closed, HttpStatusCode.Forbidden)).GetProperty("code").GetString());
        Assert.NotEqual(HttpStatusCode.OK, (await TokenResponseAsync(t, app)).StatusCode);
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync("/api/v1/api-access/settings", new { apiEnabled = true }));
        Assert.Equal("api.insufficient_scope", (await JsonAsync(await CallAsync(t, token), HttpStatusCode.Forbidden)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_app_limited_to_address_ranges_is_refused_from_anywhere_else()
    {
        var t = await FreshAsync();
        Assert.Contains("validation.api.ip_ranges", await ErrorKeysAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Bad ranges", kind = "application", scopes = new[] { "content:read" }, ipRanges = new[] { "not a range" } })));
        var app = await AppAsync(t, new { ipRanges = new[] { "203.0.113.0/24" } });

        // Tokens only from the ranges.
        var outsideToken = await TokenResponseAsync(t, app, Outside);
        Assert.Equal(HttpStatusCode.BadRequest, outsideToken.StatusCode);
        Assert.Contains("unauthorized_client", await outsideToken.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var token = await TokenAsync(t, app, Office);

        // Calls only from the ranges; the refusal is audited and logged.
        var refused = await JsonAsync(await CallAsync(t, token, Outside), HttpStatusCode.Forbidden);
        Assert.Equal("security.network_not_allowed", refused.GetProperty("code").GetString());
        Assert.Equal("app", refused.GetProperty("area").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ApiTokenRefused, app.Id);
        Assert.Equal("api.insufficient_scope", (await JsonAsync(await CallAsync(t, token, Office), HttpStatusCode.Forbidden)).GetProperty("code").GetString());

        // Removing the ranges opens the app to any address the tenant allows.
        await JsonAsync(await t["admin"].Client.PutAsJsonAsync($"/api/v1/api-access/apps/{app.Id}", new { ipRanges = Array.Empty<string>() }));
        Assert.Equal("api.insufficient_scope", (await JsonAsync(await CallAsync(t, token, Outside), HttpStatusCode.Forbidden)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Token_requests_are_limited_per_app()
    {
        var t = await FreshAsync();
        var app = await AppAsync(t);
        HttpResponseMessage? limited = null;
        for (var i = 0; i < ApiRules.TokenRequestsPerMinute + 1 && limited is null; i++)
        {
            var response = await From(t, null).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = app.ClientId, ["client_secret"] = "tbs_wrong", ["scope"] = "content:read",
            }));
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }
        Assert.NotNull(limited);
        Assert.NotNull(limited!.Headers.RetryAfter);
        Assert.Contains("temporarily_unavailable", await limited.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        // Another app of the tenant is not affected.
        var other = await AppAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await TokenResponseAsync(t, other)).StatusCode);
    }

    [Fact]
    public async Task The_usage_page_shows_each_tenant_only_its_own_calls()
    {
        var t = await FreshAsync();
        var app = await AppAsync(t);
        var token = await TokenAsync(t, app);
        await CallAsync(t, token);
        await CallAsync(t, token);

        var usage = await JsonAsync(await t["admin"].Client.GetAsync("/api/v1/api-access/usage?days=30"));
        var mine = usage.GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("appId").GetString() == app.Id);
        Assert.Equal(2, mine.GetProperty("calls").GetInt64());
        Assert.Equal(2, mine.GetProperty("denied").GetInt64());
        Assert.Equal(2, mine.GetProperty("errors").GetProperty("denied").GetInt64());
        Assert.Equal("GET /api/public/v1/me", mine.GetProperty("topOperations")[0].GetProperty("operation").GetString());
        Assert.NotEqual(JsonValueKind.Null, mine.GetProperty("lastCallAt").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.GetAsync("/api/v1/api-access/usage?days=91")).StatusCode);

        // Another tenant sees none of these calls, and a person without api.view sees no usage at all.
        var other = await JsonAsync(await world.B["admin"].Client.GetAsync("/api/v1/api-access/usage"));
        Assert.DoesNotContain(other.GetProperty("apps").EnumerateArray(), a => a.GetProperty("appId").GetString() == app.Id);
        Assert.Equal(0, await world.Database.CountInTenantAsync(world.B.Id, "select count(*) from publicapi.call_log where app_id = @a::uuid", ("a", app.Id)));
        await world.SeedUserAsync(t, "reader", TeamBeats.Modules.Tenancy.Contracts.MembershipTypes.Employee);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["reader"].Client.GetAsync("/api/v1/api-access/usage")).StatusCode);

        // The call log keeps 90 days.
        await world.Database.OwnerExecInTenantAsync(t.Id, "update publicapi.call_log set at = now() - interval '91 days'");
        var run = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await sp.GetRequiredService<PublicApiMaintenance>().RunDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await sp.GetRequiredService<TeamBeats.Persistence.IDbSession>().CommitAsync();
            return result;
        });
        Assert.Equal(2, run.CallsPruned);
    }
}
