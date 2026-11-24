using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record ProductionPolicyView(bool Available, bool Enabled, double RatePercent, int DailyCap, int RetentionDays, DateTimeOffset? UpdatedAt, bool CanEdit);

/// <summary>A policy change; a field left out keeps its value.</summary>
public sealed record ProductionPolicyRequest(bool? Enabled, double? RatePercent, int? DailyCap, int? RetentionDays);

public sealed record ProductionOptOutView(bool Available, bool OptedOut);

public sealed record ProductionOptOutRequest(bool? OptedOut);

/// <summary>A metric of an agent's production samples in the window, per language, with the number of samples that scored it.</summary>
public sealed record ProductionMetricView(string Key, string Locale, double? Value, int Samples);

/// <summary>
/// One sampled run as evaluation keeps it: scores, verdicts and metadata, never the person. Content is shown only while
/// it is retained, and only to someone who may read the agent's evaluation.
/// </summary>
public sealed record ProductionSampleView(Guid Id, Guid AgentRunId, int AgentVersion, string Language, string Classification, string Mode, string Status,
    string RunStatus, string? Outcome, int LatencyMs, int Tokens, int ToolCalls, int Denials, IReadOnlyDictionary<string, CaseMetric> Metrics,
    DateTimeOffset SampledAt, DateTimeOffset? ScoredAt, ProductionContent? Content, DateTimeOffset? ContentExpiresAt);

public sealed record ProductionSummary(Guid AgentId, int WindowDays, int Samples, int Scored, IReadOnlyList<ProductionMetricView> Metrics,
    double? LatencyP95Ms, double? AverageTokens, int Denials, IReadOnlyList<ProductionSampleView> Recent);

/// <summary>What a sample holds until it is scored, and longer only under the tenant's retention.</summary>
public sealed record ProductionContent(string Question, string? Answer, string Evidence, IReadOnlyList<EvalRunStep> Steps, IReadOnlyList<EvalRunSource> Read,
    IReadOnlyList<EvalRunSource> Cited, string? Escalation);

/// <summary>
/// The rules of production sampling (spec 034 US7, FR-023), kept pure so each is tested on its own. A run is chosen by a
/// hash of its ID, so the decision is stable and needs no random state; Restricted runs give metadata metrics only;
/// content outlives scoring only when retention is on and the run was Public or Internal.
/// </summary>
public static class ProductionSampling
{
    public const double DefaultRatePercent = 2;
    public const int DefaultDailyCap = 1000;
    public const int MaxRetentionDays = 30;
    public const int WindowDays = 7;
    public const int RecentSamples = 20;

    public const string Full = "full";
    public const string Deterministic = "deterministic";
    public const string Metadata = "metadata";

    /// <summary>The reference-free metrics of a sampled run with content.</summary>
    public static readonly IReadOnlyList<string> ContentMetrics = ["groundedness", "relevance", "citation_correctness", "refusal_correctness", "unauthorized_attempts"];

    /// <summary>The metrics read from metadata alone, for every sample.</summary>
    public static readonly IReadOnlyList<string> MetadataMetrics = ["latency_ms", "tokens", "tool_calls", "denials"];

    /// <summary>Whether the run falls in the sample: the first four bytes of a hash of its ID, as a share of 100.</summary>
    public static bool Selected(Guid runId, double ratePercent)
    {
        if (ratePercent <= 0)
        {
            return false;
        }
        var hash = SHA256.HashData(runId.ToByteArray());
        var share = BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue * 100;
        return share < ratePercent;
    }

    public static string ModeFor(string classification) => classification == "restricted" ? Metadata : Full;

    public static bool Retains(string classification, int retentionDays) => retentionDays > 0 && classification is "public" or "internal";

    /// <summary>
    /// The reference-free scores of a run with content. Groundedness and relevance come from the judge; citation correctness
    /// asks that an answer built on sources cites them; a refusal is correct when the run found nothing to answer from;
    /// the trajectory check fails on any attempt the gateway refused as unauthorized.
    /// </summary>
    public static Dictionary<string, CaseMetric> Score(EvalRunRecord run, IReadOnlyDictionary<string, JudgeVerdict?> verdicts)
    {
        var metrics = CaseEvaluators.Evaluate(["groundedness", "relevance", "citation_correctness", "unauthorized_attempts"], new CaseExpectations(), run.Language, run, verdicts);
        var answered = run.Status == "completed" && run.Answer is not null;
        if (answered)
        {
            metrics["refusal_correctness"] = new CaseMetric(CaseMetricStates.Scored, !CaseEvaluators.Refused(run) || run.Read.Count == 0 ? 1 : 0);
        }
        return metrics;
    }
}

/// <summary>
/// The tenant's production evaluation policy and each person's opt-out (spec 034 US7 scenarios 1, 4 and 5). The policy is
/// set by the AI Policy Administrator; anyone with <c>eval.view</c> across the tenant reads it. A person may always
/// change their own opt-out, even where the package has no production evaluation, so a later upgrade honors it.
/// </summary>
public sealed class ProductionPolicyService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAuditWriter audit, IClock clock)
{
    internal sealed record Policy(bool Enabled, double RatePercent, int DailyCap, int RetentionDays, DateTimeOffset? UpdatedAt);

    internal async Task<Policy> LoadAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync("select enabled, rate_percent, daily_cap, retention_days, updated_at from evaluation.production_policy", null,
            r => new Policy(r.GetBoolean(0), (double)r.GetDecimal(1), r.GetInt32(2), r.GetInt32(3), r.GetFieldValue<DateTimeOffset>(4)), ct);
        return rows.Count == 0 ? new Policy(true, ProductionSampling.DefaultRatePercent, ProductionSampling.DefaultDailyCap, 0, null) : rows[0];
    }

    public async Task<ProductionPolicyView> GetAsync(CancellationToken ct)
    {
        var manage = (await authz.CheckAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct)).Allowed;
        if (!manage && !(await authz.CheckAsync(Permissions.EvalView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var available = await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct);
        var p = await LoadAsync(ct);
        return new ProductionPolicyView(available, p.Enabled, p.RatePercent, p.DailyCap, p.RetentionDays, p.UpdatedAt, manage && available);
    }

    public async Task<ProductionPolicyView> SaveAsync(ProductionPolicyRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.AiEvalsProduction, ct);
        var current = await LoadAsync(ct);
        var next = new Policy(request.Enabled ?? current.Enabled, Math.Round(request.RatePercent ?? current.RatePercent, 2), request.DailyCap ?? current.DailyCap,
            request.RetentionDays ?? current.RetentionDays, null);
        var errors = new ValidationErrors();
        if (next.RatePercent is < 0 or > 100 || double.IsNaN(next.RatePercent))
        {
            errors.Add("ratePercent", "validation.eval.production_rate");
        }
        if (next.DailyCap is < 1 or > ProductionSampling.DefaultDailyCap)
        {
            errors.Add("dailyCap", "validation.eval.production_cap");
        }
        if (next.RetentionDays is < 0 or > ProductionSampling.MaxRetentionDays)
        {
            errors.Add("retentionDays", "validation.eval.production_retention");
        }
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into evaluation.production_policy (tenant_id, enabled, rate_percent, daily_cap, retention_days, updated_by, updated_at)
            values (platform.current_tenant_id(), @enabled, @rate, @cap, @days, @user, @now)
            on conflict (tenant_id) do update set enabled = excluded.enabled, rate_percent = excluded.rate_percent, daily_cap = excluded.daily_cap,
              retention_days = excluded.retention_days, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { enabled = next.Enabled, rate = (decimal)next.RatePercent, cap = next.DailyCap, days = next.RetentionDays, user = context.RequireUser(), now }, ct);
        if (next.RetentionDays < current.RetentionDays)
        {
            // A shorter retention applies to content already kept.
            await session.ExecAsync(
                """
                update evaluation.production_sample set content = case when @days = 0 then null else content end,
                  content_expires_at = case when @days = 0 then null else least(content_expires_at, sampled_at + make_interval(days => @days)) end
                where content is not null and status = 'scored'
                """, new { days = next.RetentionDays }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalPolicyChanged, "eval_production_policy", null,
            Changes: new
            {
                level = "production",
                from = new { current.Enabled, current.RatePercent, current.DailyCap, current.RetentionDays },
                to = new { next.Enabled, next.RatePercent, next.DailyCap, next.RetentionDays },
            }), ct);
        return new ProductionPolicyView(true, next.Enabled, next.RatePercent, next.DailyCap, next.RetentionDays, now, true);
    }

    public async Task<ProductionOptOutView> OptOutAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        var opted = await session.ScalarAsync<long>("select count(*) from evaluation.production_opt_out where user_id = @user", new { user }, ct) > 0;
        return new ProductionOptOutView(await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct), opted);
    }

    public async Task<ProductionOptOutView> SetOptOutAsync(ProductionOptOutRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (request.OptedOut is not { } opted)
        {
            throw AppException.Validation("optedOut", "validation.required");
        }
        if (opted)
        {
            await session.ExecAsync(
                "insert into evaluation.production_opt_out (tenant_id, user_id, at) values (platform.current_tenant_id(), @user, @now) on conflict do nothing",
                new { user, now = clock.UtcNow }, ct);
        }
        else
        {
            await session.ExecAsync("delete from evaluation.production_opt_out where user_id = @user", new { user }, ct);
        }
        return new ProductionOptOutView(await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct), opted);
    }
}

/// <summary>
/// Decides, as a run ends, whether production evaluation samples it (spec 034 US7): the package must include it, the
/// tenant must not have turned it off, the person must not have opted out, the run's hash must fall in the rate and the
/// agent must be under its daily cap. Only then is the content read, once, and kept until the worker scores it;
/// Restricted runs keep no content at all. Scoring goes through the outbox, so the person's request does not wait for it.
/// </summary>
public sealed class ProductionSampler(IDbSession session, IEntitlementService entitlements, ProductionPolicyService policies, IOutbox outbox, IClock clock)
    : IProductionSampler
{
    public async Task OfferAsync(ProductionRunFacts facts, Func<CancellationToken, Task<ProductionRunContent>> content, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct))
        {
            return;
        }
        var policy = await policies.LoadAsync(ct);
        if (!policy.Enabled || !ProductionSampling.Selected(facts.RunId, policy.RatePercent))
        {
            return;
        }
        if (await session.ScalarAsync<long>("select count(*) from evaluation.production_opt_out where user_id = @user", new { user = facts.UserId }, ct) > 0)
        {
            return;
        }
        var now = clock.UtcNow;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var counted = await session.ScalarAsync<int?>(
            """
            insert into evaluation.production_day (tenant_id, agent_id, day, sampled) values (platform.current_tenant_id(), @agent, @day, 1)
            on conflict (tenant_id, agent_id, day) do update set sampled = evaluation.production_day.sampled + 1
              where evaluation.production_day.sampled < @cap
            returning sampled
            """, new { agent = facts.AgentId, day, cap = policy.DailyCap }, ct);
        if (counted is null)
        {
            return;
        }
        var mode = ProductionSampling.ModeFor(facts.Classification);
        ProductionContent? kept = null;
        if (mode == ProductionSampling.Full)
        {
            var c = await content(ct);
            kept = new ProductionContent(c.Question, c.Answer, c.Evidence, c.Steps, c.Read, c.Cited, c.Escalation);
        }
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into evaluation.production_sample (tenant_id, id, agent_run_id, agent_id, agent_version, org_unit_id, language, classification, mode, status,
              run_status, outcome, model, input_tokens, output_tokens, latency_ms, tool_calls, denials, content, sampled_at)
            values (platform.current_tenant_id(), @id, @run, @agent, @version, @unit, @language, @classification, @mode, 'queued',
              @status, @outcome, @model, @in, @out, @latency, @calls, @denials, @content, @now)
            on conflict (tenant_id, agent_run_id) do nothing
            """, new
            {
                id, run = facts.RunId, agent = facts.AgentId, version = facts.AgentVersion, unit = facts.OrgUnitId, language = facts.Language,
                classification = facts.Classification, mode, status = facts.Status, outcome = facts.Outcome, model = facts.Model, @in = facts.InputTokens,
                @out = facts.OutputTokens, latency = facts.DurationMs, calls = facts.ToolCalls, denials = facts.Denials,
                content = kept is null ? null : Jsonb.From(kept), now,
            }, ct);
        await outbox.EnqueueAsync(new ProductionSampleCaptured(id), ct);
    }
}

/// <summary>
/// Scores a sampled run on the workers (spec 034 US7): the judge goes through the AI Gateway under the run's own
/// classification, so Confidential content is judged only by a model eligible for it, and without one only the
/// deterministic metrics count. After scoring, the content is dropped unless the tenant retains samples and the run was
/// Public or Internal.
/// </summary>
public sealed class ProductionScorer(IDbSession session, EvaluationJudge judge, ProductionPolicyService policies, IClock clock, ILogger<ProductionScorer> logger)
    : IMessageHandler<ProductionSampleCaptured>
{
    public async Task HandleAsync(ProductionSampleCaptured message, CancellationToken ct)
    {
        using var span = EvalTelemetry.Start("eval.production", message.SampleId);
        var rows = await session.QueryAsync(
            """
            select agent_run_id, language, classification, mode, run_status, outcome, model, input_tokens, output_tokens, latency_ms, tool_calls, denials,
              content::text, sampled_at, status
            from evaluation.production_sample where id = @id
            """, new { id = message.SampleId },
            r => new
            {
                Run = r.GetGuid(0), Language = r.GetString(1), Classification = r.GetString(2), Mode = r.GetString(3), Status = r.GetString(4),
                Outcome = r.Get<string?>("outcome"), Model = r.Get<string?>("model"), In = r.GetInt32(7), Out = r.GetInt32(8), Latency = r.GetInt32(9),
                Calls = r.GetInt32(10), Denials = r.GetInt32(11), Content = r.Get<string?>("content"), SampledAt = r.GetFieldValue<DateTimeOffset>(13),
                Done = r.GetString(14) == "scored",
            }, ct);
        if (rows.Count == 0 || rows[0].Done)
        {
            return;
        }
        var s = rows[0];
        var metrics = new Dictionary<string, CaseMetric>(StringComparer.Ordinal)
        {
            ["latency_ms"] = new(CaseMetricStates.Scored, s.Latency),
            ["tokens"] = new(CaseMetricStates.Scored, s.In + s.Out),
            ["tool_calls"] = new(CaseMetricStates.Scored, s.Calls),
            ["denials"] = new(CaseMetricStates.Scored, s.Denials),
        };
        var mode = s.Mode;
        if (s.Content is { } text && JsonSerializer.Deserialize<ProductionContent>(text, EvalJson.Options) is { } content)
        {
            var record = new EvalRunRecord(s.Run, s.Status, s.Outcome, null, content.Answer, s.Language, s.Model, s.In, s.Out, s.Latency, content.Steps, content.Read,
                content.Cited, content.Escalation);
            var classification = Enum.TryParse<DataClassification>(s.Classification, ignoreCase: true, out var level) ? level : DataClassification.Confidential;
            var verdicts = await judge.JudgeAsync(["groundedness", "relevance"], s.Language, content.Question, new CaseExpectations(), record, classification, ct,
                content.Evidence.Length == 0 ? null : content.Evidence);
            foreach (var (key, value) in ProductionSampling.Score(record, verdicts))
            {
                metrics[key] = value;
            }
            if (record.Answer is not null && verdicts.Count > 0 && verdicts.Values.All(v => v is null))
            {
                mode = ProductionSampling.Deterministic;
            }
        }
        var policy = await policies.LoadAsync(ct);
        var keep = s.Content is not null && ProductionSampling.Retains(s.Classification, policy.RetentionDays);
        await session.ExecAsync(
            """
            update evaluation.production_sample set status = 'scored', mode = @mode, metrics = @metrics, scored_at = @now,
              content = case when @keep then content else null end,
              content_expires_at = case when @keep then @expires else null end
            where id = @id
            """, new
            {
                id = message.SampleId, mode, metrics = Jsonb.From(metrics), now = clock.UtcNow, keep,
                expires = s.SampledAt.AddDays(policy.RetentionDays),
            }, ct);
        logger.LogDebug("Production sample {SampleId} scored in mode {Mode}", message.SampleId, mode);
    }
}

/// <summary>
/// Production results of an agent (spec 034 US7): the window's samples, each metric per language with its sample count,
/// and the latest samples. Read by whoever may read the agent's evaluation; it names no person.
/// </summary>
public sealed class ProductionResults(IDbSession session, EvalAccess access, IEntitlementService entitlements, IClock clock)
{
    public async Task<ProductionSummary> ForAgentAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await access.ReadableAgentAsync(agentId, ct);
        await entitlements.DemandAsync(Capabilities.AiEvalsProduction, ct);
        var since = clock.UtcNow.AddDays(-ProductionSampling.WindowDays);
        var samples = await session.QueryAsync(
            """
            select id, agent_run_id, agent_version, language, classification, mode, status, run_status, outcome, latency_ms, input_tokens + output_tokens,
              tool_calls, denials, metrics::text, sampled_at, scored_at, content::text, content_expires_at
            from evaluation.production_sample where agent_id = @agent and sampled_at >= @since order by sampled_at desc
            """, new { agent = agent.Id, since },
            r => new ProductionSampleView(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7),
                r.Get<string?>("outcome"), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12),
                JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(r.GetString(13), EvalJson.Options)!, r.GetFieldValue<DateTimeOffset>(14),
                r.Get<DateTimeOffset?>("scored_at"),
                r.GetString(6) == "scored" && r.Get<string?>("content") is { } c ? JsonSerializer.Deserialize<ProductionContent>(c, EvalJson.Options) : null,
                r.Get<DateTimeOffset?>("content_expires_at")), ct);
        var scored = samples.Where(x => x.Status == "scored").ToList();
        var metrics = ProductionSampling.ContentMetrics
            .SelectMany(key => scored.GroupBy(x => x.Language).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
            {
                var values = g.Select(x => x.Metrics.GetValueOrDefault(key)).Where(m => m is { State: CaseMetricStates.Scored, Value: not null }).Select(m => m!.Value!.Value).ToList();
                return new ProductionMetricView(key, g.Key, values.Count == 0 ? null : Math.Round(values.Average(), 4), values.Count);
            }))
            .Where(m => m.Samples > 0)
            .ToList();
        return new ProductionSummary(agent.Id, ProductionSampling.WindowDays, samples.Count, scored.Count, metrics, RunScorer.P95(scored.Select(x => x.LatencyMs)),
            scored.Count == 0 ? null : Math.Round(scored.Average(x => x.Tokens), 1), scored.Sum(x => x.Denials), samples.Take(ProductionSampling.RecentSamples).ToList());
    }
}

/// <summary>Drops retained sample content past its time and day counters older than a week (spec 034 section 8).</summary>
public sealed class ProductionRetention(IDbSession session)
{
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync("delete from evaluation.production_day where day < @day", new { day = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-7) }, ct);
        return await session.ExecAsync(
            "update evaluation.production_sample set content = null, content_expires_at = null where content is not null and content_expires_at <= @now",
            new { now }, ct);
    }
}

/// <summary>Runs <see cref="ProductionRetention"/> for every active tenant every hour.</summary>
public sealed class ProductionRetentionWorker(IServiceScopeFactory scopes, IClock clock, ILogger<ProductionRetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            IReadOnlyList<TenantInfo> tenants = [];
            try
            {
                await using var listScope = scopes.CreateAsyncScope();
                tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Production evaluation retention could not list tenants");
            }
            foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                    context.SetTenant(tenant.Id);
                    context.SetActor(ActorType.System, null);
                    await scope.ServiceProvider.GetRequiredService<ProductionRetention>().PurgeAsync(clock.UtcNow, stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Production evaluation retention failed for tenant {TenantId}", tenant.Id);
                }
            }
            await Task.Delay(Tick, stoppingToken);
        }
    }
}
