using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R2 FR-020, US3 scenario 5 and the signage default (T035): retention deletes old versions or archived items
/// after the period and never what a legal hold keeps; the health dashboard flags ownerless sites and live items of
/// archived sites; a signage address shows only the site's Public items meant for everyone and stops after rotation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ContentRetentionSignageTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("crt-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(t, "pia", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        return t;
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return status == HttpStatusCode.NoContent ? default : await response.ReadAsync<JsonElement>();
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static object Variants(string title) => new Dictionary<string, object>
    {
        ["en"] = new { title, body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
    };

    private static async Task<Guid> PublishAsync(HttpClient client, string title, string type = "news", string classification = "internal", object? audience = null,
        Guid? siteId = null)
    {
        var id = Id(await OkAsync(client.PostAsJsonAsync(Items, new { siteId, type, classification, audience, variants = Variants(title) }), HttpStatusCode.Created));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private static async Task RepublishAsync(HttpClient client, Guid id, string title)
    {
        await OkAsync(client.PutAsJsonAsync($"{Items}/{id}", new { variants = Variants(title) }));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { material = true }));
    }

    private static async Task<int[]> VersionsAsync(HttpClient client, Guid id) =>
        (await OkAsync(client.GetAsync($"{Items}/{id}/versions"))).EnumerateArray().Select(v => v.GetProperty("number").GetInt32()).Order().ToArray();

    private Task RunAsync(TestTenant t, DateTimeOffset now) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<ContentScheduler>().RunDueAsync(now, 200, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private Task HoldAsync(TestTenant t, Guid item) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<ILegalHolds>().PlaceAsync(TeamBeats.Modules.Content.Items.ResourceType, item, "Case 42");
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private static object Type(string key, int? months, string? action) => new
    {
        key, requiresOwner = key == "page", reviewMonths = key == "page" ? 12 : (int?)null, showHistoryToReaders = false, underReviewLabel = true,
        retentionMonths = months, retentionAction = action,
    };

    [Fact]
    public async Task Retention_deletes_old_versions_or_archived_items_after_the_period_and_skips_legal_hold()
    {
        var t = await FreshAsync();
        var pia = t["pia"].Client;
        var admin = t["admin"].Client;

        // Shape: a period needs its action and the other way round; the default keeps everything.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/content/settings", new { types = new[] { Type("news", 6, null) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/content/settings", new { types = new[] { Type("news", 6, "purge") } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pia.PutAsJsonAsync("/api/v1/content/settings", new { types = new[] { Type("news", 6, "versions") } })).StatusCode);

        var kept = await PublishAsync(pia, "Canteen v1");
        var held = await PublishAsync(pia, "Board v1");
        foreach (var (id, name) in new[] { (kept, "Canteen"), (held, "Board") })
        {
            await RepublishAsync(pia, id, $"{name} v2");
            await RepublishAsync(pia, id, $"{name} v3");
        }
        var gone = await PublishAsync(pia, "Old notice", type: "announcement");
        var heldNotice = await PublishAsync(pia, "Held notice", type: "announcement");
        var liveNotice = await PublishAsync(pia, "Live notice", type: "announcement");
        await OkAsync(pia.PostAsync($"{Items}/{gone}/archive", null));
        await OkAsync(pia.PostAsync($"{Items}/{heldNotice}/archive", null));
        await HoldAsync(t, held);
        await HoldAsync(t, heldNotice);

        var now = DateTimeOffset.UtcNow;
        await RunAsync(t, now.AddYears(2)); // No period set yet: nothing goes.
        Assert.Equal(new[] { 1, 2, 3 }, await VersionsAsync(pia, kept));

        var settings = await OkAsync(admin.PutAsJsonAsync("/api/v1/content/settings",
            new { types = new[] { Type("news", 6, "versions"), Type("announcement", 1, "item") } }));
        var news = settings.GetProperty("types").EnumerateArray().Single(x => x.GetProperty("key").GetString() == "news");
        Assert.Equal(6, news.GetProperty("retentionMonths").GetInt32());

        await RunAsync(t, now.AddDays(20)); // Inside the periods.
        Assert.Equal(new[] { 1, 2, 3 }, await VersionsAsync(pia, kept));
        Assert.Equal(HttpStatusCode.OK, (await pia.GetAsync($"{Items}/{gone}")).StatusCode);

        await RunAsync(t, now.AddMonths(7));
        Assert.Equal(new[] { 3 }, await VersionsAsync(pia, kept)); // The live version stays.
        Assert.Equal("Canteen v3", (await OkAsync(t["emp"].Client.GetAsync($"{Items}/{kept}/view"))).GetProperty("title").GetString());
        Assert.Equal(new[] { 1, 2, 3 }, await VersionsAsync(pia, held));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentRetentionApplied, kept.ToString());

        Assert.Equal(HttpStatusCode.NotFound, (await pia.GetAsync($"{Items}/{gone}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pia.GetAsync($"{Items}/{heldNotice}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await pia.GetAsync($"{Items}/{liveNotice}")).StatusCode); // Only archived items go.
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentRetentionApplied, gone.ToString());
    }

    [Fact]
    public async Task Health_flags_ownerless_sites_and_live_items_of_archived_sites()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "site_owner", names = new Dictionary<string, string> { ["en"] = "Site owner" },
            permissions = new[] { Permissions.ContentView, Permissions.ContentAdmin, Permissions.ContentPublish, Permissions.ContentEdit }, delegable = true,
        })).StatusCode);
        var site = Id(await OkAsync(admin.PostAsJsonAsync("/api/v1/content/sites", new { names = new Dictionary<string, string> { ["en"] = "Gulf" }, orgUnitId = gulf }),
            HttpStatusCode.Created));

        async Task<JsonElement> HealthAsync() =>
            (await OkAsync(admin.GetAsync("/api/v1/content/health"))).EnumerateArray().Single(s => s.GetProperty("siteId").GetGuid() == site);

        // A tenant administrator looks after everything but owns no site: the Gulf site has no owner.
        Assert.True((await HealthAsync()).GetProperty("ownerless").GetBoolean());
        await world.SeedUserAsync(t, "olga", MembershipTypes.Employee);
        await world.AssignAsync(t, "olga", "site_owner", "org_subtree", holding);
        Assert.False((await HealthAsync()).GetProperty("ownerless").GetBoolean());

        var item = await PublishAsync(t["olga"].Client, "Gulf news", siteId: site);
        await OkAsync(admin.PostAsync($"/api/v1/content/sites/{site}/archive", null));
        var health = await HealthAsync();
        Assert.True(health.GetProperty("archived").GetBoolean());
        Assert.Equal(1, health.GetProperty("liveLinks").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await t["emp"].Client.GetAsync($"{Items}/{item}/view")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["pia"].Client.GetAsync("/api/v1/content/health")).StatusCode);
    }

    [Fact]
    public async Task A_signage_address_shows_only_public_items_for_everyone_and_stops_after_rotation()
    {
        var t = await FreshAsync();
        var admin = t["admin"].Client;
        var pia = t["pia"].Client;
        var sites = await OkAsync(admin.GetAsync("/api/v1/content/sites"));
        var site = sites[0].GetProperty("id").GetGuid();

        var open = await PublishAsync(pia, "Lobby welcome", classification: "public");
        var inside = await PublishAsync(pia, "Staff only", classification: "internal");
        var targeted = await PublishAsync(pia, "Public for one unit", classification: "public",
            audience: new { anyOf = new[] { new { membershipTypes = new[] { "contractor" } } } });

        Assert.Equal(HttpStatusCode.NotFound, (await pia.PostAsJsonAsync($"/api/v1/content/sites/{site}/screens", new { name = "Lobby" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/v1/content/sites/{site}/screens", new { name = " " })).StatusCode);
        var created = await OkAsync(admin.PostAsJsonAsync($"/api/v1/content/sites/{site}/screens", new { name = "Lobby" }), HttpStatusCode.Created);
        var path = created.GetProperty("path").GetString()!;
        var screen = created.GetProperty("screen").GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentScreenCreated, screen.ToString());
        Assert.Single((await OkAsync(admin.GetAsync($"/api/v1/content/sites/{site}/screens"))).EnumerateArray());
        var token = path["/signage/".Length..];

        var anonymous = world.Factory.TenantClient(t.Key);
        var feed = await OkAsync(anonymous.GetAsync($"/api/v1/signage/{token}"));
        Assert.Equal("Lobby", feed.GetProperty("name").GetString());
        var ids = feed.GetProperty("items").EnumerateArray().Select(Id).ToList();
        Assert.Contains(open, ids);
        Assert.DoesNotContain(inside, ids);
        Assert.DoesNotContain(targeted, ids);

        // Another tenant's host does not know the token.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.TenantClient(other.Key).GetAsync($"/api/v1/signage/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.PostAsync($"/api/v1/content/screens/{screen}/rotate", null)).StatusCode);

        var rotated = await OkAsync(admin.PostAsync($"/api/v1/content/screens/{screen}/rotate", null));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentScreenRotated, screen.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/signage/{token}")).StatusCode);
        var fresh = rotated.GetProperty("path").GetString()!["/signage/".Length..];
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/v1/signage/{fresh}")).StatusCode);

        await OkAsync(admin.DeleteAsync($"/api/v1/content/screens/{screen}"), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.ContentScreenRemoved, screen.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/v1/signage/{fresh}")).StatusCode);
    }
}
