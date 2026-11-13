namespace TeamBeats.Modules.Spaces.Contracts;

/// <summary>What the current user may do in a space: read its content, own it. Active is false once it is archived.</summary>
public sealed record SpaceAccess(Guid SpaceId, string Name, bool Reads, bool Owns, bool Active);

/// <summary>
/// The current user's access to a space, for modules that share their own records into a space (spec 026 FR-024: a
/// compliance project shared read-only to an auditor guest space). The space's readers, guests included, are the audience.
/// </summary>
public interface ISpaceAccess
{
    /// <summary>The viewer's access, or null when the space is unknown to them or spaces are not in the plan.</summary>
    Task<SpaceAccess?> ForViewerAsync(Guid spaceId, CancellationToken ct = default);
}
