using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

/// <summary>The models a call may use, in order, with the parameters after the tenant's limits.</summary>
internal sealed record RoutePlan(string UseCase, DataClassification Classification, IReadOnlyList<CatalogueEntry> Candidates, int? MaxTokens, double? Temperature)
{
    /// <summary>What the budget step decided, so the call can give back its requests or send threshold alerts when it ends.</summary>
    public BudgetDecision? Budget { get; init; }
}

/// <summary>A plan, or the reason there is none and, for a budget, when it resets.</summary>
internal sealed record PlanOutcome(RoutePlan? Plan, string? Denial, DateTimeOffset? ResetsAt = null)
{
    public void Deconstruct(out RoutePlan? plan, out string? denial)
    {
        plan = Plan;
        denial = Denial;
    }

    public static implicit operator PlanOutcome((RoutePlan? Plan, string? Denial) value) => new(value.Plan, value.Denial);
}

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
    UsageRecorder usage,
    BudgetGuard budgets,
    OrgPolicies orgPolicies,
    PrivateEndpoints endpoints,
    IDbSession session,
    IOrganizationHierarchy hierarchy,
    IClock clock,
    ILogger<AiGatewayService> logger) : IAiGateway, IAiClassificationPolicy
{
    /// <summary>Characters per token for the size estimate before a call; deliberately low so Arabic text is not underestimated.</summary>
    public const double CharsPerToken = 3.0;

    private readonly Dictionary<string, IModelProvider> byKey = providers.ToDictionary(p => p.Key, StringComparer.Ordinal);

    internal static int Estimate(IEnumerable<string> texts) => (int)Math.Ceiling(texts.Sum(t => t.Length) / CharsPerToken);

    private static int Estimate(AiRequest request) =>
        Estimate(request.Messages.Select(m => m.Content).Concat(request.Tools?.Select(t => t.Description + t.Parameters.GetRawText()) ?? []))
        + request.Messages.Sum(m => m.Attachments?.Count ?? 0) * AiAttachment.EstimatedTokens;

    /// <summary>Steps 1 to 6: who may call, where the content may go, which models are left, and the parameters after limits.</summary>
    internal async Task<PlanOutcome> PlanAsync(string useCase, string? model, DataClassification? classification, int inputTokens, int? maxTokens,
        double? temperature, IReadOnlyCollection<string> needs, string? modelUnderTest, bool platformCharged, CancellationToken ct, Guid? routeChangeId = null,
        BudgetSubject? subject = null, IReadOnlyCollection<Guid>? resourceUnits = null)
    {
        context.RequireTenant();
        if (!AiUseCases.All.Contains(useCase))
        {
            return (null, AiDenials.InvalidRequest);
        }

        // 1. Entitlement and the monthly request quota (spec 005 US3), counted from the call records of the metering step
        // and decided by the entitlement service with the tenant's enforcement. Judge calls of platform suites are charged
        // to the platform and do not count.
        if (!await entitlements.IsEnabledAsync(Capabilities.AiAssistant, ct))
        {
            return (null, AiDenials.NotInPackage);
        }
        if (!platformCharged && await entitlements.GetLimitAsync(Capabilities.AiAssistantMonthlyRequests, ct) is not null &&
            !(await entitlements.CheckUsageAsync(Capabilities.AiAssistantMonthlyRequests, await UsageRecorder.MonthCountAsync(session, clock.UtcNow, ct), 1, ct)).Allowed)
        {
            return (null, AiDenials.QuotaExhausted);
        }

        // 2. Budget (US5): the strictest action of every budget that matches the tenant, the acting unit and its parents, the
        // agent and the person. Judge calls of platform suites are not the tenant's spend. A request it counted is given
        // back when a later step refuses the call.
        BudgetDecision? budget = null;
        if (subject is not null && !platformCharged)
        {
            budget = await budgets.CheckAsync(subject, ct);
            if (budget.Denial is { } exhausted)
            {
                return new PlanOutcome(null, exhausted, budget.ResetsAt);
            }
        }
        var outcome = await RouteAsync(useCase, model, classification, inputTokens, maxTokens, temperature, needs, modelUnderTest, routeChangeId,
            AiUseCases.NotChat.Contains(useCase) ? null : budget?.Downgrade, subject?.Unit, resourceUnits, ct);
        if (outcome.Plan is null)
        {
            await budgets.ReleaseAsync(budget?.Reserved ?? []);
            return outcome;
        }
        return outcome with { Plan = outcome.Plan with { Budget = budget } };
    }

    /// <summary>
    /// Steps 3 to 6. <paramref name="downgrade"/> is the cheaper model a budget switched to (US5 scenario 1): it replaces
    /// the route and goes through the same policy steps. Embeddings are never downgraded.
    /// </summary>
    private async Task<PlanOutcome> RouteAsync(string useCase, string? model, DataClassification? classification, int inputTokens, int? maxTokens,
        double? temperature, IReadOnlyCollection<string> needs, string? modelUnderTest, Guid? routeChangeId, string? downgrade, Guid? unit,
        IReadOnlyCollection<Guid>? resourceUnits, CancellationToken ct)
    {
        // 3. Classification policy. A missing classification is a programming error, never defaulted to Public.
        if (classification is not { } level)
        {
            logger.LogError("AI call without a classification for use case {UseCase}; the calling module must classify its content", useCase);
            return (null, AiDenials.ClassificationMissing);
        }
        var policy = (await policies.EffectiveAsync(ct)).Document;
        if (routeChangeId is { } change)
        {
            // A release gate run (spec 034 US5 scenario 4) tries the proposed routes before they apply.
            if (!AiUseCases.Gated.Contains(useCase) || await policies.ProposedDocumentAsync(change, ct) is not { } proposed)
            {
                return (null, AiDenials.InvalidRequest);
            }
            policy = proposed;
        }

        // US7: the acting unit's policies, and those of every unit the request is about, only narrow the tenant policy.
        if (await orgPolicies.LimitsAsync(policy, unit, resourceUnits, ct) is { } limits)
        {
            if (level > OrgPolicies.Ceiling(limits))
            {
                return (null, AiDenials.ClassificationBlocked);
            }
            policy = OrgPolicies.Apply(policy, limits);
        }
        var snapshot = await catalogue.ForTenantAsync(ct);
        // Restricted content goes only to a tenant's own private endpoint whose ceiling is Restricted, never to a shared
        // provider under any setting (FR-007); Confidential needs a model the tenant allowed for it. A classification the
        // tenant keeps on private endpoints (sovereign routing, US6 scenario 2) has no shared provider among its candidates,
        // so a failure ends the call instead of falling back.
        var sovereign = policy.Sovereign?.Contains(Classifications.Key(level)) == true;
        bool Classified(CatalogueEntry e) => e.Ceiling >= level
            && (level < DataClassification.Confidential || (e.ConfidentialEligible && policy.Confidential.Contains(e.Model)))
            && (level < DataClassification.Restricted || e.Provider == PrivateEndpoints.ProviderKey)
            && (!sovereign || e.Provider == PrivateEndpoints.ProviderKey);
        if (level >= DataClassification.Confidential && !policy.Confidential.Any(m => snapshot.Models.TryGetValue(m, out var e) && Classified(e)))
        {
            return (null, AiDenials.ClassificationBlocked);
        }

        // 4. Allowlist and defaults. A named model must be allowed and in the catalogue; the use case route follows it.
        var route = policy.RouteOf(useCase, snapshot);
        List<string> chain;
        if (model is not null)
        {
            if (!policy.Allowlist.Contains(model) || !snapshot.Models.TryGetValue(model, out var named) || !named.Active ||
                !named.Can(ModelCapabilities.For(useCase)))
            {
                return (null, AiDenials.ModelNotAllowed);
            }
            chain = [model, .. route.Where(m => m != model)];
        }
        else
        {
            chain = [.. route];
        }
        if (downgrade is not null)
        {
            if (!policy.Allowlist.Contains(downgrade) || !snapshot.Models.TryGetValue(downgrade, out var cheaper) || !cheaper.Active)
            {
                return (null, AiDenials.BudgetExhausted);
            }
            chain = [downgrade];
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

    /// <summary>
    /// The highest level whose plan is not refused for its classification (steps 3 and 4). Other refusals, such as a
    /// model allowlist or an unreachable provider, are not about the content, so they do not lower the ceiling; a use case
    /// that cannot be called at all (not in the package, quota used up) has no ceiling.
    /// </summary>
    public async Task<DataClassification?> CeilingAsync(string useCase, CancellationToken ct = default)
    {
        string[] needs = [ModelCapabilities.For(useCase)];
        foreach (var level in new[] { DataClassification.Restricted, DataClassification.Confidential, DataClassification.Internal, DataClassification.Public })
        {
            var (_, denial) = await PlanAsync(useCase, null, level, 0, null, null, needs, null, platformCharged: false, ct);
            if (denial is AiDenials.InvalidRequest or AiDenials.NotInPackage or AiDenials.QuotaExhausted)
            {
                // Refused before the content is looked at: no call of this use case can be made now.
                return null;
            }
            if (denial != AiDenials.ClassificationBlocked)
            {
                return level;
            }
        }
        return null;
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
        if (request.Messages.Any(m => m.Attachments is { Count: > 0 }))
        {
            needs.Add(ModelCapabilities.Vision);
        }
        return [.. needs];
    }

    private BudgetSubject Subject(Guid? unit, Guid? agent) => new(unit, agent, context.ActorType == ActorType.User ? context.ActorId : null);

    private async Task<PlanOutcome> PlanChatAsync(AiRequest request, Guid? unit, CancellationToken ct)
    {
        if (request.Messages.Count == 0 || (request.UseCase == AiUseCases.Evaluation && request.Tools is { Count: > 0 }) || AiUseCases.NotChat.Contains(request.UseCase))
        {
            return (null, AiDenials.InvalidRequest);
        }
        return await PlanAsync(request.UseCase, request.Model, request.Classification, Estimate(request), request.MaxTokens, request.Temperature, Needs(request),
            request.ModelUnderTest, request.UseCase == AiUseCases.Evaluation && request.PlatformCharged, ct, request.RouteChangeId, Subject(unit, request.AgentId),
            request.ResourceUnitIds);
    }

    public async Task<AiResult> ChatAsync(AiRequest request, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var meta = Meta(request);
        meta = meta with { UnitId = await UnitAsync(meta, ct) };
        var outcome = await PlanChatAsync(request, meta.UnitId, ct);
        if (outcome.Plan is not { } plan)
        {
            await DeniedAsync(meta, outcome.Denial!, watch, ct);
            return AiResult.Denied(outcome.Denial!, outcome.ResetsAt);
        }

        // 7 and 8. Routing and fallback: one retry on a retryable error, then the next model that passed the same policy.
        foreach (var entry in plan.Candidates)
        {
            var provider = byKey[entry.Provider];
            var circuit = CircuitOf(entry);
            if (!circuits.Allow(circuit))
            {
                continue;
            }
            var (reachable, target) = await TargetAsync(entry, ct);
            if (!reachable)
            {
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                ProviderReply reply;
                try
                {
                    reply = await provider.ChatAsync(new ProviderChat(entry.Model, request.Messages, request.Tools, request.ResponseSchema, plan.MaxTokens, plan.Temperature, target), ct);
                }
                catch (ProviderException ex)
                {
                    circuits.Record(circuit, failed: true);
                    logger.LogWarning("AI provider {Provider} failed for {Model} (retryable {Retryable}): {Message}", provider.Key, entry.Model, ex.Retryable, ex.Message);
                    if (ex.Retryable && attempt == 0)
                    {
                        continue;
                    }
                    break;
                }
                circuits.Record(circuit, failed: false);

                // 9. Output checks: tool calls for tools not offered are dropped; structured output must match the schema.
                var calls = OfferedOnly(reply.ToolCalls, request.Tools, entry.Model);
                if (request.ResponseSchema is { } schema && calls.Count == 0 && !StructuredOutput.Matches(reply.Content, schema))
                {
                    logger.LogWarning("AI model {Model} returned output that does not match the response schema", entry.Model);
                    break;
                }
                var completion = new AiCompletion(reply.Content, calls, reply.Usage, entry.Model, provider.Key, entry != plan.Candidates[0], reply.Partial, reply.FinishReason);
                await ServedAsync(meta, plan, completion, watch, ct);
                return AiResult.Ok(completion);
            }
        }
        await budgets.ReleaseAsync(plan.Budget?.Reserved ?? []);
        await DeniedAsync(meta, AiDenials.ProviderUnavailable, watch, ct);
        return AiResult.Denied(AiDenials.ProviderUnavailable);
    }

    public async IAsyncEnumerable<AiStreamChunk> StreamChatAsync(AiRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var meta = Meta(request);
        meta = meta with { UnitId = await UnitAsync(meta, ct) };
        var outcome = await PlanChatAsync(request, meta.UnitId, ct);
        if (outcome.Plan is not { } plan)
        {
            await DeniedAsync(meta, outcome.Denial!, watch, ct);
            yield return new AiStreamChunk(null, AiResult.Denied(outcome.Denial!, outcome.ResetsAt));
            yield break;
        }
        var input = Estimate(request);
        foreach (var entry in plan.Candidates)
        {
            var provider = byKey[entry.Provider];
            var circuit = CircuitOf(entry);
            if (!circuits.Allow(circuit))
            {
                continue;
            }
            var (reachable, target) = await TargetAsync(entry, ct);
            if (!reachable)
            {
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var text = new StringBuilder();
                ProviderReply? final = null;
                ProviderException? failure = null;
                await using (var parts = provider.StreamAsync(new ProviderChat(entry.Model, request.Messages, request.Tools, request.ResponseSchema, plan.MaxTokens, plan.Temperature, target), ct)
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
                circuits.Record(circuit, failed: failure is not null);
                var fallback = entry != plan.Candidates[0];
                if (final is not null)
                {
                    var completion = new AiCompletion(text.ToString(), OfferedOnly(final.ToolCalls, request.Tools, entry.Model), final.Usage, entry.Model, provider.Key, fallback,
                        final.Partial, final.FinishReason);
                    await ServedAsync(meta, plan, completion, watch, ct);
                    yield return new AiStreamChunk(null, AiResult.Ok(completion));
                    yield break;
                }
                if (text.Length > 0)
                {
                    // Cut off after the first words: the caller gets what arrived, flagged partial, metered for the tokens received.
                    var partial = new AiCompletion(text.ToString(), [], new AiUsage(input, Estimate([text.ToString()])), entry.Model, provider.Key, fallback, true, "cut");
                    await ServedAsync(meta, plan, partial, watch, ct);
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
        await budgets.ReleaseAsync(plan.Budget?.Reserved ?? []);
        await DeniedAsync(meta, AiDenials.ProviderUnavailable, watch, ct);
        yield return new AiStreamChunk(null, AiResult.Denied(AiDenials.ProviderUnavailable));
    }

    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
    {
        if (request.Inputs.Count == 0)
        {
            return EmbeddingResult.Denied(AiDenials.InvalidRequest);
        }
        var served = await ServeAsync(AiUseCases.Embed, request.Model, request.Classification, request.ActingUnitId, Estimate(request.Inputs),
            async (provider, model, target) =>
            {
                var result = await provider.EmbedAsync(model, request.Inputs, ct, target);
                return (result, result.Usage);
            }, ct);
        return served.Result is { } r
            ? new EmbeddingResult(r.Vectors, r.Usage, served.Model, served.Provider, null)
            : EmbeddingResult.Denied(served.Denial!, served.ResetsAt);
    }

    /// <summary>Speech to text (spec 014 FR-026). The audio goes to the provider for this call and is never stored or logged.</summary>
    public async Task<TranscriptionResult> TranscribeAsync(TranscriptionRequest request, CancellationToken ct = default)
    {
        if (request.Audio.Length == 0)
        {
            return TranscriptionResult.Denied(AiDenials.InvalidRequest);
        }
        // About a token per 40 bytes of compressed speech is enough for budgets; the provider reports the real use.
        var served = await ServeAsync(AiUseCases.Transcribe, null, request.Classification, request.ActingUnitId, request.Audio.Length / 40,
            async (provider, model, target) =>
            {
                var result = await provider.TranscribeAsync(model, request.Audio, request.MediaType, request.Language, ct, target);
                return (result, result.Usage);
            }, ct);
        return served.Result is { } r
            ? new TranscriptionResult(r.Text, r.Usage, served.Model, served.Provider, null)
            : TranscriptionResult.Denied(served.Denial!, served.ResetsAt);
    }

    /// <summary>Text to speech (spec 014 FR-026).</summary>
    public async Task<SpeechResult> SpeakAsync(SpeechRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return SpeechResult.Denied(AiDenials.InvalidRequest);
        }
        var served = await ServeAsync(AiUseCases.Speak, null, request.Classification, request.ActingUnitId, Estimate([request.Text]),
            async (provider, model, target) =>
            {
                var result = await provider.SpeakAsync(model, request.Text, SpeechVoice, ct, target);
                return (result, result.Usage);
            }, ct);
        return served.Result is { } r
            ? new SpeechResult(r.Audio, r.MediaType, r.Usage, served.Model, served.Provider, null)
            : SpeechResult.Denied(served.Denial!, served.ResetsAt);
    }

    /// <summary>The voice answers are read in. One neutral voice for every tenant until a tenant setting is specified.</summary>
    public const string SpeechVoice = "alloy";

    private sealed record Served<T>(T? Result, string? Model, string? Provider, string? Denial, DateTimeOffset? ResetsAt) where T : class;

    /// <summary>
    /// The pipeline for a call that is not chat (embeddings, speech): the same policy, budget and routing steps, then the
    /// route's models in order with one retry for a retryable failure, as for chat.
    /// </summary>
    private async Task<Served<T>> ServeAsync<T>(string useCase, string? model, DataClassification? classification, Guid? actingUnit, int inputTokens,
        Func<IModelProvider, string, ProviderTarget?, Task<(T Result, AiUsage Usage)>> call, CancellationToken ct) where T : class
    {
        var watch = Stopwatch.StartNew();
        var meta = new CallMeta(useCase, model, classification, null, null, actingUnit, false);
        meta = meta with { UnitId = await UnitAsync(meta, ct) };
        var outcome = await PlanAsync(useCase, model, classification, inputTokens, null, null, [ModelCapabilities.For(useCase)], null,
            false, ct, subject: Subject(meta.UnitId, null));
        if (outcome.Plan is not { } plan)
        {
            await DeniedAsync(meta, outcome.Denial!, watch, ct);
            return new Served<T>(null, null, null, outcome.Denial, outcome.ResetsAt);
        }
        foreach (var entry in plan.Candidates)
        {
            var provider = byKey[entry.Provider];
            var circuit = CircuitOf(entry);
            if (!circuits.Allow(circuit))
            {
                continue;
            }
            var (reachable, target) = await TargetAsync(entry, ct);
            if (!reachable)
            {
                continue;
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    var (result, usage) = await call(provider, entry.Model, target);
                    circuits.Record(circuit, failed: false);
                    var completion = new AiCompletion(string.Empty, [], usage, entry.Model, provider.Key, entry != plan.Candidates[0], false, "stop");
                    await ServedAsync(meta, plan, completion, watch, ct);
                    return new Served<T>(result, entry.Model, provider.Key, null, null);
                }
                catch (ProviderException ex)
                {
                    circuits.Record(circuit, failed: true);
                    if (ex.Retryable && attempt == 0)
                    {
                        continue;
                    }
                    break;
                }
            }
        }
        await budgets.ReleaseAsync(plan.Budget?.Reserved ?? []);
        await DeniedAsync(meta, AiDenials.ProviderUnavailable, watch, ct);
        return new Served<T>(null, null, null, AiDenials.ProviderUnavailable, null);
    }

    /// <summary>One circuit per shared provider, and one per private endpoint, so one tenant's server being down never closes another's.</summary>
    private static string CircuitOf(CatalogueEntry e) => PrivateEndpoints.Parse(e.Model) is { } p ? $"{PrivateEndpoints.ProviderKey}/{p.Endpoint:N}" : e.Provider;

    /// <summary>For a private model, where its endpoint is and its key; a model whose endpoint cannot be reached now is skipped like a failed one.</summary>
    private async Task<(bool Reachable, ProviderTarget? Target)> TargetAsync(CatalogueEntry e, CancellationToken ct)
    {
        if (e.Provider != PrivateEndpoints.ProviderKey)
        {
            return (true, null);
        }
        var target = await endpoints.TargetAsync(e.Model, ct);
        if (target is null)
        {
            logger.LogWarning("AI private endpoint for {Model} is disabled or its key is missing; the model was skipped", e.Model);
        }
        return (target is not null, target);
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

    private sealed record CallMeta(string UseCase, string? ModelRequested, DataClassification? Classification, Guid? AgentId, int? AgentVersion, Guid? UnitId,
        bool PlatformCharged, IReadOnlyList<AiMessage>? Messages = null);

    private static CallMeta Meta(AiRequest r) => new(r.UseCase, r.Model, r.Classification, r.AgentId, r.AgentVersion, r.ActingUnitId,
        r.UseCase == AiUseCases.Evaluation && r.PlatformCharged, r.Messages);

    /// <summary>The acting unit (US7 scenario 3): the unit the calling module named for the resource, else the user's primary membership.</summary>
    private async Task<Guid?> UnitAsync(CallMeta meta, CancellationToken ct)
    {
        if (meta.UnitId is { } unit)
        {
            return unit;
        }
        if (context.ActorType != ActorType.User || context.ActorId is not { } user)
        {
            return null;
        }
        var memberships = await hierarchy.GetActiveMembershipsAsync(user, ct);
        return (memberships.FirstOrDefault(m => m.IsPrimary) ?? memberships.FirstOrDefault())?.OrgUnitId;
    }

    // 10. Metering and telemetry: one call record per request, metadata only (US8 scenario 1).
    private async Task ServedAsync(CallMeta meta, RoutePlan plan, AiCompletion completion, Stopwatch watch, CancellationToken ct)
    {
        var latency = (int)watch.Elapsed.TotalMilliseconds;
        var model = plan.Candidates.FirstOrDefault(c => c.Model == completion.Model);
        logger.LogInformation(
            "ai.call tenant {Tenant} user {User} use case {UseCase} model {Model} provider {Provider} input {Input} output {Output} latency {Latency} ms classification {Classification} fallback {Fallback} partial {Partial} correlation {Correlation}",
            context.TenantId, context.ActorId, plan.UseCase, completion.Model, completion.Provider, completion.Usage.InputTokens, completion.Usage.OutputTokens,
            latency, Classifications.Key(plan.Classification), completion.FallbackUsed, completion.Partial, Correlation.Id);
        await usage.RecordAsync(new CallRecord(meta.UseCase, await UnitAsync(meta, ct), meta.AgentId, meta.AgentVersion, completion.Model, meta.ModelRequested,
            completion.Provider, completion.Usage, latency, UsageRecorder.Cost(model, completion.Usage), null, meta.Classification, completion.FallbackUsed,
            completion.Partial, meta.PlatformCharged, meta.Messages, completion.Content), ct);
        if (plan.Budget is { } budget)
        {
            await budgets.AlertAsync(budget);
        }
    }

    private async Task DeniedAsync(CallMeta meta, string reason, Stopwatch watch, CancellationToken ct)
    {
        logger.LogWarning("ai.call_denied tenant {Tenant} user {User} use case {UseCase} model requested {Model} classification {Classification} reason {Reason} correlation {Correlation}",
            context.TenantId, context.ActorId, meta.UseCase, meta.ModelRequested, meta.Classification is { } c ? Classifications.Key(c) : null, reason, Correlation.Id);
        if (context.TenantId is null)
        {
            return;
        }
        await usage.RecordAsync(new CallRecord(meta.UseCase, await UnitAsync(meta, ct), meta.AgentId, meta.AgentVersion, null, meta.ModelRequested, null, AiUsage.None,
            (int)watch.Elapsed.TotalMilliseconds, 0, reason, meta.Classification, false, false, meta.PlatformCharged), ct);
    }
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
