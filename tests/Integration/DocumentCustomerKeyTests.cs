using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 011 R3 customer-managed keys per library (spec 026 FR-007): a library manager turns a library onto the tenant
/// data key once a key is connected; new files are stored encrypted and read back as they were; files stored before are
/// moved by the background pass; it cannot be turned off; a revoked key leaves the files closed until it is restored;
/// other packages, other people and other tenants are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class DocumentCustomerKeyTests(TenantWorld world)
{
    private const string Libraries = "/api/v1/documents/libraries";
    private const string Docs = "/api/v1/documents";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> KeyAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("detailKey", out var detail) && detail.ValueKind == JsonValueKind.String ? detail.GetString()! : body.GetProperty("code").GetString()!;
    }

    private async Task<(TestTenant T, Guid Library)> FreshAsync(string package = "enterprise_gov")
    {
        var t = await world.CreateTenantAsync("dck-" + Guid.NewGuid().ToString("N")[..8], package);
        await world.SeedUserAsync(t, "emp", MembershipTypes.Employee);
        var library = await OkAsync(t["admin"].Client.PostAsJsonAsync(Libraries, new
        {
            names = new Dictionary<string, string> { ["en"] = "Board papers" }, scope = "tenant",
        }), HttpStatusCode.Created);
        return (t, library.GetProperty("id").GetGuid());
    }

    private static async Task<(Guid Document, Guid Version)> PublishAsync(HttpClient client, Guid library, string name, string content)
    {
        var view = await OkAsync(client.PostAsync($"{Libraries}/{library}/files?name={Uri.EscapeDataString(name)}&publish=true",
            new ByteArrayContent(Encoding.UTF8.GetBytes(content))), HttpStatusCode.Created);
        return (view.GetProperty("id").GetGuid(), view.GetProperty("effective").GetProperty("id").GetGuid());
    }

    private static async Task<string> DownloadAsync(HttpClient client, Guid document)
    {
        var token = await OkAsync(client.GetAsync($"{Docs}/{document}/download-token"));
        var file = await client.GetAsync(token.GetProperty("url").GetString());
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        return await file.Content.ReadAsStringAsync();
    }

    /// <summary>The file as it lies in object storage, below the product.</summary>
    private async Task<byte[]> RawAsync(TestTenant t, Guid library, Guid document, Guid version)
    {
        var storage = world.Factory.Services.GetRequiredService<IObjectStorage>();
        await using var stream = await storage.OpenReadAsync($"tenants/{t.Id:D}/documents/{library:N}/{document:N}/{version:N}") ?? throw new InvalidOperationException("missing");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static readonly byte[] Magic = [0x00, (byte)'T', (byte)'B', (byte)'E', (byte)'N', (byte)'C', (byte)'1', 0x00];

    private static bool Sealed(byte[] raw) => raw.Length > 8 && raw.AsSpan(0, 8).SequenceEqual(Magic);

    private Task<int> PassAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var done = await sp.GetRequiredService<LibraryEncryption>().RunAsync(50, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return done;
        });

    private Task<string?> CheckAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var status = await sp.GetRequiredService<TenantKeyService>().CheckAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return status;
        });

    private async Task<FakeKeyVault> ConnectAsync(TestTenant t)
    {
        var vault = FakeKeyVault.Azure(world.Factory.Rest);
        var secretRef = SecretReferences.For(t.Id, "kms-app");
        world.Factory.Secrets.Set(secretRef, "fictional-client-secret");
        await OkAsync(t["admin"].Client.PutAsJsonAsync("/api/v1/security/keys", new
        {
            provider = "azure_key_vault", keyUri = vault.KeyUri, secretRef,
            directoryId = "3f1c2b8e-4a6d-4c2e-9b1a-6d5e4f3a2b10", clientId = "8a7b6c5d-4e3f-4a2b-9c1d-0e9f8a7b6c5d",
        }));
        return vault;
    }

    [Fact]
    public async Task A_library_on_the_customer_key_stores_files_encrypted_moves_older_ones_and_stays_on_it()
    {
        var (t, library) = await FreshAsync();
        var admin = t["admin"].Client;
        var url = $"{Libraries}/{library}/encryption";
        var (before, beforeVersion) = await PublishAsync(admin, library, "minutes-march.txt", "Minutes of the March board meeting");
        Assert.False(Sealed(await RawAsync(t, library, before, beforeVersion)));

        // Without a connected key the library cannot use it; people who do not manage the library get nothing.
        var none = await OkAsync(admin.GetAsync(url));
        Assert.False(none.GetProperty("available").GetBoolean());
        Assert.Equal("conflict.documents.customer_key_missing", await KeyAsync(await admin.PutAsJsonAsync(url, new { customerKey = true })));
        Assert.Contains((await t["emp"].Client.PutAsJsonAsync(url, new { customerKey = true })).StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound });

        var vault = await ConnectAsync(t);
        Assert.True((await OkAsync(admin.GetAsync(url))).GetProperty("available").GetBoolean());
        var on = await OkAsync(admin.PutAsJsonAsync(url, new { customerKey = true }));
        Assert.True(on.GetProperty("customerKey").GetBoolean());
        Assert.Equal(1, on.GetProperty("pending").GetInt32());

        // New files are stored encrypted and read back as uploaded.
        const string Secret = "Minutes of the April board meeting";
        var (after, afterVersion) = await PublishAsync(admin, library, "minutes-april.txt", Secret);
        var raw = await RawAsync(t, library, after, afterVersion);
        Assert.True(Sealed(raw));
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.Equal(Secret, await DownloadAsync(admin, after));

        // The older file still opens before the pass, and is encrypted after it.
        Assert.Equal("Minutes of the March board meeting", await DownloadAsync(admin, before));
        Assert.True(await PassAsync(t) >= 1);
        Assert.True(Sealed(await RawAsync(t, library, before, beforeVersion)));
        Assert.Equal("Minutes of the March board meeting", await DownloadAsync(admin, before));
        Assert.Equal(0, (await OkAsync(admin.GetAsync(url))).GetProperty("pending").GetInt32());

        // It cannot be turned off.
        Assert.Equal("conflict.documents.customer_key_fixed", await KeyAsync(await admin.PutAsJsonAsync(url, new { customerKey = false })));

        // A revoked key closes the files, with nothing lost; restoring it opens them again.
        vault.Disable();
        Assert.Equal("revoked", await CheckAsync(t));
        var closed = await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            try
            {
                await using var s = await sp.GetRequiredService<DocumentStorage>().OpenReadAsync($"documents/{library:N}/{after:N}/{afterVersion:N}");
                return "opened";
            }
            catch (AppException ex)
            {
                return ex.Code;
            }
        });
        Assert.Equal(ProblemCodes.TenantKeyUnavailable, closed);
        vault.Enable();
        Assert.Equal("active", await CheckAsync(t));
        Assert.Equal(Secret, await DownloadAsync(admin, after));

        // Another tenant does not find the library.
        var (other, _) = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["admin"].Client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Packages_without_customer_keys_are_refused_and_other_libraries_stay_plain()
    {
        var (t, library) = await FreshAsync("workplace_plus");
        var admin = t["admin"].Client;
        Assert.False((await OkAsync(admin.GetAsync($"{Libraries}/{library}/encryption"))).GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"{Libraries}/{library}/encryption", new { customerKey = true })).StatusCode);

        // In a tenant with a key, a library that does not use it keeps its files as they are.
        var (g, plain) = await FreshAsync();
        await ConnectAsync(g);
        var (doc, version) = await PublishAsync(g["admin"].Client, plain, "plain.txt", "Plain text");
        Assert.False(Sealed(await RawAsync(g, plain, doc, version)));
        Assert.Equal("Plain text", await DownloadAsync(g["admin"].Client, doc));
    }
}
