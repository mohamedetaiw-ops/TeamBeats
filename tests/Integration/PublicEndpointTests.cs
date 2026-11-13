using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T010 and the first operations of T011: every public operation declares one catalogue scope; lists page with
/// cursors; readable resources carry ETags; the request ID and language follow the caller; the idempotency store keeps
/// the first answer 24 hours. <c>org:read</c> is answered for the app's own roles only: an app with a role at Acme Gulf
/// sees Gulf, not Egypt nor the group above, and another tenant's units are not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicEndpointTests(TenantWorld world)
{
    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pe-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        await world.CreateUnitAsync(t, "GulfSales", gulf, "department");
        return t;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement;
    }

    /// <summary>An application app with org:read and the App reader role at the given scope, and its access token.</summary>
    private async Task<(string Id, string Token)> AppAsync(TestTenant t, string scopeType, Guid? scopeId)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Org sync", kind = "application", scopes = new[] { "org:read", "content:read" } }), HttpStatusCode.Created);
        var id = app.GetProperty("id").GetString()!;
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "app", subjectId = Guid.Parse(id), roleKey = BuiltInRoles.AppReader, scopeType, scopeId }), HttpStatusCode.Created);
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.GetProperty("clientId").GetString()!, ["client_secret"] = secret, ["scope"] = "org:read",
        })));
        return (id, tokens.GetProperty("access_token").GetString()!);
    }

    private Task<HttpResponseMessage> GetAsync(TestTenant t, string token, string path, Action<HttpRequestMessage>? change = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        change?.Invoke(request);
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    [Fact]
    public void Every_public_operation_declares_exactly_one_catalogue_scope()
    {
        var endpoints = world.Factory.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(PublicEndpoints.Prefix, StringComparison.Ordinal) == true).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e =>
        {
            var scopes = e.Metadata.GetOrderedMetadata<PublicScopeMetadata>();
            Assert.True(scopes.Count == 1, $"{e.RoutePattern.RawText} declares {scopes.Count} scopes");
            Assert.NotNull(ScopeCatalogue.Find(scopes[0].Scope));
            Assert.StartsWith(PublicEndpoints.Version1 + "/", e.RoutePattern.RawText, StringComparison.Ordinal);
        });
        // The public API never changes roles, policies, identity, security, entitlements or app registrations (FR-012).
        Assert.DoesNotContain(endpoints, e => e.RoutePattern.RawText!.Contains("/authz", StringComparison.Ordinal)
            || e.RoutePattern.RawText.Contains("/api-access", StringComparison.Ordinal) || e.RoutePattern.RawText.Contains("/security", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_app_reads_only_the_units_its_roles_reach_with_cursors_etags_and_its_language()
    {
        var t = await FreshAsync();
        var (_, gulfToken) = await AppAsync(t, "org_unit", t.Unit("Gulf"));

        var page = await JsonAsync(await GetAsync(t, gulfToken, "/api/public/v1/org-units"));
        Assert.Equal(["Gulf"], page.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{t.Unit("Egypt")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{t.Unit("Group")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{t.Unit("GulfSales")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{Guid.NewGuid()}")).StatusCode);
        // Another tenant's unit is not found, and the token is refused on another tenant's host.
        var other = await world.CreateUnitAsync(world.B, "Elsewhere-" + Guid.NewGuid().ToString("N")[..4], null, "organization");
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{other}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(world.B, gulfToken, "/api/public/v1/org-units")).StatusCode);

        // The name follows Accept-Language within the tenant's locales; codes and identifiers never change.
        var single = await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{t.Unit("Gulf")}", r => r.Headers.AcceptLanguage.ParseAdd("ar"));
        var unit = await JsonAsync(single);
        Assert.Equal("وحدة Gulf", unit.GetProperty("name").GetString());
        Assert.Equal("Gulf", unit.GetProperty("code").GetString());
        Assert.Equal("subsidiary", unit.GetProperty("type").GetString());
        Assert.Equal("ar", single.Content.Headers.ContentLanguage.Single());
        Assert.Equal("Gulf", (await JsonAsync(await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{t.Unit("Gulf")}"))).GetProperty("name").GetString());

        // ETag with conditional requests, and the request ID echoed.
        var etag = single.Headers.ETag!;
        var again = await GetAsync(t, gulfToken, $"/api/public/v1/org-units/{t.Unit("Gulf")}", r =>
        {
            r.Headers.AcceptLanguage.ParseAdd("ar");
            r.Headers.IfNoneMatch.Add(etag);
            r.Headers.TryAddWithoutValidation("X-Request-Id", "sync-run-42");
        });
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Equal("sync-run-42", again.Headers.GetValues("X-Request-Id").Single());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from publicapi.call_log where correlation_id = 'sync-run-42' and status = 304"));

        // A role at the whole organization reaches every unit, page by page.
        var (_, allToken) = await AppAsync(t, "tenant", null);
        var codes = new List<string>();
        string? cursor = null;
        do
        {
            var next = await JsonAsync(await GetAsync(t, allToken, "/api/public/v1/org-units?limit=1" + (cursor is null ? "" : "&cursor=" + cursor)));
            Assert.Single(next.GetProperty("items").EnumerateArray());
            codes.AddRange(next.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("code").GetString()!));
            cursor = next.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        Assert.Equal(["Group", "Egypt", "Gulf", "GulfSales"], codes);
        Assert.Equal(["Egypt", "Gulf"], (await JsonAsync(await GetAsync(t, allToken, $"/api/public/v1/org-units?parentId={t.Unit("Group")}&limit=200")))
            .GetProperty("items").EnumerateArray().Select(u => u.GetProperty("code").GetString()).Where(c => c != "Group").Take(2));
        Assert.Equal("validation.failed", (await JsonAsync(await GetAsync(t, allToken, "/api/public/v1/org-units?limit=201"), HttpStatusCode.BadRequest))
            .GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(t, allToken, "/api/public/v1/org-units?cursor=not-a-cursor")).StatusCode);
    }

    [Fact]
    public async Task A_token_without_the_scope_or_a_package_without_the_capability_is_refused()
    {
        var t = await FreshAsync();
        var (_, token) = await AppAsync(t, "tenant", null);
        // The token holds org:read only; content:read was not asked for.
        Assert.Equal("api.insufficient_scope", (await JsonAsync(await GetAsync(t, token, "/api/public/v1/me"), HttpStatusCode.Forbidden)).GetProperty("code").GetString());
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.ApiPublic, new { enabled = false });
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(t, token, "/api/public/v1/org-units")).StatusCode);
    }

    [Fact]
    public async Task The_idempotency_store_replays_the_first_answer_for_24_hours_and_refuses_a_reused_key()
    {
        var t = await FreshAsync();
        var (appId, _) = await AppAsync(t, "tenant", null);
        var app = Guid.Parse(appId);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        async Task<T> StoreAsync<T>(Func<PublicIdempotency, Task<T>> work) => await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await work(new PublicIdempotency(sp.GetRequiredService<IDbSession>(), clock));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

        var hash = PublicIdempotency.Hash("POST", "/api/public/v1/requests", "{\"a\":1}"u8);
        Assert.IsType<IdempotencyStart.Run>(await StoreAsync(s => s.BeginAsync(app, "k1", hash, CancellationToken.None)));
        Assert.Equal("in_progress", Assert.IsType<IdempotencyStart.Refused>(await StoreAsync(s => s.BeginAsync(app, "k1", hash, CancellationToken.None))).Reason);
        await StoreAsync(async s => { await s.CompleteAsync(app, "k1", 201, "application/json", "{\"id\":\"x\"}", CancellationToken.None); return 0; });
        var replay = Assert.IsType<IdempotencyStart.Replay>(await StoreAsync(s => s.BeginAsync(app, "k1", hash, CancellationToken.None)));
        Assert.Equal((201, "{\"id\":\"x\"}"), (replay.Status, replay.Body));
        var other = PublicIdempotency.Hash("POST", "/api/public/v1/requests", "{\"a\":2}"u8);
        Assert.Equal("reused", Assert.IsType<IdempotencyStart.Refused>(await StoreAsync(s => s.BeginAsync(app, "k1", other, CancellationToken.None))).Reason);
        // Another tenant knows nothing of the key.
        Assert.Equal(0, await world.Database.CountInTenantAsync(world.B.Id, "select count(*) from publicapi.idempotency"));

        // A server error is not kept, and a hold whose call never ended is released after 5 minutes.
        Assert.IsType<IdempotencyStart.Run>(await StoreAsync(s => s.BeginAsync(app, "k2", hash, CancellationToken.None)));
        await StoreAsync(async s => { await s.CompleteAsync(app, "k2", 500, "application/problem+json", "{}", CancellationToken.None); return 0; });
        Assert.IsType<IdempotencyStart.Run>(await StoreAsync(s => s.BeginAsync(app, "k2", hash, CancellationToken.None)));
        clock.UtcNow = clock.UtcNow.AddMinutes(6);
        Assert.IsType<IdempotencyStart.Run>(await StoreAsync(s => s.BeginAsync(app, "k2", hash, CancellationToken.None)));

        // After 24 hours the key is new again.
        clock.UtcNow = clock.UtcNow.AddHours(25);
        Assert.IsType<IdempotencyStart.Run>(await StoreAsync(s => s.BeginAsync(app, "k1", other, CancellationToken.None)));
        Assert.False(PublicIdempotency.ValidKey(""));
        Assert.False(PublicIdempotency.ValidKey("has space"));
        Assert.True(PublicIdempotency.ValidKey(Guid.NewGuid().ToString()));
    }

    private sealed class MutableClock(DateTimeOffset now) : TeamBeats.Primitives.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}
