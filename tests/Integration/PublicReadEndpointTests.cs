using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T011, the read scopes of the owning modules: <c>content:read</c> (009), <c>documents:read</c> (011),
/// <c>people:read</c> (010) and <c>policies:read</c> (025). A person reads what they read in TeamBeats. An application
/// app reads only what its own roles cover: with the App reader role at Acme Gulf it reads Gulf content, libraries,
/// people and policies, and not Egypt's nor the tenant-wide ones, since it is not a member of the tenant (section 7).
/// Another tenant's resources are not found.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicReadEndpointTests(TenantWorld world)
{
    private const string Redirect = "http://127.0.0.1:7777/callback";
    private const string Public = "/api/public/v1";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pr-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        foreach (var (name, unit) in new[] { ("gia", "Gulf"), ("gus", "Gulf"), ("eli", "Egypt"), ("co", "Gulf"), ("legal", "Gulf") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return new Org(t, gulf, egypt);
    }

    /// <summary>An app of another tenant, with the App reader role at its whole organization.</summary>
    private async Task<(TestTenant T, string Token)> ForeignAsync()
    {
        var other = await world.CreateTenantAsync("pr-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        return (other, await AppTokenAsync(other, "tenant", null));
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static readonly string[] ReadScopes = ["content:read", "documents:read", "people:read", "policies:read"];

    /// <summary>An application app with the read scopes and the App reader role at the given scope, and its access token.</summary>
    private async Task<string> AppTokenAsync(TestTenant t, string scopeType, Guid? scopeId)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps",
            new { name = "Reader " + Guid.NewGuid().ToString("N")[..4], kind = "application", scopes = ReadScopes }), HttpStatusCode.Created);
        var id = app.GetProperty("id").GetString()!;
        var secret = (await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/api-access/apps/{id}/credentials", new { }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/assignments",
            new { subjectType = "app", subjectId = Guid.Parse(id), roleKey = BuiltInRoles.AppReader, scopeType, scopeId }), HttpStatusCode.Created);
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = app.GetProperty("clientId").GetString()!, ["client_secret"] = secret,
            ["scope"] = string.Join(' ', ReadScopes),
        })));
        return tokens.GetProperty("access_token").GetString()!;
    }

    /// <summary>A delegated app the person consented to, and the person's access token from the code flow with PKCE.</summary>
    private async Task<string> PersonTokenAsync(TestTenant t, string user)
    {
        var app = await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/api-access/apps", new
        {
            name = "Notes " + Guid.NewGuid().ToString("N")[..4], kind = "delegated_public", scopes = ReadScopes, redirectUris = new[] { Redirect }, publisher = "Acme IT",
        }), HttpStatusCode.Created);
        var clientId = app.GetProperty("clientId").GetString()!;
        await JsonAsync(await t[user].Client.PostAsJsonAsync("/api/v1/me/app-consent", new { clientId, redirectUri = Redirect, scopes = ReadScopes }));
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var authorize = await t[user].Client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = clientId, ["response_type"] = "code", ["redirect_uri"] = Redirect, ["scope"] = string.Join(' ', ReadScopes), ["state"] = "s",
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        }));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        var tokens = await JsonAsync(await world.Factory.TenantClient(t.Key).PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier,
        })));
        return tokens.GetProperty("access_token").GetString()!;
    }

    private Task<HttpResponseMessage> GetAsync(TestTenant t, string token, string path, Action<HttpRequestMessage>? change = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        change?.Invoke(request);
        return world.Factory.TenantClient(t.Key).SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetForeignAsync(string path)
    {
        var (other, token) = await ForeignAsync();
        return await GetAsync(other, token, path);
    }

    private async Task<IReadOnlyList<Guid>> IdsAsync(TestTenant t, string token, string path) =>
        (await JsonAsync(await GetAsync(t, token, path))).GetProperty("items").EnumerateArray().Select(Id).ToList();

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
    }

    // ---- Content ----

    private static async Task<Guid> SiteAsync(TestTenant t, string name, Guid unit) =>
        Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/sites",
            new { names = new Dictionary<string, string> { ["en"] = name }, orgUnitId = unit }), HttpStatusCode.Created));

    private static async Task<Guid> NewsAsync(TestTenant t, Guid? site, string title, object? audience = null)
    {
        var item = Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/items", new
        {
            siteId = site, type = "news", audience,
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title, summary = "Summary", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title + " body" } } } } } },
                ["ar"] = new { title = title + " ع", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "نص" } } } } } },
            },
        }), HttpStatusCode.Created));
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync($"/api/v1/content/items/{item}/publish", new { material = true }));
        return item;
    }

    [Fact]
    public async Task Content_is_read_within_the_apps_roles_and_the_persons_feed()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gulfSite = await SiteAsync(t, "Gulf news", o.Gulf);
        var egyptSite = await SiteAsync(t, "Egypt news", o.Egypt);
        var gulfNews = await NewsAsync(t, gulfSite, "Gulf canteen");
        var egyptNews = await NewsAsync(t, egyptSite, "Egypt canteen");
        var everyone = await NewsAsync(t, null, "Group results");
        var draft = Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/content/items", new
        {
            siteId = gulfSite, type = "news",
            variants = new Dictionary<string, object> { ["en"] = new { title = "Not yet", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = "x" } } } } } } },
        }), HttpStatusCode.Created));
        await DispatchAsync();

        var gulfApp = await AppTokenAsync(t, "org_unit", o.Gulf);
        Assert.Equal([gulfNews], await IdsAsync(t, gulfApp, $"{Public}/content"));
        var read = await GetAsync(t, gulfApp, $"{Public}/content/{gulfNews}", r => r.Headers.AcceptLanguage.ParseAdd("ar"));
        var item = await JsonAsync(read);
        Assert.Equal("Gulf canteen ع", item.GetProperty("title").GetString());
        Assert.Equal("ar", item.GetProperty("locale").GetString());
        Assert.Equal("paragraph", item.GetProperty("body").GetProperty("blocks")[0].GetProperty("type").GetString());
        Assert.EndsWith($"/content/read/{gulfNews}", item.GetProperty("webUrl").GetString(), StringComparison.Ordinal);
        Assert.NotNull(read.Headers.ETag);
        // Egypt's item, the tenant-wide item, a draft, an unknown ID and another tenant's item are not found.
        foreach (var id in new[] { egyptNews, everyone, draft, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfApp, $"{Public}/content/{id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await GetForeignAsync($"{Public}/content/{gulfNews}")).StatusCode);

        // At the whole organization the app reads everything live, page by page.
        var allApp = await AppTokenAsync(t, "tenant", null);
        var seen = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await JsonAsync(await GetAsync(t, allApp, $"{Public}/content?limit=1" + (cursor is null ? "" : "&cursor=" + cursor)));
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(Id));
            cursor = page.GetProperty("nextCursor").GetString();
        }
        while (cursor is not null);
        Assert.Equal(new[] { gulfNews, egyptNews, everyone }.Order(), seen.Order());
        Assert.Equal([egyptNews], await IdsAsync(t, allApp, $"{Public}/content?siteId={egyptSite}"));

        // A person reads what they read in TeamBeats: every item open to all members, by link too.
        var person = await PersonTokenAsync(t, "gia");
        Assert.Contains(everyone, await IdsAsync(t, person, $"{Public}/content"));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(t, person, $"{Public}/content/{egyptNews}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, person, $"{Public}/content/{draft}")).StatusCode);
    }

    // ---- Documents ----

    private static async Task<Guid> LibraryAsync(TestTenant t, string name, Guid unit) =>
        Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/documents/libraries",
            new { names = new Dictionary<string, string> { ["en"] = name, ["ar"] = name + " ع" }, scope = "org_unit", scopeId = unit }), HttpStatusCode.Created));

    private static async Task<Guid> FileAsync(TestTenant t, Guid library, string name, string content) =>
        Id(await JsonAsync(await t["admin"].Client.PostAsync($"/api/v1/documents/libraries/{library}/files?name={Uri.EscapeDataString(name)}&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes(content))), HttpStatusCode.Created));

    [Fact]
    public async Task Documents_and_their_files_are_read_within_the_apps_roles()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gulfLibrary = await LibraryAsync(t, "Gulf quality", o.Gulf);
        var egyptLibrary = await LibraryAsync(t, "Egypt quality", o.Egypt);
        var guide = await FileAsync(t, gulfLibrary, "guide.txt", "Gulf guide");
        var other = await FileAsync(t, egyptLibrary, "other.txt", "Egypt guide");

        var gulfApp = await AppTokenAsync(t, "org_unit", o.Gulf);
        var libraries = (await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/libraries", r => r.Headers.AcceptLanguage.ParseAdd("ar")))).GetProperty("items");
        Assert.Equal([gulfLibrary], libraries.EnumerateArray().Select(Id));
        Assert.Equal("Gulf quality ع", libraries[0].GetProperty("name").GetString());
        Assert.Equal([guide], await IdsAsync(t, gulfApp, $"{Public}/libraries/{gulfLibrary}/documents"));
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfApp, $"{Public}/libraries/{egyptLibrary}/documents")).StatusCode);

        var doc = await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/documents/{guide}"));
        Assert.Equal("1.0", doc.GetProperty("version").GetProperty("label").GetString());
        Assert.EndsWith($"{Public}/documents/{guide}/content", doc.GetProperty("contentUrl").GetString(), StringComparison.Ordinal);
        Assert.Single((await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/documents/{guide}/versions"))).GetProperty("items").EnumerateArray());
        var file = await GetAsync(t, gulfApp, $"{Public}/documents/{guide}/content");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("Gulf guide", await file.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", file.Headers.GetValues("X-Content-Type-Options").Single());

        foreach (var path in new[] { $"/documents/{other}", $"/documents/{other}/content", $"/documents/{Guid.NewGuid()}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfApp, Public + path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await GetForeignAsync($"{Public}/documents/{guide}/content")).StatusCode);

        // The person reads the libraries of their own unit, as in TeamBeats.
        var person = await PersonTokenAsync(t, "eli");
        Assert.Contains(egyptLibrary, (await JsonAsync(await GetAsync(t, person, $"{Public}/libraries"))).GetProperty("items").EnumerateArray().Select(Id));
        Assert.Equal("Egypt guide", await (await GetAsync(t, person, $"{Public}/documents/{other}/content")).Content.ReadAsStringAsync());
    }

    // ---- People ----

    [Fact]
    public async Task People_are_read_within_the_apps_roles_with_the_fields_every_employee_sees()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gulfApp = await AppTokenAsync(t, "org_unit", o.Gulf);
        var people = (await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/people?limit=200"))).GetProperty("items").EnumerateArray().Select(Id).ToList();
        Assert.Contains(t["gia"].Id, people);
        Assert.Contains(t["gus"].Id, people);
        Assert.DoesNotContain(t["eli"].Id, people);
        Assert.DoesNotContain(t["admin"].Id, people);

        var gia = await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/people/{t["gia"].Id}"));
        Assert.Equal(t["gia"].Id, Id(gia));
        Assert.Equal(JsonValueKind.Object, gia.GetProperty("fields").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(t, gulfApp, $"{Public}/people/{t["gia"].Id}/org-chart")).StatusCode);
        foreach (var id in new[] { t["eli"].Id, Guid.NewGuid(), world.B["admin"].Id })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfApp, $"{Public}/people/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfApp, $"{Public}/people/{id}/org-chart")).StatusCode);
        }

        // A person searches the whole directory, as in TeamBeats; paging uses the cursor.
        var person = await PersonTokenAsync(t, "gia");
        var first = await JsonAsync(await GetAsync(t, person, $"{Public}/people?limit=1"));
        Assert.Single(first.GetProperty("items").EnumerateArray());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        var second = await JsonAsync(await GetAsync(t, person, $"{Public}/people?limit=1&cursor={cursor}"));
        Assert.NotEqual(Id(first.GetProperty("items")[0]), Id(second.GetProperty("items")[0]));
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(t, person, $"{Public}/people/{t["eli"].Id}")).StatusCode);
    }

    // ---- Policies ----

    private async Task<Guid> PolicyAsync(TestTenant t, string code, Guid unit)
    {
        var titles = new Dictionary<string, string> { ["en"] = "Code " + code, ["ar"] = "مدونة " + code };
        var id = Id(await JsonAsync(await t["co"].Client.PostAsJsonAsync("/api/v1/policies", new
        {
            code, names = titles, orgUnitId = unit, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));
        await JsonAsync(await t["co"].Client.PutAsJsonAsync($"/api/v1/policies/{id}/draft", new
        {
            titles, material = true,
            clauses = new[]
            {
                new
                {
                    level = 1, titles = new Dictionary<string, string> { ["en"] = "Gifts", ["ar"] = "الهدايا" },
                    bodies = new Dictionary<string, object>
                    {
                        ["en"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "No gifts" } } } } },
                        ["ar"] = new { blocks = new object[] { new { type = "paragraph", children = new[] { new { text = "لا هدايا" } } } } },
                    },
                },
            },
        }));
        await JsonAsync(await t["co"].Client.PostAsJsonAsync($"/api/v1/policies/{id}/submit", new { }));
        var task = (await JsonAsync(await t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await JsonAsync(await t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        return id;
    }

    [Fact]
    public async Task Policies_in_force_are_read_within_the_apps_roles_and_a_person_sees_their_attestation()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gulf = await PolicyAsync(t, "G-1", o.Gulf);
        var egypt = await PolicyAsync(t, "E-1", o.Egypt);
        var unpublished = Id(await JsonAsync(await t["co"].Client.PostAsJsonAsync("/api/v1/policies", new
        {
            code = "G-2", names = new Dictionary<string, string> { ["en"] = "Later" }, orgUnitId = o.Gulf, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created));

        var gulfApp = await AppTokenAsync(t, "org_unit", o.Gulf);
        Assert.Equal([gulf], await IdsAsync(t, gulfApp, $"{Public}/policies"));
        var policy = await JsonAsync(await GetAsync(t, gulfApp, $"{Public}/policies/{gulf}", r => r.Headers.AcceptLanguage.ParseAdd("ar")));
        Assert.Equal("مدونة G-1", policy.GetProperty("name").GetString());
        Assert.Equal("الهدايا", policy.GetProperty("clauses")[0].GetProperty("title").GetString());
        Assert.Equal(1, policy.GetProperty("version").GetInt32());
        Assert.Equal(JsonValueKind.Null, policy.GetProperty("attestation").ValueKind);
        foreach (var id in new[] { egypt, unpublished, Guid.NewGuid() })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, gulfApp, $"{Public}/policies/{id}")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await GetForeignAsync($"{Public}/policies/{gulf}")).StatusCode);

        // A person in Gulf reads the policies that apply to them, with their own attestation status.
        var person = await PersonTokenAsync(t, "gia");
        Assert.Contains(gulf, await IdsAsync(t, person, $"{Public}/policies"));
        var mine = await JsonAsync(await GetAsync(t, person, $"{Public}/policies/{gulf}"));
        Assert.Equal(JsonValueKind.Object, mine.GetProperty("attestation").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(t, person, $"{Public}/policies/{egypt}")).StatusCode);
    }
}
