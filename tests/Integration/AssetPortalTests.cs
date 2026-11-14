using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R2 US5 (FR-012): brand managers curate collections and assets; an image gets PNG renditions at set widths,
/// its SVG and a PDF; open collections are read by every member, a restricted one by its unit and by people whose
/// access request a brand manager approved, with the request in the brand managers' task inbox; usage rights that end
/// take the asset out of the portal and the picker and tell other modules; every download is recorded for the report;
/// only brand managers curate; packages without the capability and other tenants are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class AssetPortalTests(TenantWorld world)
{
    private const string Api = "/api/v1/assets";

    /// <summary>A 2 by 1 PNG header is enough for the portal to read the size; the fake converter makes the renditions.</summary>
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0x02, 0x00, 0, 0, 0x01, 0x00, 8, 6, 0, 0, 0,
    ];

    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 100"><rect width="400" height="100" fill="#3b2a8f"/></svg>""";

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("ast-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "bm", "maya", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "bm", BuiltInRoles.BrandManager, "tenant", null);
        return t;
    }

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
        var body = JsonDocument.Parse(text).RootElement;
        if (body.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value.EnumerateArray().First().GetProperty("key").GetString()!;
        }
        return body.TryGetProperty("detailKey", out var detail) ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid collection, string name, byte[] bytes, string? query = null) =>
        client.PostAsync($"{Api}/collections/{collection}/assets?name={Uri.EscapeDataString(name)}{query}", new ByteArrayContent(bytes));

    private static async Task<Guid> CollectionAsync(HttpClient client, string name, Guid? unit = null) =>
        Id(await OkAsync(client.PostAsJsonAsync($"{Api}/collections", new { names = new Dictionary<string, string> { ["en"] = name }, restrictedTo = unit }),
            HttpStatusCode.Created));

    [Fact]
    public async Task A_brand_manager_curates_assets_with_renditions_and_every_member_downloads_them()
    {
        var t = await FreshAsync();
        var (bm, maya) = (t["bm"].Client, t["maya"].Client);

        // Only brand managers curate; the portal says so before there is any collection.
        Assert.True((await OkAsync(bm.GetAsync($"{Api}/portal"))).GetProperty("canManage").GetBoolean());
        Assert.False((await OkAsync(maya.GetAsync($"{Api}/portal"))).GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await maya.PostAsJsonAsync($"{Api}/collections", new { names = new Dictionary<string, string> { ["en"] = "Mine" } })).StatusCode);
        Assert.Equal("validation.assets.name",
            await ProblemKeyAsync(bm.PostAsJsonAsync($"{Api}/collections", new { names = new Dictionary<string, string> { ["en"] = " " } }), HttpStatusCode.BadRequest));
        var logos = await CollectionAsync(bm, "Logos");
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetCollectionCreated, logos.ToString());

        // An image gets its renditions: PNG at the widths that fit (the smallest always), a PDF; an SVG keeps itself too.
        var before = world.Factory.Gotenberg.Screenshots;
        var png = await OkAsync(UploadAsync(bm, logos, "mark.png", Png, "&title=Primary%20mark&notes=Keep%20clear%20space"), HttpStatusCode.Created);
        var mark = Id(png);
        Assert.Equal(512, png.GetProperty("width").GetInt32());
        Assert.Equal(256, png.GetProperty("height").GetInt32());
        var keys = png.GetProperty("renditions").EnumerateArray().Where(r => r.GetProperty("ready").GetBoolean()).Select(r => r.GetProperty("key").GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "original", "png_256", "png_512", "pdf" }, keys);
        Assert.Equal(before + 2, world.Factory.Gotenberg.Screenshots);
        Assert.Equal("Primary mark", png.GetProperty("names").GetProperty("en").GetString());
        Assert.Equal("Keep clear space", png.GetProperty("usageNotes").GetProperty("en").GetString());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetUploaded, mark.ToString());

        var svg = await OkAsync(UploadAsync(bm, logos, "wordmark.svg", Encoding.UTF8.GetBytes(Svg)), HttpStatusCode.Created);
        Assert.Equal(400, svg.GetProperty("width").GetInt32());
        Assert.Contains(svg.GetProperty("renditions").EnumerateArray(), r => r.GetProperty("key").GetString() == "svg");
        var template = await OkAsync(UploadAsync(bm, logos, "letterhead.docx", Encoding.UTF8.GetBytes("PK fake docx")), HttpStatusCode.Created);
        Assert.Equal("original", Assert.Single(template.GetProperty("renditions").EnumerateArray()).GetProperty("key").GetString());

        // What the portal does not keep, and an SVG that is not one.
        Assert.Equal("validation.assets.file_type", await ProblemKeyAsync(UploadAsync(bm, logos, "run.exe", [1, 2, 3]), HttpStatusCode.BadRequest));
        Assert.Equal("validation.assets.file_type", await ProblemKeyAsync(UploadAsync(bm, logos, "fake.svg", Encoding.UTF8.GetBytes("not an image")), HttpStatusCode.BadRequest));
        Assert.Equal("validation.assets.rights_expiry",
            await ProblemKeyAsync(UploadAsync(bm, logos, "old.png", Png, $"&rightsExpireAt={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"))}"), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(maya, logos, "mine.png", Png)).StatusCode);

        // Every member reads an open collection and downloads; each download is recorded with the rendition.
        var seen = await OkAsync(maya.GetAsync($"{Api}/collections/{logos}/assets"));
        Assert.Equal(3, seen.GetArrayLength());
        var file = await maya.GetAsync($"{Api}/{mark}/renditions/png_512");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal("image/png", file.Content.Headers.ContentType?.MediaType);
        Assert.Contains(" fake 512x256", Encoding.ASCII.GetString(await file.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await maya.GetAsync($"{Api}/{mark}/renditions/original")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["omar"].Client.GetAsync($"{Api}/{mark}/renditions/png_512")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await maya.GetAsync($"{Api}/{mark}/renditions/png_1024")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await maya.GetAsync($"{Api}/{mark}/thumbnail")).StatusCode);

        var report = await OkAsync(bm.GetAsync($"{Api}/downloads"));
        Assert.Equal(3, report.GetProperty("downloads").GetArrayLength());
        var total = report.GetProperty("totals").EnumerateArray().Single(r => r.GetProperty("rendition").GetString() == "png_512");
        Assert.Equal(2, total.GetProperty("downloads").GetInt32());
        Assert.Equal(2, total.GetProperty("people").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await maya.GetAsync($"{Api}/downloads")).StatusCode);

        // The picker offers current images only.
        var picks = await OkAsync(maya.GetAsync($"{Api}/picker"));
        Assert.Equal(2, picks.GetArrayLength());
        Assert.Single((await OkAsync(maya.GetAsync($"{Api}/picker?q=primary"))).EnumerateArray());

        // Retired, an asset leaves the portal for readers; brand managers still see it.
        await OkAsync(bm.PutAsJsonAsync($"{Api}/{mark}", new { names = new Dictionary<string, string> { ["en"] = "Primary mark" }, status = "retired", version = png.GetProperty("version").GetInt32() }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetChanged, mark.ToString());
        Assert.Equal(2, (await OkAsync(maya.GetAsync($"{Api}/collections/{logos}/assets"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await maya.GetAsync($"{Api}/{mark}/renditions/png_512")).StatusCode);
        Assert.Equal(3, (await OkAsync(bm.GetAsync($"{Api}/collections/{logos}/assets"))).GetArrayLength());

        // Another tenant reaches nothing; a package without the portal is refused.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["bm"].Client.GetAsync($"{Api}/collections/{logos}/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["maya"].Client.GetAsync($"{Api}/{mark}/renditions/original")).StatusCode);
        var small = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await small["bm"].Client.GetAsync($"{Api}/collections")).StatusCode);
    }

    [Fact]
    public async Task A_restricted_collection_is_read_by_its_unit_and_by_people_whose_request_was_approved()
    {
        var t = await FreshAsync();
        var (bm, maya, omar) = (t["bm"].Client, t["maya"].Client, t["omar"].Client);
        var marketing = await world.CreateUnitAsync(t, "Marketing", null, "department");
        await world.AddMembershipAsync(t, "maya", "Marketing");
        Assert.Equal("validation.assets.unit",
            await ProblemKeyAsync(bm.PostAsJsonAsync($"{Api}/collections", new { names = new Dictionary<string, string> { ["en"] = "X" }, restrictedTo = Guid.NewGuid() }),
                HttpStatusCode.BadRequest));
        var campaign = await CollectionAsync(bm, "Campaign 2027", marketing);
        var hero = Id(await OkAsync(UploadAsync(bm, campaign, "hero.png", Png), HttpStatusCode.Created));

        // The unit reads it; others see it exists, cannot open it, and can ask.
        Assert.Single((await OkAsync(maya.GetAsync($"{Api}/collections/{campaign}/assets"))).EnumerateArray());
        var listed = (await OkAsync(omar.GetAsync($"{Api}/collections"))).EnumerateArray().Single(c => Id(c) == campaign);
        Assert.False(listed.GetProperty("canRead").GetBoolean());
        Assert.Equal(0, listed.GetProperty("assetCount").GetInt32());
        Assert.Equal("authz.denied.assets_restricted", await ProblemKeyAsync(omar.GetAsync($"{Api}/collections/{campaign}/assets"), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, (await omar.GetAsync($"{Api}/{hero}/renditions/original")).StatusCode);
        Assert.Empty((await OkAsync(omar.GetAsync($"{Api}/picker"))).EnumerateArray());

        Assert.Equal("validation.assets.reason",
            await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/collections/{campaign}/access-requests", new { reason = "" }), HttpStatusCode.BadRequest));
        Assert.Equal("conflict.assets.already_readable",
            await ProblemKeyAsync(maya.PostAsJsonAsync($"{Api}/collections/{campaign}/access-requests", new { reason = "Need it" }), HttpStatusCode.Conflict));
        var asked = await OkAsync(omar.PostAsJsonAsync($"{Api}/collections/{campaign}/access-requests", new { reason = "Regional launch deck" }));
        Assert.True(asked.GetProperty("accessRequested").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetAccessRequested, campaign.ToString());
        Assert.Equal("conflict.assets.already_requested",
            await ProblemKeyAsync(omar.PostAsJsonAsync($"{Api}/collections/{campaign}/access-requests", new { reason = "Again" }), HttpStatusCode.Conflict));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'assets.access_requested'", ("u", t["bm"].Id)));

        // The request is in the brand manager's task inbox, and nobody else's.
        var task = Assert.Single((await OkAsync(bm.GetAsync("/api/v1/tasks"))).EnumerateArray(), i => i.GetProperty("source").GetString() == "asset_access");
        Assert.Equal("/assets/requests", task.GetProperty("link").GetString());
        Assert.DoesNotContain((await OkAsync(maya.GetAsync("/api/v1/tasks"))).EnumerateArray(), i => i.GetProperty("source").GetString() == "asset_access");

        var request = (await OkAsync(bm.GetAsync($"{Api}/access-requests"))).EnumerateArray().Single(r => r.GetProperty("status").GetString() == "pending");
        Assert.Equal("Regional launch deck", request.GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await omar.PostAsJsonAsync($"{Api}/access-requests/{Id(request)}", new { decision = "approve" })).StatusCode);
        Assert.Equal("validation.assets.decision",
            await ProblemKeyAsync(bm.PostAsJsonAsync($"{Api}/access-requests/{Id(request)}", new { decision = "maybe" }), HttpStatusCode.BadRequest));
        await OkAsync(bm.PostAsJsonAsync($"{Api}/access-requests/{Id(request)}", new { decision = "approve" }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetAccessDecided, campaign.ToString());
        Assert.Equal("conflict.assets.request_decided",
            await ProblemKeyAsync(bm.PostAsJsonAsync($"{Api}/access-requests/{Id(request)}", new { decision = "decline" }), HttpStatusCode.Conflict));

        // Approved: Omar reads it, and is told.
        Assert.Single((await OkAsync(omar.GetAsync($"{Api}/collections/{campaign}/assets"))).EnumerateArray());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'assets.access_approved'", ("u", t["omar"].Id)));
        Assert.DoesNotContain((await OkAsync(bm.GetAsync("/api/v1/tasks"))).EnumerateArray(), i => i.GetProperty("source").GetString() == "asset_access");

        // Changing the collection keeps who was given access; opening it to everyone reaches every member.
        var current = (await OkAsync(bm.GetAsync($"{Api}/collections"))).EnumerateArray().Single(c => Id(c) == campaign);
        var changed = await OkAsync(bm.PutAsJsonAsync($"{Api}/collections/{campaign}", new
        {
            names = new Dictionary<string, string> { ["en"] = "Campaign 2027" }, restrictedTo = (Guid?)null, version = current.GetProperty("version").GetInt32(),
        }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetCollectionChanged, campaign.ToString());
        Assert.Null(changed.GetProperty("restrictedTo").GetString());
        await world.SeedUserAsync(t, "lina", MembershipTypes.Employee);
        Assert.Single((await OkAsync(t["lina"].Client.GetAsync($"{Api}/collections/{campaign}/assets"))).EnumerateArray());
        // A guest never reads a collection open to the members.
        await world.SeedUserAsync(t, "partner", MembershipTypes.Guest);
        Assert.Equal(HttpStatusCode.NotFound, (await t["partner"].Client.GetAsync($"{Api}/collections/{campaign}/assets")).StatusCode);
        Assert.Empty((await OkAsync(t["partner"].Client.GetAsync($"{Api}/collections"))).EnumerateArray());
    }

    [Fact]
    public async Task An_asset_whose_usage_rights_end_leaves_the_portal_and_the_picker()
    {
        var t = await FreshAsync();
        var (bm, maya) = (t["bm"].Client, t["maya"].Client);
        var photos = await CollectionAsync(bm, "Photography");
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        var shot = await OkAsync(UploadAsync(bm, photos, "office.png", Png, $"&rightsExpireAt={Uri.EscapeDataString(expiry.ToString("O"))}"), HttpStatusCode.Created);
        var id = Id(shot);
        Assert.Single((await OkAsync(maya.GetAsync($"{Api}/picker"))).EnumerateArray());

        Assert.Equal(1, await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var done = await sp.GetRequiredService<Assets>().ExpireAsync(expiry.AddMinutes(1), 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return done;
        }));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.AssetExpired, id.ToString());
        Assert.Equal(1L, await world.Database.OutboxCountAsync("AssetRightsExpired", t.Id));

        Assert.Empty((await OkAsync(maya.GetAsync($"{Api}/collections/{photos}/assets"))).EnumerateArray());
        Assert.Empty((await OkAsync(maya.GetAsync($"{Api}/picker"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await maya.GetAsync($"{Api}/{id}/renditions/original")).StatusCode);
        var managed = Assert.Single((await OkAsync(bm.GetAsync($"{Api}/collections/{photos}/assets"))).EnumerateArray());
        Assert.True(managed.GetProperty("expired").GetBoolean());

        // New rights bring it back.
        await OkAsync(bm.PutAsJsonAsync($"{Api}/{id}", new
        {
            names = new Dictionary<string, string> { ["en"] = "office" }, rightsExpireAt = DateTimeOffset.UtcNow.AddYears(1), version = managed.GetProperty("version").GetInt32(),
        }));
        Assert.Single((await OkAsync(maya.GetAsync($"{Api}/picker"))).EnumerateArray());
    }

    [Fact]
    public async Task A_page_shows_a_current_asset_image_and_its_owner_hears_when_the_rights_end()
    {
        var t = await FreshAsync();
        var (bm, maya, author) = (t["bm"].Client, t["maya"].Client, t["admin"].Client);
        var photos = await CollectionAsync(bm, "Photography");
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        var id = Id(await OkAsync(UploadAsync(bm, photos, "office.png", Png, $"&rightsExpireAt={Uri.EscapeDataString(expiry.ToString("O"))}"), HttpStatusCode.Created));
        static object Page(Guid asset) => new
        {
            type = "news",
            variants = new Dictionary<string, object>
            {
                ["en"] = new { title = "New office", body = new { blocks = new object[] { new { type = "image", assetId = asset, alt = "The new office" } } } },
            },
        };

        // Only a current asset the author reads goes on a page.
        Assert.Equal("validation.content.image", await ProblemKeyAsync(author.PostAsJsonAsync("/api/v1/content/items", Page(Guid.NewGuid())), HttpStatusCode.BadRequest));
        var item = Id(await OkAsync(author.PostAsJsonAsync("/api/v1/content/items", Page(id)), HttpStatusCode.Created));

        // Readers get the page image without it counting as a download; another tenant never does.
        var image = await maya.GetAsync($"{Api}/{id}/image");
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.asset_download"));
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["maya"].Client.GetAsync($"{Api}/{id}/image")).StatusCode);
        Assert.Equal("validation.content.image",
            await ProblemKeyAsync(other["admin"].Client.PostAsJsonAsync("/api/v1/content/items", Page(id)), HttpStatusCode.BadRequest));

        // The rights end: the image leaves the page for everyone and the page's owner is told which page it was.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            await sp.GetRequiredService<Assets>().ExpireAsync(expiry.AddMinutes(1), 100, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < 20 && await dispatcher.DispatchBatchAsync(500, CancellationToken.None) > 0; i++)
        {
        }
        Assert.Equal(HttpStatusCode.NotFound, (await maya.GetAsync($"{Api}/{id}/image")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bm.GetAsync($"{Api}/{id}/image")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from notifications.notification where user_id = @u and template_key = 'content.asset_image_expired' and link = @link",
            ("u", t["admin"].Id), ("link", $"/content/items/{item}")));

        // A new page cannot use it; the page that has it can still be edited until its owner replaces the image.
        Assert.Equal("validation.content.image", await ProblemKeyAsync(author.PostAsJsonAsync("/api/v1/content/items", Page(id)), HttpStatusCode.BadRequest));
        var page = (JsonElement)JsonSerializer.SerializeToElement(Page(id));
        await OkAsync(author.PutAsJsonAsync($"/api/v1/content/items/{item}", new { variants = page.GetProperty("variants"), changeNote = "Typo" }));
    }
}
