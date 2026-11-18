using TeamBeats.Modules.Analytics.Contracts;

namespace TeamBeats.Modules.Content;

/// <summary>Campaigns for the analytics workspace (spec 021 US2): the same rights as the campaign dashboard of spec 009 US8.</summary>
internal sealed class ContentCampaignSource(ContentCampaigns campaigns) : IAnalyticsCampaignSource
{
    public Task<IReadOnlyList<AnalyticsCampaign>> ListAsync(CancellationToken ct = default) => campaigns.AnalyticsListAsync(ct);

    public Task<AnalyticsCampaignFunnel> FunnelAsync(Guid campaignId, CancellationToken ct = default) => campaigns.FunnelAsync(campaignId, ct);
}
