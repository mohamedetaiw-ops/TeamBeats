using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Agents;

/// <summary>A question for the assistant; <c>conversationId</c> continues an earlier answer's conversation.</summary>
public sealed record PublicQuestion(string? Question, Guid? ConversationId);

/// <summary>A source the answer cites: an item the person may read, at the section it quotes.</summary>
public sealed record PublicCitation(int N, string Source, Guid ItemId, string Kind, string Title, string Anchor, string Locale, int? Version);

/// <summary>
/// The assistant's answer (spec 014), marked as AI generated. <c>outcome</c> is <c>answered</c>, a no-evidence answer
/// when nothing the person may read supports one, or unavailable when the AI provider or the policy says no; the reason
/// says which.
/// </summary>
public sealed record PublicAnswer(Guid Id, Guid ConversationId, string? Answer, string Outcome, string? Reason, string Language,
    IReadOnlyList<PublicCitation> Citations, bool Generated, bool RetrievalDegraded);

/// <summary>
/// The public operation of <c>assistant:ask</c> (spec 035 section 6), for delegated apps only: a cited answer from the
/// assistant of spec 014 through the AI Gateway, over content the person may read, counted against the tenant AI budget
/// and capped by classification as inside TeamBeats. When the AI provider is down the answer says so; nothing else in
/// the public API depends on AI (FR-030).
/// </summary>
public static class AssistantPublicEndpoints
{
    public static void MapAssistantPublicEndpoints(this IEndpointRouteBuilder app)
    {
        var assistant = app.MapPublicApi("Public: assistant");
        assistant.MapPost("/assistant/answers", async (PublicQuestion request, AssistantService service, IDbSession session, CancellationToken ct) =>
        {
            var turn = await service.AskAsync(new TurnRequest(request.ConversationId, request.Question), ct);
            await session.CommitAsync(ct);
            return Results.Ok(new PublicAnswer(turn.Id, turn.ConversationId, turn.Answer, turn.Outcome, turn.Reason, turn.Language,
                turn.Citations.Select(c => new PublicCitation(c.N, c.Source, c.ItemId, c.Kind, c.Title, c.Anchor, c.Locale, c.Version)).ToList(),
                turn.Generated, turn.RetrievalDegraded));
        }).RequireScope(ScopeCatalogue.AssistantAsk).Idempotent().Produces<PublicAnswer>();
    }
}
