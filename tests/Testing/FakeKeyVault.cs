using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace TeamBeats.Testing;

/// <summary>
/// Stands in for a customer's key service behind <see cref="FakeRestApi"/> (spec 026 US8): an Azure Key Vault key
/// (with the Entra token endpoint) or a HashiCorp Vault transit key, each on its own host. Versions are random AES keys
/// held here, so wrapping is real and a wrong version or key does not unwrap. The customer's actions are methods:
/// <see cref="Rotate"/>, <see cref="Disable"/>, <see cref="Enable"/>, and <see cref="Down"/> for an outage.
/// </summary>
public sealed class FakeKeyVault
{
    public const string EntraHost = "login.microsoftonline.com";

    private readonly List<(string Id, byte[] Key)> versions = [];
    private readonly object gate = new();
    private int wraps;
    private int unwraps;

    public string Host { get; }
    public string Name { get; }
    public bool Transit { get; }
    public bool Enabled { get; private set; } = true;
    public bool IsDown { get; private set; }

    public int Wraps => wraps;
    public int Unwraps => unwraps;

    private FakeKeyVault(string host, string name, bool transit)
    {
        Host = host;
        Name = name;
        Transit = transit;
        Rotate();
    }

    /// <summary>An Azure Key Vault key at <c>https://{host}/keys/{name}</c>. Any bearer token from the fake Entra endpoint works.</summary>
    public static FakeKeyVault Azure(FakeRestApi rest, string? host = null)
    {
        var vault = new FakeKeyVault(host ?? $"kv{Guid.NewGuid():N}"[..20] + ".vault.azure.net", "teambeats", transit: false);
        rest.On(EntraHost, s => s.Body is { } body && body.Contains("client_secret=", StringComparison.Ordinal)
            ? FakeRestApi.Json(HttpStatusCode.OK, """{"access_token":"fake-entra-token","expires_in":3600}""")
            : FakeRestApi.Json(HttpStatusCode.BadRequest, """{"error":"invalid_client"}"""));
        rest.On(vault.Host, vault.Azure);
        return vault;
    }

    /// <summary>A Vault transit key at <c>https://{host}/v1/transit/keys/{name}</c>, for the token <paramref name="token"/>.</summary>
    public static FakeKeyVault VaultTransit(FakeRestApi rest, string token, string? host = null)
    {
        var vault = new FakeKeyVault(host ?? $"vault-{Guid.NewGuid():N}"[..20] + ".example.test", "teambeats", transit: true);
        rest.On(vault.Host, s => s.Headers.TryGetValue("X-Vault-Token", out var t) && t == token ? vault.Vault(s) : new HttpResponseMessage(HttpStatusCode.Forbidden));
        return vault;
    }

    public string KeyUri => Transit ? $"https://{Host}/v1/transit/keys/{Name}" : $"https://{Host}/keys/{Name}";

    public string LatestVersion
    {
        get
        {
            lock (gate)
            {
                return versions[^1].Id;
            }
        }
    }

    public void Rotate()
    {
        lock (gate)
        {
            var id = Transit ? (versions.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : Guid.NewGuid().ToString("N");
            versions.Add((id, RandomNumberGenerator.GetBytes(32)));
        }
    }

    public void Disable() => Enabled = false;

    public void Enable() => Enabled = true;

    public void Down(bool down = true) => IsDown = down;

    private byte[]? KeyOf(string version)
    {
        lock (gate)
        {
            return versions.FirstOrDefault(v => v.Id == version).Key;
        }
    }

    private static byte[] Seal(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. cipher, .. tag];
    }

    private static byte[]? Open(byte[] key, byte[] sealedBytes)
    {
        if (sealedBytes.Length < 28)
        {
            return null;
        }
        var plain = new byte[sealedBytes.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(12, plain.Length), sealedBytes.AsSpan(12 + plain.Length), plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private static string B64Url(byte[] v) => Convert.ToBase64String(v).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string v)
    {
        var s = v.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private HttpResponseMessage Azure(FakeRestApi.Sent s)
    {
        if (IsDown)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        if (!s.Headers.TryGetValue("Authorization", out var auth) || auth != "Bearer fake-entra-token")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        var parts = s.Uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0] != "keys" || parts[1] != Name)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        if (s.Method == "GET" && parts.Length == 2)
        {
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                key = new { kid = $"https://{Host}/keys/{Name}/{LatestVersion}", kty = "RSA" },
                attributes = new { enabled = Enabled },
            }));
        }
        if (s.Method != "POST" || parts.Length != 4)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
        if (!Enabled)
        {
            return FakeRestApi.Json(HttpStatusCode.Forbidden, """{"error":{"code":"Forbidden","message":"Operation is not allowed on a disabled key."}}""");
        }
        if (KeyOf(parts[2]) is not { } key)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var body = JsonDocument.Parse(s.Body!).RootElement;
        var value = FromB64Url(body.GetProperty("value").GetString()!);
        if (parts[3] == "wrapkey")
        {
            Interlocked.Increment(ref wraps);
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { kid = $"https://{Host}/keys/{Name}/{parts[2]}", value = B64Url(Seal(key, value)) }));
        }
        Interlocked.Increment(ref unwraps);
        return Open(key, value) is { } plain
            ? FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { kid = $"https://{Host}/keys/{Name}/{parts[2]}", value = B64Url(plain) }))
            : FakeRestApi.Json(HttpStatusCode.BadRequest, """{"error":{"code":"BadParameter"}}""");
    }

    private HttpResponseMessage Vault(FakeRestApi.Sent s)
    {
        if (IsDown)
        {
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
        var path = s.Uri.AbsolutePath;
        if (s.Method == "GET" && path == $"/v1/transit/keys/{Name}")
        {
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { name = Name, latest_version = int.Parse(LatestVersion, System.Globalization.CultureInfo.InvariantCulture) } }));
        }
        if (!Enabled)
        {
            return FakeRestApi.Json(HttpStatusCode.Forbidden, """{"errors":["permission denied"]}""");
        }
        var body = JsonDocument.Parse(s.Body ?? "{}").RootElement;
        if (s.Method == "POST" && path == $"/v1/transit/encrypt/{Name}")
        {
            Interlocked.Increment(ref wraps);
            var version = body.TryGetProperty("key_version", out var kv) ? kv.GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : LatestVersion;
            var sealedBytes = Seal(KeyOf(version)!, Convert.FromBase64String(body.GetProperty("plaintext").GetString()!));
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { ciphertext = $"vault:v{version}:{Convert.ToBase64String(sealedBytes)}", key_version = int.Parse(version, System.Globalization.CultureInfo.InvariantCulture) } }));
        }
        if (s.Method == "POST" && path == $"/v1/transit/decrypt/{Name}")
        {
            Interlocked.Increment(ref unwraps);
            var parts = body.GetProperty("ciphertext").GetString()!.Split(':');
            if (parts.Length != 3 || KeyOf(parts[1][1..]) is not { } key || Open(key, Convert.FromBase64String(parts[2])) is not { } plain)
            {
                return FakeRestApi.Json(HttpStatusCode.BadRequest, """{"errors":["invalid ciphertext"]}""");
            }
            return FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { data = new { plaintext = Convert.ToBase64String(plain) } }));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
