using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

public sealed record AppRequest(string? Name, string? Kind, string? Description, string? Publisher, IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? RedirectUris, bool? AllowGuests, int? Version);

public sealed record AppView(Guid Id, string ClientId, string Kind, string Name, string? Description, string? Publisher, IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris, bool AllowGuests, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, int Version);

public sealed record CredentialView(Guid Id, string Kind, string? Hint, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record AppDetail(AppView App, IReadOnlyList<CredentialView> Credentials, IReadOnlyList<string> AdminConsentScopes);

/// <param name="Kind"><c>secret</c> (the default) or <c>public_key</c> for private key JWT, the preferred kind (FR-005).</param>
/// <param name="PublicKey">For a public key: the public JWK, EC P-256 or RSA of at least 2048 bits.</param>
public sealed record CredentialRequest(int? ExpiresInMonths, string? Kind = null, System.Text.Json.JsonElement? PublicKey = null);

/// <summary>A new credential. A secret is shown in this answer only, and TeamBeats keeps a salted hash; a public key has no secret.</summary>
public sealed record IssuedCredential(CredentialView Credential, string? Secret);

public sealed record AdminConsentRequest(IReadOnlyList<string>? Scopes);

public sealed record ApiSettingsView(int AccessTokenMinutes, int RefreshTokenDays, IReadOnlyList<string> UserConsentScopes, bool ApiEnabled, int Version);

public sealed record ApiSettingsRequest(int? AccessTokenMinutes, int? RefreshTokenDays, IReadOnlyList<string>? UserConsentScopes, bool? ApiEnabled, int? Version);

/// <summary>One scope as a consent screen shows it: whether the person may consent alone and whether it is granted.</summary>
public sealed record ConsentScopeView(string Key, string DescriptionKey, bool UserConsent, bool Granted);

/// <summary>What the consent page shows before the person decides (FR-013). App names are text, never markup.</summary>
public sealed record ConsentPrompt(string ClientId, string Name, string? Publisher, string? Description, string Kind, IReadOnlyList<ConsentScopeView> Scopes,
    bool NeedsAdmin, bool GuestRefused);

public sealed record ConsentGrantRequest(string? ClientId, string? RedirectUri, IReadOnlyList<string>? Scopes);

/// <summary>A consent as API administrators see it (FR-029): a person's own, or the tenant's admin consent when <c>UserId</c> is null.</summary>
public sealed record ConsentAdminView(Guid ConsentId, Guid AppId, string AppName, Guid? UserId, IReadOnlyList<string> Scopes, Guid GrantedBy, DateTimeOffset GrantedAt,
    DateTimeOffset? LastUsedAt);

public sealed record ConnectedAppView(Guid ConsentId, Guid AppId, string Name, string? Publisher, IReadOnlyList<string> Scopes, DateTimeOffset GrantedAt,
    DateTimeOffset? LastUsedAt);

/// <summary>The rules of spec 035 section 6 and FR-004 to FR-006 that do not need the database.</summary>
public static class ApiRules
{
    public const int MaxRedirectUris = 10;
    public const int MaxActiveCredentials = 2;
    public const int DefaultCredentialMonths = 6;
    public const int MaxCredentialMonths = 12;
    public const int DefaultAccessTokenMinutes = 10;
    public const int MaxAccessTokenMinutes = 60;
    public const int DefaultRefreshTokenDays = 30;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(2);

    /// <summary>The resource indicator of the REST API on a tenant host (FR-006).</summary>
    public static string ApiResource(string origin) => origin + "/api/public";

    /// <summary>The resource indicator of the MCP server on a tenant host (FR-019).</summary>
    public static string McpResource(string origin) => origin + "/mcp";

    /// <summary>The catalogue default for user consent (FR-013): the read scopes.</summary>
    public static IReadOnlyList<string> DefaultUserConsentScopes { get; } = ScopeCatalogue.All.Where(s => s.UserConsent).Select(s => s.Key).ToList();

    public static ValidationErrors Check(AppRequest request, bool create)
    {
        var errors = new ValidationErrors();
        if (create || request.Name is not null)
        {
            var name = request.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 80)
            {
                errors.Add("name", "validation.api.name");
            }
        }
        if (create && (request.Kind is null || !AppKinds.All.Contains(request.Kind)))
        {
            errors.Add("kind", "validation.api.kind");
        }
        if (request.Description is { Length: > 500 })
        {
            errors.Add("description", "validation.api.description");
        }
        if (request.Publisher is { Length: > 80 })
        {
            errors.Add("publisher", "validation.api.publisher");
        }
        return errors;
    }

    /// <summary>Scopes an app of this kind may hold: application apps only application scopes, others only delegated ones.</summary>
    public static void CheckScopes(IReadOnlyList<string>? scopes, string kind, ValidationErrors errors)
    {
        if (scopes is null || scopes.Count == 0)
        {
            errors.Add("scopes", "validation.api.scopes_required");
            return;
        }
        foreach (var key in scopes)
        {
            var scope = ScopeCatalogue.Find(key);
            if (scope is null)
            {
                errors.Add("scopes", "validation.api.scope_unknown");
            }
            else if (kind == AppKinds.Application ? !scope.AllowsApplication : !scope.AllowsDelegated)
            {
                errors.Add("scopes", "validation.api.scope_not_for_kind");
            }
        }
    }

    /// <summary>
    /// Exact redirect URIs (FR-004): HTTPS, or HTTP on a loopback address for native apps and MCP clients. No fragment and
    /// no wildcard. Application apps have none, every other kind at least one.
    /// </summary>
    public static void CheckRedirectUris(IReadOnlyList<string>? uris, string kind, ValidationErrors errors)
    {
        var list = uris ?? [];
        if (kind == AppKinds.Application)
        {
            if (list.Count > 0)
            {
                errors.Add("redirectUris", "validation.api.redirect_not_for_kind");
            }
            return;
        }
        if (list.Count == 0 || list.Count > MaxRedirectUris)
        {
            errors.Add("redirectUris", "validation.api.redirect_count");
            return;
        }
        foreach (var value in list)
        {
            if (!ValidRedirect(value, kind))
            {
                errors.Add("redirectUris", "validation.api.redirect_invalid");
            }
        }
    }

    private static bool ValidRedirect(string value, string kind)
    {
        if (value.Length > 512 || value.Contains('*') || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }
        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return true;
        }
        // Native apps listen on a loopback port (RFC 8252 section 7.3); a web app never does.
        return uri.Scheme == Uri.UriSchemeHttp && kind != AppKinds.DelegatedWeb
            && (uri.Host == "localhost" || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
    }

    public static string NewClientId() => "tb_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));

    public static string NewSecret() => "tbs_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>A client secret as stored: a random salt and the SHA-256 of salt and secret.</summary>
    public static string HashSecret(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + "$" + Convert.ToBase64String(Digest(salt, secret));
    }

    public static bool VerifySecret(string secret, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 2)
        {
            return false;
        }
        try
        {
            return CryptographicOperations.FixedTimeEquals(Digest(Convert.FromBase64String(parts[0]), secret), Convert.FromBase64String(parts[1]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Digest(byte[] salt, string secret) => SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(secret)]);

    /// <summary>The stored form of a refresh token generation: only its hash is kept.</summary>
    public static string HashGeneration(string generation) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("rt:" + generation)));
}
