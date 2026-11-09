using System.Text.Json;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.AiGateway.Contracts;

/// <summary>The use cases a model call is made for (spec 013 US1 scenario 3). Each has a default model and a fallback chain.</summary>
public static class AiUseCases
{
    public const string Assistant = "assistant";
    public const string Summarize = "summarize";
    public const string Translate = "translate";
    public const string Draft = "draft";
    public const string Embed = "embed";
    public const string Agent = "agent";
    public const string Evaluation = "evaluation";

    public static readonly IReadOnlyList<string> All = [Assistant, Summarize, Translate, Draft, Embed, Agent, Evaluation];

    /// <summary>Use cases whose route change runs the release gate (US1 scenario 6): the assistant and published agents.</summary>
    public static readonly IReadOnlySet<string> Gated = new HashSet<string>(StringComparer.Ordinal) { Assistant, Agent };
}

/// <summary>Data classification of a request (constitution IX). The order matters: the highest one in a request decides.</summary>
public enum DataClassification
{
    Public = 0,
    Internal = 1,
    Confidential = 2,
    Restricted = 3,
}

/// <summary>
/// Machine-readable reasons a call did not produce an answer (spec 013 FR-016). Each has a localized message under
/// <c>ai.denied.{reason}</c> so a feature can show the right degraded state, never a raw provider error.
/// </summary>
public static class AiDenials
{
    public const string NotInPackage = "not_in_package";
    public const string QuotaExhausted = "quota_exhausted";
    public const string BudgetExhausted = "budget_exhausted";
    public const string ClassificationBlocked = "classification_blocked";
    public const string ModelNotAllowed = "model_not_allowed";
    public const string RegionBlocked = "region_blocked";
    public const string ProviderUnavailable = "provider_unavailable";
    public const string ContextTooLarge = "context_too_large";

    /// <summary>A programming error (US2 scenario 4): the caller sent no classification. Logged for engineering.</summary>
    public const string ClassificationMissing = "classification_missing";

    /// <summary>A programming error: the request breaks the contract (an unknown use case, tools on an evaluation call, a judge equal to the model under test).</summary>
    public const string InvalidRequest = "invalid_request";

    public static readonly IReadOnlyList<string> All =
    [
        NotInPackage, QuotaExhausted, BudgetExhausted, ClassificationBlocked, ModelNotAllowed, RegionBlocked, ProviderUnavailable, ContextTooLarge,
        ClassificationMissing, InvalidRequest,
    ];
}

public static class AiRoles
{
    public const string System = "system";
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
}

/// <summary>One message of a conversation. <paramref name="ToolCallId"/> links a tool result to the call it answers.</summary>
public sealed record AiMessage(string Role, string Content, string? ToolCallId = null, IReadOnlyList<AiToolCall>? ToolCalls = null);

/// <summary>A tool the model may call, with a JSON schema for its arguments.</summary>
public sealed record AiTool(string Name, string Description, JsonElement Parameters);

/// <summary>A tool call returned by the model. Arguments are the JSON text the model produced and are untrusted.</summary>
public sealed record AiToolCall(string Id, string Name, string Arguments);

/// <summary>
/// A chat request (spec 013 FR-003). Tenant, acting user and acting unit come from the server context, never from this
/// object. <paramref name="Classification"/> is the highest classification of the content in the request and is
/// required: a missing one is refused. <paramref name="Model"/> optionally names a model, which must be allowed by the
/// tenant policy. <paramref name="ModelUnderTest"/> is set on evaluation calls so the judge is never the same model.
/// </summary>
public sealed record AiRequest(
    string UseCase,
    IReadOnlyList<AiMessage> Messages,
    DataClassification? Classification,
    IReadOnlyList<AiTool>? Tools = null,
    JsonElement? ResponseSchema = null,
    string? Model = null,
    int? MaxTokens = null,
    double? Temperature = null,
    Guid? AgentId = null,
    int? AgentVersion = null,
    string? ModelUnderTest = null,
    Guid? ActingUnitId = null,
    bool PlatformCharged = false);

public sealed record AiUsage(int InputTokens, int OutputTokens)
{
    public static readonly AiUsage None = new(0, 0);
}

/// <summary>A model answer and how it was produced.</summary>
public sealed record AiCompletion(
    string Content,
    IReadOnlyList<AiToolCall> ToolCalls,
    AiUsage Usage,
    string Model,
    string Provider,
    bool FallbackUsed,
    bool Partial,
    string FinishReason);

/// <summary>The typed result of a chat call (FR-016): an answer, or a denial reason and nothing sent or nothing usable.</summary>
public sealed record AiResult(AiCompletion? Completion, string? Denial)
{
    public bool IsOk => Completion is not null;

    public static AiResult Ok(AiCompletion completion) => new(completion, null);

    public static AiResult Denied(string reason) => new(null, reason);
}

/// <summary>A piece of a streamed answer. The last chunk carries the result, which is also how a denial is streamed.</summary>
public sealed record AiStreamChunk(string? Delta, AiResult? Result);

/// <summary>An embedding request (use case <c>embed</c>). Classification is required as for chat.</summary>
public sealed record EmbeddingRequest(IReadOnlyList<string> Inputs, DataClassification? Classification, string? Model = null, Guid? ActingUnitId = null);

public sealed record EmbeddingResult(IReadOnlyList<float[]>? Vectors, AiUsage Usage, string? Model, string? Provider, string? Denial)
{
    public bool IsOk => Vectors is not null;

    public static EmbeddingResult Denied(string reason) => new(null, AiUsage.None, null, null, reason);
}

/// <summary>
/// The single way to call a model in the product (spec 013 FR-001, constitution VIII). The gateway applies the
/// pipeline of FR-002 in order and refuses before anything leaves the product when a step fails.
/// </summary>
public interface IAiGateway
{
    Task<AiResult> ChatAsync(AiRequest request, CancellationToken ct = default);

    IAsyncEnumerable<AiStreamChunk> StreamChatAsync(AiRequest request, CancellationToken ct = default);

    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

/// <summary>
/// Which content the gateway would accept now for a use case (spec 012 US3 scenario 2, spec 013 FR-002 step 3). Callers
/// that gather content for a model, such as search retrieval, drop what is above the ceiling before they build the
/// request and tell the person evidence was withheld, instead of having the whole call refused.
/// </summary>
public interface IAiClassificationPolicy
{
    /// <summary>The highest classification a call of <paramref name="useCase"/> could carry now, or null when no call of it can be made now or even public content would be refused.</summary>
    Task<DataClassification?> CeilingAsync(string useCase, CancellationToken ct = default);
}

/// <summary>The tenant model policy changed and applies to new calls (spec 013 section 12).</summary>
public sealed record AiPolicyChanged(int Version) : IIntegrationEvent;

/// <summary>
/// A policy change alters the route behind the assistant or agents (FR-019). It waits for the release gate of spec 034,
/// which answers with its gate decision for <paramref name="RouteChangeId"/>.
/// </summary>
public sealed record AiModelRouteChangeProposed(Guid RouteChangeId, IReadOnlyList<string> UseCases, IReadOnlyDictionary<string, IReadOnlyList<string>> Before,
    IReadOnlyDictionary<string, IReadOnlyList<string>> After) : IIntegrationEvent;
