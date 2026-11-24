using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Identity;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 010 T012 (US3 scenario 3): a person who has not uploaded a photo shows their Entra ID photo, checked and
/// resized like an upload, after a directory sync. A changed directory photo replaces it, one the directory no longer
/// has goes, a person's own upload always wins, and a directory photo someone removed does not come back until the
/// directory has a new one. Unchanged photos are not downloaded again. Microsoft Graph is the fake of the test factory.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DirectoryPhotoTests(TenantWorld world)
{
    private static byte[] Png(int width, int height, string pixels)
    {
        byte[] ihdr = [(byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width, (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height, 8, 2, 0, 0, 0];
        using var s = new MemoryStream();
        s.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        foreach (var (type, data) in new[] { ("IHDR", ihdr), ("IDAT", Encoding.Latin1.GetBytes(pixels)), ("IEND", Array.Empty<byte>()) })
        {
            s.Write([(byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length]);
            s.Write(Encoding.ASCII.GetBytes(type));
            s.Write(data);
            s.Write(new byte[4]);
        }
        return s.ToArray();
    }

    private async Task<TestUser> UserAsync(SyncedTenant s, string name, string oid)
    {
        var userId = await world.Factory.InScopeAsync(s.Id, sp => Sql.ScalarAsync<Guid>(sp.GetRequiredService<IDbSession>(),
            "select user_id from identity.directory_user_link where directory_id = @d", new { d = oid }));
        var token = await world.Factory.InScopeAsync(s.Id, async sp =>
        {
            var value = await sp.GetRequiredService<SessionService>().CreateUserSessionAsync(userId, "test", CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return value;
        });
        var user = new TestUser(name, userId, s.Email(name), token, world.Factory.TenantClient(s.Key, token));
        s.Tenant.Users[name] = user;
        return user;
    }

    private static async Task<string?> PhotoOfAsync(HttpClient client, Guid person)
    {
        var response = await client.GetAsync($"/api/v1/people/{person}");
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var view = await response.Content.ReadFromJsonAsync<JsonElement>();
        return view.TryGetProperty("photo", out var photo) && photo.ValueKind == JsonValueKind.String ? photo.GetString() : null;
    }

    private Task<string?> SourceAsync(SyncedTenant s, TestUser user) =>
        world.Database.TenantScalarAsync<string?>(s.Id, "select photo_source from people.profile where user_id = @u", ("u", user.Id));

    /// <summary>A directory sync, with every photo due again as if a day had passed.</summary>
    private async Task SyncAsync(SyncedTenant s)
    {
        await world.Database.OwnerExecInTenantAsync(s.Id, "update people.profile set directory_photo_checked_at = null where tenant_id = @t", ("t", s.Id));
        await DirectoryTestSetup.SyncAsync(world, s);
        await world.Factory.Services.GetRequiredService<TeamBeats.Messaging.OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
    }

    private async Task<(SyncedTenant S, Dictionary<string, string> Oids)> SetupAsync()
    {
        var s = await DirectoryTestSetup.CreateAsync(world, "dph");
        var oids = new[] { "omar", "sara", "kofi" }.ToDictionary(n => n, _ => Guid.NewGuid().ToString("D"));
        s.Data.AddUser(oids["omar"], "Omar Saleh", s.Email("omar"));
        s.Data.AddUser(oids["sara"], "Sara Ali", s.Email("sara"));
        s.Data.AddUser(oids["kofi"], "Kofi Mensah", s.Email("kofi"));
        s.Data.SetPhoto(oids["omar"], Png(648, 648, "omar"), "image/png");
        s.Data.SetPhoto(oids["sara"], Png(648, 648, "sara"), "image/png");
        await DirectoryTestSetup.SyncAsync(world, s);
        foreach (var (name, oid) in oids)
        {
            await UserAsync(s, name, oid);
        }
        return (s, oids);
    }

    [Fact]
    public async Task People_without_their_own_photo_show_the_directory_photo_and_it_follows_the_directory()
    {
        var before = world.Factory.Gotenberg.Screenshots;
        var (s, oids) = await SetupAsync();
        var t = s.Tenant;

        // Omar and Sara have directory photos, resized like an upload; Kofi has none and shows initials.
        var omar = await PhotoOfAsync(t["kofi"].Client, t["omar"].Id);
        Assert.NotNull(omar);
        Assert.Equal("directory", await SourceAsync(s, t["omar"]));
        Assert.True(world.Factory.Gotenberg.Screenshots >= before + 4);
        Assert.Contains("object-fit:cover", world.Factory.Gotenberg.LastScreenshotHtml!);
        var image = await t["kofi"].Client.GetAsync(omar);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Null(await PhotoOfAsync(t["omar"].Client, t["kofi"].Id));

        // Nothing changed in the directory: the photo is not downloaded again.
        var downloads = s.Data.PhotoDownloads;
        await SyncAsync(s);
        Assert.Equal(downloads, s.Data.PhotoDownloads);
        Assert.Equal(omar, await PhotoOfAsync(t["kofi"].Client, t["omar"].Id));

        // A new directory photo replaces it; a photo the directory no longer has goes.
        s.Data.SetPhoto(oids["omar"], Png(648, 648, "omar-new"), "image/png");
        s.Data.RemovePhoto(oids["sara"]);
        await SyncAsync(s);
        var changed = await PhotoOfAsync(t["kofi"].Client, t["omar"].Id);
        Assert.NotNull(changed);
        Assert.NotEqual(omar, changed);
        Assert.Null(await PhotoOfAsync(t["kofi"].Client, t["sara"].Id));

        // Kofi gets one later.
        s.Data.SetPhoto(oids["kofi"], Png(400, 400, "kofi"), "image/png");
        await SyncAsync(s);
        Assert.NotNull(await PhotoOfAsync(t["omar"].Client, t["kofi"].Id));

        // Another tenant sees nothing of it.
        var other = await world.CreateTenantAsync("dph-x-" + Guid.NewGuid().ToString("N")[..6], "workplace");
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(changed)).StatusCode);
    }

    [Fact]
    public async Task An_own_upload_wins_and_a_removed_directory_photo_stays_removed_until_the_directory_changes_it()
    {
        var (s, oids) = await SetupAsync();
        var t = s.Tenant;

        // Sara uploads her own photo: the directory photo never replaces it.
        var content = new ByteArrayContent(Png(300, 300, "sara-own"));
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        Assert.Equal(HttpStatusCode.OK, (await t["sara"].Client.PutAsync("/api/v1/me/photo", content)).StatusCode);
        var own = await PhotoOfAsync(t["omar"].Client, t["sara"].Id);
        s.Data.SetPhoto(oids["sara"], Png(648, 648, "sara-new"), "image/png");
        await SyncAsync(s);
        Assert.Equal(own, await PhotoOfAsync(t["omar"].Client, t["sara"].Id));
        Assert.Equal("own", await SourceAsync(s, t["sara"]));

        // When she removes her upload, the directory photo shows again at the next sync.
        Assert.Equal(HttpStatusCode.OK, (await t["sara"].Client.DeleteAsync("/api/v1/me/photo")).StatusCode);
        Assert.Null(await PhotoOfAsync(t["omar"].Client, t["sara"].Id));
        await SyncAsync(s);
        Assert.NotNull(await PhotoOfAsync(t["omar"].Client, t["sara"].Id));
        Assert.Equal("directory", await SourceAsync(s, t["sara"]));

        // Omar removes his directory photo: it does not come back until the directory has a new one.
        Assert.Equal(HttpStatusCode.OK, (await t["omar"].Client.DeleteAsync("/api/v1/me/photo")).StatusCode);
        await SyncAsync(s);
        Assert.Null(await PhotoOfAsync(t["sara"].Client, t["omar"].Id));
        s.Data.SetPhoto(oids["omar"], Png(648, 648, "omar-new"), "image/png");
        await SyncAsync(s);
        Assert.NotNull(await PhotoOfAsync(t["sara"].Client, t["omar"].Id));
    }

    [Fact]
    public async Task Bad_images_an_unreachable_directory_and_a_converter_outage_change_nothing()
    {
        var (s, oids) = await SetupAsync();
        var t = s.Tenant;
        var omar = await PhotoOfAsync(t["kofi"].Client, t["omar"].Id);

        // Not an image we take: what shows stays.
        s.Data.SetPhoto(oids["omar"], Encoding.UTF8.GetBytes("<svg/>"), "image/svg+xml");
        await SyncAsync(s);
        Assert.Equal(omar, await PhotoOfAsync(t["kofi"].Client, t["omar"].Id));

        // The conversion service is down: nothing is read or changed.
        s.Data.SetPhoto(oids["omar"], Png(648, 648, "omar-2"), "image/png");
        world.Factory.Gotenberg.HtmlDown = true;
        try
        {
            await SyncAsync(s);
        }
        finally
        {
            world.Factory.Gotenberg.HtmlDown = false;
        }
        Assert.Equal(omar, await PhotoOfAsync(t["kofi"].Client, t["omar"].Id));

        // Graph refuses the photo reads (consent withdrawn): profiles keep their photos.
        var downloads = s.Data.PhotoDownloads;
        world.Factory.Graph.Revoke(s.Directory);
        await world.Database.OwnerExecInTenantAsync(s.Id, "update people.profile set directory_photo_checked_at = null where tenant_id = @t", ("t", s.Id));
        await world.Factory.InScopeAsync(s.Id, async sp =>
        {
            await sp.GetRequiredService<TeamBeats.Modules.People.DirectoryProfilePhotos>().ApplyAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        Assert.Equal(downloads, s.Data.PhotoDownloads);
        Assert.Equal(omar, await PhotoOfAsync(t["kofi"].Client, t["omar"].Id));
    }
}
