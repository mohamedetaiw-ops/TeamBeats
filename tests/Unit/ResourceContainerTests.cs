using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Authorization;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 029 FR-006: a role assigned on a space reaches what the space contains (its library and discussion) and
/// nothing else: not another space's parts, not a resource of the same ID and another type, and never tenant coverage.
/// </summary>
public sealed class ResourceContainerTests
{
    private static EffectiveGrants SpaceMember(Guid space) => new(Guid.NewGuid(), true,
        [new Grant(Guid.NewGuid(), "space_member", ScopeType.Resource, space, "space", new HashSet<string> { "doc.view", "doc.contribute" }, new HashSet<Guid>(), "direct")],
        new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), [], new Dictionary<string, string>());

    private static bool Allowed(EffectiveGrants g, string permission, ResourceDescriptor d) => Evaluator.Evaluate(g, permission, AuthzTarget.Resource(d), []).Allowed;

    [Fact]
    public void A_space_role_reaches_the_resources_the_space_contains_and_nothing_else()
    {
        var space = Guid.NewGuid();
        var g = SpaceMember(space);
        var library = new ResourceDescriptor("doc_library", Guid.NewGuid(), null, ResourceVisibility.Restricted, "internal", new ResourceRef("space", space));
        Assert.True(Allowed(g, "doc.contribute", library));
        Assert.False(Allowed(g, "doc.manage", library));

        Assert.False(Allowed(g, "doc.view", library with { Container = new ResourceRef("space", Guid.NewGuid()) }));
        Assert.False(Allowed(g, "doc.view", library with { Container = new ResourceRef("site", space) }));
        Assert.False(Allowed(g, "doc.view", library with { Container = null }));
        Assert.False(Evaluator.Coverage(g, "doc.view").WholeTenant);
        Assert.Empty(Evaluator.Coverage(g, "doc.view").Units);
    }
}
