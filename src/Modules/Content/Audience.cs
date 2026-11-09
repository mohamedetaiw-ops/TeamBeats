using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Content;

/// <summary>What an audience is evaluated on for one person.</summary>
public sealed record AudienceFacts(
    IReadOnlySet<Guid> DirectUnits,
    IReadOnlySet<Guid> UnitsWithAncestors,
    string? Location,
    IReadOnlySet<Guid> Groups,
    IReadOnlySet<string> Roles,
    string? MembershipType);

public static class AudienceRules
{
    public const int MaxClauses = 20;
    public const int MaxValues = 100;

    public static bool Matches(ContentAudience audience, AudienceFacts f) =>
        audience.IsEveryone || audience.AnyOf!.Any(c => Matches(c, f));

    public static bool Matches(AudienceClause c, AudienceFacts f) =>
        (c.OrgUnits is not { Count: > 0 } || c.OrgUnits.Any(u => u.Subtree ? f.UnitsWithAncestors.Contains(u.Id) : f.DirectUnits.Contains(u.Id)))
        && (c.Locations is not { Count: > 0 } || (f.Location is { } l && c.Locations.Contains(l, StringComparer.OrdinalIgnoreCase)))
        && (c.Groups is not { Count: > 0 } || c.Groups.Any(f.Groups.Contains))
        && (c.Roles is not { Count: > 0 } || c.Roles.Any(f.Roles.Contains))
        && (c.MembershipTypes is not { Count: > 0 } || (f.MembershipType is { } t && c.MembershipTypes.Contains(t)));

    /// <summary>Shape checks: known membership types, sizes, no empty clause, no dimension R1 cannot evaluate yet.</summary>
    public static void Validate(ContentAudience? audience, string field, ValidationErrors errors)
    {
        if (audience?.AnyOf is not { } clauses)
        {
            return;
        }
        if (clauses.Count > MaxClauses)
        {
            errors.Add(field, "validation.content.audience_too_large");
            return;
        }
        foreach (var c in clauses)
        {
            if (c is null)
            {
                errors.Add(field, "validation.content.audience_empty_clause");
                return;
            }
            var count = (c.OrgUnits?.Count ?? 0) + (c.Locations?.Count ?? 0) + (c.Groups?.Count ?? 0) + (c.Roles?.Count ?? 0) + (c.MembershipTypes?.Count ?? 0);
            if (c.Attributes is { Count: > 0 } || c.Channels is { Count: > 0 })
            {
                errors.Add(field, "validation.content.audience_not_supported");
                return;
            }
            if (count == 0)
            {
                errors.Add(field, "validation.content.audience_empty_clause");
                return;
            }
            if (count > MaxValues || c.OrgUnits?.Any(u => u is null || u.Id == Guid.Empty) == true || c.Groups?.Any(g => g == Guid.Empty) == true
                || c.Locations?.Any(l => string.IsNullOrWhiteSpace(l) || l.Length > 100) == true
                || c.Roles?.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 100) == true
                || c.MembershipTypes?.Any(t => !Tenancy.Contracts.MembershipTypes.All.Contains(t)) == true)
            {
                errors.Add(field, "validation.content.audience_value");
                return;
            }
        }
    }
}

/// <summary>Loads the audience facts of a person through the other modules' contracts, cached per request.</summary>
public sealed class AudienceFactsLoader(
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IAuthorizationProvisioning roles,
    IProfiles profiles)
{
    private readonly Dictionary<Guid, AudienceFacts> cache = [];

    public async Task<AudienceFacts> ForAsync(Guid user, CancellationToken ct)
    {
        if (cache.TryGetValue(user, out var known))
        {
            return known;
        }
        var direct = new HashSet<Guid>();
        var all = new HashSet<Guid>();
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(user, ct))
        {
            direct.Add(m.OrgUnitId);
            all.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(m.OrgUnitId, ct));
        }
        var location = (await profiles.ViewAsync(user, [user], ct)).FirstOrDefault()?.Location;
        var roleKeys = (await roles.GetUserAssignmentsAsync(user, ct)).Select(a => a.RoleKey).ToHashSet(StringComparer.Ordinal);
        var facts = new AudienceFacts(direct, all, location, await roles.GroupIdsOfAsync(user, ct), roleKeys, (await memberships.GetAsync(user, ct))?.MembershipType);
        cache[user] = facts;
        return facts;
    }
}

/// <summary>The spec 009 audience model for other modules, and for critical announcements in this one.</summary>
public sealed class Audiences(AudienceFactsLoader facts, ITenantMemberships memberships, IOrganizationHierarchy hierarchy) : IAudiences
{
    public void Validate(ContentAudience? audience, string field, ValidationErrors errors) => AudienceRules.Validate(audience, field, errors);

    public async Task<bool> IncludesAsync(ContentAudience audience, Guid userId, CancellationToken ct = default) =>
        (await memberships.GetAsync(userId, ct))?.Status == MembershipStatus.Active
        && (audience.IsEveryone || AudienceRules.Matches(audience, await facts.ForAsync(userId, ct)));

    /// <summary>
    /// Active members the audience matches. When every clause names org units, only members of those units are
    /// evaluated; otherwise every active member is.
    /// </summary>
    public async Task<IReadOnlySet<Guid>> MembersAsync(ContentAudience audience, CancellationToken ct = default)
    {
        var active = (await memberships.ListAsync(ct)).Where(m => m.Status == MembershipStatus.Active).Select(m => m.UserId).ToHashSet();
        if (audience.IsEveryone)
        {
            return active;
        }
        IEnumerable<Guid> candidates = active;
        if (audience.AnyOf!.All(c => c.OrgUnits is { Count: > 0 }))
        {
            var units = audience.AnyOf!.SelectMany(c => c.OrgUnits!).ToList();
            var subtrees = await hierarchy.GetSubtreesAsync(units.Where(u => u.Subtree).Select(u => u.Id), ct);
            var all = subtrees.Concat(units.Where(u => !u.Subtree).Select(u => u.Id)).Distinct().ToList();
            var members = await hierarchy.GetMemberUserIdsAsync(all, ct);
            candidates = active.Where(members.Contains);
        }
        var result = new HashSet<Guid>();
        foreach (var user in candidates)
        {
            if (AudienceRules.Matches(audience, await facts.ForAsync(user, ct)))
            {
                result.Add(user);
            }
        }
        return result;
    }
}
