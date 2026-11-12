using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TeamBeats.Modules.Integrations.CustomRest;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.Google;

/// <summary>The Google endpoints a Google Workspace connection calls. Each host must be on the tenant allowlist.</summary>
public static class GoogleHosts
{
    public const string TokenUrl = "https://oauth2.googleapis.com/token";
    public const string DirectoryBase = "https://admin.googleapis.com/admin/directory/v1";
    public const string DriveBase = "https://www.googleapis.com/drive/v3";

    public const string DirectoryScope = "https://www.googleapis.com/auth/admin.directory.user.readonly https://www.googleapis.com/auth/admin.directory.group.readonly";
    public const string DriveScope = "https://www.googleapis.com/auth/drive.readonly";
    public const string CalendarBase = "https://www.googleapis.com/calendar/v3";
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.events.readonly";

    /// <summary>The hosts each feature needs, the token host first.</summary>
    public static IReadOnlyList<Uri> For(IEnumerable<string> features)
    {
        var hosts = new List<Uri> { new(TokenUrl) };
        foreach (var f in features)
        {
            hosts.Add(new Uri(f switch
            {
                GoogleFeatures.Directory => DirectoryBase,
                GoogleFeatures.Calendar => CalendarBase,
                _ => DriveBase,
            }));
        }
        return hosts;
    }
}

/// <summary>A token for one Google user, or the reason there is none.</summary>
public sealed record GoogleToken(string? AccessToken, string? ErrorClass)
{
    public bool Ok => AccessToken is not null;
}

/// <summary>
/// Service account sign-in with domain-wide delegation (RFC 7523 JWT bearer grant): TeamBeats signs a short assertion
/// with the service account's private key, read from the secret manager, and Google answers with a token that acts
/// as <c>sub</c>, a user of the customer's domain. Calls as that user see only what the user may see. Tokens are cached
/// per tenant, connection, user and scope until a minute before they expire, and never logged.
/// </summary>
public sealed class GoogleSignIn(HostAllowlist allowlist, ISecretStore secrets, IHttpClientFactory httpClients, RestTokenCache tokens, IClock clock)
{
    public static readonly TimeSpan AssertionLifetime = TimeSpan.FromMinutes(60);

    public Task<bool> IsAllowedAsync(Uri uri, CancellationToken ct) => allowlist.IsAllowedAsync(uri, ct);

    /// <summary>The private key and client email from the secret: a PEM key, or the service account key file Google gives.</summary>
    public async Task<(RSA? Key, string? ClientEmail)> KeyAsync(Guid tenantId, GoogleSettings settings, CancellationToken ct)
    {
        var secret = settings.PrivateKeyRef is null ? null : await secrets.GetTenantSecretAsync(tenantId, settings.PrivateKeyRef, ct);
        if (string.IsNullOrWhiteSpace(secret))
        {
            return (null, null);
        }
        string pem = secret;
        string? email = null;
        if (secret.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(secret);
                pem = doc.RootElement.TryGetProperty("private_key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "";
                email = doc.RootElement.TryGetProperty("client_email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            }
            catch (JsonException)
            {
                return (null, null);
            }
        }
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            return (rsa, email);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return (null, null);
        }
    }

    /// <summary>A token acting as <paramref name="subject"/> with <paramref name="scope"/>.</summary>
    public async Task<GoogleToken> TokenAsync(PackConnection connection, GoogleSettings settings, string subject, string scope, CancellationToken ct, bool fresh = false)
    {
        var key = $"google:{connection.TenantId:N}:{connection.Id:N}:{settings.ServiceAccountEmail}:{settings.PrivateKeyRef}:{subject.ToLowerInvariant()}:{scope}";
        var now = clock.UtcNow;
        if (fresh)
        {
            tokens.Drop(key);
        }
        else if (tokens.Get(key, now) is { } cached)
        {
            return new GoogleToken(cached, null);
        }
        var tokenUri = new Uri(GoogleHosts.TokenUrl);
        if (!await allowlist.IsAllowedAsync(tokenUri, ct))
        {
            return new GoogleToken(null, ConnectorErrorClasses.HostNotAllowed);
        }
        var (rsa, _) = await KeyAsync(connection.TenantId, settings, ct);
        if (rsa is null)
        {
            return new GoogleToken(null, ConnectorErrorClasses.CredentialMissing);
        }
        string assertion;
        using (rsa)
        {
            assertion = Assertion(rsa, settings.ServiceAccountEmail!, subject, scope, now);
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        });
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(tokenUri, content, ct);
        }
        catch (HttpRequestException)
        {
            return new GoogleToken(null, ConnectorErrorClasses.NotResponding);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new GoogleToken(null, ConnectorErrorClasses.NotResponding);
        }
        using (response)
        {
            var status = (int)response.StatusCode;
            if (status is 400 or 401 or 403)
            {
                // invalid_grant or unauthorized_client: the key, the delegation or the user is not accepted.
                return new GoogleToken(null, ConnectorErrorClasses.CredentialRefused);
            }
            if (!response.IsSuccessStatusCode)
            {
                return new GoogleToken(null, status == 429 ? ConnectorErrorClasses.Throttled : ConnectorErrorClasses.ServerError);
            }
            if (await RestSignIn.ReadJsonAsync(response, ct) is not { ValueKind: JsonValueKind.Object } json
                || !json.TryGetProperty("access_token", out var t) || t.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(t.GetString()))
            {
                return new GoogleToken(null, ConnectorErrorClasses.InvalidResponse);
            }
            var seconds = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 300;
            tokens.Set(key, t.GetString()!, now.AddSeconds(Math.Max(0, seconds - 60)));
            return new GoogleToken(t.GetString(), null);
        }
    }

    /// <summary>Forgets a token Google refused, so the next call asks for a new one.</summary>
    public void Refused(PackConnection connection, GoogleSettings settings, string subject, string scope) =>
        tokens.Drop($"google:{connection.TenantId:N}:{connection.Id:N}:{settings.ServiceAccountEmail}:{settings.PrivateKeyRef}:{subject.ToLowerInvariant()}:{scope}");

    /// <summary>The signed JWT assertion (RS256) of the JWT bearer grant.</summary>
    public static string Assertion(RSA key, string issuer, string subject, string scope, DateTimeOffset now)
    {
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = issuer,
            sub = subject,
            scope,
            aud = GoogleHosts.TokenUrl,
            iat = now.ToUnixTimeSeconds(),
            exp = now.Add(AssertionLifetime).ToUnixTimeSeconds(),
        }));
        var signed = $"{header}.{claims}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signed}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
