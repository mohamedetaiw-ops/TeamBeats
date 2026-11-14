using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R2 US8 (FR-016, FR-017): a view-only library gives readers a watermarked preview and refuses them every
/// download in the API while contributors still download; a library that allows it shares folders read-only with a guest
/// space the manager owns, for a set number of days, with every guest view audited; revoking, expiry and turning sharing
/// off end the guests' access; a Restricted library is never shared; packages without the capability are refused and
/// other tenants see nothing.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentSharingTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private sealed record Org(TestTenant T, Guid Library);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("shr-" + Guid.NewGuid().ToString("N")[..8], package);
        foreach (var name in new[] { "con", "dee", "laila" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        if (package != "workplace")
        {
            // Custom roles are not in Workplace; there the tenant administrator manages the library.
            var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
            {
                key = "doc_manager", names = new Dictionary<string, string> { ["en"] = "Library manager" },
                permissions = new[] { Permissions.DocView, Permissions.DocContribute, Permissions.DocManage }, delegable = true,
            });
            Assert.Equal(HttpStatusCode.Created, role.StatusCode);
            await world.AssignAsync(t, "con", "doc_manager", "tenant", null);
        }
        var library = await OkAsync(t["admin"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Audit evidence", ["ar"] = "أدلة التدقيق" }, scope = "tenant",
        }), HttpStatusCode.Created);
        return new Org(t, Id(library));
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

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> call) => (await call).StatusCode;

    /// <summary>What a guest gets for what is not shared with them: nothing they can read.</summary>
    private static async Task AssertHiddenAsync(Task<HttpResponseMessage> call)
    {
        var status = (await call).StatusCode;
        Assert.True(status is HttpStatusCode.NotFound or HttpStatusCode.Forbidden, status.ToString());
    }

    private static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();

    private static async Task<Guid> PublishedAsync(HttpClient client, Guid library, string name, Guid? folder = null)
    {
        var url = $"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true" + (folder is { } f ? $"&folder={f}" : "");
        return Id(await OkAsync(client.PostAsync(url, new ByteArrayContent(Encoding.UTF8.GetBytes("content of " + name))), HttpStatusCode.Created));
    }

    /// <summary>Changes the library's settings the way the settings form does: everything it has, with the change.</summary>
    private static async Task<HttpResponseMessage> SetAsync(HttpClient client, Guid library, object change, HttpClient? reader = null)
    {
        var view = await OkAsync((reader ?? client).GetAsync($"{Libraries}/{library}"));
        var body = new Dictionary<string, object?>
        {
            ["names"] = JsonSerializer.Deserialize<Dictionary<string, string>>(view.GetProperty("names").GetRawText()),
            ["allowedExtensions"] = null,
            ["maxFileMb"] = null,
            ["classificationDefault"] = view.GetProperty("classificationDefault").GetString(),
            ["version"] = view.GetProperty("version").GetInt32(),
        };
        foreach (var p in change.GetType().GetProperties())
        {
            body[p.Name] = p.GetValue(change);
        }
        return await client.PutAsJsonAsync($"{Libraries}/{library}", body);
    }

    [Fact]
    public async Task A_view_only_library_gives_readers_a_watermarked_preview_and_no_download()
    {
        var o = await FreshAsync();
        var (t, con, dee) = (o.T, o.T["con"].Client, o.T["dee"].Client);
        var doc = await PublishedAsync(con, o.Library, "audit-plan.txt");
        await OkAsync(dee.GetAsync($"{Docs}/{doc}/download-token"));

        // Only a manager turns it on.
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(dee, o.Library, new { viewOnly = true })).StatusCode);
        var on = await OkAsync(SetAsync(con, o.Library, new { viewOnly = true }));
        Assert.True(on.GetProperty("viewOnly").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocLibraryChanged, o.Library.ToString());

        // The reader sees the document as not downloadable and previews it with their name, email and the time.
        var seen = await OkAsync(dee.GetAsync($"{Docs}/{doc}"));
        Assert.False(seen.GetProperty("downloadable").GetBoolean());
        var preview = await OkAsync(dee.GetAsync($"{Docs}/{doc}/preview"));
        var watermark = preview.GetProperty("watermark").GetString()!;
        Assert.Contains(t["dee"].Email, watermark, StringComparison.Ordinal);
        Assert.Contains("UTC", watermark, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(dee.GetAsync(preview.GetProperty("url").GetString())));

        // The API refuses the reader a file, whichever way they ask.
        Assert.Equal("authz.denied.documents_view_only", await ProblemKeyAsync(dee.GetAsync($"{Docs}/{doc}/download-token"), HttpStatusCode.Forbidden));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(dee.GetAsync($"{Docs}/{doc}/controlled-copy")));

        // Contributors still download, and their previews carry no watermark.
        Assert.True((await OkAsync(con.GetAsync($"{Docs}/{doc}"))).GetProperty("downloadable").GetBoolean());
        await OkAsync(con.GetAsync($"{Docs}/{doc}/download-token"));
        Assert.Equal(JsonValueKind.Null, (await OkAsync(con.GetAsync($"{Docs}/{doc}/preview"))).GetProperty("watermark").ValueKind);

        // Turned off, the reader downloads again.
        await OkAsync(SetAsync(con, o.Library, new { viewOnly = false }));
        await OkAsync(dee.GetAsync($"{Docs}/{doc}/download-token"));

        // A package without the capability cannot turn it on; other tenants never reach the library.
        var small = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(small.T["admin"].Client, small.Library, new { viewOnly = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(small.T["admin"].Client, o.Library, new { viewOnly = true }, t["admin"].Client)).StatusCode);
    }

    [Fact]
    public async Task A_folder_shared_with_a_guest_space_is_read_only_to_its_guests_until_the_share_ends()
    {
        var o = await FreshAsync();
        var (t, con) = (o.T, o.T["con"].Client);
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/spaces/manage/settings", new { creationUnits = Array.Empty<Guid>(), guestsAllowed = true, guestDays = 60, version = 0 }));
        var space = Id(await OkAsync(con.PostAsJsonAsync("/api/v1/spaces", new { name = "External audit", description = "Audit.", visibility = "private", template = "project" }),
            HttpStatusCode.Created));
        var lailaSpace = Id(await OkAsync(t["laila"].Client.PostAsJsonAsync("/api/v1/spaces", new { name = "Laila's space", description = "Hers.", visibility = "private", template = "project" }),
            HttpStatusCode.Created));
        await world.SeedUserAsync(t, "auditor", MembershipTypes.Guest);
        await OkAsync(con.PostAsJsonAsync($"/api/v1/spaces/{space}/guests", new { email = t["auditor"].Email }));
        var auditor = t["auditor"].Client;

        var folder = Id(await OkAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/folders", new { name = "Evidence" }), HttpStatusCode.Created));
        var inFolder = await PublishedAsync(con, o.Library, "access-review.txt", folder);
        var elsewhere = await PublishedAsync(con, o.Library, "board-minutes.txt");
        await AssertHiddenAsync(auditor.GetAsync($"{Docs}/{inFolder}"));

        // Sharing needs the library to allow it, and turning that on needs a manager.
        Assert.Equal("conflict.documents.guest_sharing_off",
            await ProblemKeyAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space }), HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(t["dee"].Client, o.Library, new { guestSharing = true })).StatusCode);
        await OkAsync(SetAsync(con, o.Library, new { guestSharing = true }));

        // Only a space the manager owns, for 1 to 365 days, and only a folder of this library.
        Assert.Equal("validation.documents.share_space",
            await ProblemKeyAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = lailaSpace }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.share_days",
            await ProblemKeyAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space, days = 400 }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.documents.share_folder",
            await ProblemKeyAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = Guid.NewGuid(), spaceId = space }), HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusAsync(t["dee"].Client.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space })));

        var share = await OkAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space, days = 30 }), HttpStatusCode.Created);
        var shareId = Id(share);
        Assert.True(share.GetProperty("live").GetBoolean());
        Assert.Equal("External audit", share.GetProperty("spaceName").GetString());
        Assert.InRange(share.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddDays(29), DateTimeOffset.UtcNow.AddDays(31));
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocSharedWithGuest, folder.ToString());
        // The folder got its own permissions, so the share reaches exactly it.
        Assert.False((await OkAsync(con.GetAsync($"{Libraries}/{o.Library}/folders"))).EnumerateArray().Single(f => Id(f) == folder).GetProperty("inherits").GetBoolean());
        Assert.Equal("conflict.documents.already_shared",
            await ProblemKeyAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space }), HttpStatusCode.Conflict));

        // The guest finds the folder from the space and reads what is in it, each view audited; nothing else, and no change.
        var listed = Assert.Single((await OkAsync(auditor.GetAsync($"{Docs}/shared-with-space/{space}"))).EnumerateArray());
        Assert.Equal(folder, listed.GetProperty("folderId").GetGuid());
        Assert.Contains((await OkAsync(auditor.GetAsync(Libraries))).EnumerateArray(), l => Id(l) == o.Library);
        var read = await OkAsync(auditor.GetAsync($"{Docs}/{inFolder}"));
        Assert.False(read.GetProperty("rights").GetProperty("contribute").GetBoolean());
        await OkAsync(auditor.GetAsync($"{Docs}/{inFolder}/preview"));
        Assert.Equal(2L, await world.Database.AuditCountAsync(t.Id, AuditActions.DocGuestViewed, inFolder.ToString()));
        await AssertHiddenAsync(auditor.GetAsync($"{Docs}/{elsewhere}"));
        Assert.NotEqual(HttpStatusCode.Created, await StatusAsync(auditor.PostAsync($"{Libraries}/{o.Library}/files?name=mine.txt&folder={folder}",
            new ByteArrayContent(Encoding.UTF8.GetBytes("mine")))));
        await AssertHiddenAsync(auditor.GetAsync($"{Libraries}/{o.Library}/guest-shares"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(auditor.GetAsync($"{Docs}/shared-with-space/{lailaSpace}")));
        // A member of the tenant gains nothing from a share.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(t["laila"].Client.GetAsync($"{Docs}/shared-with-space/{space}")));

        // Another tenant sees neither the share nor the folder.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other.T["con"].Client.GetAsync($"{Libraries}/{o.Library}/guest-shares")));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(other.T["con"].Client.PostAsync($"{Docs}/guest-shares/{shareId}/revoke", null)));

        // Revoked: the guest reads nothing more.
        var revoked = await OkAsync(con.PostAsync($"{Docs}/guest-shares/{shareId}/revoke", null));
        Assert.False(revoked.GetProperty("live").GetBoolean());
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.DocGuestShareRevoked, folder.ToString());
        await AssertHiddenAsync(auditor.GetAsync($"{Docs}/{inFolder}"));
        Assert.Equal("conflict.documents.share_revoked", await ProblemKeyAsync(con.PostAsync($"{Docs}/guest-shares/{shareId}/revoke", null), HttpStatusCode.Conflict));

        // Expired: the same.
        var again = Id(await OkAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space, days = 1 }), HttpStatusCode.Created));
        await OkAsync(auditor.GetAsync($"{Docs}/{inFolder}"));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update documents.guest_share set expires_at = now() - interval '1 minute' where id = @id", ("id", again));
        await AssertHiddenAsync(auditor.GetAsync($"{Docs}/{inFolder}"));
        Assert.Empty((await OkAsync(auditor.GetAsync($"{Docs}/shared-with-space/{space}"))).EnumerateArray());

        // Turning sharing off ends every live share at once.
        await OkAsync(con.PostAsJsonAsync($"{Libraries}/{o.Library}/guest-shares", new { folderId = folder, spaceId = space }), HttpStatusCode.Created);
        await OkAsync(auditor.GetAsync($"{Docs}/{inFolder}"));
        await OkAsync(SetAsync(con, o.Library, new { guestSharing = false }));
        await AssertHiddenAsync(auditor.GetAsync($"{Docs}/{inFolder}"));
        Assert.DoesNotContain((await OkAsync(con.GetAsync($"{Libraries}/{o.Library}/guest-shares"))).EnumerateArray(), s => s.GetProperty("live").GetBoolean());

        // A Restricted library is never shared with guests.
        Assert.Equal("validation.documents.guest_sharing_restricted",
            await ProblemKeyAsync(SetAsync(con, o.Library, new { guestSharing = true, classificationDefault = "restricted" }), HttpStatusCode.BadRequest));

        // A package without the capability cannot allow sharing.
        var small = await FreshAsync("workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(small.T["admin"].Client, small.Library, new { guestSharing = true })).StatusCode);
    }
}
