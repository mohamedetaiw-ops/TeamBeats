namespace TeamBeats.Modules.Documents.Contracts;

/// <summary>
/// Brand asset images that other modules place on pages (spec 011 US5): a page's image block names an asset by id, and
/// readers get it through the portal's own checks at <c>/api/v1/assets/{id}/image</c>.
/// </summary>
public interface IAssetImages
{
    /// <summary>Whether the asset is a current image the caller reads: active, with usage rights that have not ended.</summary>
    Task<bool> UsableAsync(Guid assetId, CancellationToken ct = default);
}
