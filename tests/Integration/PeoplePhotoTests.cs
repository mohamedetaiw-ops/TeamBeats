using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 FR-013, US3 scenario 3 and section 14: a person uploads their own photo, which is type and size checked,
/// loses its metadata, is resized through the conversion service and kept under the tenant's path; everyone in the
/// tenant sees it, nobody outside does; a manager or HR over the person removes it, audited.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class PeoplePhotoTests(TenantWorld world)
{
    private static byte[] Png(params (string Type, byte[] Data)[] chunks)
    {
        using var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (var (type, data) in chunks)
        {
            s.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]);
        }
        return s.ToArray();
    }

    private static byte[] Ihdr(int width, int height) =>
        [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 2, 0, 0, 0];

    private static readonly byte[] Photo = Png(("IHDR", Ihdr(640, 480)), ("tEXt", Encoding.Latin1.GetBytes("Location\0Riyadh 24.7N")),
        ("IDAT", Encoding.Latin1.GetBytes("pixels")), ("IEND", []));

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] data, string type = "image/png")
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PutAsync("/api/v1/me/photo", content);
    }

    private static async Task<JsonElement> OkAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorKeyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).Single();
    }

    private static async Task<string?> PhotoOfAsync(HttpClient client, Guid person)
    {
        var view = await OkAsync(await client.GetAsync($"/api/v1/people/{person}"));
        return view.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.String ? photo.GetString() : null;
    }

    private async Task<TestTenant> TenantAsync()
    {
        var tenant = await world.CreateTenantAsync("pph-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await world.SeedUserAsync(tenant, "sara", MembershipTypes.Employee);
        await world.SeedUserAsync(tenant, "omar", MembershipTypes.Employee);
        return tenant;
    }

    [Fact]
    public async Task An_uploaded_photo_loses_its_metadata_is_resized_and_shows_to_the_tenant_only()
    {
        var t = await TenantAsync();
        var sara = t["sara"];
        var before = world.Factory.Gotenberg.Screenshots;

        var view = await OkAsync(await UploadAsync(sara.Client, Photo));
        var link = view.GetProperty("photo").GetString()!;
        Assert.StartsWith($"/api/v1/people/{sara.Id:D}/photo?v=1", link, StringComparison.Ordinal);
        Assert.Equal(before + 2, world.Factory.Gotenberg.Screenshots);
        // What reached the conversion service is the picture without the text chunk that held a position.
        var sent = world.Factory.Gotenberg.LastScreenshotHtml!;
        var base64 = sent[(sent.IndexOf("base64,", StringComparison.Ordinal) + 7)..sent.IndexOf('"', sent.IndexOf("base64,", StringComparison.Ordinal))];
        var image = Encoding.Latin1.GetString(Convert.FromBase64String(base64));
        Assert.DoesNotContain("Riyadh", image, StringComparison.Ordinal);
        Assert.Contains("pixels", image, StringComparison.Ordinal);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PeopleProfileUpdated, sara.Id.ToString());

        // Everyone in the tenant sees it, in the profile and in search, at the nearest kept size.
        Assert.Equal(link, await PhotoOfAsync(t["omar"].Client, sara.Id));
        var found = await OkAsync(await t["omar"].Client.GetAsync($"/api/v1/people?q={Uri.EscapeDataString(sara.Email)}"));
        Assert.Equal(link, found.GetProperty("items")[0].GetProperty("photo").GetString());
        var small = await t["omar"].Client.GetAsync($"/api/v1/people/{sara.Id}/photo?size=48");
        Assert.Equal(HttpStatusCode.OK, small.StatusCode);
        Assert.Equal("image/png", small.Content.Headers.ContentType?.MediaType);
        Assert.Contains("fake 96x96", Encoding.ASCII.GetString(await small.Content.ReadAsByteArrayAsync()), StringComparison.Ordinal);
        Assert.Contains("fake 256x256", await t["omar"].Client.GetStringAsync($"/api/v1/people/{sara.Id}/photo"), StringComparison.Ordinal);

        // Nobody in another tenant can open it, even with the link.
        var other = await world.CreateTenantAsync("ppo-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(link)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.DeleteAsync($"/api/v1/people/{sara.Id}/photo")).StatusCode);

        // A new photo has a new link; removing it brings back the initials.
        var second = (await OkAsync(await UploadAsync(sara.Client, Photo))).GetProperty("photo").GetString()!;
        Assert.EndsWith("v=2", second, StringComparison.Ordinal);
        var removed = await OkAsync(await sara.Client.DeleteAsync("/api/v1/me/photo"));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("photo").ValueKind);
        Assert.Null(await PhotoOfAsync(t["omar"].Client, sara.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.GetAsync(second)).StatusCode);
    }

    [Fact]
    public async Task Only_PNG_or_JPEG_up_to_2_MB_is_taken_and_a_converter_outage_changes_nothing()
    {
        var t = await TenantAsync();
        var sara = t["sara"].Client;
        Assert.Equal("validation.people.photo_type", await ErrorKeyAsync(await UploadAsync(sara, Encoding.ASCII.GetBytes("GIF89a....."), "image/gif")));
        // A file that says PNG but is not one is refused by its content, not its label.
        Assert.Equal("validation.people.photo_type", await ErrorKeyAsync(await UploadAsync(sara, Encoding.ASCII.GetBytes("<svg onload=alert(1)>"))));
        var big = new byte[2 * 1024 * 1024 + 10];
        Photo.CopyTo(big, 0);
        Assert.Equal("validation.people.photo_size", await ErrorKeyAsync(await UploadAsync(sara, big)));
        Assert.Equal("validation.people.photo_size", await ErrorKeyAsync(await UploadAsync(sara, Png(("IHDR", Ihdr(9000, 10)), ("IDAT", [1]), ("IEND", [])))));

        world.Factory.Gotenberg.HtmlDown = true;
        try
        {
            var refused = await UploadAsync(sara, Photo);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("conflict.people.photo_processing", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            world.Factory.Gotenberg.HtmlDown = false;
        }
        Assert.Null(await PhotoOfAsync(t["omar"].Client, t["sara"].Id));
    }

    [Fact]
    public async Task A_colleague_cannot_remove_a_photo_and_an_administrator_can_with_audit()
    {
        var t = await TenantAsync();
        var link = (await OkAsync(await UploadAsync(t["sara"].Client, Photo))).GetProperty("photo").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.DeleteAsync($"/api/v1/people/{t["sara"].Id}/photo")).StatusCode);
        Assert.Equal(link, await PhotoOfAsync(t["omar"].Client, t["sara"].Id));

        Assert.Equal(HttpStatusCode.NoContent, (await t["admin"].Client.DeleteAsync($"/api/v1/people/{t["sara"].Id}/photo")).StatusCode);
        await world.Database.AssertAuditedAsync(t.Id, AuditActions.PeoplePhotoRemoved, t["sara"].Id.ToString());
        Assert.Null(await PhotoOfAsync(t["omar"].Client, t["sara"].Id));
        Assert.Equal(HttpStatusCode.NotFound, (await t["omar"].Client.GetAsync(link)).StatusCode);
    }
}
