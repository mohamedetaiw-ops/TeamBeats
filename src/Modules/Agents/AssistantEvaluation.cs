using System.Diagnostics;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// The assistant port evaluation uses (spec 034, spec 014 T008); see <see cref="IAssistantEvaluationRuntime"/>. It
/// builds the same request as an assistant turn without history or routing: the system prompt, the passages in the
/// untrusted envelope and the question, on the assistant use case, and applies the same citation check.
/// </summary>
public sealed class AssistantEvaluationRuntime(ITenantContext context, IEntitlementService entitlements, IAiGateway gateway) : IAssistantEvaluationRuntime
{
    public async Task<EvalRunRecord> RunCaseAsync(string question, string language, IReadOnlyList<EvalPassage> passages, Guid? routeChangeId = null,
        CancellationToken ct = default)
    {
        _ = context.RequireUser();
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAssistant, ct))
        {
            throw AppException.Conflict("conflict.assistant.unavailable");
        }
        var text = question.Trim();
        if (text.Length is 0 or > AssistantService.MaxQuestionLength)
        {
            throw AppException.Validation("question", "validation.assistant.question");
        }
        var locale = language == "ar" ? "ar" : "en";
        var retrieved = passages.Select((p, i) => new RetrievedPassage(IAssistantEvaluationRuntime.Source, IAssistantEvaluationRuntime.PassageId(i + 1), "page", p.Title,
            p.Text, string.Empty, locale, "internal", null, null, 1)).ToList();
        var read = retrieved.Select((p, i) => new EvalRunSource(i + 1, p.Source, p.ItemId)).ToList();
        var id = Ids.New();
        if (retrieved.Count == 0)
        {
            // Like a turn without permitted evidence: nothing goes to a model.
            return new EvalRunRecord(id, "completed", AssistantOutcomes.NoEvidence, null, null, locale, null, 0, 0, 0, [], [], [], null);
        }
        List<AiMessage> messages =
        [
            new(AiRoles.System, AssistantPrompt.System(locale)),
            new(AiRoles.User, AssistantPrompt.Passages(retrieved)),
            new(AiRoles.User, text),
        ];
        var watch = Stopwatch.StartNew();
        var result = await gateway.ChatAsync(new AiRequest(AiUseCases.Assistant, messages, DataClassification.Internal, MaxTokens: AssistantService.MaxAnswerTokens,
            Temperature: 0.2, RouteChangeId: routeChangeId), ct);
        var duration = (int)Math.Min(int.MaxValue, watch.ElapsedMilliseconds);
        if (result.Completion is not { } completion)
        {
            var outcome = result.Denial == AiDenials.ClassificationBlocked ? AssistantOutcomes.PolicyBlocked : AssistantOutcomes.Unavailable;
            return new EvalRunRecord(id, "failed", outcome, result.Denial, null, locale, null, 0, 0, duration, [], read, [], null);
        }
        if (string.IsNullOrWhiteSpace(completion.Content))
        {
            return new EvalRunRecord(id, "failed", AssistantOutcomes.Unavailable, "refused", null, locale, completion.Model, completion.Usage.InputTokens,
                completion.Usage.OutputTokens, duration, [], read, [], null);
        }
        var (answer, cited) = AssistantPrompt.Check(completion.Content, retrieved.Count);
        return new EvalRunRecord(id, "completed", AssistantOutcomes.Answered, null, answer, locale, completion.Model, completion.Usage.InputTokens,
            completion.Usage.OutputTokens, duration, [], read, cited.Select(n => new EvalRunSource(n, retrieved[n - 1].Source, retrieved[n - 1].ItemId)).ToList(), null);
    }
}
