using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace TeamBeats.Modules.Identity.Sso;

/// <summary>Port to an OpenID Connect provider (ADR-0013: use cases reach external systems only through ports).</summary>
public interface IOidcProvider
{
    /// <summary>Discovery document with signing keys. Throws <see cref="OidcProviderUnavailableException"/> when unreachable.</summary>
    Task<OpenIdConnectConfiguration> GetConfigurationAsync(string issuer, CancellationToken ct);

    /// <summary>Redeems an authorization code (PKCE) and returns the raw ID token.</summary>
    Task<string> RedeemCodeAsync(OpenIdConnectConfiguration configuration, CodeRedemption redemption, CancellationToken ct);
}

public sealed record CodeRedemption(string ClientId, string ClientSecret, string Code, string RedirectUri, string CodeVerifier);

public sealed class OidcProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// HTTP adapter. Discovery and keys are cached per issuer by the IdentityModel configuration manager, which refreshes
/// keys on its own schedule. The HTTP client is the named client <see cref="ClientName"/>, so tests can replace the
/// network with a fake identity provider.
/// </summary>
public sealed class HttpOidcProvider(IHttpClientFactory httpClients) : IOidcProvider
{
    public const string ClientName = "oidc";

    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> Managers = new(StringComparer.Ordinal);

    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string issuer, CancellationToken ct)
    {
        var manager = Managers.GetOrAdd(issuer, i => new ConfigurationManager<OpenIdConnectConfiguration>(
            i.TrimEnd('/') + "/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(httpClients.CreateClient(ClientName)) { RequireHttps = true }));
        try
        {
            return await manager.GetConfigurationAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Managers.TryRemove(issuer, out _);
            throw new OidcProviderUnavailableException("Identity provider discovery failed.", ex);
        }
    }

    public async Task<string> RedeemCodeAsync(OpenIdConnectConfiguration configuration, CodeRedemption redemption, CancellationToken ct)
    {
        using var client = httpClients.CreateClient(ClientName);
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = redemption.Code,
            ["redirect_uri"] = redemption.RedirectUri,
            ["client_id"] = redemption.ClientId,
            ["client_secret"] = redemption.ClientSecret,
            ["code_verifier"] = redemption.CodeVerifier,
            ["scope"] = "openid profile email",
        });
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync(configuration.TokenEndpoint, body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OidcProviderUnavailableException("Token endpoint unreachable.", ex);
        }
        using (response)
        {
            if ((int)response.StatusCode >= 500)
            {
                throw new OidcProviderUnavailableException($"Token endpoint returned {(int)response.StatusCode}.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new OidcTokenRejectedException("code_rejected");
            }
            var tokens = await response.Content.ReadFromJsonAsync<TokenResponse>(ct);
            return tokens?.IdToken is { Length: > 0 } idToken ? idToken : throw new OidcTokenRejectedException("no_id_token");
        }
    }

    private sealed record TokenResponse([property: JsonPropertyName("id_token")] string? IdToken);
}

/// <summary>The provider or the token was not acceptable. <see cref="Reason"/> is a short code for audit, never token content.</summary>
public sealed class OidcTokenRejectedException(string reason) : Exception(reason)
{
    public string Reason { get; } = reason;
}
