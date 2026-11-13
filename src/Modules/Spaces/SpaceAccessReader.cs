using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Spaces;

/// <summary><see cref="ISpaceAccess"/> from the viewer's standing: a secret space they are not in stays unknown.</summary>
public sealed class SpaceAccessReader(SpacesService spaces) : ISpaceAccess
{
    public async Task<SpaceAccess?> ForViewerAsync(Guid spaceId, CancellationToken ct = default)
    {
        try
        {
            var standing = await spaces.VisibleAsync(spaceId, ct);
            return new SpaceAccess(spaceId, standing.Space.Name, standing.Reads, standing.Owns, standing.Space.Active);
        }
        catch (AppException e) when (e.Status is 403 or 404)
        {
            return null;
        }
    }
}
