using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TeamBeats.Modules.Integrations.Microsoft365;

/// <summary>An application token for one customer directory, with the application permissions Microsoft granted.</summary>
public sealed record AppToken(string AccessToken, IReadOnlySet<string> Roles);

public sealed class GraphUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Consent is missing or was revoked: Microsoft refuses a token for this directory.</summary>
public sealed class GraphConsentMissingException() : Exception("Admin consent is missing for this directory.");

/// <summary>Port to Microsoft identity platform and Graph (ADR-0013). External DTOs stay inside the adapter.</summary>
public interface IMicrosoftGraph
{
    string ClientId { get; }

    /// <summary>Microsoft's admin consent page for a directory.</summary>
    string AdminConsentUrl(string externalTenantId, IEnumerable<string> scopes, string redirectUri, string state);

    Task<AppToken> AcquireAppTokenAsync(string externalTenantId, CancellationToken ct);

    Task<long> CountAsync(AppToken token, string collection, CancellationToken ct);
}

/// <summary>
/// HTTP adapter for the TeamBeats multi-tenant application. Its client ID and secret are platform configuration
/// (<c>Integrations:Microsoft365:ClientId</c> and <c>ClientSecret</c>), filled from the platform's secret store.
/// </summary>
public sealed class HttpMicrosoftGraph(IHttpClientFactory httpClients, IConfiguration configuration) : IMicrosoftGraph
{
    public const string ClientName = "msgraph";
    public const string LoginAuthority = "https://login.microsoftonline.com";
    public const string GraphBase = "https://graph.microsoft.com/v1.0";

    public string ClientId => configuration["Integrations:Microsoft365:ClientId"] ?? string.Empty;

    public string AdminConsentUrl(string externalTenantId, IEnumerable<string> scopes, string redirectUri, string state) =>
        $"{LoginAuthority}/{Uri.EscapeDataString(externalTenantId)}/v2.0/adminconsent?client_id={Uri.EscapeDataString(ClientId)}"
        + $"&scope={Uri.EscapeDataString(string.Join(' ', scopes))}&redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}";

    public async Task<AppToken> AcquireAppTokenAsync(string externalTenantId, CancellationToken ct)
    {
        var secret = configuration["Integrations:Microsoft365:ClientSecret"];
        if (string.IsNullOrEmpty(ClientId) || string.IsNullOrEmpty(secret))
        {
            throw new GraphUnavailableException("The Microsoft 365 application is not configured on this installation.");
        }
        using var client = httpClients.CreateClient(ClientName);
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = ClientId,
            ["client_secret"] = secret,
            ["scope"] = "https://graph.microsoft.com/.default",
        });
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{LoginAuthority}/{Uri.EscapeDataString(externalTenantId)}/oauth2/v2.0/token", body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GraphUnavailableException("Token endpoint unreachable.", ex);
        }
        using (response)
        {
            if ((int)response.StatusCode is 400 or 401 or 403)
            {
                throw new GraphConsentMissingException();
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new GraphUnavailableException($"Token endpoint returned {(int)response.StatusCode}.");
            }
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            var token = json.GetProperty("access_token").GetString()!;
            return new AppToken(token, RolesOf(token));
        }
    }

    public async Task<long> CountAsync(AppToken token, string collection, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{GraphBase}/{collection}/$count");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Add("ConsistencyLevel", "eventual");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new GraphUnavailableException("Graph unreachable.", ex);
        }
        using (response)
        {
            if ((int)response.StatusCode is 401 or 403)
            {
                throw new GraphConsentMissingException();
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new GraphUnavailableException($"Graph returned {(int)response.StatusCode}.");
            }
            return long.Parse(await response.Content.ReadAsStringAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Reads the <c>roles</c> claim of a token Microsoft just issued to us over TLS. Only used to compare granted
    /// permissions with the features' needs; the token is never trusted for anything else here.
    /// </summary>
    private static IReadOnlySet<string> RolesOf(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return new HashSet<string>();
        }
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return doc.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
            ? roles.EnumerateArray().Select(r => r.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }
}
