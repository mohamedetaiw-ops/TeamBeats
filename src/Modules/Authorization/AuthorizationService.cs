using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Authorization;

/// <summary>The central policy service for the current user (constitution III).</summary>
public sealed class AuthorizationService(
    ITenantContext context,
    EffectiveGrantsLoader loader,
    IDbSession session,
    IEnumerable<IRequestAttributeProvider> requestAttributes,
    ILogger<AuthorizationService> logger) : IAuthorizationService
{
    private EffectiveGrants? _current;

    private async Task<EffectiveGrants> CurrentAsync(CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || context.ActorId is null || context.TenantId is null)
        {
            // Platform operators and anonymous callers hold no tenant permissions (spec 001 FR-010).
            return EffectiveGrants.None(Guid.Empty);
        }
        return _current ??= await WithRequestAsync(await loader.LoadAsync(context.ActorId.Value, ct), ct);
    }

    private async Task<EffectiveGrants> WithRequestAsync(EffectiveGrants grants, CancellationToken ct)
    {
        var request = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var provider in requestAttributes)
        {
            foreach (var (key, value) in await provider.GetAsync(ct))
            {
                if (ConditionAttributes.All.TryGetValue(key, out var attribute) && attribute.Source == AttributeSource.Request)
                {
                    request[key] = value;
                }
            }
        }
        return request.Count == 0 ? grants : grants with { RequestAttributes = request };
    }

    public Task<EffectiveGrants> GrantsForAsync(Guid userId, CancellationToken ct) => loader.LoadAsync(userId, ct);

    public async Task<Decision> CheckAsync(string permission, AuthzTarget target, CancellationToken ct = default) =>
        await EvaluateAsync(await CurrentAsync(ct), permission, target, ct);

    public async Task<Decision> EvaluateAsync(EffectiveGrants grants, string permission, AuthzTarget target, CancellationToken ct)
    {
        IReadOnlyList<AclEntry> acl = target is ResourceTarget rt
            ? await LoadAclAsync(rt.Descriptor.ResourceType, [rt.Descriptor.ResourceId], ct)
            : [];
        var decision = Evaluator.Evaluate(grants, permission, target, acl);
        if (!decision.Allowed)
        {
            // Denials are telemetry, not audit (spec 004 section 13).
            Activity.Current?.AddEvent(new ActivityEvent("authz.denied", tags: new ActivityTagsCollection { ["permission"] = permission }));
            logger.LogDebug("Denied {Permission} on {Target} for {UserId}", permission, target, grants.UserId);
        }
        return decision;
    }

    public async Task DemandAsync(string permission, AuthzTarget target, CancellationToken ct = default)
    {
        if (!(await CheckAsync(permission, target, ct)).Allowed)
        {
            throw AppException.Denied();
        }
    }

    public async Task DemandVisibleAsync(string permission, AuthzTarget target, CancellationToken ct = default)
    {
        if (!(await CheckAsync(permission, target, ct)).Allowed)
        {
            throw AppException.NotFound();
        }
    }

    public async Task<bool> HoldsAnywhereAsync(string permission, CancellationToken ct = default)
    {
        var g = await CurrentAsync(ct);
        return g.ActiveMember && Evaluator.ApplicableWithoutResource(g).Any(x => x.Permissions.Contains(permission));
    }

    public async Task<OrgCoverage> CoverageAsync(string permission, CancellationToken ct = default) =>
        Evaluator.Coverage(await CurrentAsync(ct), permission);

    public async Task<IReadOnlyList<ResourceDescriptor>> FilterAsync(string permission, IEnumerable<ResourceDescriptor> resources, CancellationToken ct = default)
    {
        var list = resources.ToList();
        var g = await CurrentAsync(ct);
        if (!g.ActiveMember || list.Count == 0)
        {
            return [];
        }

        var acl = new List<AclEntry>();
        foreach (var byType in list.GroupBy(r => r.ResourceType))
        {
            acl.AddRange(await LoadAclAsync(byType.Key, byType.Select(r => r.ResourceId).ToArray(), ct));
        }
        var aclByResource = acl.ToLookup(a => (a.ResourceType, a.ResourceId));
        return list.Where(r => Evaluator.Evaluate(g, permission, AuthzTarget.Resource(r), aclByResource[(r.ResourceType, r.ResourceId)].ToList()).Allowed).ToList();
    }

    public void DemandPlatformOperator()
    {
        if (context.ActorType != ActorType.PlatformOperator)
        {
            throw AppException.Denied();
        }
    }

    private async Task<IReadOnlyList<AclEntry>> LoadAclAsync(string resourceType, Guid[] ids, CancellationToken ct) =>
        await session.QueryAsync(
            "select resource_type, resource_id, subject_type, subject_id, permission_key, condition::text as condition from authz.resource_acl where resource_type = @t and resource_id = any(@ids)",
            new { t = resourceType, ids },
            r => new AclEntry(r.GetString(0), r.GetGuid(1), r.GetString(2), r.GetGuid(3), r.GetString(4), GrantCondition.FromStored(r.Get<string>("condition"))), ct);
}
