using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>
/// Counts of one group of a campaign's audience. The rate is the attested share of the audience; it is null when the
/// audience is under the minimum group size, and so are the counts.
/// </summary>
public sealed record ComplianceCountsView(int Audience, int? Attested, int? Overdue, double? Rate);

/// <summary>A unit or location row; <see cref="Group"/> is <c>other</c> for the folded small groups and <c>none</c> for people without one.</summary>
public sealed record ComplianceGroupView(string Group, IReadOnlyDictionary<string, string> Names, ComplianceCountsView Counts);

/// <summary>The attested share of today's audience at the end of a day, from launch to the deadline or today.</summary>
public sealed record ComplianceTrendPoint(DateOnly Day, double Rate);

public sealed record ComplianceCampaignView(Guid CampaignId, Guid PolicyId, int Version, IReadOnlyDictionary<string, string> Names, DateTimeOffset LaunchedAt,
    DateTimeOffset Deadline, int DaysLeft, ComplianceCountsView Total, IReadOnlyList<ComplianceGroupView> Units, IReadOnlyList<ComplianceGroupView> Locations,
    IReadOnlyList<ComplianceTrendPoint> Trend);

public sealed record ComplianceAnalyticsView(int MinGroupSize, ComplianceCountsView Total, IReadOnlyList<ComplianceCampaignView> Campaigns);

/// <summary>
/// Policy compliance analytics (spec 021 US5, T013): the attestation rate of each open campaign by unit and location,
/// overdue counts and the trend to the deadline, all with the minimum group size. Policies decides whose attestations
/// count; the drill-down to people is spec 025's compliance view, which keeps its own permissions.
/// </summary>
public sealed class ComplianceAnalytics(IAnalyticsComplianceSource source, IEntitlementService entitlements, ITenantContext context, AnalyticsPolicies policies,
    IOrganizationHierarchy organization, IClock clock)
{
    /// <summary>The most points of a trend; a longer campaign is sampled every few days, always ending on its last day.</summary>
    public const int MaxPoints = 60;

    public async Task<ComplianceAnalyticsView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
        var campaigns = await source.OpenCampaignsAsync(ct);
        var min = (await policies.GetAsync(ct)).MinGroupSize;
        var now = clock.UtcNow;
        var units = campaigns.SelectMany(c => c.Members).Select(m => m.OrgUnitId).OfType<Guid>().Distinct().ToList();
        var names = units.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(units, ct);
        var none = new Dictionary<string, string>();
        var views = campaigns.Select(c =>
        {
            var byUnit = Groups(c.Members, m => m.OrgUnitId?.ToString(), min)
                .Select(g => new ComplianceGroupView(g.Group, Guid.TryParse(g.Group, out var id) ? names.GetValueOrDefault(id) ?? none : none, g.Counts)).ToList();
            var byLocation = Groups(c.Members, m => string.IsNullOrEmpty(m.Location) ? null : m.Location, min)
                .Select(g => new ComplianceGroupView(g.Group, none, g.Counts)).ToList();
            var days = (int)Math.Ceiling((c.Deadline - now).TotalDays);
            return new ComplianceCampaignView(c.CampaignId, c.PolicyId, c.Version, c.Names, c.LaunchedAt, c.Deadline, Math.Max(0, days), Counts(c.Members, min),
                byUnit, byLocation, c.Members.Count < min ? [] : Trend(c, now));
        }).ToList();
        return new ComplianceAnalyticsView(min, Counts(campaigns.SelectMany(c => c.Members).ToList(), min), views);
    }

    internal static ComplianceCountsView Counts(IReadOnlyCollection<AnalyticsComplianceMember> members, int min)
    {
        if (members.Count < min)
        {
            return new ComplianceCountsView(members.Count, null, null, null);
        }
        var attested = members.Count(m => m.Status == "attested");
        return new ComplianceCountsView(members.Count, attested, members.Count(m => m.Status == "overdue"), Math.Round((double)attested / members.Count, 4));
    }

    /// <summary>
    /// Groups under the minimum fold into "other", and the smallest shown one with them while "other" is still too
    /// small, so no group can be worked out from the total; "other" is left out when it stays too small.
    /// </summary>
    internal static IReadOnlyList<(string Group, ComplianceCountsView Counts)> Groups(IReadOnlyList<AnalyticsComplianceMember> members,
        Func<AnalyticsComplianceMember, string?> key, int min)
    {
        var merged = ItemAnalytics.Merge(members.GroupBy(m => key(m) ?? ItemAnalytics.NoUnit, StringComparer.Ordinal), min);
        var other = merged.GetValueOrDefault(ItemAnalytics.Other);
        var rows = merged.Where(g => g.Key != ItemAnalytics.Other).OrderByDescending(g => g.Value.Count).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, Counts(g.Value, min))).ToList();
        if (other is { } o && o.Count >= min)
        {
            rows.Add((ItemAnalytics.Other, Counts(o, min)));
        }
        return rows;
    }

    /// <summary>The attested share of today's audience at the end of each day from launch to the deadline or today.</summary>
    internal static IReadOnlyList<ComplianceTrendPoint> Trend(AnalyticsComplianceCampaign campaign, DateTimeOffset now)
    {
        var first = DateOnly.FromDateTime(campaign.LaunchedAt.UtcDateTime);
        var last = DateOnly.FromDateTime((campaign.Deadline < now ? campaign.Deadline : now).UtcDateTime);
        if (last < first || campaign.Members.Count == 0)
        {
            return [];
        }
        var span = last.DayNumber - first.DayNumber;
        var step = Math.Max(1, (int)Math.Ceiling((span + 1) / (double)MaxPoints));
        var attested = campaign.Members.Where(m => m.Status == "attested" && m.AttestedAt is not null)
            .Select(m => DateOnly.FromDateTime(m.AttestedAt!.Value.UtcDateTime)).Order().ToList();
        var points = new List<ComplianceTrendPoint>();
        for (var offset = 0; ; offset += step)
        {
            var day = offset >= span ? last : first.AddDays(offset);
            var done = attested.Count(a => a <= day);
            points.Add(new ComplianceTrendPoint(day, Math.Round((double)done / campaign.Members.Count, 4)));
            if (day == last)
            {
                break;
            }
        }
        return points;
    }
}
