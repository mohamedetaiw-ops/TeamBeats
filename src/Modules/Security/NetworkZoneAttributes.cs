using System.Net;
using TeamBeats.Authorization.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>
/// The <c>network_zone</c> request attribute for spec 004 conditions (spec 026 US1 scenario 5): every named zone of the
/// tenant policy that the caller's address falls in. An address in no zone leaves the attribute out, so a condition on
/// it fails closed. The address is the connection's after the trusted forwarded headers middleware, never client input.
/// </summary>
public sealed class NetworkZoneAttributes(ITenantContext context, PolicyStore store) : IRequestAttributeProvider
{
    public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken ct)
    {
        if (context.TenantId is not { } tenant || !IPAddress.TryParse(context.SourceIp, out var address))
        {
            return new Dictionary<string, string>();
        }
        var policy = await store.ForTenantAsync(tenant, ct);
        var zones = NetworkRules.Zones(policy, address);
        return zones.Count == 0
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { [ConditionAttributes.NetworkZone] = string.Join(",", zones) };
    }
}
