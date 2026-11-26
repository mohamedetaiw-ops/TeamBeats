using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 009 R3 T038 (US10, FR-025): a site shared read-only with a guest space. Only someone who manages the site and
/// owns the space shares it, for 1 to 365 days, never an archived site; the space's guests read the site's published
/// Public and Internal items and nothing else; revoking, expiry, archiving and leaving the space end it; shares are
/// audited; the package and tenant boundaries hold. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class GuestSiteTests(TenantWorld world)
{
    private const string Items = "/api/v1/content/items";
    private const string Sites = "/api/v1/content/sites";
    private const string Shared = "/api/v1/content/shared-with-space";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        return problem.TryGetProperty("errors", out var errors) ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()! : problem.GetProperty("detailKey").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static object Variants(string title) => new Dictionary<string, object>
    {
        ["en"] = new { title, summary = title + " in short.", body = new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text = title + " text." } } } } } },
    };

    private static async Task<Guid> PublishAsync(HttpClient client, Guid site, string title, string classification = "internal")
    {
        var id = Id(await OkAsync(client.PostAsJsonAsync(Items, new { siteId = site, type = "news", classification, variants = Variants(title) }), HttpStatusCode.Created));
        await OkAsync(client.PostAsJsonAsync($"{Items}/{id}/publish", new { }));
        return id;
    }

    private sealed record World(TestTenant T, Guid Site, Guid Space, Guid OtherSpace);

    private async Task<World> SetupAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("gst-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        await world.SeedUserAsync(t, "laila", MembershipTypes.Employee);
        var admin = t["admin"].Client;
        await OkAsync(admin.PutAsJsonAsync("/api/v1/spaces/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        var space = Id(await OkAsync(admin.PostAsJsonAsync("/api/v1/spaces", new { name = "Distributors", description = "Partners.", visibility = "private", template = "project" }),
            HttpStatusCode.Created));
        var other = Id(await OkAsync(t["laila"].Client.PostAsJsonAsync("/api/v1/spaces", new { name = "Laila's space", description = "Hers.", visibility = "private", template = "project" }),
            HttpStatusCode.Created));
        await world.SeedUserAsync(t, "partner", MembershipTypes.Guest);
        await OkAsync(admin.PostAsJsonAsync($"/api/v1/spaces/{space}/guests", new { email = t["partner"].Email }));
        var site = Id(await OkAsync(admin.PostAsJsonAsync(Sites, new { names = new Dictionary<string, string> { ["en"] = "Partner news", ["ar"] = "أخبار الشركاء" } }), HttpStatusCode.Created));
        return new World(t, site, space, other);
    }

    [Fact]
    public async Task A_site_shared_with_a_guest_space_shows_its_public_and_internal_items_to_the_guests_until_the_share_ends()
    {
        var w = await SetupAsync();
        var admin = w.T["admin"].Client;
        var partner = w.T["partner"].Client;
        var guide = await PublishAsync(admin, w.Site, "Price list guide");
        var open = await PublishAsync(admin, w.Site, "Open day", "public");
        var secret = await PublishAsync(admin, w.Site, "Margins", "confidential");
        var draft = Id(await OkAsync(admin.PostAsJsonAsync(Items, new { siteId = w.Site, type = "news", classification = "internal", variants = Variants("Draft") }), HttpStatusCode.Created));
        var elsewhere = await PublishAsync(admin, Id((await OkAsync(admin.GetAsync(Sites))).EnumerateArray().First(s => s.GetProperty("isDefault").GetBoolean())), "Staff party");

        // Before sharing, the guest finds nothing.
        Assert.Empty((await OkAsync(partner.GetAsync($"{Shared}/{w.Space}"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items")).StatusCode);

        // Only into a space the sharer owns, for 1 to 365 days; the guest and employees cannot share.
        var shares = $"{Sites}/{w.Site}/guest-shares";
        Assert.Equal("validation.content.share_space", await ProblemKeyAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.OtherSpace }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.content.share_days", await ProblemKeyAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.Space, days = 400 }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.NotFound, (await w.T["laila"].Client.PostAsJsonAsync(shares, new { spaceId = w.OtherSpace })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await partner.PostAsJsonAsync(shares, new { spaceId = w.Space })).StatusCode);
        var share = await OkAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.Space }), HttpStatusCode.Created);
        Assert.True(share.GetProperty("live").GetBoolean());
        Assert.Equal("Distributors", share.GetProperty("spaceName").GetString());
        Assert.Equal(30, (int)Math.Round((share.GetProperty("expiresAt").GetDateTimeOffset() - share.GetProperty("createdAt").GetDateTimeOffset()).TotalDays));
        await world.Database.AssertAuditedAsync(w.T.Id, AuditActions.SiteSharedWithGuests, w.Site.ToString());
        Assert.Equal("conflict.content.already_shared", await ProblemKeyAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.Space }), HttpStatusCode.Conflict));
        Assert.Single((await OkAsync(admin.GetAsync(shares))).EnumerateArray());

        // The guest reads the published Public and Internal items, and nothing else.
        var listed = Assert.Single((await OkAsync(partner.GetAsync($"{Shared}/{w.Space}"))).EnumerateArray());
        Assert.Equal(w.Site, listed.GetProperty("siteId").GetGuid());
        var items = (await OkAsync(partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items"))).EnumerateArray().Select(Id).ToArray();
        Assert.Equal(new[] { guide, open }.Order(), items.Order());
        var read = await OkAsync(partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items/{guide}"));
        Assert.Equal("Price list guide", read.GetProperty("title").GetString());
        Assert.Equal("Partner news", read.GetProperty("siteNames").GetProperty("en").GetString());
        foreach (var hidden in new[] { secret, draft, elsewhere })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items/{hidden}")).StatusCode);
        }
        Assert.NotEqual(HttpStatusCode.OK, (await partner.GetAsync($"{Items}/{guide}/view")).StatusCode);
        var feed = await partner.GetAsync("/api/v1/content/feed");
        if (feed.StatusCode == HttpStatusCode.OK)
        {
            Assert.DoesNotContain(guide.ToString(), await feed.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        // A person who does not read the space reaches nothing through it.
        Assert.Equal(HttpStatusCode.NotFound, (await w.T["laila"].Client.GetAsync($"{Shared}/{w.Space}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await partner.GetAsync($"{Shared}/{w.OtherSpace}")).StatusCode);

        // Revoking ends it at once and is audited; revoking twice is a conflict.
        var revoked = await OkAsync(admin.PostAsync($"/api/v1/content/site-guest-shares/{Id(share)}/revoke", null));
        Assert.False(revoked.GetProperty("live").GetBoolean());
        await world.Database.AssertAuditedAsync(w.T.Id, AuditActions.SiteGuestShareRevoked, w.Site.ToString());
        Assert.Equal("conflict.content.share_revoked", await ProblemKeyAsync(admin.PostAsync($"/api/v1/content/site-guest-shares/{Id(share)}/revoke", null), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.NotFound, (await partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items/{guide}")).StatusCode);

        // Expiry and archiving end a share too.
        var again = Id(await OkAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.Space, days = 5 }), HttpStatusCode.Created));
        Assert.Equal(2, (await OkAsync(partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items"))).GetArrayLength());
        await world.Database.OwnerExecInTenantAsync(w.T.Id, "update content.site_guest_share set expires_at = now() - interval '1 minute' where id = @id", ("id", again));
        Assert.Empty((await OkAsync(partner.GetAsync($"{Shared}/{w.Space}"))).EnumerateArray());
        await OkAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.Space }), HttpStatusCode.Created);
        await OkAsync(admin.PostAsync($"{Sites}/{w.Site}/archive", null));
        Assert.Empty((await OkAsync(partner.GetAsync($"{Shared}/{w.Space}"))).EnumerateArray());
        Assert.Equal("conflict.content.site_archived", await ProblemKeyAsync(admin.PostAsJsonAsync(shares, new { spaceId = w.Space }), HttpStatusCode.Conflict));
        await OkAsync(admin.PostAsync($"{Sites}/{w.Site}/restore", null));
        Assert.Single((await OkAsync(partner.GetAsync($"{Shared}/{w.Space}"))).EnumerateArray());

        // Leaving the space ends it for that guest (with no space left, the guest is also signed out).
        await world.Factory.InScopeAsync(w.T.Id, async sp =>
        {
            Assert.True(await sp.GetRequiredService<ISpaceGuestReview>().RemoveGuestAsync(w.Space, w.T["partner"].Id, "test", CancellationToken.None));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return true;
        });
        Assert.NotEqual(HttpStatusCode.OK, (await partner.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items")).StatusCode);

        // Another tenant reaches none of it.
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Sites}/{w.Site}/guest-shares")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.PostAsync($"/api/v1/content/site-guest-shares/{Id(share)}/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["partner"].Client.GetAsync($"{Shared}/{w.Space}/sites/{w.Site}/items")).StatusCode);
    }

    [Fact]
    public async Task Without_guest_sites_in_the_package_a_site_cannot_be_shared()
    {
        var w = await SetupAsync("workplace_plus");
        Assert.Equal(HttpStatusCode.Forbidden, (await w.T["admin"].Client.PostAsJsonAsync($"{Sites}/{w.Site}/guest-shares", new { spaceId = w.Space })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await w.T["partner"].Client.GetAsync($"{Shared}/{w.Space}")).StatusCode);
    }
}
