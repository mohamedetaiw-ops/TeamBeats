using System.Runtime.CompilerServices;
using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

public sealed record TurnRequest(Guid? ConversationId, string? Question);

/// <summary>A cited passage: <paramref name="N"/> is the marker used in the answer, <paramref name="Anchor"/> opens the item at the passage.</summary>
public sealed record CitationView(int N, string Source, Guid ItemId, string Kind, string Title, string Anchor, string Locale, int? Version, DateTimeOffset? UpdatedAt);

/// <summary>
/// One turn as its user sees it. <paramref name="Generated"/> marks the answer as AI generated (FR-007);
/// <paramref name="Withheld"/> says some content the person could read was kept from the assistant by the AI policy.
/// </summary>
public sealed record TurnView(Guid Id, Guid ConversationId, string Question, string? Answer, string Outcome, string? Reason, string Language, string? Agent,
    IReadOnlyList<CitationView> Citations, bool Withheld, bool RetrievalDegraded, bool Generated, DateTimeOffset CreatedAt, bool? Helpful);

/// <summary>A piece of a streamed turn: text as it arrives, then the finished turn.</summary>
public sealed record TurnStreamEvent(string? Delta, TurnView? Done);

internal sealed record PreparedTurn(
    Guid TurnId,
    Guid ConversationId,
    bool NewConversation,
    int Position,
    string Question,
    string Language,
    RetrievalResult Retrieval,
    DataClassification Classification,
    AiRequest? Request,
    string? Outcome);

/// <summary>
/// The knowledge agent's turn pipeline (spec 014 T003, US1, FR-002 to FR-004, FR-010 to FR-012, FR-021). Retrieval runs
/// as the person first; with no passages nothing is sent to a model and the turn says why. Otherwise the passages go to
/// the AI Gateway in the untrusted envelope at the highest classification of the context, the answer's citations are
/// checked against the passages of the turn, and the turn is stored with the model and prompt version. The assistant
/// holds no tools (FR-005).
/// </summary>
public sealed class AssistantService(IDbSession session, ITenantContext context, IEntitlementService entitlements, ISearchRetrieval retrieval,
    IAiGateway gateway, IOutbox outbox, IClock clock)
{
    public const int MaxQuestionLength = 2000;
    public const int HistoryTurns = 3;
    public const int MaxAnswerTokens = 900;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal async Task<PreparedTurn> PrepareAsync(TurnRequest request, CancellationToken ct)
    {
        var question = (request.Question ?? string.Empty).Trim();
        if (question.Length == 0 || question.Length > MaxQuestionLength)
        {
            throw AppException.Validation("question", "validation.assistant.question");
        }
        await entitlements.DemandAsync(Capabilities.AiAssistant, ct);
        var user = context.RequireUser();

        var conversationId = request.ConversationId ?? Ids.New();
        var position = 1;
        var floor = DataClassification.Public;
        var history = new List<AiMessage>();
        if (request.ConversationId is { } existing)
        {
            var found = await session.QueryAsync(
                "select turns, classification from agents.conversation where id = @id and user_id = @user for update",
                new { id = existing, user }, r => (Turns: r.GetInt32(0), Classification: r.GetString(1)), ct);
            if (found.Count == 0)
            {
                throw AppException.NotFound();
            }
            position = found[0].Turns + 1;
            floor = AssistantPrompt.Highest([found[0].Classification], DataClassification.Public);
            var earlier = await session.QueryAsync(
                """
                select question, answer from agents.turn
                where conversation_id = @id and user_id = @user and outcome = 'answered'
                order by position desc limit @n
                """, new { id = existing, user, n = HistoryTurns }, r => (Question: r.GetString(0), Answer: r.GetString(1)), ct);
            foreach (var (q, a) in Enumerable.Reverse(earlier))
            {
                history.Add(new AiMessage(AiRoles.User, q));
                // Old markers point at an earlier turn's passages, so they are dropped from the history the model sees.
                history.Add(new AiMessage(AiRoles.Assistant, AssistantPrompt.Check(a, 0).Answer));
            }
        }

        var language = AssistantPrompt.Language(question);
        var evidence = await retrieval.RetrieveAsync(new RetrievalRequest(question, AiUseCases.Assistant), ct);
        var turnId = Ids.New();
        if (evidence.Passages.Count == 0)
        {
            var outcome = evidence.Withheld > 0 ? AssistantOutcomes.PolicyBlocked : AssistantOutcomes.NoEvidence;
            return new PreparedTurn(turnId, conversationId, request.ConversationId is null, position, question, language, evidence, floor, null, outcome);
        }

        var classification = AssistantPrompt.Highest(evidence.Passages.Select(p => p.Classification), floor);
        List<AiMessage> messages =
        [
            new(AiRoles.System, AssistantPrompt.System(language)),
            .. history,
            new(AiRoles.User, AssistantPrompt.Passages(evidence.Passages)),
            new(AiRoles.User, question),
        ];
        var ai = new AiRequest(AiUseCases.Assistant, messages, classification, MaxTokens: MaxAnswerTokens, Temperature: 0.2);
        return new PreparedTurn(turnId, conversationId, request.ConversationId is null, position, question, language, evidence, classification, ai, null);
    }

    /// <summary>Answers a turn in one call, for clients that do not read a stream.</summary>
    public async Task<TurnView> AskAsync(TurnRequest request, CancellationToken ct)
    {
        var prepared = await PrepareAsync(request, ct);
        var result = prepared.Request is null ? null : await gateway.ChatAsync(prepared.Request, ct);
        return await CompleteAsync(prepared, result, ct);
    }

    /// <summary>
    /// Streams a turn: the text as the model writes it, then the finished turn with its checked citations. Validation and
    /// permission errors are raised before anything is streamed.
    /// </summary>
    public async Task<IAsyncEnumerable<TurnStreamEvent>> StreamAsync(TurnRequest request, CancellationToken ct)
    {
        var prepared = await PrepareAsync(request, ct);
        return Run(prepared, ct);
    }

    private async IAsyncEnumerable<TurnStreamEvent> Run(PreparedTurn prepared, [EnumeratorCancellation] CancellationToken ct)
    {
        AiResult? result = null;
        if (prepared.Request is not null)
        {
            await foreach (var chunk in gateway.StreamChatAsync(prepared.Request, ct))
            {
                if (chunk.Result is not null)
                {
                    result = chunk.Result;
                }
                else if (!string.IsNullOrEmpty(chunk.Delta))
                {
                    yield return new TurnStreamEvent(chunk.Delta, null);
                }
            }
            result ??= AiResult.Denied(AiDenials.ProviderUnavailable);
        }
        yield return new TurnStreamEvent(null, await CompleteAsync(prepared, result, ct));
    }

    internal async Task<TurnView> CompleteAsync(PreparedTurn prepared, AiResult? result, CancellationToken ct)
    {
        var user = context.RequireUser();
        var passages = prepared.Retrieval.Passages;
        string outcome;
        string? reason = null;
        string? answer = null;
        var citations = new List<CitationView>();
        if (result is null)
        {
            outcome = prepared.Outcome!;
        }
        else if (result.Completion is { } completion && !string.IsNullOrWhiteSpace(completion.Content))
        {
            outcome = AssistantOutcomes.Answered;
            var (text, cited) = AssistantPrompt.Check(completion.Content, passages.Count);
            answer = text;
            citations.AddRange(cited.Select(n =>
            {
                var p = passages[n - 1];
                return new CitationView(n, p.Source, p.ItemId, p.Kind, p.Title, p.Anchor, p.Locale, p.Version, p.UpdatedAt);
            }));
        }
        else if (result.Completion is not null)
        {
            outcome = AssistantOutcomes.Unavailable;
            reason = "refused";
        }
        else
        {
            outcome = result.Denial == AiDenials.ClassificationBlocked ? AssistantOutcomes.PolicyBlocked : AssistantOutcomes.Unavailable;
            reason = result.Denial;
        }

        var now = clock.UtcNow;
        var classification = AssistantPrompt.Name(prepared.Classification);
        if (prepared.NewConversation)
        {
            await session.ExecAsync(
                """
                insert into agents.conversation (tenant_id, id, user_id, title, classification, turns, created_at, last_turn_at)
                values (platform.current_tenant_id(), @id, @user, @title, @classification, 1, @now, @now)
                """, new { id = prepared.ConversationId, user, title = AssistantPrompt.Title(prepared.Question), classification, now }, ct);
        }
        else
        {
            await session.ExecAsync(
                "update agents.conversation set turns = @position, last_turn_at = @now, classification = @classification where id = @id and user_id = @user",
                new { id = prepared.ConversationId, user, position = prepared.Position, now, classification }, ct);
        }

        var completionInfo = result?.Completion;
        var settings = new { useCase = AiUseCases.Assistant, topK = ISearchRetrieval.DefaultTopK, passages = passages.Count };
        await session.ExecAsync(
            """
            insert into agents.turn (tenant_id, id, conversation_id, user_id, position, question, answer, outcome, reason, language, agent, citations,
              withheld, retrieval_degraded, classification, model, provider, prompt_version, retrieval, input_tokens, output_tokens, created_at)
            values (platform.current_tenant_id(), @id, @conversation, @user, @position, @question, @answer, @outcome, @reason, @language, null,
              cast(@citations as jsonb), @withheld, @degraded, @classification, @model, @provider, @prompt, cast(@retrieval as jsonb), @input, @output, @now)
            """,
            new
            {
                id = prepared.TurnId, conversation = prepared.ConversationId, user, position = prepared.Position, question = prepared.Question, answer, outcome,
                reason, language = prepared.Language, citations = JsonSerializer.Serialize(citations, Json), withheld = prepared.Retrieval.Withheld,
                degraded = prepared.Retrieval.Degraded, classification, model = completionInfo?.Model, provider = completionInfo?.Provider,
                prompt = AssistantPrompt.Version, retrieval = JsonSerializer.Serialize(settings, Json), input = completionInfo?.Usage.InputTokens ?? 0,
                output = completionInfo?.Usage.OutputTokens ?? 0, now,
            }, ct);
        await outbox.EnqueueAsync(new AssistantAnswered(prepared.TurnId, prepared.ConversationId, user, null, outcome, citations.Count), ct);

        return new TurnView(prepared.TurnId, prepared.ConversationId, prepared.Question, answer, outcome, reason, prepared.Language, null, citations,
            prepared.Retrieval.Withheld > 0, prepared.Retrieval.Degraded, answer is not null, now, null);
    }
}
