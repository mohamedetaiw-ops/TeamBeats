namespace TeamBeats.TenantContext;

public enum InstallationMode
{
    /// <summary>Many tenants, each on a subdomain of <see cref="HostResolutionOptions.BaseDomain"/>.</summary>
    Shared,

    /// <summary>One tenant on a single host (spec 001, decided 2026-11-04).</summary>
    Dedicated,
}

public sealed class HostResolutionOptions
{
    public const string Section = "Tenancy:Hosts";

    public InstallationMode Mode { get; set; } = InstallationMode.Shared;

    /// <summary>For shared installs, for example <c>teambeats.example</c>; tenants live at <c>{key}.teambeats.example</c>.</summary>
    public string BaseDomain { get; set; } = "teambeats.localhost";

    /// <summary>Subdomain reserved for the platform administration console.</summary>
    public string PlatformSubdomain { get; set; } = "platform";

    /// <summary>For dedicated installs, the only tenant key served.</summary>
    public string? DedicatedTenantKey { get; set; }

    /// <summary>For dedicated installs, the host that serves the platform console (optional).</summary>
    public string? DedicatedPlatformHost { get; set; }
}

public enum HostMatch
{
    None,
    Platform,
    TenantKey,
}

/// <summary>Pure host-to-scope mapping. Shared by API and sign-in discovery (spec 001 T016, spec 003).</summary>
public static class HostResolver
{
    public static (HostMatch Match, string? TenantKey) Resolve(string? host, HostResolutionOptions options)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return (HostMatch.None, null);
        }

        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        var colon = h.LastIndexOf(':');
        if (colon > 0 && !h.Contains(']'))
        {
            h = h[..colon];
        }

        if (options.Mode == InstallationMode.Dedicated)
        {
            if (options.DedicatedPlatformHost is { } ph && string.Equals(h, ph, StringComparison.OrdinalIgnoreCase))
            {
                return (HostMatch.Platform, null);
            }
            return options.DedicatedTenantKey is { } key ? (HostMatch.TenantKey, key) : (HostMatch.None, null);
        }

        var suffix = "." + options.BaseDomain.ToLowerInvariant();
        if (!h.EndsWith(suffix, StringComparison.Ordinal))
        {
            return (HostMatch.None, null);
        }

        var label = h[..^suffix.Length];
        if (label.Length == 0 || label.Contains('.'))
        {
            return (HostMatch.None, null);
        }

        return label == options.PlatformSubdomain ? (HostMatch.Platform, null) : (HostMatch.TenantKey, label);
    }
}
