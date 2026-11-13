using System.Runtime.CompilerServices;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// A question to the assistant. <paramref name="AgentId"/> asks a particular agent the person may use (switching agent),
/// and <paramref name="Knowledge"/> asks the knowledge agent without routing (going back). Otherwise the turn is routed.
/// <paramref name="Scope"/> on the first turn keeps the conversation to one item (spec 025 FR-011); later turns keep the
/// conversation's scope and never route.
/// </summary>
/// <remarks>
/// <paramref name="MaxClassification"/> caps what the answer may draw on, for an external AI client (spec 035 US3
/// scenario 4): passages above it are left out as withheld, and a conversation that already holds more is refused.
/// </remarks>
public sealed record TurnRequest(Guid? ConversationId, string? Question, Guid? AgentId = null, bool? Knowledge = null, AssistantScope? Scope = null,
    string? MaxClassification = null);

/// <summary>
/// The item a conversation is kept to: <paramref name="Source"/> is its search source and <paramref name="ItemId"/> the
/// item. In R1 only policies offer it ("Ask about this policy").
/// </summary>
public sealed record AssistantScope(string Source, Guid ItemId)
{
    public const string Policies = "policies";

    public static readonly IReadOnlyList<string> Sources = [Policies];
}

/// <summary>A cited passage: <paramref name="N"/> is the marker used in the answer, <paramref name="Anchor"/> opens the item at the passage.</summary>
public sealed record CitationView(int N, string Source, Guid ItemId, string Kind, string Title, string Anchor, string Locale, int? Version, DateTimeOffset? UpdatedAt);

/// <summary>
/// One turn as its user sees it. <paramref name="Generated"/> marks the answer as AI generated (FR-007);
/// <paramref name="Withheld"/> says some content the person could read was kept from the assistant by the AI policy.
/// </summary>
public sealed record TurnView(Guid Id, Guid ConversationId, string Question, string? Answer, string Outcome, string? Reason, string Language, string? Agent,
    IReadOnlyList<CitationView> Citations, bool Withheld, bool RetrievalDegraded, bool Generated, DateTimeOffset CreatedAt, bool? Helpful,
    AgentChoice? AnsweredBy = null, RunView? Run = null, IReadOnlyList<AgentChoice>? Agents = null);

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
    string? Outcome,
    RouteView? Route = null,
    AssistantScope? Scope = null);

/// <summary>
/// The knowledge agent's turn pipeline (spec 014 T003, US1, FR-002 to FR-004, FR-010 to FR-012, FR-021). Retrieval runs
/// as the person first; with no passages nothing is sent to a model and the turn says why. Otherwise the passages go to
/// the AI Gateway in the untrusted envelope at the highest classification of the context, the answer's citations are
/// checked against the passages of the turn, and the turn is stored with the model and prompt version. The assistant
/// holds no tools (FR-005).
/// </summary>
public sealed class AssistantService(IDbSession session, ITenantContext context, IEntitlementService entitlements, ISearchRetrieval retrieval,
    IAiGateway gateway, IOutbox outbox, IClock clock, AgentRouter router, AgentRuns runs, IAuditWriter audit)
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
        var scope = request.Scope;
        if (request.ConversationId is { } existing)
        {
            var found = await session.QueryAsync(
                "select turns, classification, scope_source, scope_item_id from agents.conversation where id = @id and user_id = @user for update",
                new { id = existing, user }, r => (Turns: r.GetInt32(0), Classification: r.GetString(1),
                    Scope: r.Get<string?>("scope_source") is { } source ? new AssistantScope(source, r.GetGuid(3)) : null), ct);
            if (found.Count == 0)
            {
                throw AppException.NotFound();
            }
            scope = found[0].Scope;
            position = found[0].Turns + 1;
            floor = AssistantPrompt.Highest([found[0].Classification], DataClassification.Public);
            if (request.MaxClassification is { } limit && Classifications.Rank(found[0].Classification) > Classifications.Rank(limit))
            {
                throw AppException.Denied();
            }
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
        else if (scope is not null)
        {
            await ScopeAsync(scope, ct);
        }

        var language = AssistantPrompt.Language(question);
        // Routing (spec 014 US2, spec 020 US7): when an agent the person may use answers, the assistant hands the turn to it
        // and gains none of its tools; otherwise the knowledge agent answers below.
        // A scoped conversation is answered by the knowledge agent from its item only, so it is never routed.
        var route = scope is null ? await router.DecideAsync(question, request.AgentId, request.Knowledge == true, ct) : null;
        if (route?.Agent is not null)
        {
            return new PreparedTurn(Ids.New(), conversationId, request.ConversationId is null, position, question, language, new RetrievalResult([], 0, false),
                floor, null, null, route);
        }
        var evidence = await retrieval.RetrieveAsync(new RetrievalRequest(question, AiUseCases.Assistant, Source: scope?.Source, ItemId: scope?.ItemId), ct);
        if (request.MaxClassification is { } cap)
        {
            var kept = evidence.Passages.Where(p => Classifications.Rank(p.Classification) is var rank && rank >= 0 && rank <= Classifications.Rank(cap)).ToList();
            evidence = evidence with { Passages = kept, Withheld = evidence.Withheld + evidence.Passages.Count - kept.Count };
        }
        var turnId = Ids.New();
        // Instruction-like text in a passage is recorded with its item (027 US5 scenario 1); the passage still goes only as marked data.
        foreach (var p in evidence.Passages)
        {
            if (InjectionDetector.Detect(p.Text) is { } rule)
            {
                await audit.WriteAsync(new AuditEntry(AuditActions.AgentInjectionDetected, "assistant_turn",
                    turnId.ToString(), Changes: new { conversation = conversationId, rule, source = p.Source, itemId = p.ItemId }), ct);
            }
        }
        if (evidence.Passages.Count == 0)
        {
            var outcome = evidence.Withheld > 0 ? AssistantOutcomes.PolicyBlocked : AssistantOutcomes.NoEvidence;
            return new PreparedTurn(turnId, conversationId, request.ConversationId is null, position, question, language, evidence, floor, null, outcome, route, scope);
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
        return new PreparedTurn(turnId, conversationId, request.ConversationId is null, position, question, language, evidence, classification, ai, null, route, scope);
    }

    /// <summary>
    /// Checks the scope of a new conversation: a source that offers it, the policy Q&A capability for policies, and an
    /// item the person may read now. An item the person may not read is not found, so its existence is never shown
    /// (spec 025 US4 scenario 6).
    /// </summary>
    private async Task ScopeAsync(AssistantScope scope, CancellationToken ct)
    {
        if (!AssistantScope.Sources.Contains(scope.Source))
        {
            throw AppException.Validation("scope", "validation.assistant.scope");
        }
        if (scope.Source == AssistantScope.Policies)
        {
            await entitlements.DemandAsync(Capabilities.AiPolicyQa, ct);
        }
        var item = await retrieval.ItemAsync(scope.Source, scope.ItemId, AiUseCases.Assistant, ct: ct);
        if (item.Passages.Count == 0 && item.Withheld == 0)
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>Answers a turn in one call, for clients that do not read a stream.</summary>
    public async Task<TurnView> AskAsync(TurnRequest request, CancellationToken ct)
    {
        var prepared = await PrepareAsync(request, ct);
        if (prepared.Route?.Agent is { } agent)
        {
            return await CompleteWithAgentAsync(prepared, agent, ct);
        }
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
        if (prepared.Route?.Agent is { } agent)
        {
            // Agent runs are answered whole (runs finish inside one request); the turn arrives as one "done" event.
            var view = await CompleteWithAgentAsync(prepared, agent, ct);
            return Single(view);
        }
        return Run(prepared, ct);
    }

    private static async IAsyncEnumerable<TurnStreamEvent> Single(TurnView view)
    {
        await Task.CompletedTask;
        yield return new TurnStreamEvent(null, view);
    }

    /// <summary>The routed agent answers the turn; the turn keeps the agent and its run, and the run keeps the routing decision.</summary>
    private async Task<TurnView> CompleteWithAgentAsync(PreparedTurn prepared, AgentChoice agent, CancellationToken ct)
    {
        var user = context.RequireUser();
        var run = await runs.RunAsync(agent.Id, new RunRequest(prepared.Question, prepared.Language), prepared.Route!.DecisionId, ct);
        var outcome = run.Status switch
        {
            RunStatuses.Completed when run.Answer is not null => AssistantOutcomes.Answered,
            RunStatuses.Waiting => AssistantOutcomes.ActionPending,
            _ when run.Outcome == RunOutcomes.PolicyBlocked => AssistantOutcomes.PolicyBlocked,
            _ => AssistantOutcomes.Unavailable,
        };
        var reason = outcome is AssistantOutcomes.Answered or AssistantOutcomes.ActionPending ? null : run.Reason ?? run.Outcome;
        var citations = run.Citations.Select(c => new CitationView(c.N, c.Source, c.ItemId, c.Source, c.Title, c.Link, run.Language, null, null)).ToList();
        var classification = await session.ScalarAsync<string>("select classification from agents.run where id = @id", new { id = run.Id }, ct) ?? "internal";
        var level = AssistantPrompt.Highest([classification], prepared.Classification);
        await SaveConversationAsync(prepared, user, AssistantPrompt.Name(level), ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.turn (tenant_id, id, conversation_id, user_id, position, question, answer, outcome, reason, language, agent, run_id, citations,
              withheld, retrieval_degraded, classification, model, provider, prompt_version, retrieval, input_tokens, output_tokens, created_at)
            values (platform.current_tenant_id(), @id, @conversation, @user, @position, @question, @answer, @outcome, @reason, @language, @agent, @run,
              cast(@citations as jsonb), 0, false, @classification, null, null, @prompt, cast(@retrieval as jsonb), 0, 0, @now)
            """,
            new
            {
                id = prepared.TurnId, conversation = prepared.ConversationId, user, position = prepared.Position, question = prepared.Question, answer = run.Answer,
                outcome, reason, language = run.Language, agent = agent.Id.ToString(), run = run.Id, citations = JsonSerializer.Serialize(citations, Json),
                classification = AssistantPrompt.Name(level), prompt = AgentRuns.PromptVersion,
                retrieval = JsonSerializer.Serialize(new { useCase = AiUseCases.Agent, route = prepared.Route.DecisionId }, Json), now,
            }, ct);
        await outbox.EnqueueAsync(new AssistantAnswered(prepared.TurnId, prepared.ConversationId, user, agent.Id.ToString(), outcome, citations.Count), ct);
        return new TurnView(prepared.TurnId, prepared.ConversationId, prepared.Question, run.Answer, outcome, reason, run.Language, agent.Id.ToString(), citations,
            false, false, run.Generated, now, null, agent, run, prepared.Route.Available);
    }

    private async Task SaveConversationAsync(PreparedTurn prepared, Guid user, string classification, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (prepared.NewConversation)
        {
            await session.ExecAsync(
                """
                insert into agents.conversation (tenant_id, id, user_id, title, classification, turns, created_at, last_turn_at, scope_source, scope_item_id)
                values (platform.current_tenant_id(), @id, @user, @title, @classification, 1, @now, @now, @source, @item)
                """, new { id = prepared.ConversationId, user, title = AssistantPrompt.Title(prepared.Question), classification, now,
                    source = prepared.Scope?.Source, item = prepared.Scope?.ItemId }, ct);
        }
        else
        {
            await session.ExecAsync(
                "update agents.conversation set turns = @position, last_turn_at = @now, classification = @classification where id = @id and user_id = @user",
                new { id = prepared.ConversationId, user, position = prepared.Position, now, classification }, ct);
        }
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
        await SaveConversationAsync(prepared, user, classification, ct);

        var completionInfo = result?.Completion;
        var settings = new { useCase = AiUseCases.Assistant, topK = ISearchRetrieval.DefaultTopK, passages = passages.Count, scope = prepared.Scope };
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
            prepared.Retrieval.Withheld > 0, prepared.Retrieval.Degraded, answer is not null, now, null, null, null, prepared.Route?.Available);
    }
}
