using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Authorization;
using TeamBeats.Modules.Tenancy.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 026 FR-009 and spec 004 section 6: what every member of the tenant may view is not open to a guest, who reaches
/// only what is shared into their space. Employees keep tenant-wide reading.
/// </summary>
public sealed class GuestVisibilityTests
{
    private static EffectiveGrants Member(string type) =>
        new(Guid.NewGuid(), true, [], new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), [],
            new Dictionary<string, string> { [ConditionAttributes.MembershipType] = type });

    private static AuthzTarget TenantWide() =>
        AuthzTarget.Resource(new ResourceDescriptor("doc_library", Guid.NewGuid(), null, ResourceVisibility.Tenant));

    [Fact]
    public void A_guest_does_not_view_what_is_open_to_every_member()
    {
        Assert.False(Evaluator.Evaluate(Member(MembershipTypes.Guest), Permissions.DocView, TenantWide(), []).Allowed);
    }

    [Fact]
    public void An_employee_views_what_is_open_to_every_member()
    {
        Assert.True(Evaluator.Evaluate(Member(MembershipTypes.Employee), Permissions.DocView, TenantWide(), []).Allowed);
    }

    [Fact]
    public void A_guest_still_views_what_an_acl_entry_gives_them()
    {
        var guest = Member(MembershipTypes.Guest);
        var target = TenantWide();
        var id = ((ResourceTarget)target).Descriptor.ResourceId;
        var acl = new[] { new AclEntry("doc_library", id, "user", guest.UserId, Permissions.DocView) };
        Assert.True(Evaluator.Evaluate(guest, Permissions.DocView, target, acl).Allowed);
    }
}
