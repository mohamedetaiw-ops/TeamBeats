using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.PublicApi;

/// <param name="IpRanges">Addresses the app may call from, as CIDR ranges (FR-027); empty means any address the tenant allows.</param>
/// <param name="CallsPerMinute">The app's own call limit, at most the platform default for its kind (FR-017); 0 returns to that default.</param>
public sealed record AppRequest(string? Name, string? Kind, string? Description, string? Publisher, IReadOnlyList<string>? Scopes,
    IReadOnlyList<string>? RedirectUris, bool? AllowGuests, int? Version, IReadOnlyList<string>? IpRanges = null, int? CallsPerMinute = null);

/// <param name="CallsPerMinute">The limit that applies: per app for an application app, per app and person for a delegated one.</param>
public sealed record AppView(Guid Id, string ClientId, string Kind, string Name, string? Description, string? Publisher, IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RedirectUris, bool AllowGuests, string Status, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, int Version,
    IReadOnlyList<string> IpRanges, int CallsPerMinute, bool CallsPerMinuteLowered);

public sealed record CredentialView(Guid Id, string Kind, string? Hint, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt);

public sealed record AppDetail(AppView App, IReadOnlyList<CredentialView> Credentials, IReadOnlyList<string> AdminConsentScopes);

/// <param name="Kind"><c>secret</c> (the default) or <c>public_key</c> for private key JWT, the preferred kind (FR-005).</param>
/// <param name="PublicKey">For a public key: the public JWK, EC P-256 or RSA of at least 2048 bits.</param>
public sealed record CredentialRequest(int? ExpiresInMonths, string? Kind = null, System.Text.Json.JsonElement? PublicKey = null);

/// <summary>A new credential. A secret is shown in this answer only, and TeamBeats keeps a salted hash; a public key has no secret.</summary>
public sealed record IssuedCredential(CredentialView Credential, string? Secret);

public sealed record AdminConsentRequest(IReadOnlyList<string>? Scopes);

/// <param name="TenantCallsPerMinute">The limit for all apps of the tenant together, null for the installation's limit.</param>
public sealed record ApiSettingsView(int AccessTokenMinutes, int RefreshTokenDays, IReadOnlyList<string> UserConsentScopes, bool ApiEnabled, int Version,
    int? TenantCallsPerMinute = null);

/// <param name="TenantCallsPerMinute">Lowers the limit for all apps together; 0 returns to the installation's limit.</param>
public sealed record ApiSettingsRequest(int? AccessTokenMinutes, int? RefreshTokenDays, IReadOnlyList<string>? UserConsentScopes, bool? ApiEnabled, int? Version,
    int? TenantCallsPerMinute = null);

/// <summary>A tenant's API usage over a period (US5 scenario 3): per app, from the call log, without bodies.</summary>
public sealed record ApiUsageView(int Days, long MonthCalls, long? MonthlyLimit, IReadOnlyList<AppUsageView> Apps);

public sealed record AppUsageView(Guid AppId, string Name, string Kind, long Calls, IReadOnlyDictionary<string, long> Errors, long Denied, long RateLimited,
    IReadOnlyList<OperationUsageView> TopOperations, DateTimeOffset? LastCallAt);

public sealed record OperationUsageView(string Operation, long Calls);

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

    // Call limits per minute (section 6): the platform defaults, which a tenant may only lower, and the platform caps.
    public const int ApplicationCallsPerMinute = 600;
    public const int ApplicationCallsCap = 3000;
    public const int DelegatedCallsPerMinute = 120;
    public const int DelegatedCallsCap = 600;
    public const int TenantCallsPerMinute = 6000;
    public const int TokenRequestsPerMinute = 60;
    public const int MaxIpRanges = 20;
    public const int CallLogDays = 90;

    /// <summary>The platform default of an app's own limit: per app for an application app, per app and person otherwise.</summary>
    public static int DefaultCallsPerMinute(string kind) => kind == AppKinds.Application ? ApplicationCallsPerMinute : DelegatedCallsPerMinute;

    /// <summary>A tenant may lower an app's limit, never raise it above the platform default (US5 scenario 4).</summary>
    public static void CheckCallsPerMinute(int? value, string kind, ValidationErrors errors)
    {
        if (value is { } v && v != 0 && (v < 1 || v > DefaultCallsPerMinute(kind)))
        {
            errors.Add("callsPerMinute", "validation.api.calls_per_minute");
        }
    }

    /// <summary>Address ranges in CIDR form (FR-027), at most 20; a single address is a range of one.</summary>
    public static void CheckIpRanges(IReadOnlyList<string>? ranges, ValidationErrors errors)
    {
        if (ranges is null)
        {
            return;
        }
        if (ranges.Count > MaxIpRanges || ranges.Any(r => ParseRange(r) is null))
        {
            errors.Add("ipRanges", "validation.api.ip_ranges");
        }
    }

    public static IPNetwork? ParseRange(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return null;
        }
        var text = value.Trim();
        if (!text.Contains('/') && IPAddress.TryParse(text, out var single))
        {
            return new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        }
        return IPNetwork.TryParse(text, out var network) ? network : null;
    }

    /// <summary>Whether the caller's address is in one of the app's ranges; an app without ranges is open to any address.</summary>
    public static bool AddressAllowed(IReadOnlyList<string> ranges, IPAddress? address)
    {
        if (ranges.Count == 0)
        {
            return true;
        }
        if (address is null)
        {
            return false;
        }
        var caller = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return ranges.Select(ParseRange).Any(n => n is { } network && network.Contains(caller));
    }

    /// <summary>The form kept: each range as network address and prefix, a single address as a /32 or /128 range.</summary>
    public static string[] NormalizeRanges(IReadOnlyList<string> ranges) =>
        ranges.Select(ParseRange).OfType<IPNetwork>().Select(n => n.ToString()).Distinct(StringComparer.Ordinal).ToArray();
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
