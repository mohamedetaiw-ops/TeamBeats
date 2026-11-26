using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.NativePush;

/// <summary>
/// Firebase Cloud Messaging settings (<c>Integrations:Fcm</c>). The service account's client e-mail and PEM private key
/// come from the secret manager. Development and test hosts without one get a fictional project and a new key per
/// process, which only fakes accept; any other host without one has no native push.
/// </summary>
public sealed class FcmOptions
{
    public const string Section = "Integrations:Fcm";

    public string? ProjectId { get; set; }
    public string? ClientEmail { get; set; }
    public string? PrivateKey { get; set; }
    public string TokenUri { get; set; } = "https://oauth2.googleapis.com/token";
    public string Endpoint { get; set; } = "https://fcm.googleapis.com/";
}

/// <summary>
/// The FCM HTTP v1 adapter (spec 030 R3 MOB-022). It signs a short JWT with RS256 for the messaging scope, swaps it for
/// an access token it keeps until five minutes before expiry, and sends one message per device token. An unknown or
/// unregistered token is reported so the caller removes the device; rate limits and server errors are transient.
/// </summary>
public sealed class FcmPushClient : INativePush, IDisposable
{
    public const string ClientName = "fcm";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly IHttpClientFactory _http;
    private readonly IClock _clock;
    private readonly ILogger<FcmPushClient> _logger;
    private readonly FcmOptions _options;
    private readonly RSA? _key;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Value, DateTimeOffset Until)? _token;

    public FcmPushClient(IHttpClientFactory http, IClock clock, IConfiguration configuration, IHostEnvironment environment, ILogger<FcmPushClient> logger)
    {
        _http = http;
        _clock = clock;
        _logger = logger;
        _options = configuration.GetSection(FcmOptions.Section).Get<FcmOptions>() ?? new FcmOptions();
        if (!string.IsNullOrWhiteSpace(_options.ProjectId) && !string.IsNullOrWhiteSpace(_options.ClientEmail) && !string.IsNullOrWhiteSpace(_options.PrivateKey))
        {
            try
            {
                _key = RSA.Create();
                _key.ImportFromPem(_options.PrivateKey);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                _logger.LogError("The messaging key in {Section} is not a PEM RSA key; native push is off.", FcmOptions.Section);
                _key?.Dispose();
                _key = null;
            }
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        {
            _options.ProjectId = "teambeats-dev";
            _options.ClientEmail = "push@teambeats-dev.invalid";
            _key = RSA.Create(2048);
        }
    }

    public bool Available => _key is not null;

    public async Task<NativePushOutcome> SendAsync(NativePushMessage message, CancellationToken ct = default)
    {
        if (_key is null)
        {
            return NativePushOutcome.Unavailable;
        }
        if (message.Token.Length is 0 or > 4096 || message.Platform is not ("android" or "ios"))
        {
            return NativePushOutcome.Rejected;
        }
        try
        {
            var token = await AccessTokenAsync(ct);
            if (token is null)
            {
                return NativePushOutcome.Transient;
            }
            var ttl = ((int)message.TimeToLive.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var body = new
            {
                message = new
                {
                    token = message.Token,
                    notification = new { title = message.Title, body = message.Body },
                    data = new Dictionary<string, string> { ["link"] = message.Link, ["tag"] = message.Tag },
                    android = new { priority = message.Urgent ? "high" : "normal", ttl = ttl + "s", notification = new { tag = message.Tag } },
                    apns = new
                    {
                        headers = new Dictionary<string, string>
                        {
                            ["apns-priority"] = message.Urgent ? "10" : "5",
                            ["apns-expiration"] = (_clock.UtcNow + message.TimeToLive).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["apns-collapse-id"] = message.Tag,
                        },
                    },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.Endpoint), $"v1/projects/{Uri.EscapeDataString(_options.ProjectId!)}/messages:send"))
            {
                Content = JsonContent.Create(body),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _http.CreateClient(ClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return NativePushOutcome.Sent;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _token = null;
                return NativePushOutcome.Transient;
            }
            if (response.StatusCode is HttpStatusCode.NotFound
                || response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync(ct)).Contains("UNREGISTERED", StringComparison.Ordinal))
            {
                return NativePushOutcome.Unregistered;
            }
            return response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError
                ? NativePushOutcome.Transient
                : NativePushOutcome.Rejected;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogInformation("The messaging service did not answer");
            return NativePushOutcome.Transient;
        }
    }

    /// <summary>The OAuth access token for the messaging scope (RFC 7523 JWT bearer grant), kept until five minutes before it ends.</summary>
    private async Task<string?> AccessTokenAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (_token is { } cached && cached.Until > now)
        {
            return cached.Value;
        }
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (_token is { } again && again.Until > now)
            {
                return again.Value;
            }
            var assertion = Assertion(now);
            using var response = await _http.CreateClient(ClientName).PostAsync(new Uri(_options.TokenUri), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }), ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("The messaging token request was refused with {Status}", (int)response.StatusCode);
                return null;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var value = json.RootElement.GetProperty("access_token").GetString();
            var seconds = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            _token = (value, now.AddSeconds(Math.Max(60, seconds - 300)));
            return value;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private string Assertion(DateTimeOffset now)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = _options.ClientEmail!,
            ["scope"] = Scope,
            ["aud"] = _options.TokenUri,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(60).ToUnixTimeSeconds(),
        }));
        var signed = $"{header}.{claims}";
        return signed + "." + B64(_key!.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public void Dispose()
    {
        _key?.Dispose();
        _tokenLock.Dispose();
    }
}
