using System.Net;
using System.Net.Sockets;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>Thrown when an outbound connection would reach an address shared SaaS must never reach (spec 017 FR-012).</summary>
public sealed class BlockedAddressException(string host) : Exception($"Outbound connection to '{host}' was refused: it resolves to a private, loopback or link-local address.");

/// <summary>
/// The one HTTP handler every connector pack uses (spec 017 FR-012, section 14). The host is resolved once, every
/// address is checked, and the socket connects to the checked address, so a DNS answer that changes between the check
/// and the connect (rebinding) cannot reach an internal address. Redirects are not followed. On-premises installs may
/// allow private addresses with <see cref="AllowPrivateSetting"/>; shared SaaS never does.
/// </summary>
public static class SafeHttp
{
    public const string ClientName = "connectors";
    public const string AllowPrivateSetting = "Integrations:Network:AllowPrivateAddresses";

    public static SocketsHttpHandler CreateHandler(bool allowPrivate) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0)
            {
                throw new HttpRequestException($"No address for '{host}'.");
            }
            if (!allowPrivate && addresses.Any(a => !IsPublic(a)))
            {
                throw new BlockedAddressException(host);
            }
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// True for an address on the public internet. Refused: unspecified, loopback, private (RFC 1918), shared address
    /// space (100.64/10), link-local, benchmarking, documentation, multicast and reserved ranges, IPv6 unique local and
    /// link-local, and IPv4 addresses mapped into IPv6.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0
                || b[0] == 10
                || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                || (b[0] == 203 && b[1] == 0 && b[2] == 113)
                || b[0] >= 224);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback) || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
            {
                return false;
            }
            var b = address.GetAddressBytes();
            // 2001:db8::/32 documentation, 64:ff9b::/96 translation of IPv4 (could reach private IPv4), ::/96 compatible.
            if ((b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) || (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b)
                || b.Take(12).All(x => x == 0))
            {
                return false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="host"/> matches an allowlist entry: an exact host, or <c>*.example.com</c> for any
    /// subdomain of example.com (not example.com itself). Comparison ignores case and a trailing dot.
    /// </summary>
    public static bool HostAllowed(string host, IEnumerable<string> allowlist)
    {
        var h = host.TrimEnd('.').ToLowerInvariant();
        foreach (var entry in allowlist)
        {
            var e = entry.ToLowerInvariant();
            if (e.StartsWith("*.", StringComparison.Ordinal) ? h.EndsWith(e[1..], StringComparison.Ordinal) && h.Length > e.Length - 1 : h == e)
            {
                return true;
            }
        }
        return false;
    }
}
