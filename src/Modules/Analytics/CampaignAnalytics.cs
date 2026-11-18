using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// What people did with a campaign or a step. Opened, clicked and completed are null when fewer people than the minimum
/// group size were reached; the shares are of the people reached.
/// </summary>
public sealed record CampaignCountsView(int People, int Delivered, int? Viewed, int? Clicked, int? Completed, double? ViewedShare, double? CompletedShare);

public sealed record CampaignStepAnalytics(Guid StepId, int Position, Guid ItemId, IReadOnlyDictionary<string, string> Titles, string Status, DateTimeOffset At,
    DateTimeOffset? SentAt, bool AcknowledgementRequired, CampaignCountsView Counts, IReadOnlyDictionary<string, int> Channels);

/// <summary>A unit or location row; <see cref="Group"/> is <c>other</c> for the merged small groups.</summary>
public sealed record CampaignGroupView(string Dimension, string Group, IReadOnlyDictionary<string, string> Names, int Audience, int Viewed, int Completed);

/// <summary>Return on effort from the tenant's estimates (US2 scenario 2). Per-person figures are null when there is nothing to divide by or it is suppressed.</summary>
public sealed record CampaignEffort(string Currency, decimal AuthoringCost, decimal SendCost, decimal TotalCost, decimal? PerReached, decimal? PerCompleted, int EstimatesVersion);

public sealed record CampaignAnalyticsView(AnalyticsCampaign Campaign, int MinGroupSize, CampaignCountsView Total, IReadOnlyDictionary<string, int> Channels,
    IReadOnlyList<CampaignStepAnalytics> Steps, IReadOnlyList<CampaignGroupView> Groups, CampaignEffort? Effort);

public sealed record CampaignComparison(CampaignAnalyticsView First, CampaignAnalyticsView Second);

/// <summary>
/// Campaign analytics (spec 021 US2, FR-009, T011): the funnel per step and in total (delivered, opened, clicked,
/// completed), deliveries by channel, the breakdown by unit and location with the minimum group size, return on effort
/// from the tenant's estimates, and two campaigns side by side. The owner (Content) decides who may see a campaign;
/// deliveries come from Notifications; Analytics reads neither module's tables.
/// </summary>
public sealed class CampaignAnalytics(IAnalyticsCampaignSource source, INotificationReach reach, IEntitlementService entitlements, ITenantContext context,
    AnalyticsPolicies policies, AnalyticsEstimates estimates, IOrganizationHierarchy organization)
{
    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
    }

    public async Task<IReadOnlyList<AnalyticsCampaign>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        return await source.ListAsync(ct);
    }

    public async Task<CampaignAnalyticsView> GetAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var funnel = await source.FunnelAsync(id, ct);
        var min = (await policies.GetAsync(ct)).MinGroupSize;
        var delivered = await reach.DeliveredByChannelAsync(funnel.Steps.Select(s => s.Link).ToList(), ct);
        var empty = new Dictionary<string, int>();
        var steps = funnel.Steps.Select(s => new CampaignStepAnalytics(s.StepId, s.Position, s.ItemId, s.Titles, s.Status, s.At, s.SentAt, s.AcknowledgementRequired,
            Counts(s.Audience, s.Delivered, s.Viewed, s.Clicked, s.Completed, min), delivered.GetValueOrDefault(s.Link) ?? empty)).ToList();
        var channels = steps.SelectMany(s => s.Channels).GroupBy(c => c.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Sum(c => c.Value), StringComparer.Ordinal);
        var total = Counts(funnel.People, funnel.Steps.Sum(s => s.Delivered), funnel.Viewed, funnel.Clicked, funnel.Completed, min);
        var groups = await GroupsAsync(funnel.Groups, min, ct);
        var effort = await EffortAsync(funnel, channels, total, ct);
        return new CampaignAnalyticsView(funnel.Campaign, min, total, channels, steps, groups, effort);
    }

    public async Task<CampaignComparison> CompareAsync(Guid first, Guid second, CancellationToken ct)
    {
        if (first == second)
        {
            throw AppException.Validation("second", "validation.analytics.compare");
        }
        return new CampaignComparison(await GetAsync(first, ct), await GetAsync(second, ct));
    }

    internal static CampaignCountsView Counts(int people, int delivered, int viewed, int clicked, int completed, int min) =>
        people < min
            ? new CampaignCountsView(people, delivered, null, null, null, null, null)
            : new CampaignCountsView(people, delivered, viewed, clicked, completed, Share(viewed, people), Share(completed, people));

    private static double Share(int part, int whole) => whole == 0 ? 0 : Math.Round((double)part / whole, 4);

    private async Task<CampaignEffort?> EffortAsync(AnalyticsCampaignFunnel funnel, IReadOnlyDictionary<string, int> channels, CampaignCountsView total, CancellationToken ct)
    {
        var (e, version, _) = await estimates.CurrentAsync(ct);
        if (e is null)
        {
            return null;
        }
        // Authoring is counted once per item the campaign sent, however many steps reuse it.
        var items = funnel.Steps.Where(s => s.SentAt is not null).Select(s => s.ItemId).Distinct().Count();
        var authoring = items * e.AuthoringHoursPerItem * e.CostPerHour;
        var sends = channels.Sum(c => c.Value * e.CostPerSend.GetValueOrDefault(c.Key));
        var cost = authoring + sends;
        decimal? Per(int? n) => n is > 0 ? Math.Round(cost / n.Value, 2) : null;
        return new CampaignEffort(e.Currency, Math.Round(authoring, 2), Math.Round(sends, 2), Math.Round(cost, 2), Per(total.Viewed), Per(total.Completed), version);
    }

    /// <summary>
    /// Folds units and locations under the minimum into "other", and keeps folding the smallest shown group while "other"
    /// is still under it, so no group can be worked out from the others; "other" is left out when it stays too small.
    /// </summary>
    internal static IReadOnlyList<(string Dimension, string Group, int Audience, int Viewed, int Completed)> Merge(IEnumerable<AnalyticsCampaignGroup> groups, int min)
    {
        var result = new List<(string, string, int, int, int)>();
        foreach (var dimension in groups.GroupBy(g => g.Dimension, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var shown = dimension.Where(g => g.Audience >= min).ToList();
            var small = dimension.Where(g => g.Audience < min).ToList();
            var other = (Audience: small.Sum(g => g.Audience), Viewed: small.Sum(g => g.Viewed), Completed: small.Sum(g => g.Completed));
            while (other.Audience > 0 && other.Audience < min && shown.Count > 0)
            {
                var smallest = shown.OrderBy(g => g.Audience).ThenBy(g => g.Key, StringComparer.Ordinal).First();
                shown.Remove(smallest);
                other = (other.Audience + smallest.Audience, other.Viewed + smallest.Viewed, other.Completed + smallest.Completed);
            }
            result.AddRange(shown.OrderByDescending(g => g.Audience).ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => (dimension.Key, g.Key, g.Audience, g.Viewed, g.Completed)));
            if (other.Audience >= min)
            {
                result.Add((dimension.Key, ItemAnalytics.Other, other.Audience, other.Viewed, other.Completed));
            }
        }
        return result;
    }

    private async Task<IReadOnlyList<CampaignGroupView>> GroupsAsync(IReadOnlyList<AnalyticsCampaignGroup> groups, int min, CancellationToken ct)
    {
        var merged = Merge(groups, min);
        var ids = merged.Where(g => g.Dimension == "unit" && Guid.TryParse(g.Group, out _)).Select(g => Guid.Parse(g.Group)).ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(ids, ct);
        var none = new Dictionary<string, string>();
        return merged.Select(g => new CampaignGroupView(g.Dimension, g.Group,
            g.Dimension == "unit" && Guid.TryParse(g.Group, out var id) ? names.GetValueOrDefault(id) ?? none : none, g.Audience, g.Viewed, g.Completed)).ToList();
    }
}
