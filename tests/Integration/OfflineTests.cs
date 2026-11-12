using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 030 slice 3 (US3, FR-009 to FR-011): the offline bundle holds only the critical announcements targeted to the
/// person and the policies that apply to them, never a Confidential item; it follows the tenant's item and size limits;
/// a withdrawn item is absent at the next sync; the key changes with the session; the package decides; tenants and
/// people never see each other's items.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class OfflineTests(TenantWorld world)
{
    private const string Api = "/api/v1/me/offline";
    private const string Items = "/api/v1/content/items";
    private const string Policies = "/api/v1/policies";

    private sealed record Org(TestTenant T, Guid PlantA, Guid PlantB);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("of-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var plantA = await world.CreateUnitAsync(t, "PlantA", holding, "department");
        var plantB = await world.CreateUnitAsync(t, "PlantB", holding, "department");
        foreach (var name in new[] { "hs", "co", "legal", "ali", "bea" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "ali", "PlantA");
        await world.AddMembershipAsync(t, "bea", "PlantB");
        await world.AssignAsync(t, "co", BuiltInRoles.ComplianceOfficer, "tenant", null);
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "safety_lead", names = new Dictionary<string, string> { ["en"] = "Safety lead" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentAuthor, Permissions.ContentEdit, Permissions.ContentPublish, Permissions.ContentPublishCritical },
            delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "hs", "safety_lead", "tenant", null);
        await DispatchAsync();
        return new Org(t, plantA, plantB);
    }

    private async Task DispatchAsync()
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 3; i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static object Units(params Guid[] units) => new { anyOf = new[] { new { orgUnits = units.Select(u => new { id = u }).ToArray() } } };

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>A live item: critical (a banner) unless <paramref name="critical"/> is false.</summary>
    private async Task<Guid> LiveAsync(Org o, string title, object audience, bool critical = true, string classification = "internal")
    {
        var hs = o.T["hs"].Client;
        var created = await OkAsync(hs.PostAsJsonAsync(Items, new
        {
            type = "announcement", classification, audience,
            variants = new Dictionary<string, object> { ["en"] = new { title, body = Body(title + " body") } },
            critical = critical ? new { presentation = "banner" } : null,
        }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(hs.PostAsJsonAsync($"{Items}/{id}/publish", new { material = true }));
        await DispatchAsync();
        return id;
    }

    /// <summary>A policy for everyone, approved by legal, in force as version 1.</summary>
    private async Task<Guid> PolicyAsync(Org o, string code, string classification = "internal")
    {
        var t = o.T;
        var names = new Dictionary<string, string> { ["en"] = code + " policy" };
        var id = (await OkAsync(t["co"].Client.PostAsJsonAsync(Policies, new
        {
            code, names, classification, route = new[] { new { approvers = new[] { t["legal"].Id } } },
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await OkAsync(t["co"].Client.PutAsJsonAsync($"{Policies}/{id}/draft", new
        {
            titles = names, material = true,
            clauses = new[] { new { level = 1, titles = new Dictionary<string, string> { ["en"] = "Scope" }, bodies = new Dictionary<string, object> { ["en"] = Body("Applies to all") } } },
        }));
        await OkAsync(t["co"].Client.PostAsJsonAsync($"{Policies}/{id}/submit", new { }));
        var task = (await OkAsync(t["legal"].Client.GetAsync("/api/v1/tasks"))).EnumerateArray().Single(i => i.GetProperty("source").GetString() == "policy_approval");
        await OkAsync(t["legal"].Client.PostAsJsonAsync($"/api/v1/tasks/{task.GetProperty("taskId").GetGuid()}/decision", new { outcome = "approve" }));
        await DispatchAsync();
        return id;
    }

    private static async Task<JsonElement> BundleAsync(HttpClient client) => await OkAsync(client.GetAsync(Api));

    private static Guid[] Ids(JsonElement bundle) => bundle.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();

    private async Task<string> NewSessionAsync(TestTenant t, string user) =>
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var token = await sp.GetRequiredService<SessionService>().CreateUserSessionAsync(t[user].Id, "test", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return token;
        });

    [Fact]
    public async Task Only_targeted_critical_items_and_policies_that_apply_are_bundled_and_never_confidential_ones()
    {
        var o = await FreshAsync();
        var t = o.T;
        var forA = await LiveAsync(o, "Gas leak drill", Units(o.PlantA));
        var forB = await LiveAsync(o, "Forklift recall", Units(o.PlantB));
        var news = await LiveAsync(o, "Canteen menu", Units(o.PlantA), critical: false);
        var secret = await LiveAsync(o, "Board decision", Units(o.PlantA), classification: "confidential");
        var conduct = await PolicyAsync(o, "COC");
        var closed = await PolicyAsync(o, "SEC", classification: "confidential");

        var bundle = await BundleAsync(t["ali"].Client);
        var ids = Ids(bundle);
        Assert.Contains(forA, ids);
        Assert.Contains(conduct, ids);
        Assert.DoesNotContain(forB, ids);
        Assert.DoesNotContain(news, ids);
        Assert.DoesNotContain(secret, ids);
        Assert.DoesNotContain(closed, ids);

        var drill = bundle.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == forA);
        Assert.Equal("announcement", drill.GetProperty("kind").GetString());
        Assert.Equal("Gas leak drill", drill.GetProperty("title").GetString());
        Assert.Equal($"/content/read/{forA}", drill.GetProperty("link").GetString());
        Assert.Equal("Gas leak drill body", drill.GetProperty("body").GetProperty("blocks")[0].GetProperty("children")[0].GetProperty("text").GetString());
        var policy = bundle.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == conduct);
        Assert.Equal("policy", policy.GetProperty("kind").GetString());
        Assert.Equal($"/policies/{conduct}", policy.GetProperty("link").GetString());
        var blocks = policy.GetProperty("body").GetProperty("blocks");
        Assert.Equal("heading", blocks[0].GetProperty("type").GetString());
        Assert.EndsWith("Scope", blocks[0].GetProperty("children")[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("Applies to all", blocks[1].GetProperty("children")[0].GetProperty("text").GetString());

        // Owner, limits and server time: the default 50 items, 50 MB and 7 days.
        Assert.Equal($"{t.Id}:{t["ali"].Id}", bundle.GetProperty("owner").GetString());
        Assert.Equal(50, bundle.GetProperty("limits").GetProperty("maxItems").GetInt32());
        Assert.Equal(50L * 1024 * 1024, bundle.GetProperty("limits").GetProperty("maxBytes").GetInt64());
        var synced = bundle.GetProperty("syncedAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromDays(7), bundle.GetProperty("expiresAt").GetDateTimeOffset() - synced);
        Assert.Equal(43, bundle.GetProperty("key").GetString()!.Length);

        // Bea in the other plant gets her own item and not Ali's.
        var bea = Ids(await BundleAsync(t["bea"].Client));
        Assert.Contains(forB, bea);
        Assert.DoesNotContain(forA, bea);

        // Withdrawn: gone at the next sync, so the device prunes it.
        await OkAsync(t["hs"].Client.PostAsync($"{Items}/{forA}/unpublish", null));
        await DispatchAsync();
        Assert.DoesNotContain(forA, Ids(await BundleAsync(t["ali"].Client)));

        var response = await t["ali"].Client.GetAsync(Api);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_tenant_limits_apply_and_their_change_is_audited()
    {
        var o = await FreshAsync();
        var t = o.T;
        var first = await LiveAsync(o, "Older notice", Units(o.PlantA));
        var second = await LiveAsync(o, "Newer notice", Units(o.PlantA));

        // Out of range values are refused (section 6: 0 to 200 items).
        var refused = await t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { offlineMaxItems = 201, offlineDays = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var saved = await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { offlineMaxItems = 1, offlineDays = 3 }));
        Assert.Equal(1, saved.GetProperty("offlineMaxItems").GetInt32());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.MobileOfflinePolicyChanged, null);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.SecurityPolicyChanged, null);

        var bundle = await BundleAsync(t["ali"].Client);
        Assert.Equal([second], Ids(bundle));
        Assert.Equal(TimeSpan.FromDays(3), bundle.GetProperty("expiresAt").GetDateTimeOffset() - bundle.GetProperty("syncedAt").GetDateTimeOffset());
        Assert.DoesNotContain(first, Ids(bundle));

        // Zero items: nothing is kept on any device.
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/security/policy", new { offlineMaxItems = 0 }));
        Assert.Empty(Ids(await BundleAsync(t["ali"].Client)));

        // Only security administrators change the limits.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["ali"].Client.PutAsJsonAsync("/api/v1/security/policy", new { offlineMaxItems = 10 })).StatusCode);
    }

    [Fact]
    public async Task The_key_belongs_to_one_session_the_package_decides_and_tenants_stay_apart()
    {
        var o = await FreshAsync();
        var t = o.T;
        var notice = await LiveAsync(o, "Evacuation route", Units(o.PlantA));

        var key = (await BundleAsync(t["ali"].Client)).GetProperty("key").GetString();
        Assert.Equal(key, (await BundleAsync(t["ali"].Client)).GetProperty("key").GetString());
        var other = await NewSessionAsync(t, "ali");
        var otherKey = (await BundleAsync(world.Factory.TenantClient(t.Key, other))).GetProperty("key").GetString();
        Assert.NotEqual(key, otherKey);
        Assert.NotEqual(key, (await BundleAsync(t["bea"].Client)).GetProperty("key").GetString());

        // After sign-out the session cannot fetch a bundle, so the device wipes its cache.
        Assert.True((await world.Factory.TenantClient(t.Key, other).PostAsync("/api/v1/auth/signout", null)).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key, other).GetAsync(Api)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await world.Factory.TenantClient(t.Key).GetAsync(Api)).StatusCode);

        // Another tenant's person sees nothing of this tenant; the workplace package has no offline reading.
        var elsewhere = await FreshAsync();
        Assert.DoesNotContain(notice, Ids(await BundleAsync(elsewhere.T["ali"].Client)));
        var workplace = await world.CreateTenantAsync("ow-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.SeedUserAsync(workplace, "ali", MembershipTypes.Employee);
        var refused = await workplace["ali"].Client.GetAsync(Api);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
