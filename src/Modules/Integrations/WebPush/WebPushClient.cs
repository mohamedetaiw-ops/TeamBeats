using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.WebPush;

/// <summary>
/// Web Push settings (<c>Integrations:WebPush</c>). The VAPID key pair comes from the secret manager as base64url values
/// (the raw 32 byte private key and the 65 byte uncompressed public key, as common VAPID tools print them). Development
/// and test hosts without a key get a new pair per process; any other host without one has no push.
/// </summary>
public sealed class WebPushOptions
{
    public const string Section = "Integrations:WebPush";

    public string? PrivateKey { get; set; }
    public string? PublicKey { get; set; }

    /// <summary>The contact push services may use about this installation (RFC 8292): a mailto: or https: address.</summary>
    public string Subject { get; set; } = "mailto:operations@teambeats.invalid";

    /// <summary>Push service hosts, matched as the host or a subdomain of it. The defaults are the browsers' own services.</summary>
    public List<string> AllowedHosts { get; set; } =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "push.services.mozilla.com",
        "push.apple.com",
        "notify.windows.com",
    ];
}

/// <summary>
/// The Web Push adapter (spec 030 US2, spec 015 push channel). Each message is encrypted with aes128gcm for the
/// browser's keys (RFC 8291), so the push service never reads it, and carries a VAPID token signed with ES256 (RFC 8292)
/// for that push service only, valid for 12 hours.
/// </summary>
public sealed class WebPushClient : IWebPush, IDisposable
{
    public const string ClientName = "web-push";
    public const int RecordSize = 4096;
    private static readonly byte[] KeyInfo = Encoding.ASCII.GetBytes("WebPush: info\0");
    private static readonly byte[] CekInfo = Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0");
    private static readonly byte[] NonceInfo = Encoding.ASCII.GetBytes("Content-Encoding: nonce\0");

    private readonly IHttpClientFactory _http;
    private readonly IClock _clock;
    private readonly ILogger<WebPushClient> _logger;
    private readonly WebPushOptions _options;
    private readonly ECDsa? _signer;
    private readonly byte[]? _publicKey;

    public WebPushClient(IHttpClientFactory http, IClock clock, IConfiguration configuration, IHostEnvironment environment, ILogger<WebPushClient> logger)
    {
        _http = http;
        _clock = clock;
        _logger = logger;
        _options = configuration.GetSection(WebPushOptions.Section).Get<WebPushOptions>() ?? new WebPushOptions();
        if (!string.IsNullOrWhiteSpace(_options.PrivateKey) && !string.IsNullOrWhiteSpace(_options.PublicKey))
        {
            try
            {
                _publicKey = Base64Url.Decode(_options.PublicKey);
                _signer = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    D = Base64Url.Decode(_options.PrivateKey),
                    Q = PointOf(_publicKey),
                });
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
            {
                _logger.LogError("Web Push keys in {Section} are not a valid P-256 key pair; push is off.", WebPushOptions.Section);
                _signer = null;
                _publicKey = null;
            }
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _publicKey = Uncompressed(_signer.ExportParameters(false).Q);
        }
    }

    public string? PublicKey => _publicKey is null ? null : Base64Url.Encode(_publicKey);

    public bool IsAllowedEndpoint(string endpoint)
    {
        if (endpoint.Length > 1000 || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }
        var host = uri.IdnHost.ToLowerInvariant();
        return _options.AllowedHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    public async Task<WebPushOutcome> SendAsync(WebPushTarget target, string payload, TimeSpan timeToLive, bool urgent, CancellationToken ct = default)
    {
        if (_signer is null || _publicKey is null)
        {
            return WebPushOutcome.Unavailable;
        }
        if (!IsAllowedEndpoint(target.Endpoint))
        {
            return WebPushOutcome.Rejected;
        }
        byte[] body;
        try
        {
            body = Encrypt(Encoding.UTF8.GetBytes(payload), Base64Url.Decode(target.P256dh), Base64Url.Decode(target.Auth));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            // The browser's keys are unusable: the subscription can never receive anything.
            return WebPushOutcome.Gone;
        }
        var endpoint = new Uri(target.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.Add("TTL", ((int)timeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("Urgency", urgent ? "high" : "normal");
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={Token(endpoint)}, k={PublicKey}");
        try
        {
            using var response = await _http.CreateClient(ClientName).SendAsync(request, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted or HttpStatusCode.NoContent => WebPushOutcome.Sent,
                // Unknown or expired subscription, or one made for another key: it will never work again.
                HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Forbidden => WebPushOutcome.Gone,
                HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout => WebPushOutcome.Transient,
                >= HttpStatusCode.InternalServerError => WebPushOutcome.Transient,
                _ => WebPushOutcome.Rejected,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("Push service {Host} did not answer", endpoint.Host);
            return WebPushOutcome.Transient;
        }
    }

    /// <summary>The aes128gcm body for one browser (RFC 8291 section 3): header with salt and our key, then one record.</summary>
    public static byte[] Encrypt(byte[] plaintext, byte[] browserPublicKey, byte[] authSecret)
    {
        if (browserPublicKey.Length != 65 || authSecret.Length != 16)
        {
            throw new ArgumentException("A push subscription has a 65 byte public key and a 16 byte auth secret.");
        }
        using var ours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var theirs = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = PointOf(browserPublicKey) });
        var ourPublic = Uncompressed(ours.ExportParameters(false).Q);
        var shared = ours.DeriveRawSecretAgreement(theirs.PublicKey);
        var salt = RandomNumberGenerator.GetBytes(16);
        var (cek, nonce) = Keys(shared, authSecret, salt, browserPublicKey, ourPublic);

        // One record: the message, then the delimiter 0x02 that marks the last record, with no padding.
        var record = new byte[plaintext.Length + 1];
        plaintext.CopyTo(record, 0);
        record[^1] = 0x02;
        var cipher = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
        {
            aes.Encrypt(nonce, record, cipher, tag);
        }
        if (cipher.Length + tag.Length > RecordSize)
        {
            throw new ArgumentException("The push message is longer than one record.");
        }
        var body = new byte[16 + 4 + 1 + ourPublic.Length + cipher.Length + tag.Length];
        salt.CopyTo(body, 0);
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(16), RecordSize);
        body[20] = (byte)ourPublic.Length;
        ourPublic.CopyTo(body, 21);
        cipher.CopyTo(body, 21 + ourPublic.Length);
        tag.CopyTo(body, 21 + ourPublic.Length + cipher.Length);
        return body;
    }

    /// <summary>The content key and nonce (RFC 8291 section 3.4, RFC 8188 section 2.2), shared by both ends.</summary>
    public static (byte[] Cek, byte[] Nonce) Keys(byte[] shared, byte[] authSecret, byte[] salt, byte[] browserPublicKey, byte[] serverPublicKey)
    {
        var keyInfo = new byte[KeyInfo.Length + 130];
        KeyInfo.CopyTo(keyInfo, 0);
        browserPublicKey.CopyTo(keyInfo, KeyInfo.Length);
        serverPublicKey.CopyTo(keyInfo, KeyInfo.Length + 65);
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        return (HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, CekInfo), HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, NonceInfo));
    }

    /// <summary>A VAPID token for the push service's origin (RFC 8292): ES256, audience, 12 hour expiry and the contact.</summary>
    private string Token(Uri endpoint)
    {
        var header = Base64Url.Encode(Encoding.UTF8.GetBytes("""{"typ":"JWT","alg":"ES256"}"""));
        var claims = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["aud"] = endpoint.GetLeftPart(UriPartial.Authority),
            ["exp"] = _clock.UtcNow.AddHours(12).ToUnixTimeSeconds(),
            ["sub"] = _options.Subject,
        }));
        var signed = $"{header}.{claims}";
        var signature = _signer!.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signed}.{Base64Url.Encode(signature)}";
    }

    public static ECPoint PointOf(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04)
        {
            throw new ArgumentException("Not an uncompressed P-256 point.");
        }
        return new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] };
    }

    public static byte[] Uncompressed(ECPoint point) => [0x04, .. point.X!, .. point.Y!];

    public void Dispose() => _signer?.Dispose();
}

/// <summary>base64url without padding, as Web Push uses it for keys and tokens.</summary>
public static class Base64Url
{
    public static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Decode(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
