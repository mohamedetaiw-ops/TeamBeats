using System.Text.RegularExpressions;

namespace TeamBeats.Primitives;

/// <summary>
/// Port to the secret manager (constitution: identity provider and connector secrets live in an approved secret
/// manager). Modules store only a reference and resolve it at the moment of use. Values are never logged, audited or
/// returned by an API.
/// </summary>
public interface ISecretStore
{
    /// <summary>Returns the secret for a reference that belongs to <paramref name="tenantId"/>, or null when absent.</summary>
    Task<string?> GetTenantSecretAsync(Guid tenantId, string reference, CancellationToken ct = default);
}

/// <summary>
/// Secret references are <c>secret://tenants/{tenantId}/{name}</c>. The tenant is part of the path so one tenant can
/// never point its configuration at another tenant's secret (constitution I).
/// </summary>
public static partial class SecretReferences
{
    public static string For(Guid tenantId, string name) => $"secret://tenants/{tenantId:D}/{name}";

    /// <summary>The secret name when the reference is well formed and belongs to the tenant; otherwise null.</summary>
    public static string? NameFor(Guid tenantId, string? reference)
    {
        if (reference is null)
        {
            return null;
        }
        var match = Pattern().Match(reference);
        if (!match.Success || !Guid.TryParse(match.Groups["tenant"].Value, out var owner) || owner != tenantId)
        {
            return null;
        }
        return match.Groups["name"].Value;
    }

    [GeneratedRegex("^secret://tenants/(?<tenant>[0-9a-f-]{36})/(?<name>[a-z0-9][a-z0-9_-]{0,62})$")]
    private static partial Regex Pattern();
}
