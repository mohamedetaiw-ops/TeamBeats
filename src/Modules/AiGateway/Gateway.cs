using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>The models a call may use, in order, with the parameters after the tenant's limits.</summary>
internal sealed record RoutePlan(string UseCase, DataClassification Classification, IReadOnlyList<CatalogueEntry> Candidates, int? MaxTokens, double? Temperature);

/// <summary>
/// The gateway pipeline (spec 013 FR-002), in this order: entitlement and quota, budget, classification policy,
/// allowlist and defaults, region and egress, context limits, routing and fallback, the provider call, output checks,
/// and metering. A failed step stops the call with a reason from <see cref="AiDenials"/> before anything is sent.
/// Tenant and user come from the server context; the classification comes from the calling module, never from the
/// client or a model.
/// </summary>
public sealed class AiGatewayService(
    ITenantContext context,
    IEntitlementService entitlements,
    ModelPolicies policies,
    ModelCatalogue catalogue,
    IEnumerable<IModelProvider> providers,
    ProviderCircuits circuits,
    ILogger<AiGatewayService> logger) : IAiGateway
{
    /// <summary>Characters per token for the size estimate before a call; deliberately low so Arabic text is not underestimated.</summary>
    public const double CharsPerToken = 3.0;

    private readonly Dictionary<string, IModelProvider> byKey = providers.ToDictionary(p => p.Key, StringComparer.Ordinal);

    internal static int Estimate(IEnumerable<string> texts) => (int)Math.Ceiling(texts.Sum(t => t.Length) / CharsPerToken);

    private static int Estimate(AiRequest request) =>
        Estimate(request.Messages.Select(m => m.Content).Concat(request.Tools?.Select(t => t.Description + t.Parameters.GetRawText()) ?? []));

    /// <summary>Steps 1 to 6: who may call, where the content may go, which models are left, and the parameters after limits.</summary>
    internal async Task<(RoutePlan? Plan, string? Denial)> PlanAsync(string useCase, string? model, DataClassification? classification, int inputTokens, int? maxTokens,
        double? temperature, IReadOnlyCollection<string> needs, string? modelUnderTest, CancellationToken ct)
    {
        context.RequireTenant();
        if (!AiUseCases.All.Contains(useCase))
        {
            return (null, AiDenials.InvalidRequest);
        }

        // 1. Entitlement. The monthly request quota is counted from the call records of the metering step.
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAssistant, ct))
        {
            return (null, AiDenials.NotInPackage);
        }

        // 2. Budget: budgets are R2; the step stays here so the order does not change.

        // 3. Classification policy. A missing classification is a programming error, never defaulted to Public.
        if (classification is not { } level)
        {
            logger.LogError("AI call without a classification for use case {UseCase}; the calling module must classify its content", useCase);
            return (null, AiDenials.ClassificationMissing);
        }
        if (level == DataClassification.Restricted)
        {
            // Restricted content only goes to approved private endpoints (R2); OpenRouter is never eligible.
            return (null, AiDenials.ClassificationBlocked);
        }
        var policy = (await policies.EffectiveAsync(ct)).Document;
        var snapshot = await catalogue.SnapshotAsync(ct);
        bool Classified(CatalogueEntry e) => e.Ceiling >= level && (level < DataClassification.Confidential || (e.ConfidentialEligible && policy.Confidential.Contains(e.Model)));
        if (level == DataClassification.Confidential && !policy.Confidential.Any(m => snapshot.Models.TryGetValue(m, out var e) && e.ConfidentialEligible))
        {
            return (null, AiDenials.ClassificationBlocked);
        }

        // 4. Allowlist and defaults. A named model must be allowed and in the catalogue; the use case route follows it.
        var route = policy.RouteOf(useCase, snapshot);
        List<string> chain;
        if (model is not null)
        {
            if (!policy.Allowlist.Contains(model) || !snapshot.Models.TryGetValue(model, out var named) || !named.Active ||
                !named.Can(useCase == AiUseCases.Embed ? ModelCapabilities.Embeddings : ModelCapabilities.Chat))
            {
                return (null, AiDenials.ModelNotAllowed);
            }
            chain = [model, .. route.Where(m => m != model)];
        }
        else
        {
            chain = [.. route];
        }
        if (useCase == AiUseCases.Evaluation && modelUnderTest is not null)
        {
            // FR-017: the judge is never the model under test.
            if (model == modelUnderTest)
            {
                return (null, AiDenials.InvalidRequest);
            }
            chain.Remove(modelUnderTest);
        }
        var candidates = chain.Select(m => snapshot.Models[m]).Where(e => needs.All(e.Can)).ToList();
        if (candidates.Count == 0)
        {
            return (null, AiDenials.ModelNotAllowed);
        }
        candidates = [.. candidates.Where(Classified)];
        if (candidates.Count == 0)
        {
            return (null, AiDenials.ClassificationBlocked);
        }

        // 5. Region and egress. An installation that cannot reach a provider reports it unreachable by design.
        if (policy.Regions is { } regions)
        {
            candidates = [.. candidates.Where(e => e.Regions.Any(regions.Contains))];
            if (candidates.Count == 0)
            {
                return (null, AiDenials.RegionBlocked);
            }
        }
        candidates = [.. candidates.Where(e => byKey.TryGetValue(e.Provider, out var p) && p.Reachable)];
        if (candidates.Count == 0)
        {
            return (null, AiDenials.ProviderUnavailable);
        }

        // 6. Context and parameter limits. Trimming is the caller's job (spec 014).
        var output = Math.Min(maxTokens ?? policy.MaxOutputTokens ?? int.MaxValue, policy.MaxOutputTokens ?? int.MaxValue);
        int? clampedTokens = output == int.MaxValue ? null : output;
        candidates = [.. candidates.Where(e => inputTokens + (clampedTokens ?? 0) <= e.ContextTokens)];
        if (candidates.Count == 0)
        {
            return (null, AiDenials.ContextTooLarge);
        }
        var t = temperature;
        if (t is { } value)
        {
            t = Math.Clamp(value, policy.MinTemperature ?? 0, policy.MaxTemperature ?? 2);
        }
        return (new RoutePlan(useCase, level, candidates, clampedTokens, t), null);
    }

    private static string[] Needs(AiRequest request)
    {
        var needs = new List<string> { ModelCapabilities.Chat };
        if (request.Tools is { Count: > 0 })
        {
            needs.Add(ModelCapabilities.Tools);
        }
        if (request.ResponseSchema is not null)
        {
            needs.Add(ModelCapabilities.StructuredOutput);
        }
        return [.. needs];
    }

    private async Task<(RoutePlan? Plan, string? Denial)> PlanChatAsync(AiRequest request, CancellationToken ct)
    {
        if (request.Messages.Count == 0 || (request.UseCase == AiUseCases.Evaluation && request.Tools is { Count: > 0 }) || request.UseCase == AiUseCases.Embed)
        {
            return (null, AiDenials.InvalidRequest);
        }
        return await PlanAsync(request.UseCase, request.Model, request.Classification, Estimate(request), request.MaxTokens, request.Temperature, Needs(request),
            request.ModelUnderTest, ct);
    }

    public async Task<AiResult> ChatAsync(AiRequest request, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var (plan, denial) = await PlanChatAsync(request, ct);
        if (plan is null)
        {
            Denied(request.UseCase, request.Model, request.Classification, denial!);
            return AiResult.Denied(denial!);
        }

        // 7 and 8. Routing and fallback: one retry on a retryable error, then the next model that passed the same policy.
        foreach (var entry in plan.Candidates)
        {
            var provider = byKey[entry.Provider];
            if (!circuits.Allow(provider.Key))
            {
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ProviderReply reply;
                try
                {
                    reply = await provider.ChatAsync(new ProviderChat(entry.Model, request.Messages, request.Tools, request.ResponseSchema, plan.MaxTokens, plan.Temperature), ct);
                }
                catch (ProviderException ex)
                {
                    circuits.Record(provider.Key, failed: true);
                    logger.LogWarning("AI provider {Provider} failed for {Model} (retryable {Retryable}): {Message}", provider.Key, entry.Model, ex.Retryable, ex.Message);
                    if (ex.Retryable && attempt == 0)
                    {
                        continue;
                    }
                    break;
                }
                circuits.Record(provider.Key, failed: false);

                // 9. Output checks: tool calls for tools not offered are dropped; structured output must match the schema.
                var calls = OfferedOnly(reply.ToolCalls, request.Tools, entry.Model);
                if (request.ResponseSchema is { } schema && calls.Count == 0 && !StructuredOutput.Matches(reply.Content, schema))
                {
                    logger.LogWarning("AI model {Model} returned output that does not match the response schema", entry.Model);
                    break;
                }
                var completion = new AiCompletion(reply.Content, calls, reply.Usage, entry.Model, provider.Key, entry != plan.Candidates[0], reply.Partial, reply.FinishReason);
                Served(plan, completion, watch.Elapsed);
                return AiResult.Ok(completion);
            }
        }
        Denied(request.UseCase, request.Model, request.Classification, AiDenials.ProviderUnavailable);
        return AiResult.Denied(AiDenials.ProviderUnavailable);
    }

    public async IAsyncEnumerable<AiStreamChunk> StreamChatAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var (plan, denial) = await PlanChatAsync(request, ct);
        if (plan is null)
        {
            Denied(request.UseCase, request.Model, request.Classification, denial!);
            yield return new AiStreamChunk(null, AiResult.Denied(denial!));
            yield break;
        }
        var input = Estimate(request);
        foreach (var entry in plan.Candidates)
        {
            var provider = byKey[entry.Provider];
            if (!circuits.Allow(provider.Key))
            {
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var text = new StringBuilder();
                ProviderReply? final = null;
                ProviderException? failure = null;
                await using (var parts = provider.StreamAsync(new ProviderChat(entry.Model, request.Messages, request.Tools, request.ResponseSchema, plan.MaxTokens, plan.Temperature), ct)
                    .GetAsyncEnumerator(ct))
                {
                    while (true)
                    {
                        ProviderStreamPart part;
                        try
                        {
                            if (!await parts.MoveNextAsync())
                            {
                                break;
                            }
                            part = parts.Current;
                        }
                        catch (ProviderException ex)
                        {
                            failure = ex;
                            break;
                        }
                        if (part.Final is { } f)
                        {
                            final = f;
                            break;
                        }
                        if (part.Delta is { Length: > 0 } delta)
                        {
                            text.Append(delta);
                            yield return new AiStreamChunk(delta, null);
                        }
                    }
                }
                circuits.Record(provider.Key, failed: failure is not null);
                var fallback = entry != plan.Candidates[0];
                if (final is not null)
                {
                    var completion = new AiCompletion(text.ToString(), OfferedOnly(final.ToolCalls, request.Tools, entry.Model), final.Usage, entry.Model, provider.Key, fallback,
                        final.Partial, final.FinishReason);
                    Served(plan, completion, watch.Elapsed);
                    yield return new AiStreamChunk(null, AiResult.Ok(completion));
                    yield break;
                }
                if (text.Length > 0)
                {
                    // Cut off after the first words: the caller gets what arrived, flagged partial, metered for the tokens received.
                    var partial = new AiCompletion(text.ToString(), [], new AiUsage(input, Estimate([text.ToString()])), entry.Model, provider.Key, fallback, true, "cut");
                    Served(plan, partial, watch.Elapsed);
                    yield return new AiStreamChunk(null, AiResult.Ok(partial));
                    yield break;
                }
                if (failure is { Retryable: true } && attempt == 0)
                {
                    continue;
                }
                break;
            }
        }
        Denied(request.UseCase, request.Model, request.Classification, AiDenials.ProviderUnavailable);
        yield return new AiStreamChunk(null, AiResult.Denied(AiDenials.ProviderUnavailable));
    }

    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        if (request.Inputs.Count == 0)
        {
            return EmbeddingResult.Denied(AiDenials.InvalidRequest);
        }
        var (plan, denial) = await PlanAsync(AiUseCases.Embed, request.Model, request.Classification, Estimate(request.Inputs), null, null, [ModelCapabilities.Embeddings], null, ct);
        if (plan is null)
        {
            Denied(AiUseCases.Embed, request.Model, request.Classification, denial!);
            return EmbeddingResult.Denied(denial!);
        }
        foreach (var entry in plan.Candidates)
        {
            var provider = byKey[entry.Provider];
            if (!circuits.Allow(provider.Key))
            {
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var result = await provider.EmbedAsync(entry.Model, request.Inputs, ct);
                    circuits.Record(provider.Key, failed: false);
                    var completion = new AiCompletion(string.Empty, [], result.Usage, entry.Model, provider.Key, entry != plan.Candidates[0], false, "stop");
                    Served(plan, completion, watch.Elapsed);
                    return new EmbeddingResult(result.Vectors, result.Usage, entry.Model, provider.Key, null);
                }
                catch (ProviderException ex)
                {
                    circuits.Record(provider.Key, failed: true);
                    if (ex.Retryable && attempt == 0)
                    {
                        continue;
                    }
                    break;
                }
            }
        }
        Denied(AiUseCases.Embed, request.Model, request.Classification, AiDenials.ProviderUnavailable);
        return EmbeddingResult.Denied(AiDenials.ProviderUnavailable);
    }

    /// <summary>Drops tool calls for tools that were not offered and records it as a policy event (edge cases).</summary>
    private List<AiToolCall> OfferedOnly(IReadOnlyList<AiToolCall> calls, IReadOnlyList<AiTool>? tools, string model)
    {
        var offered = tools?.Select(t => t.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var kept = calls.Where(c => offered.Contains(c.Name)).ToList();
        if (kept.Count < calls.Count)
        {
            logger.LogWarning("AI policy event: model {Model} called {Count} tool(s) that were not offered; the calls were dropped", model, calls.Count - kept.Count);
        }
        return kept;
    }

    // 10. Metering and telemetry: metadata only (US8 scenario 1). Call records for the usage view come with the metering slice.
    private void Served(RoutePlan plan, AiCompletion completion, TimeSpan elapsed) =>
        logger.LogInformation(
            "ai.call tenant {Tenant} user {User} use case {UseCase} model {Model} provider {Provider} input {Input} output {Output} latency {Latency} ms classification {Classification} fallback {Fallback} partial {Partial} correlation {Correlation}",
            context.TenantId, context.ActorId, plan.UseCase, completion.Model, completion.Provider, completion.Usage.InputTokens, completion.Usage.OutputTokens,
            (int)elapsed.TotalMilliseconds, Classifications.Key(plan.Classification), completion.FallbackUsed, completion.Partial, Correlation.Id);

    private void Denied(string useCase, string? model, DataClassification? classification, string reason) =>
        logger.LogWarning("ai.call_denied tenant {Tenant} user {User} use case {UseCase} model requested {Model} classification {Classification} reason {Reason} correlation {Correlation}",
            context.TenantId, context.ActorId, useCase, model, classification is { } c ? Classifications.Key(c) : null, reason, Correlation.Id);
}

/// <summary>
/// A small check of structured output against the response schema: the text must be JSON of the schema's top-level type,
/// with every required property present. Full schema validation is the provider's job when it supports strict mode.
/// </summary>
internal static class StructuredOutput
{
    public static bool Matches(string content, JsonElement schema)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return false;
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                var ok = type.GetString() switch
                {
                    "object" => root.ValueKind == JsonValueKind.Object,
                    "array" => root.ValueKind == JsonValueKind.Array,
                    "string" => root.ValueKind == JsonValueKind.String,
                    "number" or "integer" => root.ValueKind == JsonValueKind.Number,
                    "boolean" => root.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => true,
                };
                if (!ok)
                {
                    return false;
                }
            }
            if (root.ValueKind == JsonValueKind.Object && schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray())
                {
                    if (name.GetString() is { } n && !root.TryGetProperty(n, out _))
                    {
                        return false;
                    }
                }
            }
            return true;
        }
    }
}
