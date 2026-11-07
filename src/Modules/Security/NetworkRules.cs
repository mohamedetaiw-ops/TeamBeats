using System.Net;
using TeamBeats.Modules.Security.Contracts;

namespace TeamBeats.Modules.Security;

/// <summary>Where a request was refused by the network rules (spec 026 US1).</summary>
public enum NetworkArea
{
    Tenant,
    Admin,
}

/// <summary>
/// Network rule evaluation (spec 026 US1, FR-001): the blocklist first, then the tenant allowlist, then the admin area
/// allowlist for admin endpoints. An empty allowlist allows every address. An unknown address fails any allowlist, so
/// a request whose source cannot be read is never let through a rule that names addresses.
/// </summary>
public static class NetworkRules
{
    public const int MaxEntries = 200;

    public static bool TryParseRange(string? text, out IPNetwork range)
    {
        range = default;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }
        if (!value.Contains('/'))
        {
            if (!IPAddress.TryParse(value, out var single))
            {
                return false;
            }
            range = new IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }
        // A range with host bits set, such as 203.0.113.5/24, is ambiguous: refuse it rather than guess what was meant.
        return IPNetwork.TryParse(value, out range) && IPAddress.TryParse(value[..value.IndexOf('/')], out var start) && start.Equals(range.BaseAddress);
    }

    /// <summary>Canonical text of a range, such as 10.0.0.0/8; a single address keeps its /32 or /128.</summary>
    public static string Canonical(IPNetwork range) => range.ToString();

    public static IReadOnlyList<IPNetwork> Parse(IEnumerable<string> ranges) =>
        ranges.Select(r => TryParseRange(r, out var n) ? (IPNetwork?)n : null).Where(n => n is not null).Select(n => n!.Value).ToList();

    public static bool Contains(IEnumerable<IPNetwork> ranges, IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return ranges.Any(r => r.BaseAddress.AddressFamily == normalized.AddressFamily && r.Contains(normalized));
    }

    /// <summary>The area that refuses the address, or null when the request may continue.</summary>
    public static NetworkArea? Refusal(SecurityPolicySnapshot policy, IPAddress? address, bool adminArea)
    {
        var block = Parse(policy.IpBlock);
        var allow = Parse(policy.IpAllow);
        var admin = Parse(policy.AdminIpAllow);
        if (address is not null && Contains(block, address))
        {
            return NetworkArea.Tenant;
        }
        if (allow.Count > 0 && (address is null || !Contains(allow, address)))
        {
            return NetworkArea.Tenant;
        }
        if (adminArea && admin.Count > 0 && (address is null || !Contains(admin, address)))
        {
            return NetworkArea.Admin;
        }
        return null;
    }
}
