using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Analytics;

/// <summary>Served AI requests in a week or use case; null figures are behind fewer people than the minimum group size.</summary>
public sealed record AiUsageGroup(string Group, long? Requests, long? Users);

/// <summary>Refused AI requests by reason (spec 013), such as a used up quota or budget.</summary>
public sealed record AiUsageDenied(string Reason, long Requests);

/// <summary>Assistant figures; rates are shares from 0 to 1, null when hidden or when nothing was asked.</summary>
public sealed record AiAssistantFigures(long? Questions, long? Users, double? AnswerRate, double? CitationRate, long? Ratings, double? HelpfulRate);

/// <summary>One agent in use with its quality scores (spec 034); usage is null when fewer people than the minimum used it.</summary>
public sealed record AiAgentFigures(Guid AgentId, IReadOnlyDictionary<string, string> Name, long? Runs, double? SuccessRate, double? HelpfulRate, string? Gate,
    IReadOnlyList<AgentQualityFigure> Quality);

/// <summary>
/// The AI usage dashboard (spec 021 section 2): requests, people, tokens and cost from spec 013, assistant answer,
/// citation and helpful rates from spec 014, and per agent in use the Agent Hub figures with the quality of spec 034.
/// Every figure is read from its owner, so it is the same number its owner's page shows (FR-016).
/// </summary>
public sealed record AiUsageView(
    DateOnly From,
    DateOnly To,
    int MinGroupSize,
    long Requests,
    long Users,
    long InputTokens,
    long OutputTokens,
    decimal Cost,
    string Currency,
    decimal Rate,
    IReadOnlyList<AiUsageGroup> Weeks,
    IReadOnlyList<AiUsageGroup> UseCases,
    IReadOnlyList<AiUsageDenied> Denied,
    AiAssistantFigures Assistant,
    IReadOnlyList<AiAgentFigures> Agents);

/// <summary>
/// AI usage for analytics viewers of the whole tenant, under <c>analytics.advanced</c> (section 9 lists AI usage among
/// the advanced dashboards). Weeks, use cases and agents used by fewer people than the minimum group size show no
/// figures; small use cases fold into Other with complementary suppression. No person is named.
/// </summary>
public sealed class AiUsageAnalytics(IEntitlementService entitlements, IAuthorizationService authz, ITenantContext context, AnalyticsPolicies policies,
    IAiUsageFigures usage, IAssistantUsageFigures assistant, IClock clock)
{
    public const int DefaultDays = 30;

    public async Task<AiUsageView> GetAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsAdvanced, ct);
        context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.AnalyticsView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (start > end || end.DayNumber - start.DayNumber > Workspace.MaxDays)
        {
            throw AppException.Validation("from", "validation.analytics.range");
        }
        var min = (await policies.GetAsync(ct)).MinGroupSize;
        var ai = await usage.SummaryAsync(start, end, ct);
        var people = await assistant.SummaryAsync(Start(start), Start(end.AddDays(1)), ct);

        var shown = people.Users >= min;
        var figures = new AiAssistantFigures(
            shown ? people.Questions : null, shown ? people.Users : null,
            shown ? Share(people.Answered, people.Questions) : null, shown ? Share(people.Cited, people.Answered) : null,
            shown ? people.Ratings : null, shown ? Share(people.Helpful, people.Ratings) : null);
        var agents = people.Agents.Select(a =>
        {
            var used = a.Users >= min;
            return new AiAgentFigures(a.AgentId, a.Name ?? new Dictionary<string, string>(), used ? a.Runs : null, used ? Share(a.Answered, a.Runs) : null,
                used ? Share(a.Helpful, a.Ratings) : null, a.Gate, a.Quality);
        }).ToList();
        var weeks = ai.Weeks.Select(w => w.Users >= min ? new AiUsageGroup(w.Key, w.Requests, w.Users) : new AiUsageGroup(w.Key, null, null)).ToList();
        return new AiUsageView(start, end, min, ai.Requests, ai.Users, ai.InputTokens, ai.OutputTokens, ai.Cost, ai.Currency, ai.Rate, weeks,
            Fold(ai.UseCases, min), [.. ai.Denied.Select(d => new AiUsageDenied(d.Key, d.Requests))], figures, agents);
    }

    private static DateTimeOffset Start(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    internal static double? Share(long part, long whole) => whole == 0 ? null : Math.Round((double)part / whole, 4);

    /// <summary>
    /// Use cases with fewer people than <paramref name="min"/> fold into Other. When Other would hold a single use case,
    /// the next smallest joins it so the small one cannot be worked out from the total. Other shows requests only, since
    /// people across use cases overlap.
    /// </summary>
    internal static IReadOnlyList<AiUsageGroup> Fold(IReadOnlyList<AiUsageCount> rows, int min)
    {
        var small = rows.Where(r => r.Users < min).ToList();
        var large = rows.Where(r => r.Users >= min).OrderBy(r => r.Requests).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
        if (small.Count == 1 && large.Count > 0)
        {
            small.Add(large[0]);
            large.RemoveAt(0);
        }
        var groups = large.OrderByDescending(r => r.Requests).ThenBy(r => r.Key, StringComparer.Ordinal)
            .Select(r => new AiUsageGroup(r.Key, r.Requests, r.Users)).ToList();
        if (small.Count > 1)
        {
            groups.Add(new AiUsageGroup(ItemAnalytics.Other, small.Sum(r => r.Requests), null));
        }
        else if (small.Count == 1)
        {
            groups.Add(new AiUsageGroup(ItemAnalytics.Other, null, null));
        }
        return groups;
    }
}
