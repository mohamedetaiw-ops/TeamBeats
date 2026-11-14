using System.Text.Json;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// The AI summary of search results (spec 012 US6, spec 014 R2): the knowledge agent's retrieval as the person, its
/// untrusted envelope and its citation check, with a prompt that asks for a short answer to the search. When no
/// permitted passage answers, or the passages that would are kept from AI by their classification, nothing is sent to a
/// model and no summary is shown (US6 scenario 2). A summary is stored as an assistant turn in a conversation of kind
/// <c>search</c>, which the assistant history never lists, so it keeps its execution record and can be marked unhelpful
/// with the assistant's feedback (US6 scenario 3), which production evaluation (spec 034) reads.
/// </summary>
internal sealed class SearchSummaries(IDbSession session, ITenantContext context, ISearchRetrieval retrieval, IAiGateway gateway, IClock clock,
    AssistantGuests guests) : ISearchSummaries
{
    /// <summary>Few passages keep the summary short and quick.</summary>
    public const int Passages = 5;
    public const int MaxTokens = 300;

    public async Task<SearchSummary> SummarizeAsync(string query, CancellationToken ct = default)
    {
        var user = context.RequireUser();
        if (!await guests.AllowedAsync(ct))
        {
            return SearchSummary.Not("unavailable");
        }
        var evidence = await retrieval.RetrieveAsync(new RetrievalRequest(query, AiUseCases.Assistant, TopK: Passages), ct);
        if (evidence.Passages.Count == 0)
        {
            return SearchSummary.Not(evidence.Withheld > 0 ? "withheld" : "none");
        }
        var language = AssistantPrompt.Language(query);
        var classification = AssistantPrompt.Highest(evidence.Passages.Select(p => p.Classification), DataClassification.Public);
        var request = new AiRequest(AiUseCases.Assistant,
            [
                new(AiRoles.System, AssistantPrompt.SummarySystem(language)),
                new(AiRoles.User, AssistantPrompt.Passages(evidence.Passages)),
                new(AiRoles.User, query),
            ], classification, MaxTokens: MaxTokens, Temperature: 0.1);
        var result = await gateway.ChatAsync(request, ct);
        if (result.Completion is not { } completion)
        {
            return SearchSummary.Not(result.Denial == AiDenials.ClassificationBlocked ? "withheld" : "unavailable");
        }
        var content = completion.Content?.Trim() ?? string.Empty;
        if (content.Length == 0 || content.StartsWith(AssistantPrompt.NoAnswer, StringComparison.Ordinal))
        {
            return SearchSummary.Not("none");
        }
        var (text, cited) = AssistantPrompt.Check(content, evidence.Passages.Count);
        // A summary that cites nothing it was given is not shown (FR-018: summaries must cite their passages).
        if (cited.Count == 0 || string.IsNullOrWhiteSpace(text))
        {
            return SearchSummary.Not("none");
        }
        var citations = cited.Select(n =>
        {
            var p = evidence.Passages[n - 1];
            return new CitationView(n, p.Source, p.ItemId, p.Kind, p.Title, p.Anchor, p.Locale, p.Version, p.UpdatedAt);
        }).ToList();

        var conversation = Ids.New();
        var turn = Ids.New();
        var now = clock.UtcNow;
        var level = AssistantPrompt.Name(classification);
        await session.ExecAsync(
            """
            insert into agents.conversation (tenant_id, id, user_id, title, classification, turns, created_at, last_turn_at, kind)
            values (platform.current_tenant_id(), @conversation, @user, @title, @level, 1, @now, @now, 'search')
            """, new { conversation, user, title = AssistantPrompt.Title(query), level, now }, ct);
        await session.ExecAsync(
            """
            insert into agents.turn (tenant_id, id, conversation_id, user_id, position, question, answer, outcome, reason, language, agent, citations,
              withheld, retrieval_degraded, classification, model, provider, prompt_version, retrieval, input_tokens, output_tokens, created_at)
            values (platform.current_tenant_id(), @turn, @conversation, @user, 1, @query, @text, @outcome, null, @language, null,
              cast(@citations as jsonb), @withheld, @degraded, @level, @model, @provider, @prompt, cast(@retrieval as jsonb), @input, @output, @now)
            """,
            new
            {
                turn, conversation, user, query, text, outcome = AssistantOutcomes.Answered, language,
                citations = JsonSerializer.Serialize(citations, AssistantService.Json), withheld = evidence.Withheld, degraded = evidence.Degraded, level,
                model = completion.Model, provider = completion.Provider, prompt = AssistantPrompt.SummaryVersion,
                retrieval = JsonSerializer.Serialize(new { useCase = AiUseCases.Assistant, topK = Passages, passages = evidence.Passages.Count, surface = "search" },
                    AssistantService.Json),
                input = completion.Usage.InputTokens, output = completion.Usage.OutputTokens, now,
            }, ct);
        return new SearchSummary("answered", turn, text, citations.Select(c => new SearchSummaryCitation(c.N, c.Title, c.Anchor, c.Kind)).ToList());
    }
}
