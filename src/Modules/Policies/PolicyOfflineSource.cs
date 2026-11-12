using TeamBeats.Localization;
using TeamBeats.Modules.Content.Contracts;

namespace TeamBeats.Modules.Policies;

/// <summary>Policies' offline items (spec 030 US3): the policies that apply to the person.</summary>
internal sealed class PolicyOfflineSource(PolicyCampaigns campaigns, RequestLocale locale) : IOfflineSource
{
    public Task<IReadOnlyList<OfflineItem>> OfflineItemsAsync(int max, CancellationToken ct = default) => campaigns.OfflineAsync(max, locale.Locale, ct);
}
