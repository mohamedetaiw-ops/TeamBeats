using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.People;

/// <summary>
/// One viewer's standing for field visibility (spec 010 FR-003, section 7): their own units with ancestors, the people
/// they manage directly or indirectly, and where they hold <c>people.hr_view</c> and <c>people.manage</c>.
/// </summary>
public sealed class Viewer(Guid id, OrgCoverage manage, OrgCoverage hr, IReadOnlySet<Guid> reports, IReadOnlySet<Guid> unitsAndAncestors)
{
    public Guid Id { get; } = id;
    public OrgCoverage Manage { get; } = manage;
    public OrgCoverage Hr { get; } = hr;
    public IReadOnlySet<Guid> Reports { get; } = reports;

    /// <summary>
    /// "Same subtree" is the branch below the top of the hierarchy that holds the person's primary unit: an Acme Gulf
    /// person's subtree field is visible across Acme Gulf and hidden from Acme Africa (section 7).
    /// </summary>
    public bool Sees(ProfileRow person, FieldPolicy policy, Guid? branchRoot)
    {
        if (person.UserId == Id || Manage.Covers(person.PrimaryUnitId))
        {
            return true;
        }
        return policy.Visibility switch
        {
            FieldVisibility.Everyone => true,
            FieldVisibility.Subtree => branchRoot is { } root && unitsAndAncestors.Contains(root),
            FieldVisibility.ManagersAndHr => Reports.Contains(person.UserId) || Hr.Covers(person.PrimaryUnitId),
            FieldVisibility.Hr => Hr.Covers(person.PrimaryUnitId),
            _ => false,
        };
    }
}

/// <summary>Builds viewers and resolves branch roots, caching hierarchy answers for one request.</summary>
public sealed class VisibilityResolver(IAuthorizationService authz, IOrganizationHierarchy hierarchy, ReportingLine lines, ITenantContext context)
{
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _ancestors = [];
    private readonly Dictionary<Guid, Viewer> _viewers = [];

    public async Task<Viewer> ViewerAsync(Guid viewerId, CancellationToken ct)
    {
        if (_viewers.TryGetValue(viewerId, out var cached))
        {
            return cached;
        }
        var units = new HashSet<Guid>();
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(viewerId, ct))
        {
            units.UnionWith(await AncestorsAsync(m.OrgUnitId, ct));
        }
        // Permission coverage is known only for the signed-in actor; any other viewer is treated as holding none.
        var self = context.ActorId == viewerId;
        var viewer = new Viewer(viewerId,
            self ? await authz.CoverageAsync(Permissions.PeopleManage, ct) : OrgCoverage.None,
            self ? await authz.CoverageAsync(Permissions.PeopleHrView, ct) : OrgCoverage.None,
            await lines.AllReportsAsync(viewerId, ct), units);
        return _viewers[viewerId] = viewer;
    }

    private async Task<IReadOnlyList<Guid>> AncestorsAsync(Guid unit, CancellationToken ct)
    {
        if (!_ancestors.TryGetValue(unit, out var list))
        {
            _ancestors[unit] = list = await hierarchy.GetAncestorsAndSelfAsync(unit, ct);
        }
        return list;
    }

    /// <summary>The unit just below the top of the person's branch, or the top itself for people placed there.</summary>
    public async Task<Guid?> BranchRootAsync(Guid? unit, CancellationToken ct)
    {
        if (unit is not { } u)
        {
            return null;
        }
        var chain = await AncestorsAsync(u, ct); // nearest first
        return chain.Count switch
        {
            0 => null,
            1 => chain[0],
            _ => chain[^2],
        };
    }

    public async Task<IReadOnlyList<VisibleField>> VisibleFieldsAsync(Viewer viewer, ProfileRow person, IReadOnlyList<FieldPolicy> policies, CancellationToken ct)
    {
        Guid? root = null;
        var rootKnown = false;
        var fields = new List<VisibleField>();
        foreach (var policy in policies)
        {
            if (person.Value(policy) is not { } value)
            {
                continue;
            }
            if (policy.Visibility == FieldVisibility.Subtree && !rootKnown)
            {
                root = await BranchRootAsync(person.PrimaryUnitId, ct);
                rootKnown = true;
            }
            if (viewer.Sees(person, policy, root))
            {
                fields.Add(new VisibleField(policy.Key, value, policy.Source));
            }
        }
        return fields;
    }
}

/// <summary>Profiles for other modules and agent tools, always reduced to what the viewer may see.</summary>
public sealed class Profiles(ProfileStore store, VisibilityResolver visibility) : IProfiles
{
    public async Task<IReadOnlyList<VisibleProfile>> ViewAsync(Guid viewerId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        await store.EnsureFreshAsync(ct);
        var policies = await store.PoliciesAsync(ct);
        var viewer = await visibility.ViewerAsync(viewerId, ct);
        var result = new List<VisibleProfile>();
        foreach (var row in await store.GetManyAsync(userIds, ct))
        {
            var fields = row.Active ? await visibility.VisibleFieldsAsync(viewer, row, policies, ct) : [];
            result.Add(new VisibleProfile(row.UserId, row.DisplayName, row.Active, row.PrimaryUnitId, row.LocationKey, fields));
        }
        return result;
    }
}
