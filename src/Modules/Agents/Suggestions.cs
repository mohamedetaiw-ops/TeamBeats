using System.Text.RegularExpressions;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// One suggested question for the page the person is on. <paramref name="Key"/> names the question (the page words it in
/// the person's language), <paramref name="Title"/> is the item's title and <paramref name="Scope"/> keeps the
/// conversation to that item when the person picks it.
/// </summary>
public sealed record AssistantSuggestion(string Key, string Title, AssistantScope Scope);

/// <summary>
/// Suggestions for a page. <paramref name="Available"/> is false when the package or the tenant does not offer them, or
/// the person may not use the assistant; <paramref name="On"/> is the person's own choice.
/// </summary>
public sealed record AssistantSuggestionsView(bool Available, bool On, IReadOnlyList<AssistantSuggestion> Suggestions);

public sealed record AssistantSuggestionPreference(bool? On);

/// <summary>
/// Proactive suggestions across the intranet (spec 014 R3, US9, FR-023). When the person opens the assistant on a page
/// that shows one item they may read (a news item or page, a document, a policy), the assistant offers up to
/// <see cref="Max"/> questions about it. Suggestions are built from rules, with no model call and nothing sent anywhere;
/// a picked suggestion is an ordinary first turn kept to the item, so retrieval, the classification ceiling, citations
/// and the request quota all apply as usual. They never come as notifications.
/// </summary>
public sealed partial class AssistantSuggestions(IDbSession session, ITenantContext context, IEntitlementService entitlements,
    ISearchRetrieval retrieval, AssistantGuests guests, IClock clock, IAnalyticsEvents analytics)
{
    public const int Max = 3;

    /// <summary>The page addresses that show one item, with the item's search source.</summary>
    [GeneratedRegex(@"^/(content/read|documents|policies)/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})/?$")]
    private static partial Regex ItemPage();

    private static readonly Dictionary<string, (string Source, string[] Keys)> Pages = new(StringComparer.Ordinal)
    {
        ["content/read"] = (AssistantScope.Content, ["summarize", "key_points"]),
        ["documents"] = (AssistantScope.Documents, ["summarize", "key_points"]),
        ["policies"] = (AssistantScope.Policies, ["explain_policy", "summarize"]),
    };

    /// <summary>The item a page address shows, or null for any other page.</summary>
    public static (string Source, Guid ItemId, string[] Keys)? Parse(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 300)
        {
            return null;
        }
        var match = ItemPage().Match(path);
        if (!match.Success)
        {
            return null;
        }
        var (source, keys) = Pages[match.Groups[1].Value];
        return (source, Guid.Parse(match.Groups[2].Value), keys);
    }

    private async Task<bool> AvailableAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.AiAssistant, ct)
        && await entitlements.IsEnabledAsync(Capabilities.AiAssistantSuggestions, ct)
        && await session.ScalarAsync<bool?>("select suggestions_enabled from agents.assistant_settings", null, ct) != false
        && await guests.AllowedAsync(ct);

    private async Task<bool> OnAsync(Guid user, CancellationToken ct) =>
        await session.ScalarAsync<bool?>("select suggestions_on from agents.assistant_preference where user_id = @user", new { user }, ct) != false;

    public async Task<AssistantSuggestionsView> ForPageAsync(string? path, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (!await AvailableAsync(ct))
        {
            return new AssistantSuggestionsView(false, false, []);
        }
        if (!await OnAsync(user, ct))
        {
            return new AssistantSuggestionsView(true, false, []);
        }
        if (Parse(path) is not { } page
            || page.Source == AssistantScope.Policies && !await entitlements.IsEnabledAsync(Capabilities.AiPolicyQa, ct))
        {
            return new AssistantSuggestionsView(true, true, []);
        }
        // Only an item the person may read now, with something the assistant may draw on; otherwise nothing is offered,
        // so a suggestion never shows that an item exists.
        var item = await retrieval.ItemAsync(page.Source, page.ItemId, AiUseCases.Assistant, ct: ct);
        if (item.Passages.Count == 0)
        {
            return new AssistantSuggestionsView(true, true, []);
        }
        await OutcomeAsync("shown", ct);
        var title = item.Passages[0].Title;
        var scope = new AssistantScope(page.Source, page.ItemId);
        return new AssistantSuggestionsView(true, true, [.. page.Keys.Take(Max).Select(k => new AssistantSuggestion(k, title, scope))]);
    }

    /// <summary>The person turns suggestions on or off for themselves.</summary>
    public async Task<AssistantSuggestionsView> SetAsync(AssistantSuggestionPreference request, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (request.On is not { } on)
        {
            throw AppException.Validation("on", "validation.assistant.suggestions_choice");
        }
        if (!await AvailableAsync(ct))
        {
            throw AppException.Validation("on", "validation.assistant.suggestions_unavailable");
        }
        await session.ExecAsync(
            """
            insert into agents.assistant_preference (tenant_id, user_id, suggestions_on, updated_at) values (platform.current_tenant_id(), @user, @on, @now)
            on conflict (tenant_id, user_id) do update set suggestions_on = excluded.suggestions_on, updated_at = excluded.updated_at
            """, new { user, on, now = clock.UtcNow }, ct);
        if (!on)
        {
            await OutcomeAsync("dismissed", ct);
        }
        return new AssistantSuggestionsView(true, on, []);
    }

    /// <summary>Spec 021 R3 FR-024: an outcome of the suggestions, for their usefulness report.</summary>
    private Task OutcomeAsync(string outcome, CancellationToken ct) =>
        analytics.EmitAsync(AnalyticsEvents.RecommendationOutcome, new Dictionary<string, object?> { ["surface"] = "assistant", ["outcome"] = outcome }, ct);
}
