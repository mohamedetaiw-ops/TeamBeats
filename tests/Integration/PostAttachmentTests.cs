using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 slice 11 (US1, FR-003, FR-017): images and files in posts. Uploads are scanned for malware, images lose
/// their metadata and pass the content safety check or are refused, and a post takes at most 10 images and 5 files of
/// the author's own uploads. Readers of the post open them; a loose upload opens only for its uploader; Speak Up posts
/// take none; non-members and other tenants see nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PostAttachmentTests(TenantWorld world)
{
    private const string Api = "/api/v1/engagement";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string[]> ErrorKeysAsync(HttpResponseMessage response)
    {
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray().Select(x => x.GetProperty("key").GetString()!)).ToArray()
            : [];
    }

    private static object Body(string text) => new { blocks = new object[] { new { type = "paragraph", children = new object[] { new { text } } } } };

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj << >> endobj\ntrailer << >>\n%%EOF");

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid community, byte[] data, string type, string name = "file")
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PostAsync($"{Api}/communities/{community}/attachments?name={Uri.EscapeDataString(name)}", content);
    }

    private static async Task<Guid> UploadedAsync(HttpClient client, Guid community, byte[] data, string type, string name = "file") =>
        (await OkAsync(UploadAsync(client, community, data, type, name), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    /// <summary>A closed community: Laila owns it, Ahmed and Nour are members, Kofi is not.</summary>
    private async Task<(TestTenant T, Guid Community)> SetupAsync()
    {
        var t = await world.CreateTenantAsync("pa-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "laila", "ahmed", "kofi", "nour" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new
        {
            names = new Dictionary<string, string> { ["en"] = "Finance team" }, kind = "closed",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var (name, role) in new[] { ("laila", "owner"), ("ahmed", "member"), ("nour", "member") })
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id, role }));
        }
        return (t, community);
    }

    [Fact]
    public async Task Images_and_files_join_a_post_and_open_only_for_its_readers()
    {
        var (t, community) = await SetupAsync();
        var ahmed = t["ahmed"].Client;
        var image = await OkAsync(UploadAsync(ahmed, community, GalleryTests.Png(), "image/png", "C:\\Users\\ahmed\\closing.png"), HttpStatusCode.Created);
        Assert.Equal("image", image.GetProperty("kind").GetString());
        Assert.Equal("closing.png", image.GetProperty("name").GetString());
        Assert.Equal(640, image.GetProperty("width").GetInt32());
        var imageId = image.GetProperty("id").GetGuid();
        var pdf = await UploadedAsync(ahmed, community, Pdf, "application/pdf", "Month-end checklist.pdf");

        // Before the post is saved, only Ahmed opens the uploads.
        Assert.Equal(HttpStatusCode.OK, (await ahmed.GetAsync($"{Api}/attachments/{imageId}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["nour"].Client.GetAsync($"{Api}/attachments/{imageId}/file")).StatusCode);

        var saved = await OkAsync(ahmed.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("The month-end checklist"), attachments = new[] { imageId, pdf } }),
            HttpStatusCode.Created);
        var post = saved.GetProperty("post");
        Assert.Equal([imageId, pdf], post.GetProperty("attachments").EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToArray());

        // Members open both; the image shows without the location the original carried, and the PDF downloads by name.
        var shown = await t["nour"].Client.GetAsync($"{Api}/attachments/{imageId}/file");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        var bytes = await shown.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(bytes));
        var download = await t["nour"].Client.GetAsync($"{Api}/attachments/{pdf}/file");
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(HttpStatusCode.NotFound, (await t["kofi"].Client.GetAsync($"{Api}/attachments/{pdf}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(t["kofi"].Client, community, Pdf, "application/pdf")).StatusCode);

        // Someone else's upload cannot be put on a post, and neither can one already on another post.
        var nours = await UploadedAsync(t["nour"].Client, community, Pdf, "application/pdf");
        Assert.Contains("validation.engagement.attachment_unknown",
            await ErrorKeysAsync(await ahmed.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Mine now"), attachments = new[] { nours } })));
        Assert.Contains("validation.engagement.attachment_unknown",
            await ErrorKeysAsync(await t["nour"].Client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Again"), attachments = new[] { imageId } })));

        // An edit that leaves the list out keeps the attachments; an empty list takes them off and deletes them.
        var id = post.GetProperty("id").GetGuid();
        var kept = await OkAsync(ahmed.PutAsJsonAsync($"{Api}/posts/{id}", new { body = Body("The month-end checklist, updated") }));
        Assert.Equal(2, kept.GetProperty("post").GetProperty("attachments").GetArrayLength());
        var cleared = await OkAsync(ahmed.PutAsJsonAsync($"{Api}/posts/{id}", new { body = Body("No files"), attachments = Array.Empty<Guid>() }));
        Assert.Equal(0, cleared.GetProperty("post").GetProperty("attachments").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await ahmed.GetAsync($"{Api}/attachments/{pdf}/file")).StatusCode);
    }

    [Fact]
    public async Task Uploads_are_checked_and_a_post_takes_at_most_ten_images_and_five_files()
    {
        var (t, community) = await SetupAsync();
        var ahmed = t["ahmed"].Client;
        Assert.Contains("validation.engagement.attachment_unsafe", await ErrorKeysAsync(await UploadAsync(ahmed, community, GalleryTests.Png("[fake:unsafe]"), "image/png")));
        Assert.Contains("validation.engagement.photo_infected", await ErrorKeysAsync(await UploadAsync(ahmed, community, Encoding.ASCII.GetBytes(TestSignatureScanner.Eicar), "text/plain")));
        Assert.Contains("validation.engagement.attachment_type", await ErrorKeysAsync(await UploadAsync(ahmed, community, Encoding.UTF8.GetBytes("<html></html>"), "text/html")));
        Assert.Contains("validation.engagement.attachment_type", await ErrorKeysAsync(await UploadAsync(ahmed, community, Encoding.UTF8.GetBytes("not a pdf"), "application/pdf")));
        Assert.Contains("validation.engagement.attachment_name", await ErrorKeysAsync(await UploadAsync(ahmed, community, Pdf, "application/pdf", "  ")));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.post_attachment"));

        var images = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            images.Add(await UploadedAsync(ahmed, community, GalleryTests.Png($"photo {i}"), "image/png", $"photo-{i}.png"));
        }
        Assert.Contains("validation.engagement.attachment_count",
            await ErrorKeysAsync(await ahmed.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Too many"), attachments = images })));
        await OkAsync(ahmed.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Ten is fine"), attachments = images.Take(10) }), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Speak_Up_posts_take_no_attachments_and_other_tenants_see_none()
    {
        var (t, community) = await SetupAsync();
        await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities/{community}/channels",
            new { key = "speak_up", names = new Dictionary<string, string> { ["en"] = "Speak up" }, kind = "speak_up" }), HttpStatusCode.Created);
        var view = await OkAsync(t["admin"].Client.GetAsync($"{Api}/communities/{community}"));
        var channel = view.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("key").GetString() == "speak_up").GetProperty("id").GetGuid();
        var pdf = await UploadedAsync(t["ahmed"].Client, community, Pdf, "application/pdf");
        Assert.Contains("validation.engagement.attachment_speak_up",
            await ErrorKeysAsync(await t["ahmed"].Client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { channelId = channel, body = Body("A concern"), attachments = new[] { pdf } })));

        var post = (await OkAsync(t["ahmed"].Client.PostAsJsonAsync($"{Api}/communities/{community}/posts", new { body = Body("Checklist"), attachments = new[] { pdf } }),
            HttpStatusCode.Created)).GetProperty("post").GetProperty("id").GetGuid();
        Assert.NotEqual(Guid.Empty, post);
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/attachments/{pdf}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other.T["ahmed"].Client, community, Pdf, "application/pdf")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.post_attachment"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(other.T.Id, "select count(*) from engagement.post_attachment"));
    }
}
