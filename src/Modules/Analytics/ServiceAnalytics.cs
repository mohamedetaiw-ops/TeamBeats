using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

public sealed record ServiceWeek(DateOnly Week, int Requests);

/// <summary>Cycle time from filing to closing, in seconds, over the requests closed in the period; null under the minimum group size.</summary>
public sealed record ServiceCycle(int Closed, double? MedianSeconds, double? P90Seconds);

/// <summary>
/// One step of a service's route over the period. <see cref="Slow"/> marks a step whose median wait is at least twice
/// the median of the steps' medians; times are null when fewer decisions than the minimum group size were made there.
/// </summary>
public sealed record ServiceStepStat(string Step, int Decisions, double? MedianSeconds, double? P90Seconds, int Automated, int Rejected, bool Slow);

/// <summary>A unit row of a structured reason; a count under the minimum is null, and so is the next smallest when only one would be hidden.</summary>
public sealed record ServiceReasonRow(string Group, IReadOnlyDictionary<string, string> Names, int Total, IReadOnlyDictionary<string, int?> Counts);

public sealed record ServiceReasonTable(string Field, IReadOnlyDictionary<string, string> Labels, IReadOnlyList<AnalyticsReasonOption> Options,
    IReadOnlyList<ServiceReasonRow> Rows, ServiceReasonRow All);

public sealed record ServiceAnalyticsView(AnalyticsService Service, DateOnly From, DateOnly To, int MinGroupSize, int Requests, IReadOnlyList<ServiceWeek> Weeks,
    ServiceCycle Cycle, int Breached, double? BreachedShare, int Rejected, double? RejectedShare, double? AutomationRate, IReadOnlyList<ServiceStepStat> Steps,
    IReadOnlyList<ServiceReasonTable> Reasons, string? HeatmapHref);

/// <summary>
/// Service analytics (spec 021 US4, FR-008, T012): requests per week, median and 90th percentile cycle time, time per
/// step with slow steps marked, service level breaches, rejections, the automation rate, and structured reasons by unit
/// with the minimum group size. Services decides who may see a service; Analytics reads none of its tables.
/// </summary>
public sealed class ServiceAnalytics(IAnalyticsServiceSource source, IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context,
    AnalyticsPolicies policies, IOrganizationHierarchy organization, IClock clock)
{
    public const int DefaultDays = 90;

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
    }

    public async Task<IReadOnlyList<AnalyticsService>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        return await source.ListAsync(ct);
    }

    public async Task<ServiceAnalyticsView> GetAsync(Guid id, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await DemandAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-DefaultDays);
        if (start > end || end.DayNumber - start.DayNumber > Workspace.MaxDays)
        {
            throw AppException.Validation("from", "validation.analytics.range");
        }
        var facts = await source.FactsAsync(id, new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), ct);
        var min = (await policies.GetAsync(ct)).MinGroupSize;
        var requests = facts.Requests;

        var weeks = requests.GroupBy(r => Adoption.WeekOf(DateOnly.FromDateTime(r.SubmittedAt.UtcDateTime))).OrderBy(g => g.Key)
            .Select(g => new ServiceWeek(g.Key, g.Count())).ToList();
        var closed = requests.Where(r => r.ClosedAt is not null).Select(r => (r.ClosedAt!.Value - r.SubmittedAt).TotalSeconds).ToList();
        var cycle = closed.Count < min ? new ServiceCycle(closed.Count, null, null) : new ServiceCycle(closed.Count, Percentile(closed, 0.5), Percentile(closed, 0.9));
        var breached = requests.Count(r => r.Breached);
        var rejected = requests.Count(r => r.Status == "rejected");
        var decisions = requests.SelectMany(r => r.Steps).ToList();
        var automation = decisions.Count < min ? (double?)null : Math.Round((double)decisions.Count(d => d.Automated) / decisions.Count, 4);
        var steps = Steps(decisions, min);
        var reasons = await ReasonsAsync(facts, min, ct);
        return new ServiceAnalyticsView(facts.Service, start, end, min, requests.Count, weeks, cycle, breached, Share(breached, requests.Count, min), rejected,
            Share(rejected, requests.Count, min), automation, steps, reasons, await HeatmapAsync(facts.Service, ct));
    }

    private static double? Share(int part, int whole, int min) => whole < min ? null : Math.Round((double)part / whole, 4);

    /// <summary>The nearest-rank percentile of a list of seconds, rounded to the second.</summary>
    internal static double Percentile(IReadOnlyList<double> values, double p)
    {
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p * sorted.Count);
        return Math.Round(sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)]);
    }

    internal static IReadOnlyList<ServiceStepStat> Steps(IReadOnlyList<AnalyticsStepFact> decisions, int min)
    {
        var groups = decisions.Where(d => d.Step.Length > 0).GroupBy(d => d.Step, StringComparer.Ordinal).ToList();
        var medians = groups.Where(g => g.Count() >= min).ToDictionary(g => g.Key, g => Percentile(g.Select(d => d.Seconds).ToList(), 0.5), StringComparer.Ordinal);
        var typical = medians.Count >= 2 ? Percentile(medians.Values.ToList(), 0.5) : (double?)null;
        return groups.Select(g =>
        {
            var times = g.Select(d => d.Seconds).ToList();
            var shown = times.Count >= min;
            var median = shown ? medians[g.Key] : (double?)null;
            return new ServiceStepStat(g.Key, times.Count, median, shown ? Percentile(times, 0.9) : null, g.Count(d => d.Automated), g.Count(d => d.Outcome == "rejected"),
                typical is { } t && median is { } m && t > 0 && m >= 2 * t);
        }).OrderByDescending(s => s.Decisions).ThenBy(s => s.Step, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Counts of one reason by unit: units with fewer requests than the minimum fold into "other" (and the smallest shown
    /// unit with them while "other" is still too small); inside a row, a count under the minimum is hidden, and when only
    /// one would be hidden the next smallest is hidden too, so it cannot be worked out from the total.
    /// </summary>
    internal static (List<(string Group, int Total, Dictionary<string, int?> Counts)> Rows, (int Total, Dictionary<string, int?> Counts) All) ReasonRows(
        IEnumerable<(string Unit, string Value)> answers, IReadOnlyList<string> options, int min)
    {
        var list = answers.ToList();
        var byUnit = list.GroupBy(a => a.Unit, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(a => a.Value).ToList(), StringComparer.Ordinal);
        var shown = byUnit.Where(u => u.Value.Count >= min).ToDictionary(u => u.Key, u => u.Value, StringComparer.Ordinal);
        var other = byUnit.Where(u => u.Value.Count < min).SelectMany(u => u.Value).ToList();
        while (other.Count > 0 && other.Count < min && shown.Count > 0)
        {
            var smallest = shown.OrderBy(u => u.Value.Count).ThenBy(u => u.Key, StringComparer.Ordinal).First();
            other.AddRange(smallest.Value);
            shown.Remove(smallest.Key);
        }
        Dictionary<string, int?> Cells(List<string> values)
        {
            var counts = options.ToDictionary(o => o, o => (int?)values.Count(v => v == o), StringComparer.Ordinal);
            var hidden = counts.Where(c => c.Value is > 0 && c.Value < min).Select(c => c.Key).ToList();
            if (hidden.Count == 1)
            {
                var next = counts.Where(c => c.Key != hidden[0] && c.Value is > 0).OrderBy(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).Select(c => c.Key).FirstOrDefault();
                if (next is not null)
                {
                    hidden.Add(next);
                }
            }
            foreach (var key in hidden)
            {
                counts[key] = null;
            }
            return counts;
        }
        var rows = shown.OrderByDescending(u => u.Value.Count).ThenBy(u => u.Key, StringComparer.Ordinal).Select(u => (u.Key, u.Value.Count, Cells(u.Value))).ToList();
        if (other.Count >= min)
        {
            rows.Add((ItemAnalytics.Other, other.Count, Cells(other)));
        }
        return (rows, (list.Count, Cells(list.Select(a => a.Value).ToList())));
    }

    private async Task<IReadOnlyList<ServiceReasonTable>> ReasonsAsync(AnalyticsServiceFacts facts, int min, CancellationToken ct)
    {
        var tables = new List<ServiceReasonTable>();
        var none = new Dictionary<string, string>();
        foreach (var field in facts.ReasonFields)
        {
            var answers = facts.Requests.Where(r => r.Reasons.ContainsKey(field.Key))
                .Select(r => ((r.OrgUnitId ?? AnalyticsAggregator.NoUnit).ToString(), r.Reasons[field.Key])).ToList();
            var options = field.Options.Select(o => o.Value).ToList();
            var (rows, all) = ReasonRows(answers, options, min);
            var ids = rows.Select(r => Guid.TryParse(r.Group, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
            var names = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyDictionary<string, string>>() : await organization.GetNamesAsync(ids, ct);
            var views = rows.Select(r =>
            {
                var group = r.Group == AnalyticsAggregator.NoUnit.ToString() ? ItemAnalytics.NoUnit : r.Group;
                var label = Guid.TryParse(r.Group, out var id) ? names.GetValueOrDefault(id) ?? none : none;
                return new ServiceReasonRow(group, label, r.Total, r.Counts);
            }).ToList();
            // With fewer answers than the minimum in all, nothing is shown.
            var allRow = all.Total < min
                ? new ServiceReasonRow("all", none, all.Total, options.ToDictionary(o => o, _ => (int?)null, StringComparer.Ordinal))
                : new ServiceReasonRow("all", none, all.Total, all.Counts);
            tables.Add(new ServiceReasonTable(field.Key, field.Labels, field.Options, all.Total < min ? [] : views, allRow));
        }
        return tables;
    }

    /// <summary>The spec 024 heatmap for operators and analysts of the service's route, when the package has process monitoring.</summary>
    private async Task<string?> HeatmapAsync(AnalyticsService service, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.WorkflowBpmn, ct))
        {
            return null;
        }
        var target = service.OwnerOrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
        var may = (await authz.CheckAsync(Permissions.ProcessAnalyze, target, ct)).Allowed || (await authz.CheckAsync(Permissions.ProcessOperate, target, ct)).Allowed;
        return may ? $"/admin/process-operations?tab=monitoring&process={Uri.EscapeDataString(service.ProcessKey)}" : null;
    }
}
