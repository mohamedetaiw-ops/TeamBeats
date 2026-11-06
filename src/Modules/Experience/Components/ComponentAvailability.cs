using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Pages;

namespace TeamBeats.Modules.Experience.Components;

/// <summary>
/// Whether each component can be used: enabled by the tenant, its capabilities in the package, and (for editors and
/// personal layouts) the permissions it declares held at the page's scope. Viewers are not checked for the component's
/// permissions here: the component's own API authorizes every call (spec 032 FR-027).
/// </summary>
public sealed class ComponentAvailability(IAuthorizationService authz, IEntitlementService entitlements, ComponentRegistry registry,
    ComponentEnablement enablement)
{
    public async Task<Func<ComponentManifest, Availability>> ForScopeAsync(Guid? orgUnitId, CancellationToken ct, bool viewer = false)
    {
        var disabled = await enablement.DisabledAsync(ct);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
        var permissions = new Dictionary<string, bool>(StringComparer.Ordinal);
        var target = orgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;
        foreach (var m in registry.All)
        {
            foreach (var c in m.Requires.Capabilities.Where(c => !capabilities.ContainsKey(c)))
            {
                capabilities[c] = await entitlements.IsEnabledAsync(c, ct);
            }
            if (!viewer)
            {
                foreach (var p in m.Requires.Permissions.Where(p => !permissions.ContainsKey(p)))
                {
                    permissions[p] = (await authz.CheckAsync(p, target, ct)).Allowed;
                }
            }
        }
        return m =>
            disabled.Contains(m.Id) ? Availability.Disabled
            : m.Requires.Capabilities.Any(c => !capabilities[c]) ? Availability.NotEntitled
            : !viewer && m.Requires.Permissions.Any(p => !permissions[p]) ? Availability.NotPermitted
            : Availability.Available;
    }
}
