using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.KeyManagement;

/// <summary>Why a key service call failed, as the key page names it.</summary>
public static class KeyErrorClasses
{
    public const string CredentialMissing = ConnectorErrorClasses.CredentialMissing;
    public const string CredentialRefused = ConnectorErrorClasses.CredentialRefused;
    public const string KeyDisabled = "key_disabled";
    public const string KeyNotFound = "key_not_found";
    public const string NotResponding = ConnectorErrorClasses.NotResponding;
    public const string ServerError = ConnectorErrorClasses.ServerError;
    public const string InvalidResponse = ConnectorErrorClasses.InvalidResponse;
    public const string BlockedAddress = ConnectorErrorClasses.BlockedAddress;
    public const string HostNotAllowed = ConnectorErrorClasses.HostNotAllowed;
}

/// <summary>
/// <see cref="IKeyManagement"/> for Azure Key Vault and HashiCorp Vault transit (spec 026 US8). Each call is a plain
/// REST request through the connector HTTP client, so the address guard applies and no SDK is needed. A refusal by the
/// service (401, 403, 404, or a disabled key) is <see cref="KeyAccess.Denied"/>; anything else that fails is
/// <see cref="KeyAccess.Unreachable"/>. Nothing sent or received is logged, and plaintext buffers are not kept.
/// </summary>
public sealed partial class KeyManagementService(
    IHttpClientFactory httpClients, ISecretStore secrets, HostAllowlist allowlist, ITenantContext context, IMemoryCache cache) : IKeyManagement
{
    public const string AzureApiVersion = "7.4";
    public const string AzureAlgorithm = "RSA-OAEP-256";
    public const string EntraHost = "login.microsoftonline.com";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^/keys/(?<name>[A-Za-z0-9-]{1,127})/?$")]
    private static partial Regex AzureKeyPath();

    [GeneratedRegex("^/v1/(?<mount>[a-z0-9_-]+(/[a-z0-9_-]+)*)/keys/(?<name>[A-Za-z0-9_.-]{1,128})/?$")]
    private static partial Regex VaultKeyPath();

    public async Task<KeyReference> ValidateAsync(KeyReference key, CancellationToken ct = default)
    {
        var tenant = context.RequireTenant();
        var errors = new ValidationErrors();
        if (!KeyProviders.All.Contains(key.Provider))
        {
            throw AppException.Validation("provider", "validation.keys.provider");
        }
        string? uri = null;
        if (key.KeyUri is not { Length: > 0 and <= 500 } text || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) || !parsed.IsDefaultPort && key.Provider == KeyProviders.AzureKeyVault)
        {
            errors.Add("keyUri", "validation.keys.key_uri");
        }
        else if (key.Provider == KeyProviders.AzureKeyVault)
        {
            var host = parsed.IdnHost.ToLowerInvariant();
            if (!(host.EndsWith(".vault.azure.net", StringComparison.Ordinal) || host.EndsWith(".managedhsm.azure.net", StringComparison.Ordinal)) || AzureKeyPath().Match(parsed.AbsolutePath) is not { Success: true } m)
            {
                errors.Add("keyUri", "validation.keys.key_uri");
            }
            else
            {
                uri = $"https://{host}/keys/{m.Groups["name"].Value}";
            }
        }
        else if (VaultKeyPath().Match(parsed.AbsolutePath) is not { Success: true } vm || IPAddress.TryParse(parsed.IdnHost.Trim('[', ']'), out _))
        {
            errors.Add("keyUri", "validation.keys.key_uri");
        }
        else if (!await allowlist.IsAllowedAsync(parsed, ct))
        {
            errors.Add("keyUri", "validation.custom_rest.host_not_allowed");
        }
        else
        {
            uri = $"https://{parsed.IdnHost.ToLowerInvariant()}{(parsed.IsDefaultPort ? "" : ":" + parsed.Port)}/v1/{vm.Groups["mount"].Value}/keys/{vm.Groups["name"].Value}";
        }
        if (SecretReferences.NameFor(tenant, key.SecretRef?.Trim()) is null)
        {
            errors.Add("secretRef", "validation.custom_rest.secret_ref");
        }
        string? directory = null, client = null;
        if (key.Provider == KeyProviders.AzureKeyVault)
        {
            if (!Guid.TryParse(key.DirectoryId, out var d))
            {
                errors.Add("directoryId", "validation.keys.directory_id");
            }
            if (!Guid.TryParse(key.ClientId, out var c))
            {
                errors.Add("clientId", "validation.keys.client_id");
            }
            directory = d.ToString("D");
            client = c.ToString("D");
        }
        errors.ThrowIfAny();
        return new KeyReference(key.Provider, uri!, key.SecretRef!.Trim(), directory, client);
    }

    public Task<KeyResult> CurrentVersionAsync(KeyReference key, CancellationToken ct = default) =>
        GuardAsync(() => key.Provider == KeyProviders.AzureKeyVault ? AzureCurrentAsync(key, ct) : VaultCurrentAsync(key, ct));

    public Task<KeyResult> WrapAsync(KeyReference key, byte[] plaintext, CancellationToken ct = default) =>
        GuardAsync(async () =>
        {
            var current = await CurrentVersionAsync(key, ct);
            if (current.Access != KeyAccess.Ok)
            {
                return current;
            }
            return key.Provider == KeyProviders.AzureKeyVault
                ? await AzureCryptAsync(key, current.Version!, "wrapkey", plaintext, ct)
                : await VaultEncryptAsync(key, current.Version!, plaintext, ct);
        });

    public Task<KeyResult> UnwrapAsync(KeyReference key, string version, byte[] wrapped, CancellationToken ct = default) =>
        GuardAsync(() => key.Provider == KeyProviders.AzureKeyVault ? AzureCryptAsync(key, version, "unwrapkey", wrapped, ct) : VaultDecryptAsync(key, wrapped, ct));

    private static async Task<KeyResult> GuardAsync(Func<Task<KeyResult>> call)
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException ex) when (ex.InnerException is BlockedAddressException)
        {
            return KeyResult.Fail(KeyAccess.Unreachable, KeyErrorClasses.BlockedAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            return KeyResult.Fail(KeyAccess.Unreachable, KeyErrorClasses.NotResponding);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return KeyResult.Fail(KeyAccess.Unreachable, KeyErrorClasses.InvalidResponse);
        }
    }

    private static KeyResult? Refused(HttpResponseMessage response) => (int)response.StatusCode switch
    {
        >= 200 and < 300 => null,
        401 or 403 => KeyResult.Fail(KeyAccess.Denied, KeyErrorClasses.CredentialRefused),
        404 => KeyResult.Fail(KeyAccess.Denied, KeyErrorClasses.KeyNotFound),
        >= 500 or 408 or 429 => KeyResult.Fail(KeyAccess.Unreachable, KeyErrorClasses.ServerError),
        _ => KeyResult.Fail(KeyAccess.Unreachable, KeyErrorClasses.InvalidResponse),
    };

    private HttpClient Client()
    {
        var client = httpClients.CreateClient(SafeHttp.ClientName);
        client.Timeout = TimeSpan.FromSeconds(10);
        return client;
    }

    // Azure Key Vault ------------------------------------------------------------------------------------------------

    private static string AzureScope(KeyReference key) =>
        new Uri(key.KeyUri).Host.EndsWith(".managedhsm.azure.net", StringComparison.Ordinal) ? "https://managedhsm.azure.net/.default" : "https://vault.azure.net/.default";

    /// <summary>An Entra access token for the app, kept until a minute before it expires.</summary>
    private async Task<(string? Token, KeyResult? Failure)> AzureTokenAsync(KeyReference key, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var cacheKey = $"kms.token:{tenant}:{key.DirectoryId}:{key.ClientId}:{AzureScope(key)}:{key.SecretRef}";
        if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return (cached, null);
        }
        var secret = await secrets.GetTenantSecretAsync(tenant, key.SecretRef ?? "", ct);
        if (secret is null)
        {
            return (null, KeyResult.Fail(KeyAccess.Denied, KeyErrorClasses.CredentialMissing));
        }
        using var client = Client();
        using var response = await client.PostAsync($"https://{EntraHost}/{key.DirectoryId}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = key.ClientId ?? "",
            ["client_secret"] = secret,
            ["scope"] = AzureScope(key),
        }), ct);
        // Entra answers 400 or 401 for a secret or app that was removed: the customer took the access away.
        if ((int)response.StatusCode is 400 or 401 or 403)
        {
            return (null, KeyResult.Fail(KeyAccess.Denied, KeyErrorClasses.CredentialRefused));
        }
        if (Refused(response) is { } failed)
        {
            return (null, failed);
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var token = body.GetProperty("access_token").GetString()!;
        var seconds = body.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 300;
        cache.Set(cacheKey, token, TimeSpan.FromSeconds(Math.Max(30, seconds - 60)));
        return (token, null);
    }

    private async Task<HttpResponseMessage> AzureSendAsync(KeyReference key, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var (token, _) = await AzureTokenAsync(key, ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }
        using var client = Client();
        return await client.SendAsync(request, ct);
    }

    private async Task<KeyResult> AzureCurrentAsync(KeyReference key, CancellationToken ct)
    {
        var (_, failure) = await AzureTokenAsync(key, ct);
        if (failure is not null)
        {
            return failure;
        }
        using var response = (await AzureSendAsync(key, HttpMethod.Get, $"{key.KeyUri}?api-version={AzureApiVersion}", null, ct));
        if (Refused(response) is { } refused)
        {
            return refused;
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        if (body.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
        {
            return KeyResult.Fail(KeyAccess.Denied, KeyErrorClasses.KeyDisabled);
        }
        var kid = body.GetProperty("key").GetProperty("kid").GetString()!;
        return new KeyResult(KeyAccess.Ok, kid[(kid.LastIndexOf('/') + 1)..]);
    }

    private async Task<KeyResult> AzureCryptAsync(KeyReference key, string version, string operation, byte[] value, CancellationToken ct)
    {
        var (_, failure) = await AzureTokenAsync(key, ct);
        if (failure is not null)
        {
            return failure;
        }
        using var response = (await AzureSendAsync(key, HttpMethod.Post, $"{key.KeyUri}/{Uri.EscapeDataString(version)}/{operation}?api-version={AzureApiVersion}",
            new { alg = AzureAlgorithm, value = Base64Url(value) }, ct));
        if (Refused(response) is { } refused)
        {
            return refused;
        }
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        return new KeyResult(KeyAccess.Ok, version, FromBase64Url(body.GetProperty("value").GetString()!));
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    // HashiCorp Vault transit ----------------------------------------------------------------------------------------

    private static (string Base, string Mount, string Name) VaultParts(KeyReference key)
    {
        var uri = new Uri(key.KeyUri);
        var m = VaultKeyPath().Match(uri.AbsolutePath);
        return (uri.GetLeftPart(UriPartial.Authority), m.Groups["mount"].Value, m.Groups["name"].Value);
    }

    private async Task<(HttpResponseMessage? Response, KeyResult? Failure)> VaultSendAsync(KeyReference key, HttpMethod method, string operation, object? body, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var (baseUri, mount, name) = VaultParts(key);
        if (!await allowlist.IsAllowedAsync(new Uri(baseUri), ct))
        {
            return (null, KeyResult.Fail(KeyAccess.Unreachable, KeyErrorClasses.HostNotAllowed));
        }
        var token = await secrets.GetTenantSecretAsync(tenant, key.SecretRef ?? "", ct);
        if (token is null)
        {
            return (null, KeyResult.Fail(KeyAccess.Denied, KeyErrorClasses.CredentialMissing));
        }
        using var request = new HttpRequestMessage(method, $"{baseUri}/v1/{mount}/{operation}/{name}");
        request.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }
        using var client = Client();
        var response = await client.SendAsync(request, ct);
        if (Refused(response) is { } refused)
        {
            response.Dispose();
            return (null, refused);
        }
        return (response, null);
    }

    private async Task<KeyResult> VaultCurrentAsync(KeyReference key, CancellationToken ct)
    {
        var (response, failure) = await VaultSendAsync(key, HttpMethod.Get, "keys", null, ct);
        if (failure is not null)
        {
            return failure;
        }
        using (response)
        {
            var body = await response!.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var data = body.GetProperty("data");
            return new KeyResult(KeyAccess.Ok, data.GetProperty("latest_version").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private async Task<KeyResult> VaultEncryptAsync(KeyReference key, string version, byte[] plaintext, CancellationToken ct)
    {
        var (response, failure) = await VaultSendAsync(key, HttpMethod.Post, "encrypt",
            new { plaintext = Convert.ToBase64String(plaintext), key_version = int.Parse(version, System.Globalization.CultureInfo.InvariantCulture) }, ct);
        if (failure is not null)
        {
            return failure;
        }
        using (response)
        {
            var body = await response!.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var ciphertext = body.GetProperty("data").GetProperty("ciphertext").GetString()!;
            return new KeyResult(KeyAccess.Ok, version, System.Text.Encoding.ASCII.GetBytes(ciphertext));
        }
    }

    private async Task<KeyResult> VaultDecryptAsync(KeyReference key, byte[] wrapped, CancellationToken ct)
    {
        var ciphertext = System.Text.Encoding.ASCII.GetString(wrapped);
        var (response, failure) = await VaultSendAsync(key, HttpMethod.Post, "decrypt", new { ciphertext }, ct);
        if (failure is not null)
        {
            return failure;
        }
        using (response)
        {
            var body = await response!.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var version = ciphertext.Split(':') is [_, var v, ..] && v.StartsWith('v') ? v[1..] : null;
            return new KeyResult(KeyAccess.Ok, version, Convert.FromBase64String(body.GetProperty("data").GetProperty("plaintext").GetString()!));
        }
    }
}
