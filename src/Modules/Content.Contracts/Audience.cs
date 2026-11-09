namespace TeamBeats.Modules.Content.Contracts;

/// <summary>An org unit target: the unit itself, or the unit and everything below it.</summary>
public sealed record UnitTarget(Guid Id, bool Subtree = true);

/// <summary>
/// One clause of an audience. Every criterion that is given must match (AND); inside a criterion any listed value
/// matches. Locations come from the people profile (spec 010), groups from the directory (spec 003), roles from role
/// assignments (spec 004). Profile attributes and channels are stored for later releases and refused until then.
/// </summary>
public sealed record AudienceClause(
    IReadOnlyList<UnitTarget>? OrgUnits = null,
    IReadOnlyList<string>? Locations = null,
    IReadOnlyList<Guid>? Groups = null,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? MembershipTypes = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Attributes = null,
    IReadOnlyList<string>? Channels = null);

/// <summary>
/// Who an item targets (FR-005): any clause matches (OR). No clauses means everyone in the tenant. Targeting decides
/// what appears in a feed, never who may read: access is checked apart from it (constitution II).
/// </summary>
public sealed record ContentAudience(IReadOnlyList<AudienceClause>? AnyOf = null)
{
    public static readonly ContentAudience Everyone = new();

    public bool IsEveryone => AnyOf is not { Count: > 0 };
}

/// <summary>
/// The audience model of spec 009 for other modules (spec 025 policies use it for who a policy applies to). Content
/// owns the model and how it is evaluated; callers keep the audience with their own records.
/// </summary>
public interface IAudiences
{
    /// <summary>Shape checks: sizes, no empty clause, known membership types, no dimension R1 cannot evaluate.</summary>
    void Validate(ContentAudience? audience, string field, TeamBeats.Primitives.ValidationErrors errors);

    /// <summary>Whether the person is an active member the audience matches.</summary>
    Task<bool> IncludesAsync(ContentAudience audience, Guid userId, CancellationToken ct = default);

    /// <summary>Every active member the audience matches.</summary>
    Task<IReadOnlySet<Guid>> MembersAsync(ContentAudience audience, CancellationToken ct = default);
}
