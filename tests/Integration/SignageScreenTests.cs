using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 015 US7 (R2, FR-021): signage screens of a location. A location administrator registers a screen at their unit
/// and gets a signed address once; the screen shows the live Public announcements marked for signage that are for
/// everyone or for that place, nothing else. A withdrawn announcement and a revoked or replaced address are gone on the
/// next read, which the screen makes every 30 seconds. Screens stay inside their tenant, unit and package.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SignageScreenTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Screens = "/api/v1/notifications/signage-screens";

    private sealed record Setup(TestTenant T, Guid Operations, Guid Warehouse, Guid Office);

    private async Task<Setup> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sg-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "pia", MembershipTypes.Employee);
        await world.SeedUserAsync(t, "lara", MembershipTypes.Employee);
        await world.AssignAsync(t, "pia", BuiltInRoles.ContentPublisher, "tenant", null);
        var operations = await world.CreateUnitAsync(t, "Operations", null, "organization");
        var warehouse = await world.CreateUnitAsync(t, "RiyadhWarehouse", operations, "department");
        var office = await world.CreateUnitAsync(t, "JeddahOffice", operations, "department");
        await world.AssignAsync(t, "lara", BuiltInRoles.LocationAdministrator, "org_subtree", warehouse);
        return new Setup(t, operations, warehouse, office);
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = await call;
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return status == HttpStatusCode.NoContent ? default : await response.ReadAsync<JsonElement>();
    }

    private static object Variants(string title) => new Dictionary<string, object>
    {
        ["en"] = new { title, summary = title + " summary", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title } } } } } },
    };

    private static async Task<Guid> PublishAsync(HttpClient client, string title, bool signage = true, string type = "announcement", string classification = "public",
        object? audience = null)
    {
        var created = await OkAsync(client.PostAsJsonAsync(Items, new { type, classification, audience, signage, variants = Variants(title) }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(signage, created.GetProperty("signage").GetBoolean());
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private static object ForUnit(Guid unit, bool subtree = true) => new { anyOf = new[] { new { orgUnits = new[] { new { id = unit, subtree } } } } };

    private static string TokenOf(JsonElement created) => created.GetProperty("path").GetString()!["/screens/".Length..];

    private static async Task<string[]> TitlesAsync(HttpClient screen, string token) =>
        (await OkAsync(screen.GetAsync($"/api/v1/screens/{token}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public async Task A_screen_shows_the_public_signage_announcements_for_its_place_and_nothing_else()
    {
        var s = await SetupAsync();
        var pia = s.T["pia"].Client;
        await PublishAsync(pia, "Safety drill at 10");
        await PublishAsync(pia, "Warehouse shift change", audience: ForUnit(s.Warehouse, subtree: false));
        await PublishAsync(pia, "Operations town hall", audience: ForUnit(s.Operations));
        await PublishAsync(pia, "Riyadh canteen menu", audience: new { anyOf = new[] { new { locations = new[] { "Riyadh warehouse" } } } });
        // None of these may reach the screen: another place, not marked, not Public, a person audience, not an announcement.
        await PublishAsync(pia, "Jeddah parking", audience: ForUnit(s.Office));
        await PublishAsync(pia, "Not for screens", signage: false);
        await PublishAsync(pia, "Internal pay notice", classification: "internal");
        await PublishAsync(pia, "Contractors only", audience: new { anyOf = new[] { new { orgUnits = new[] { new { id = s.Warehouse, subtree = true } }, membershipTypes = new[] { "contractor" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, (await pia.PostAsJsonAsync(Items, new { type = "news", signage = true, variants = Variants("News on screens") })).StatusCode);

        var lara = s.T["lara"].Client;
        Assert.Equal(HttpStatusCode.BadRequest, (await lara.PostAsJsonAsync(Screens, new { name = " ", orgUnitId = s.Warehouse })).StatusCode);
        var created = await OkAsync(lara.PostAsJsonAsync(Screens, new { name = "Dock 3 screen", orgUnitId = s.Warehouse, location = "Riyadh Warehouse" }), HttpStatusCode.Created);
        var screenId = created.GetProperty("screen").GetProperty("id").GetGuid();
        await world.Database.AssertAuditedAsync(s.T.Id, AuditActions.NotificationSignageScreenRegistered, screenId.ToString());
        var token = TokenOf(created);

        var screen = world.Factory.TenantClient(s.T.Key);
        var feed = await OkAsync(screen.GetAsync($"/api/v1/screens/{token}"));
        Assert.Equal("Dock 3 screen", feed.GetProperty("name").GetString());
        Assert.InRange(feed.GetProperty("refreshSeconds").GetInt32(), 1, 30);
        var titles = feed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "Safety drill at 10", "Warehouse shift change", "Operations town hall", "Riyadh canteen menu" }, titles);
        // Only the title, summary, locale and time go to the screen: no body, audience or author.
        var item = feed.GetProperty("items")[0];
        Assert.Equal(["id", "locale", "publishedAt", "summary", "title"], item.EnumerateObject().Select(p => p.Name).Order().ToArray());

        // The administrator sees when the screen last asked.
        var listed = Assert.Single((await OkAsync(lara.GetAsync(Screens))).EnumerateArray());
        Assert.NotEqual(JsonValueKind.Null, listed.GetProperty("lastSeenAt").ValueKind);
    }

    [Fact]
    public async Task Withdrawing_an_announcement_or_revoking_or_replacing_the_address_takes_effect_on_the_next_read()
    {
        var s = await SetupAsync();
        var pia = s.T["pia"].Client;
        var drill = await PublishAsync(pia, "Safety drill at 10");
        await PublishAsync(pia, "Shift change");
        var lara = s.T["lara"].Client;
        var created = await OkAsync(lara.PostAsJsonAsync(Screens, new { name = "Dock 3 screen", orgUnitId = s.Warehouse }), HttpStatusCode.Created);
        var id = created.GetProperty("screen").GetProperty("id").GetGuid();
        var token = TokenOf(created);
        var screen = world.Factory.TenantClient(s.T.Key);
        Assert.Equal(2, (await TitlesAsync(screen, token)).Length);

        await OkAsync(pia.PostAsync($"{Items}/{drill}/unpublish", null));
        Assert.Equal(["Shift change"], await TitlesAsync(screen, token));

        var rotated = await OkAsync(lara.PostAsync($"{Screens}/{id}:rotate", null));
        await world.Database.AssertAuditedAsync(s.T.Id, AuditActions.NotificationSignageScreenRotated, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await screen.GetAsync($"/api/v1/screens/{token}")).StatusCode);
        var fresh = TokenOf(rotated);
        Assert.Equal(["Shift change"], await TitlesAsync(screen, fresh));

        await OkAsync(lara.DeleteAsync($"{Screens}/{id}"), HttpStatusCode.NoContent);
        await world.Database.AssertAuditedAsync(s.T.Id, AuditActions.NotificationSignageScreenRevoked, id.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await screen.GetAsync($"/api/v1/screens/{fresh}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await screen.GetAsync("/api/v1/screens/not-a-real-token-at-all-000")).StatusCode);
    }

    [Fact]
    public async Task Screens_stay_inside_their_unit_tenant_and_package()
    {
        var s = await SetupAsync();
        var lara = s.T["lara"].Client;

        // Lara manages the warehouse only: another unit is refused, its screens are not listed and read as not found.
        Assert.Equal(HttpStatusCode.Forbidden, (await lara.PostAsJsonAsync(Screens, new { name = "Jeddah lobby", orgUnitId = s.Office })).StatusCode);
        var office = await OkAsync(s.T["admin"].Client.PostAsJsonAsync(Screens, new { name = "Jeddah lobby", orgUnitId = s.Office }), HttpStatusCode.Created);
        var officeId = office.GetProperty("screen").GetProperty("id").GetGuid();
        Assert.Empty((await OkAsync(lara.GetAsync(Screens))).EnumerateArray());
        Assert.Equal(1, (await OkAsync(s.T["admin"].Client.GetAsync(Screens))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await lara.DeleteAsync($"{Screens}/{officeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await lara.PostAsync($"{Screens}/{officeId}:rotate", null)).StatusCode);
        // An employee without the role cannot see the section's data at all.
        Assert.Equal(HttpStatusCode.Forbidden, (await s.T["pia"].Client.GetAsync(Screens)).StatusCode);

        // The address opens nothing on another tenant's host.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await world.Factory.TenantClient(other.T.Key).GetAsync($"/api/v1/screens/{TokenOf(office)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await world.Factory.TenantClient(s.T.Key).GetAsync($"/api/v1/screens/{TokenOf(office)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.DeleteAsync($"{Screens}/{officeId}")).StatusCode);

        // Workplace has no signage: the section and its feeds are closed.
        var basic = await SetupAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic.T["admin"].Client.PostAsJsonAsync(Screens, new { name = "Lobby", orgUnitId = basic.Warehouse })).StatusCode);
        var nav = await OkAsync(basic.T["admin"].Client.GetAsync("/api/v1/admin/navigation"));
        Assert.DoesNotContain(nav.EnumerateArray(), n => n.GetProperty("key").GetString() == "signage");
        var navPlus = await OkAsync(lara.GetAsync("/api/v1/admin/navigation"));
        Assert.Contains(navPlus.EnumerateArray(), n => n.GetProperty("key").GetString() == "signage");
    }
}
