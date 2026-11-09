using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 T008 (FR-009): PDF, image, video and text preview from the file itself; Office files converted to PDF through
/// the Gotenberg adapter once per version; a failed conversion gives "preview not available"; preview tokens are bound
/// to the person and the tenant, open only previews and are not downloads; unscanned or infected files never preview.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentPreviewTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private async Task<(TestTenant T, Guid Library)> FreshAsync()
    {
        var t = await world.CreateTenantAsync("dp-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        await world.CreateUnitAsync(t, "Cairo", holding, "department");
        foreach (var name in new[] { "con", "dee", "cai" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AddMembershipAsync(t, "con", "Dubai");
        await world.AddMembershipAsync(t, "dee", "Dubai");
        await world.AddMembershipAsync(t, "cai", "Cairo");
        var role = await t["admin"].Client.PostAsJsonAsync("/api/v1/authz/roles", new
        {
            key = "doc_contributor", names = new Dictionary<string, string> { ["en"] = "Contributor" },
            permissions = new[] { Permissions.DocView, Permissions.DocContribute }, delegable = true,
        });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);
        await world.AssignAsync(t, "con", "doc_contributor", "org_unit", dubai);
        var library = await OkAsync(t["admin"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Quality", ["ar"] = "الجودة" }, scope = "org_unit", scopeId = dubai,
        }), HttpStatusCode.Created);
        return (t, library.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<Guid> UploadAsync(HttpClient client, Guid library, string name, string content, bool publish = true)
    {
        var doc = await OkAsync(client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish={(publish ? "true" : "false")}",
            new ByteArrayContent(Encoding.UTF8.GetBytes(content))), HttpStatusCode.Created);
        return doc.GetProperty("id").GetGuid();
    }

    private static async Task<(string Body, string Type, string? Disposition)> OpenAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        return (await response.Content.ReadAsStringAsync(), response.Content.Headers.ContentType!.MediaType!, response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Pdf_image_video_and_text_preview_from_the_file_inline_for_permitted_people_only()
    {
        var (t, library) = await FreshAsync();
        var con = t["con"].Client;
        var cases = new[]
        {
            ("manual.pdf", "%PDF-1.4 manual", "pdf", "application/pdf"),
            ("site.png", "PNG-bytes", "image", "image/png"),
            ("induction.mp4", "MP4-bytes", "video", "video/mp4"),
            ("notes.txt", "Plain notes", "text", "text/plain"),
        };
        foreach (var (name, content, kind, type) in cases)
        {
            var doc = await UploadAsync(con, library, name, content);
            var preview = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{doc}/preview"));
            Assert.Equal(kind, preview.GetProperty("kind").GetString());
            Assert.Equal(type, preview.GetProperty("contentType").GetString());
            var (body, served, disposition) = await OpenAsync(t["dee"].Client, preview.GetProperty("url").GetString()!);
            Assert.Equal(content, body);
            Assert.Equal(type, served);
            Assert.Equal("inline", disposition);
        }

        var pdf = await UploadAsync(con, library, "policy.pdf", "%PDF-1.4 policy");
        var url = (await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{pdf}/preview"))).GetProperty("url").GetString()!;
        // A preview is not a download: no download event, and neither token opens the other route.
        var downloads = await world.Database.OutboxCountAsync("DocumentDownloaded", t.Id);
        await OpenAsync(t["dee"].Client, url);
        Assert.Equal(downloads, await world.Database.OutboxCountAsync("DocumentDownloaded", t.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync(url.Replace("/previews/", "/files/"))).StatusCode);
        var download = (await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{pdf}/download-token"))).GetProperty("url").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync(download.Replace("/files/", "/previews/"))).StatusCode);

        // The token is the viewer's; other units and other tenants get not found for the preview and the token.
        Assert.Equal(HttpStatusCode.NotFound, (await con.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["cai"].Client.GetAsync($"{Docs}/{pdf}/preview")).StatusCode);
        var other = await world.CreateTenantAsync("dp-x-" + Guid.NewGuid().ToString("N")[..6], "workplace_plus");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync($"{Docs}/{pdf}/preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(url)).StatusCode);

        // Readers preview only the effective version; a draft stays with contributors.
        var draft = await UploadAsync(con, library, "draft.txt", "not yet", publish: false);
        Assert.Equal(HttpStatusCode.NotFound, (await t["dee"].Client.GetAsync($"{Docs}/{draft}/preview")).StatusCode);
        Assert.Equal("text", (await OkAsync(con.GetAsync($"{Docs}/{draft}/preview"))).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Office_files_are_converted_once_per_version_and_a_failed_conversion_shows_preview_not_available()
    {
        var (t, library) = await FreshAsync();
        var con = t["con"].Client;
        var doc = await UploadAsync(con, library, "report.docx", "word document body");
        var before = world.Factory.Gotenberg.Conversions;

        var first = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{doc}/preview"));
        Assert.Equal("pdf", first.GetProperty("kind").GetString());
        Assert.Equal("application/pdf", first.GetProperty("contentType").GetString());
        var (body, type, _) = await OpenAsync(t["dee"].Client, first.GetProperty("url").GetString()!);
        Assert.Equal("%PDF-1.7 converted document.docx", body);
        Assert.Equal("application/pdf", type);
        // The original file name is not sent to the converter, and the PDF is kept for the next viewer.
        await OkAsync(con.GetAsync($"{Docs}/{doc}/preview"));
        Assert.Equal(before + 1, world.Factory.Gotenberg.Conversions);
        Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id,
            "select count(*) from documents.document_version where document_id = @d and preview_state = 'ready' and preview_key like '%.preview.pdf'", ("d", doc)));

        var broken = await UploadAsync(con, library, "broken.xlsx", FakeGotenberg.Broken);
        foreach (var _ in Enumerable.Range(0, 2))
        {
            var unavailable = await OkAsync(t["dee"].Client.GetAsync($"{Docs}/{broken}/preview"));
            Assert.Equal("unavailable", unavailable.GetProperty("kind").GetString());
            Assert.Equal("failed", unavailable.GetProperty("reason").GetString());
            Assert.Equal(JsonValueKind.Null, unavailable.GetProperty("url").ValueKind);
        }
        Assert.Equal(before + 2, world.Factory.Gotenberg.Conversions);
        // Download still works for the file itself.
        Assert.Equal(HttpStatusCode.OK, (await t["dee"].Client.GetAsync($"{Docs}/{broken}/download-token")).StatusCode);
    }

    [Fact]
    public async Task An_infected_file_is_never_previewed()
    {
        var (t, library) = await FreshAsync();
        var con = t["con"].Client;
        var infected = await UploadAsync(con, library, "invoice.pdf", TestSignatureScanner.Eicar);
        var refused = await con.GetAsync($"{Docs}/{infected}/preview");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(0L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from documents.download_token where document_id = @d", ("d", infected)));
    }
}
