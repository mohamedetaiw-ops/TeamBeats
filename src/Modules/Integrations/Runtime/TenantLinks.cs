using Microsoft.Extensions.Options;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>
/// The https address of a path on the current tenant's own host, for messages sent to Slack or Teams. Only paths are
/// taken: an absolute, protocol-relative or backslashed link is refused, so a message can never point elsewhere.
/// </summary>
public sealed class TenantLinks(ITenantContext context, IOptions<HostResolutionOptions> hosts, ITenantRegistry tenants)
{
    public async Task<string> AbsoluteAsync(string path, CancellationToken ct)
    {
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
        {
            throw new ArgumentException("Links must be paths on the tenant host.", nameof(path));
        }
        var h = hosts.Value;
        if (h.Mode == InstallationMode.Dedicated)
        {
            return $"https://{h.BaseDomain}{path}";
        }
        var tenant = await tenants.FindByIdAsync(context.RequireTenant(), ct) ?? throw AppException.NotFound();
        return $"https://{tenant.Key}.{h.BaseDomain}{path}";
    }
}
