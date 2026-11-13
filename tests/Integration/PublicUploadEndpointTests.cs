using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;
using static TeamBeats.Integration.Tests.PublicApiTokens;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 035 T011, <c>documents:write</c> (011). A person uploads a document or a new version with their own rights at
/// the library, as in TeamBeats: a contributor at Acme Gulf uploads to the Gulf library, an employee who only reads is
/// refused, the Egypt library is not found, and the same file twice is a duplicate. An application app acting as
/// itself never uploads, since a document's owner and author are people.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PublicUploadEndpointTests(TenantWorld world)
{
    private const string Public = "/api/public/v1";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Egypt, Guid GulfLibrary, Guid EgyptLibrary);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pu-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus", "en", ["en", "ar"]);
        var group = await world.CreateUnitAsync(t, "Group", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", group, "subsidiary");
        var egypt = await world.CreateUnitAsync(t, "Egypt", group, "subsidiary");
        foreach (var (name, unit) in new[] { ("gia", "Gulf"), ("gus", "Gulf") })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, unit);
        }
        await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "doc_contributor", names = new Dictionary<string, string> { ["en"] = "Contributor" },
            permissions = new[] { Permissions.DocView, Permissions.DocContribute }, delegable = true,
        }), HttpStatusCode.Created);
        await world.AssignAsync(t, "gia", "doc_contributor", "org_unit", gulf);
        await world.SetCapabilityAsync(t, TeamBeats.Entitlements.Contracts.Capabilities.ApiMaxApps, new { enabled = true, limit = 20 });
        return new Org(t, gulf, egypt, await LibraryAsync(t, "Gulf quality", gulf), await LibraryAsync(t, "Egypt quality", egypt));
    }

    private static async Task<Guid> LibraryAsync(TestTenant t, string name, Guid unit) =>
        Id(await JsonAsync(await t["admin"].Client.PostAsJsonAsync("/api/v1/documents/libraries",
            new { names = new Dictionary<string, string> { ["en"] = name, ["ar"] = name + " ع" }, scope = "org_unit", scopeId = unit }), HttpStatusCode.Created));

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private Task<HttpResponseMessage> UploadAsync(TestTenant t, string token, string path, string content) =>
        SendAsync(world, t, token, HttpMethod.Post, path, null, r => r.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)));

    [Fact]
    public async Task A_contributor_uploads_a_document_and_a_new_version_with_their_own_rights()
    {
        var o = await FreshAsync();
        var t = o.T;
        var gia = await PersonAsync(world, t, "gia", ["documents:read", "documents:write"]);

        var doc = await JsonAsync(await UploadAsync(t, gia, $"{Public}/libraries/{o.GulfLibrary}/documents?name=guide.txt&publish=true", "Forklift guide"),
            HttpStatusCode.Created);
        Assert.Equal("guide.txt", doc.GetProperty("name").GetString());
        Assert.Equal("1.0", doc.GetProperty("version").GetProperty("label").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from documents.document where id = '{Id(doc)}' and owner_user_id = '{t["gia"].Id}'"));

        // The same file again is a duplicate; a new version is checked in as the next minor version.
        Assert.Equal(HttpStatusCode.Conflict,
            (await UploadAsync(t, gia, $"{Public}/libraries/{o.GulfLibrary}/documents?name=copy.txt", "Forklift guide")).StatusCode);
        var updated = await JsonAsync(await UploadAsync(t, gia, $"{Public}/documents/{Id(doc)}/versions?name=guide.txt&note=Brakes", "Forklift guide, brakes"),
            HttpStatusCode.Created);
        Assert.Equal("1.1", updated.GetProperty("version").GetProperty("label").GetString());
        Assert.Equal(2, (await JsonAsync(await SendAsync(world, t, gia, HttpMethod.Get, $"{Public}/documents/{Id(doc)}/versions")))
            .GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(t, gia, $"{Public}/documents/{Id(doc)}/versions", "No name")).StatusCode);

        // Another unit's library is not found; a reader without contribute rights is refused.
        Assert.Equal(HttpStatusCode.NotFound,
            (await UploadAsync(t, gia, $"{Public}/libraries/{o.EgyptLibrary}/documents?name=egypt.txt", "Egypt")).StatusCode);
        var gus = await PersonAsync(world, t, "gus", ["documents:write"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(t, gus, $"{Public}/libraries/{o.GulfLibrary}/documents?name=gus.txt", "Gus")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(t, gus, $"{Public}/documents/{Id(doc)}/versions?name=guide.txt", "Gus edit")).StatusCode);

        // Another tenant does not accept the token.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await UploadAsync(other.T, gia, $"{Public}/libraries/{other.GulfLibrary}/documents?name=x.txt", "x")).StatusCode);
    }

    [Fact]
    public async Task An_application_app_never_uploads()
    {
        var o = await FreshAsync();
        var t = o.T;
        var app = await AppAsync(world, t, ["documents:read", "documents:write"], "tenant", null);
        var refused = await UploadAsync(t, app, $"{Public}/libraries/{o.GulfLibrary}/documents?name=app.txt", "From the app");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("documents.app_upload", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.document"));
    }
}
