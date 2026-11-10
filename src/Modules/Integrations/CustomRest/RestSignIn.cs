using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Integrations.CustomRest;

/// <summary>
/// The REST plumbing that custom REST and the ticketing packs share (spec 017 FR-003, FR-012): checking the base URL
/// and sign-in when settings are saved, signing a request with a secret manager reference, OAuth client credential
/// tokens, mapping the HTTP status of an answer, and reading at most 1 MB of it. Every host is checked against the
/// tenant allowlist; the connector HTTP client refuses internal addresses.
/// </summary>
public sealed class RestSignIn(HostAllowlist allowlist, ISecretStore secrets, IHttpClientFactory httpClients, RestTokenCache tokens, IClock clock)
{
    public const int MaxResponseBytes = 1024 * 1024;

    public static readonly string[] AllTypes = ["none", "api_key", "bearer", "basic", "oauth_client_credentials"];

    public Task<bool> IsAllowedAsync(Uri uri, CancellationToken ct) => allowlist.IsAllowedAsync(uri, ct);

    /// <summary>Adds the credential to <paramref name="request"/>; returns a failure when the secret is missing or refused.</summary>
    public async Task<PackCallResult?> AuthorizeAsync(PackConnection connection, RestSettings settings, HttpRequestMessage request, CancellationToken ct)
    {
        var a = settings.Auth;
        if (a.Type == "none")
        {
            return null;
        }
        var secret = a.SecretRef is null ? null : await secrets.GetTenantSecretAsync(connection.TenantId, a.SecretRef, ct);
        if (string.IsNullOrEmpty(secret))
        {
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialMissing);
        }
        switch (a.Type)
        {
            case "api_key":
                request.Headers.TryAddWithoutValidation(a.Header!, secret);
                return null;
            case "bearer":
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                return null;
            case "basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{a.Username}:{secret}")));
                return null;
            case "oauth_client_credentials":
                var (token, failure) = await TokenAsync(connection, settings, secret, ct);
                if (token is null)
                {
                    return failure;
                }
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return null;
            default:
                return PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
    }

    /// <summary>Forgets the cached token after the system refused it, so the next call asks for a new one.</summary>
    public void Refused(PackConnection connection, RestSettings settings)
    {
        if (settings.Auth.Type == "oauth_client_credentials")
        {
            tokens.Drop(TokenKey(connection, settings));
        }
    }

    private async Task<(string? Token, PackCallResult? Failure)> TokenAsync(PackConnection connection, RestSettings settings, string secret, CancellationToken ct)
    {
        var key = TokenKey(connection, settings);
        var now = clock.UtcNow;
        if (tokens.Get(key, now) is { } cached)
        {
            return (cached, null);
        }
        var a = settings.Auth;
        var tokenUri = new Uri(a.TokenUrl!);
        if (!await allowlist.IsAllowedAsync(tokenUri, ct))
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.HostNotAllowed));
        }
        var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = a.ClientId!, ["client_secret"] = secret };
        if (!string.IsNullOrEmpty(a.Scope))
        {
            form["scope"] = a.Scope;
        }
        using var client = httpClients.CreateClient(SafeHttp.ClientName);
        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(tokenUri, content, ct);
        var status = (int)response.StatusCode;
        if (status is 400 or 401 or 403)
        {
            return (null, PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused));
        }
        if (!response.IsSuccessStatusCode)
        {
            return (null, PackCallResult.Technical(status == 429 ? ConnectorErrorClasses.Throttled : ConnectorErrorClasses.ServerError, retryAfter: RetryAfter(response)));
        }
        var payload = await ReadLimitedAsync(response, ct);
        try
        {
            using var doc = JsonDocument.Parse(payload ?? "{}");
            var token = doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
            }
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var n) ? n : 300;
            tokens.Set(key, token, now.AddSeconds(Math.Max(0, seconds - 60)));
            return (token, null);
        }
        catch (JsonException)
        {
            return (null, PackCallResult.Technical(ConnectorErrorClasses.InvalidResponse));
        }
    }

    private static string TokenKey(PackConnection c, RestSettings s) =>
        $"{c.TenantId:N}:{c.Id:N}:{s.Auth.TokenUrl}:{s.Auth.ClientId}:{s.Auth.SecretRef}:{s.Auth.Scope}";

    /// <summary>
    /// The failure an HTTP status means, or null for 2xx: 401 and 403 are a refused credential, 429 is throttling,
    /// 5xx may have acted, a 4xx in <paramref name="business"/> is that business error, and any other 4xx is refused.
    /// </summary>
    public PackCallResult? MapStatus(PackConnection connection, RestSettings settings, HttpResponseMessage response, IReadOnlyDictionary<int, string>? business = null)
    {
        var status = (int)response.StatusCode;
        if (status is 401 or 403)
        {
            Refused(connection, settings);
            return PackCallResult.Fatal(ConnectorErrorClasses.CredentialRefused);
        }
        if (status == 429)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.Throttled, retryAfter: RetryAfter(response));
        }
        if (status >= 500)
        {
            return PackCallResult.Technical(ConnectorErrorClasses.ServerError, mayHaveActed: true, retryAfter: RetryAfter(response));
        }
        if (status >= 400)
        {
            return business is not null && business.TryGetValue(status, out var code) ? PackCallResult.Business(code) : PackCallResult.Fatal(ConnectorErrorClasses.RequestRefused);
        }
        return status is < 200 or >= 300 ? PackCallResult.Fatal(ConnectorErrorClasses.InvalidResponse) : null;
    }

    public static TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta is { } d ? (d > TimeSpan.FromMinutes(30) ? TimeSpan.FromMinutes(30) : d) : null;

    /// <summary>The answer as text, or null when it is larger than 1 MB.</summary>
    public static async Task<string?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        int read;
        while (total <= MaxResponseBytes && (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
        {
            total += read;
        }
        return total > MaxResponseBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>The answer as JSON, or null when it is too large or not JSON.</summary>
    public static async Task<JsonElement?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await ReadLimitedAsync(response, ct);
        if (payload is null)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static DateOnly? ExpiresOn(RestAuth? auth) =>
        auth?.ExpiresOn is { } e && DateOnly.TryParseExact(e, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>
    /// Checks a base URL and sign-in when settings are saved (keys under <c>validation.custom_rest</c>), adding problems
    /// to <paramref name="errors"/>. <paramref name="types"/> are the sign-in methods this kind accepts. Returns the
    /// settings as stored, with only the fields that apply to the sign-in type, or null when they cannot be stored.
    /// </summary>
    public async Task<RestSettings?> ValidateAsync(Guid tenantId, string? baseUrl, RestAuth? a, IReadOnlyCollection<string> types, ValidationErrors errors,
        CancellationToken ct, string baseField = "baseUrl")
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(baseUri.UserInfo)
            || !string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) || !baseUri.IsDefaultPort && baseUri.Port is < 1 or > 65535
            || IPAddress.TryParse(baseUri.IdnHost.Trim('[', ']'), out _))
        {
            errors.Add(baseField, "validation.custom_rest.base_url");
            baseUri = null;
        }
        else if (!await allowlist.IsAllowedAsync(baseUri, ct))
        {
            errors.Add(baseField, "validation.custom_rest.host_not_allowed");
        }
        if (a is null)
        {
            errors.Add("auth.type", "validation.custom_rest.auth_type");
            return null;
        }
        if (!types.Contains(a.Type))
        {
            errors.Add("auth.type", "validation.custom_rest.auth_type");
        }
        if (a.Type != "none" && SecretReferences.NameFor(tenantId, a.SecretRef) is null)
        {
            errors.Add("auth.secretRef", "validation.custom_rest.secret_ref");
        }
        if (a.Type == "api_key" && (a.Header is null || a.Header.Length > 64 || a.Header.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            || a.Header.Equals("host", StringComparison.OrdinalIgnoreCase) || a.Header.Equals("cookie", StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("auth.header", "validation.custom_rest.headers");
        }
        if (a.Type == "basic" && (string.IsNullOrEmpty(a.Username) || a.Username.Length > 200 || a.Username.Contains(':')))
        {
            errors.Add("auth.username", "validation.custom_rest.username");
        }
        if (a.Type == "oauth_client_credentials")
        {
            if (!Uri.TryCreate(a.TokenUrl, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps || !await allowlist.IsAllowedAsync(tokenUri, ct))
            {
                errors.Add("auth.tokenUrl", "validation.custom_rest.host_not_allowed");
            }
            if (string.IsNullOrEmpty(a.ClientId) || a.ClientId.Length > 200)
            {
                errors.Add("auth.clientId", "validation.custom_rest.client_id");
            }
            if (a.Scope is { Length: > 500 })
            {
                errors.Add("auth.scope", "validation.custom_rest.scope");
            }
        }
        if (a.ExpiresOn is not null && ExpiresOn(a) is null)
        {
            errors.Add("auth.expiresOn", "validation.custom_rest.expires_on");
        }
        if (baseUri is null)
        {
            return null;
        }
        return new RestSettings(baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/'), new RestAuth(a.Type,
            a.Type == "api_key" ? a.Header : null, a.Type == "basic" ? a.Username : null, a.Type == "none" ? null : a.SecretRef,
            a.Type == "oauth_client_credentials" ? a.TokenUrl : null, a.Type == "oauth_client_credentials" ? a.ClientId : null,
            a.Type == "oauth_client_credentials" ? a.Scope : null, a.ExpiresOn));
    }

    /// <summary>Test connection checks for the base URL host, the credential and, for OAuth client credentials, a fresh token.</summary>
    public async Task<List<PackCheck>> TestAsync(PackConnection connection, RestSettings settings, CancellationToken ct)
    {
        var checks = new List<PackCheck>();
        var hostOk = await allowlist.IsAllowedAsync(new Uri(settings.BaseUrl.TrimEnd('/') + "/"), ct);
        checks.Add(new PackCheck("base_url", hostOk, hostOk ? "integrations.test.host_allowed" : "integrations.test.host_not_allowed",
            hostOk ? null : ConnectorErrorClasses.HostNotAllowed));
        if (settings.Auth.Type == "none")
        {
            return checks;
        }
        var secret = settings.Auth.SecretRef is null ? null : await secrets.GetTenantSecretAsync(connection.TenantId, settings.Auth.SecretRef, ct);
        var found = !string.IsNullOrEmpty(secret);
        checks.Add(new PackCheck("credential", found, found ? "integrations.test.credential_found" : "integrations.test.credential_missing",
            found ? null : ConnectorErrorClasses.CredentialMissing));
        if (found && settings.Auth.Type == "oauth_client_credentials")
        {
            tokens.Drop(TokenKey(connection, settings));
            PackCheck check;
            try
            {
                var (token, failure) = await TokenAsync(connection, settings, secret!, ct);
                check = token is not null
                    ? new PackCheck("token", true, "integrations.test.token_ok")
                    : new PackCheck("token", false, "integrations.test.token_refused", failure?.ErrorClass);
            }
            catch (HttpRequestException)
            {
                check = new PackCheck("token", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                check = new PackCheck("token", false, "integrations.test.not_responding", ConnectorErrorClasses.NotResponding);
            }
            checks.Add(check);
        }
        return checks;
    }
}
