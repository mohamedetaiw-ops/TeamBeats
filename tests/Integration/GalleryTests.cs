using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 023 T014 (US7, FR-017): albums in a community, photos scanned for malware (infected refused), stored without
/// location metadata, checked for content safety through the AI Gateway, shown at once only when safe and the album
/// does not ask for approval, otherwise waiting for a moderator; tags only for people who can read the community, who
/// are told and can remove their tag; removal by the uploader or a moderator; closed communities and other tenants see
/// nothing. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class GalleryTests(TenantWorld world)
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

    private static void Chunk(MemoryStream s, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        s.Write(length);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]);
    }

    /// <summary>A small PNG whose picture data carries a marker and whose text chunk holds a made-up location.</summary>
    internal static byte[] Png(string marker = "", int width = 640, int height = 480)
    {
        using var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk(s, "IHDR", ihdr);
        Chunk(s, "tEXt", Encoding.Latin1.GetBytes("GPS\0lat 24.7136 lon 46.6753"));
        Chunk(s, "IDAT", Encoding.Latin1.GetBytes("picture data " + marker));
        Chunk(s, "IEND", []);
        return s.ToArray();
    }

    /// <summary>A small JPEG with an EXIF segment holding a made-up location.</summary>
    internal static byte[] Jpeg()
    {
        using var s = new MemoryStream();
        s.Write([0xFF, 0xD8]);
        var exif = Encoding.Latin1.GetBytes("Exif\0\0GPS lat 24.7136");
        s.Write([0xFF, 0xE1, (byte)((exif.Length + 2) >> 8), (byte)(exif.Length + 2)]);
        s.Write(exif);
        s.Write([0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01]);
        s.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        s.Write(Encoding.Latin1.GetBytes("scan data"));
        s.Write([0xFF, 0xD9]);
        return s.ToArray();
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, Guid album, byte[] data, string type = "image/png", string? caption = null)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return client.PostAsync($"{Api}/albums/{album}/photos{(caption is null ? "" : $"?caption={Uri.EscapeDataString(caption)}")}", content);
    }

    private sealed record Setup(TestTenant T, Guid Community, Guid Album, Guid ApprovalAlbum);

    private async Task<Setup> SetupAsync()
    {
        var t = await world.CreateTenantAsync("ga-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "laila", "ahmed", "kofi", "nour" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        // A closed community: Laila owns it, Ahmed and Nour are members, Kofi is not.
        var community = (await OkAsync(t["admin"].Client.PostAsJsonAsync($"{Api}/communities", new
        {
            names = new Dictionary<string, string> { ["en"] = "Photography club" }, kind = "closed",
        }), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        foreach (var (name, role) in new[] { ("laila", "owner"), ("ahmed", "member"), ("nour", "member") })
        {
            await OkAsync(t["admin"].Client.PutAsJsonAsync($"{Api}/communities/{community}/members", new { userId = t[name].Id, role }));
        }
        var album = (await OkAsync(t["ahmed"].Client.PostAsJsonAsync($"{Api}/communities/{community}/albums", new { title = "Desert trip", requiresApproval = false }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var approval = (await OkAsync(t["laila"].Client.PostAsJsonAsync($"{Api}/communities/{community}/albums", new { title = "Annual dinner", requiresApproval = true }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        return new Setup(t, community, album, approval);
    }

    [Fact]
    public async Task Safe_photos_show_while_flagged_unchecked_and_approval_photos_wait_for_a_moderator()
    {
        var (t, community, album, approval) = await SetupAsync();
        var safe = await OkAsync(UploadAsync(t["ahmed"].Client, album, Png(), caption: "Sunset over the dunes"), HttpStatusCode.Created);
        Assert.Equal("visible", safe.GetProperty("status").GetString());
        Assert.Equal(640, safe.GetProperty("width").GetInt32());
        var flagged = await OkAsync(UploadAsync(t["ahmed"].Client, album, Png("[fake:unsafe]")), HttpStatusCode.Created);
        Assert.Equal("pending", flagged.GetProperty("status").GetString());
        Assert.Equal("safety", flagged.GetProperty("heldReason").GetString());
        var held = await OkAsync(UploadAsync(t["nour"].Client, approval, Jpeg(), "image/jpeg"), HttpStatusCode.Created);
        Assert.Equal("approval", held.GetProperty("heldReason").GetString());

        // Members see what shows; waiting photos only reach their uploader and the moderators.
        var seen = await OkAsync(t["nour"].Client.GetAsync($"{Api}/albums/{album}"));
        Assert.Single(seen.GetProperty("photos").EnumerateArray());
        Assert.Empty(seen.GetProperty("pending").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["nour"].Client.GetAsync($"{Api}/photos/{flagged.GetProperty("id").GetGuid()}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await t["ahmed"].Client.GetAsync($"{Api}/photos/{flagged.GetProperty("id").GetGuid()}/file")).StatusCode);
        var moderator = await OkAsync(t["laila"].Client.GetAsync($"{Api}/albums/{album}"));
        Assert.Single(moderator.GetProperty("pending").EnumerateArray());
        Assert.True(moderator.GetProperty("canModerate").GetBoolean());

        // Only moderators approve; the approved photo shows to everyone.
        var heldId = held.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await t["nour"].Client.PostAsync($"{Api}/photos/{heldId}:approve", null)).StatusCode);
        Assert.Equal("visible", (await OkAsync(t["laila"].Client.PostAsync($"{Api}/photos/{heldId}:approve", null))).GetProperty("status").GetString());
        Assert.Single((await OkAsync(t["ahmed"].Client.GetAsync($"{Api}/albums/{approval}"))).GetProperty("photos").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, (await t["laila"].Client.PostAsync($"{Api}/photos/{heldId}:approve", null)).StatusCode);

        // The stored files carry no location metadata, and the JPEG keeps its picture.
        var png = await (await t["nour"].Client.GetAsync($"{Api}/photos/{safe.GetProperty("id").GetGuid()}/file")).Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(png));
        Assert.Contains("picture data", Encoding.Latin1.GetString(png));
        var jpeg = await (await t["nour"].Client.GetAsync($"{Api}/photos/{heldId}/file")).Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("GPS", Encoding.Latin1.GetString(jpeg));
        Assert.Contains("scan data", Encoding.Latin1.GetString(jpeg));
        Assert.Equal(480, (await OkAsync(t["nour"].Client.GetAsync($"{Api}/albums/{approval}"))).GetProperty("photos")[0].GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Uploads_are_checked_for_type_size_and_malware_and_need_posting_rights()
    {
        var (t, community, album, _) = await SetupAsync();
        Assert.Contains("validation.engagement.photo_type", await ErrorKeysAsync(await UploadAsync(t["ahmed"].Client, album, Encoding.UTF8.GetBytes("<svg/>"), "image/svg+xml")));
        Assert.Contains("validation.engagement.photo_type", await ErrorKeysAsync(await UploadAsync(t["ahmed"].Client, album, Png(), "image/jpeg")));
        Assert.Contains("validation.engagement.photo_size", await ErrorKeysAsync(await UploadAsync(t["ahmed"].Client, album, Png(width: 20000))));
        var infected = Png(TestSignatureScanner.Eicar);
        Assert.Contains("validation.engagement.photo_infected", await ErrorKeysAsync(await UploadAsync(t["ahmed"].Client, album, infected)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, "select count(*) from engagement.album_photo"));
        Assert.Contains("validation.engagement.photo_caption", await ErrorKeysAsync(await UploadAsync(t["ahmed"].Client, album, Png(), caption: new string('a', 301))));

        // Kofi is not a member of the closed community: its albums and photos do not exist for him.
        var photo = (await OkAsync(UploadAsync(t["ahmed"].Client, album, Png()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await t["kofi"].Client.GetAsync($"{Api}/communities/{community}/albums")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["kofi"].Client.GetAsync($"{Api}/albums/{album}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["kofi"].Client.GetAsync($"{Api}/photos/{photo}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(t["kofi"].Client, album, Png())).StatusCode);

        // Galleries need the package capability: a Workplace version with communities but no galleries, for one tenant.
        var basic = await world.CreateTenantAsync("ga-w-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        var open = (await OkAsync(basic["admin"].Client.PostAsJsonAsync($"{Api}/communities", new { names = new Dictionary<string, string> { ["en"] = "Club" }, kind = "open" }),
            HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await world.PinPackageVersionAsync(basic, "workplace", new Dictionary<string, object>
        {
            [Capabilities.AiAssistant] = new { enabled = true },
            [Capabilities.AiAssistantMonthlyRequests] = new { enabled = true, limit = 9999 },
            [Capabilities.OrgHierarchy] = new { enabled = true },
            [Capabilities.EngagementCommunities] = new { enabled = true },
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Api}/communities/{open}/albums")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await basic["admin"].Client.GetAsync($"{Api}/communities/{open}")).StatusCode);
    }

    [Fact]
    public async Task Tagged_people_are_told_and_can_remove_their_tag_and_photos_can_be_removed()
    {
        var (t, _, album, _) = await SetupAsync();
        var photo = (await OkAsync(UploadAsync(t["ahmed"].Client, album, Png()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        Assert.Contains("validation.engagement.photo_tag_person",
            await ErrorKeysAsync(await t["ahmed"].Client.PutAsJsonAsync($"{Api}/photos/{photo}/tags", new { userIds = new[] { t["kofi"].Id } })));
        Assert.Equal(HttpStatusCode.Forbidden, (await t["nour"].Client.PutAsJsonAsync($"{Api}/photos/{photo}/tags", new { userIds = new[] { t["nour"].Id } })).StatusCode);
        var tagged = await OkAsync(t["ahmed"].Client.PutAsJsonAsync($"{Api}/photos/{photo}/tags", new { userIds = new[] { t["nour"].Id } }));
        Assert.Single(tagged.GetProperty("tags").EnumerateArray());
        await OkAsync(t["ahmed"].Client.PutAsJsonAsync($"{Api}/photos/{photo}/tags", new { userIds = new[] { t["nour"].Id } }));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from notifications.notification where user_id = '{t["nour"].Id}' and template_key = 'engagement.photo.tagged'"));

        await OkAsync(t["nour"].Client.DeleteAsync($"{Api}/photos/{photo}/tags/me"), HttpStatusCode.NoContent);
        var page = await OkAsync(t["nour"].Client.GetAsync($"{Api}/albums/{album}"));
        Assert.Empty(page.GetProperty("photos")[0].GetProperty("tags").EnumerateArray());

        // Nour cannot remove Ahmed's photo; Laila moderates and does, which is audited.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["nour"].Client.DeleteAsync($"{Api}/photos/{photo}")).StatusCode);
        await OkAsync(t["laila"].Client.DeleteAsync($"{Api}/photos/{photo}"), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await t["ahmed"].Client.GetAsync($"{Api}/photos/{photo}/file")).StatusCode);
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from audit.audit_event where target_id = '{photo}' and action = '{AuditActions.EngagementContentRemoved}'"));
    }

    [Fact]
    public async Task Galleries_stay_in_their_tenant()
    {
        var (t, community, album, _) = await SetupAsync();
        var photo = (await OkAsync(UploadAsync(t["ahmed"].Client, album, Png()), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var other = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/albums/{album}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/photos/{photo}/file")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.DeleteAsync($"{Api}/photos/{photo}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(other.T["ahmed"].Client, album, Png())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["admin"].Client.GetAsync($"{Api}/communities/{community}/albums")).StatusCode);
    }
}
