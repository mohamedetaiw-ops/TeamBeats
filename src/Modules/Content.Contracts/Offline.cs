namespace TeamBeats.Modules.Content.Contracts;

/// <summary>
/// One item a person may read with no connection (spec 030 US3, FR-009): its kind, version and text in one language,
/// the in-app link it opens from, and when it last changed. Sources hand out only items classified up to Internal
/// (FR-010) that the person may read now.
/// </summary>
public sealed record OfflineItem(string Kind, Guid Id, int Version, string Locale, string Title, string? Summary, BodyDocument Body, string Link,
    DateTimeOffset UpdatedAt);

public static class OfflineKinds
{
    public const string Announcement = "announcement";
    public const string Policy = "policy";
}

/// <summary>
/// A module's offline items for the signed-in person, newest first, at most <c>max</c>. A module whose capabilities are
/// off returns none. Experience composes the sources into the offline bundle.
/// </summary>
public interface IOfflineSource
{
    Task<IReadOnlyList<OfflineItem>> OfflineItemsAsync(int max, CancellationToken ct = default);
}

public static class OfflineClassifications
{
    /// <summary>Confidential and Restricted items are never kept on a device (FR-010).</summary>
    public static bool Allowed(string classification) => classification is "public" or "internal";
}
