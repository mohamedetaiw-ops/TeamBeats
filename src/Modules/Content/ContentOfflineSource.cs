using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Modules.Content;

/// <summary>Content's offline items (spec 030 US3): the critical announcements targeted to the person.</summary>
internal sealed class ContentOfflineSource(CriticalAnnouncements critical) : IOfflineSource
{
    public Task<IReadOnlyList<OfflineItem>> OfflineItemsAsync(int max, CancellationToken ct = default) => critical.OfflineAsync(max, ct);
}
