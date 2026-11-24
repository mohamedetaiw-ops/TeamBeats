using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>A proposed comparison: a challenger version or a challenger model, never both, and the share of people it gets.</summary>
public sealed record ComparisonRequest(Guid? AgentId, int? ChallengerVersion, string? ChallengerModel, double? SharePercent, string? Purpose);

public sealed record ComparisonDecisionRequest(bool? Approve, string? Note);

/// <summary>One arm's production scores in the comparison: per metric and language, with the number of samples that scored it.</summary>
public sealed record ArmResult(string Arm, int Samples, IReadOnlyList<ProductionMetricView> Metrics, double? LatencyP95Ms, double? AverageTokens, int Denials);

public sealed record ComparisonView(Guid Id, Guid AgentId, string Kind, int ChampionVersion, int ChallengerVersion, string? ChallengerModel, double SharePercent,
    string Status, string? Purpose, string GateStatus, Guid? GateRunId, Guid ProposedBy, DateTimeOffset ProposedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt,
    string? DecisionNote, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt, string? StopReason, IReadOnlyList<ArmResult> Arms, bool CanDecide, bool CanStop);

public sealed record ComparisonsView(Guid AgentId, bool Available, bool CanPropose, IReadOnlyList<ComparisonView> Comparisons);

public static class ComparisonStatuses
{
    public const string Proposed = "proposed";
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Rejected = "rejected";
}

public static class ComparisonArms
{
    public const string Champion = "champion";
    public const string Challenger = "challenger";
}

/// <summary>The rules of online comparisons (spec 034 US9, FR-026), kept pure so each is tested on its own.</summary>
public static partial class ComparisonRules
{
    public const double DefaultSharePercent = 10;
    public const double MinSharePercent = 1;
    public const double MaxSharePercent = 50;
    public const int MaxText = 500;
    public const int Listed = 20;

    /// <summary>
    /// Whether the person gets the challenger: a hash of the comparison and the person, as a share of 100. The same
    /// person is always on the same arm of one comparison, and a new comparison draws again.
    /// </summary>
    public static bool InChallenger(Guid comparisonId, Guid userId, double sharePercent)
    {
        var bytes = new byte[32];
        comparisonId.TryWriteBytes(bytes);
        userId.TryWriteBytes(bytes.AsSpan(16));
        var hash = SHA256.HashData(bytes);
        return BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue * 100 < sharePercent;
    }

    /// <summary>A model name as the AI Gateway routes it, for example <c>openai/gpt-4o-mini</c>.</summary>
    public static bool ValidModel(string model) => ModelPattern().IsMatch(model);

    [GeneratedRegex("^[a-z0-9][a-z0-9._:/-]{1,99}$")]
    private static partial Regex ModelPattern();
}

/// <summary>
/// Online A/B and champion and challenger comparisons of an agent (spec 034 US9 scenarios 2 and 3, FR-026). A designer
/// of the agent proposes one: another version that must have passed its release gate, or the version in force on
/// another model, which is first evaluated on that model with the full gate suites. The AI Policy Administrator, other
/// than the proposer, approves or rejects it; a comparison never runs without a passed challenger gate, so High risk
/// agents get no bypass. While it runs, the share of people it names gets the challenger, each always the same arm;
/// stopping it, or a new version taking over, returns everyone to the champion.
/// </summary>
public sealed class ComparisonService(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuthorizationProvisioning roles,
    IEntitlementService entitlements, IAgentCatalog agents, EvalAccess access, GateService gates, RunService runs, ProductionPolicyService policies,
    INotificationSender notifications, IAuditWriter audit, IClock clock, ILogger<ComparisonService> logger)
{
    public const string ProposedTemplate = "evaluation.comparison_proposed";

    private sealed record Row(Guid Id, Guid Agent, Guid Unit, string Kind, int Champion, int Challenger, string? Model, double Share, Guid? GateRun, string? Purpose,
        string Status, Guid ProposedBy, DateTimeOffset ProposedAt, Guid? DecidedBy, DateTimeOffset? DecidedAt, string? Note, DateTimeOffset? StartedAt,
        DateTimeOffset? StoppedAt, string? StopReason);

    private const string Columns =
        """
        select id, agent_id, org_unit_id, kind, champion_version, challenger_version, challenger_model, share_percent, gate_run_id, purpose, status, proposed_by,
          proposed_at, decided_by, decided_at, decision_note, started_at, stopped_at, stop_reason
        from evaluation.comparison
        """;

    private Task<List<Row>> QueryAsync(string where, object args, CancellationToken ct) =>
        session.QueryAsync(Columns + " " + where, args,
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.Get<string?>("challenger_model"),
                (double)r.GetDecimal(7), r.Get<Guid?>("gate_run_id"), r.Get<string?>("purpose"), r.GetString(10), r.GetGuid(11), r.GetFieldValue<DateTimeOffset>(12),
                r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"), r.Get<string?>("decision_note"), r.Get<DateTimeOffset?>("started_at"),
                r.Get<DateTimeOffset?>("stopped_at"), r.Get<string?>("stop_reason")), ct);

    private async Task<bool> MayManageAsync(CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<ComparisonsView> ListAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await access.ReadableAgentAsync(agentId, ct);
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsExperiments, ct))
        {
            return new ComparisonsView(agent.Id, false, false, []);
        }
        var rows = await QueryAsync("where agent_id = @agent order by proposed_at desc limit @limit", new { agent = agent.Id, limit = ComparisonRules.Listed }, ct);
        var views = new List<ComparisonView>();
        foreach (var row in rows)
        {
            views.Add(await ViewAsync(row, agent, ct));
        }
        return new ComparisonsView(agent.Id, true, await access.CanDesignAsync(agent, ct) && agent.CurrentVersion is not null, views);
    }

    public async Task<ComparisonView> GetAsync(Guid id, CancellationToken ct)
    {
        var (row, agent) = await LoadAsync(id, ct);
        return await ViewAsync(row, agent, ct);
    }

    private async Task<(Row Row, AgentFacts Agent)> LoadAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiEvalsExperiments, ct);
        var rows = await QueryAsync("where id = @id", new { id }, ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        return (rows[0], await access.ReadableAgentAsync(rows[0].Agent, ct));
    }

    public async Task<ComparisonView> ProposeAsync(ComparisonRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiEvalsExperiments, ct);
        var errors = new ValidationErrors();
        if (request.AgentId is null)
        {
            errors.Add("agentId", "validation.eval.comparison_agent");
        }
        var model = string.IsNullOrWhiteSpace(request.ChallengerModel) ? null : request.ChallengerModel.Trim();
        if ((request.ChallengerVersion is null) == (model is null))
        {
            errors.Add("challengerVersion", "validation.eval.comparison_challenger");
        }
        if (model is not null && !ComparisonRules.ValidModel(model))
        {
            errors.Add("challengerModel", "validation.eval.comparison_model");
        }
        var share = Math.Round(request.SharePercent ?? ComparisonRules.DefaultSharePercent, 2);
        if (double.IsNaN(share) || share is < ComparisonRules.MinSharePercent or > ComparisonRules.MaxSharePercent)
        {
            errors.Add("sharePercent", "validation.eval.comparison_share");
        }
        var purpose = string.IsNullOrWhiteSpace(request.Purpose) ? null : request.Purpose.Trim();
        if (purpose is { Length: > ComparisonRules.MaxText })
        {
            errors.Add("purpose", "validation.eval.comparison_purpose");
        }
        errors.ThrowIfAny();
        var agent = await access.DesignableAgentAsync(request.AgentId!.Value, ct);
        if (agent.Status != "published" || agent.CurrentVersion is not { } champion)
        {
            throw AppException.Conflict("conflict.eval.comparison_unpublished");
        }
        var challenger = request.ChallengerVersion ?? champion;
        if (model is null)
        {
            if (challenger == champion || await agents.VersionAsync(agent.Id, challenger, ct) is not { State: "in_review" or "published" })
            {
                throw AppException.Validation("challengerVersion", "validation.eval.comparison_version");
            }
        }
        if ((await QueryAsync("where agent_id = @agent and status in ('proposed', 'running')", new { agent = agent.Id }, ct)).Count > 0)
        {
            throw AppException.Conflict("conflict.eval.comparison_active");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        var user = context.RequireUser();
        // A model challenger is the version in force on another model: it gets its own gate, the full gate suites run on that model.
        var gateRun = model is null ? (Guid?)null : await runs.CreateAsync(agent, champion, RunTriggers.Comparison, ct, model: model);
        await session.ExecAsync(
            """
            insert into evaluation.comparison (tenant_id, id, agent_id, org_unit_id, kind, champion_version, challenger_version, challenger_model, share_percent,
              gate_run_id, purpose, status, proposed_by, proposed_at)
            values (platform.current_tenant_id(), @id, @agent, @unit, @kind, @champion, @challenger, @model, @share, @gateRun, @purpose, 'proposed', @user, @now)
            """, new
            {
                id, agent = agent.Id, unit = agent.OrgUnitId, kind = model is null ? "version" : "model", champion, challenger, model, share = (decimal)share, gateRun,
                purpose, user, now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalComparisonProposed, "eval_comparison", id.ToString(), TargetOrgUnitId: agent.OrgUnitId,
            Changes: new { agentId = agent.Id, champion, challenger, model, share }), ct);
        var approvers = (await roles.TenantHoldersAsync(Permissions.EvalPolicyManage, ct)).Where(u => u != user).ToHashSet();
        if (approvers.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(approvers, NotificationCategories.System, ProposedTemplate, $"/admin/evaluation/agents/{agent.Id}",
                DedupeKey: $"{ProposedTemplate}:{id:N}"), ct);
        }
        logger.LogInformation("eval.comparison_proposed tenant {Tenant} comparison {Comparison} agent {Agent}", context.TenantId, id, agent.Id);
        return await GetAsync(id, ct);
    }

    /// <summary>The challenger's gate: the version's release gate, or for a model challenger its gate run on that model.</summary>
    private async Task<string> GateStatusAsync(Row row, CancellationToken ct)
    {
        if (row.GateRun is not { } runId)
        {
            return (await gates.ResultAsync(row.Agent, row.Challenger, ct)).Status;
        }
        var run = await session.QueryAsync("select status, verdict from evaluation.run where id = @runId", new { runId },
            r => (Status: r.GetString(0), Verdict: r.Get<string?>("verdict")), ct);
        return run.Count == 0 ? GateStatuses.NotRun : run[0].Status switch
        {
            RunStatuses.Queued or RunStatuses.Running => GateStatuses.Running,
            RunStatuses.Cancelled => GateStatuses.Cancelled,
            _ => run[0].Verdict switch
            {
                EvalVerdicts.Passed => GateStatuses.Passed,
                EvalVerdicts.Incomplete => GateStatuses.Incomplete,
                _ => GateStatuses.Failed,
            },
        };
    }

    public async Task<ComparisonView> DecideAsync(Guid id, ComparisonDecisionRequest request, CancellationToken ct)
    {
        var (row, agent) = await LoadAsync(id, ct);
        await authz.DemandAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct);
        if (request.Approve is not { } approve)
        {
            throw AppException.Validation("approve", "validation.eval.comparison_decision");
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > ComparisonRules.MaxText })
        {
            throw AppException.Validation("note", "validation.eval.comparison_note");
        }
        if (row.Status != ComparisonStatuses.Proposed)
        {
            throw AppException.Conflict("conflict.eval.comparison_decided");
        }
        var user = context.RequireUser();
        if (row.ProposedBy == user)
        {
            throw AppException.Conflict("conflict.eval.comparison_own");
        }
        if (approve)
        {
            if (agent.CurrentVersion != row.Champion)
            {
                throw AppException.Conflict("conflict.eval.comparison_stale");
            }
            // Comparisons never bypass the gate (US9 scenario 3): only a passed challenger runs, whatever the agent's risk class.
            if (await GateStatusAsync(row, ct) != GateStatuses.Passed)
            {
                throw AppException.Conflict("conflict.eval.comparison_gate");
            }
            // Both arms are measured by production evaluation; without it a comparison would show nothing.
            if (!(await policies.LoadAsync(ct)).Enabled || !await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct))
            {
                throw AppException.Conflict("conflict.eval.comparison_no_sampling");
            }
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update evaluation.comparison set status = @status, decided_by = @user, decided_at = @now, decision_note = @note, started_at = case when @approve then @now end
            where id = @id
            """, new { id, status = approve ? ComparisonStatuses.Running : ComparisonStatuses.Rejected, user, now, note, approve }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalComparisonDecided, "eval_comparison", id.ToString(), TargetOrgUnitId: row.Unit,
            Changes: new { agentId = row.Agent, approved = approve, champion = row.Champion, challenger = row.Challenger, model = row.Model, share = row.Share, note }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Stops a running comparison, or withdraws a proposed one; everyone is on the champion from the next run.</summary>
    public async Task<ComparisonView> StopAsync(Guid id, CancellationToken ct)
    {
        var (row, agent) = await LoadAsync(id, ct);
        if (!await MayManageAsync(ct) && !await access.CanDesignAsync(agent, ct))
        {
            throw AppException.Denied();
        }
        if (row.Status is not (ComparisonStatuses.Proposed or ComparisonStatuses.Running))
        {
            throw AppException.Conflict("conflict.eval.comparison_stopped");
        }
        var reason = row.Status == ComparisonStatuses.Running ? "stopped" : "withdrawn";
        var now = clock.UtcNow;
        await session.ExecAsync("update evaluation.comparison set status = 'stopped', stopped_by = @user, stopped_at = @now, stop_reason = @reason where id = @id",
            new { id, user = context.RequireUser(), now, reason }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalComparisonStopped, "eval_comparison", id.ToString(), TargetOrgUnitId: row.Unit,
            Changes: new { agentId = row.Agent, reason }), ct);
        return await GetAsync(id, ct);
    }

    private async Task<ComparisonView> ViewAsync(Row row, AgentFacts agent, CancellationToken ct)
    {
        var manage = await MayManageAsync(ct);
        var active = row.Status is ComparisonStatuses.Proposed or ComparisonStatuses.Running;
        var user = context.RequireUser();
        return new ComparisonView(row.Id, row.Agent, row.Kind, row.Champion, row.Challenger, row.Model, row.Share, row.Status, row.Purpose, await GateStatusAsync(row, ct),
            row.GateRun, row.ProposedBy, row.ProposedAt, row.DecidedBy, row.DecidedAt, row.Note, row.StartedAt, row.StoppedAt, row.StopReason,
            row.StartedAt is null ? [] : await ArmsAsync(row.Id, ct), manage && row.Status == ComparisonStatuses.Proposed && row.ProposedBy != user,
            active && (manage || await access.CanDesignAsync(agent, ct)));
    }

    /// <summary>Both arms on the same production metrics, per language, from the samples taken while the comparison ran.</summary>
    private async Task<IReadOnlyList<ArmResult>> ArmsAsync(Guid id, CancellationToken ct)
    {
        var samples = await session.QueryAsync(
            "select arm, language, latency_ms, input_tokens + output_tokens, denials, metrics::text from evaluation.production_sample where comparison_id = @id and status = 'scored'",
            new { id }, r => (Arm: r.GetString(0), Language: r.GetString(1), Latency: r.GetInt32(2), Tokens: r.GetInt32(3), Denials: r.GetInt32(4),
                Metrics: JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(r.GetString(5), EvalJson.Options)!), ct);
        return [.. new[] { ComparisonArms.Champion, ComparisonArms.Challenger }.Select(arm =>
        {
            var mine = samples.Where(s => s.Arm == arm).ToList();
            var metrics = ProductionSampling.ContentMetrics
                .SelectMany(key => mine.GroupBy(s => s.Language).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
                {
                    var values = g.Select(s => s.Metrics.GetValueOrDefault(key)).Where(m => m is { State: CaseMetricStates.Scored, Value: not null })
                        .Select(m => m!.Value!.Value).ToList();
                    return new ProductionMetricView(key, g.Key, values.Count == 0 ? null : Math.Round(values.Average(), 4), values.Count);
                }))
                .Where(m => m.Samples > 0)
                .ToList();
            return new ArmResult(arm, mine.Count, metrics, RunScorer.P95(mine.Select(s => s.Latency)), mine.Count == 0 ? null : Math.Round(mine.Average(s => s.Tokens), 1),
                mine.Sum(s => s.Denials));
        })];
    }
}

/// <summary>
/// Gives a person's run its arm of the agent's running comparison (spec 034 US9 scenario 2). Only a comparison of the
/// version in force applies: once another version takes over, the comparison is stopped and everyone is on the champion.
/// </summary>
public sealed class ComparisonArmAllocator(IDbSession session, IEntitlementService entitlements, IClock clock) : IAgentArmAllocator
{
    public async Task<AgentArm?> ArmAsync(Guid agentId, int championVersion, Guid userId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsExperiments, ct))
        {
            return null;
        }
        var rows = await session.QueryAsync(
            "select id, champion_version, challenger_version, challenger_model, share_percent from evaluation.comparison where agent_id = @agentId and status = 'running'",
            new { agentId }, r => (Id: r.GetGuid(0), Champion: r.GetInt32(1), Challenger: r.GetInt32(2), Model: r.Get<string?>("challenger_model"), Share: (double)r.GetDecimal(4)),
            ct);
        if (rows.Count == 0)
        {
            return null;
        }
        var c = rows[0];
        if (c.Champion != championVersion)
        {
            await session.ExecAsync(
                "update evaluation.comparison set status = 'stopped', stopped_at = @now, stop_reason = 'champion_changed' where id = @id and status = 'running'",
                new { id = c.Id, now = clock.UtcNow }, ct);
            return null;
        }
        return ComparisonRules.InChallenger(c.Id, userId, c.Share)
            ? new AgentArm(c.Id, ComparisonArms.Challenger, c.Challenger, c.Model)
            : new AgentArm(c.Id, ComparisonArms.Champion, c.Champion, null);
    }
}
