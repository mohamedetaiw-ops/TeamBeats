using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Tenancy;

/// <summary>Spec 034 FR-006: the tenant and org memberships of a synthetic test identity, made inside one transaction.</summary>
public sealed class TestIdentityMemberships(ITenantMemberships memberships, OrgHierarchyService hierarchy, OrgMembershipService orgMemberships) : ITestIdentityMemberships
{
    public async Task AddAsync(Guid userId, string membershipType, Guid orgUnitId, CancellationToken ct = default)
    {
        var unit = await hierarchy.LoadAsync(orgUnitId, ct) ?? throw AppException.NotFound();
        if (unit.Status != "active")
        {
            throw AppException.Validation("orgUnitId", "validation.org.unit_inactive");
        }
        await memberships.CreateAsync(userId, membershipType, ct);
        await orgMemberships.AddCoreAsync(orgUnitId, userId, "primary", null, null, ct);
    }
}
