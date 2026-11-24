using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>What the judge said about one metric of a production sample. The reason is kept only while the content is.</summary>
public sealed record StoredVerdict(string Verdict, double Score, string? Reason);

/// <summary>A judge verdict as a reviewer decided it: the judge's verdict and the reviewer's.</summary>
public sealed record ReviewedVerdict(string Judge, string Reviewer)
{
    public bool Overturned => Judge != Reviewer;
}

public sealed record ReviewItemSummary(Guid Id, Guid AgentId, IReadOnlyDictionary<string, string>? AgentName, int AgentVersion, string Language,
    IReadOnlyList<string> Reasons, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ReviewedAt);

public sealed record ReviewQueueView(bool Available, IReadOnlyList<ReviewItemSummary> Items);

/// <summary>
/// One flagged sample for its reviewer. <see cref="Content"/> is shown only while it is retained and only when the
/// reviewer may read every source the run read; otherwise <see cref="ContentWithheld"/> says why.
/// </summary>
public sealed record ReviewItemView(Guid Id, Guid SampleId, Guid AgentId, IReadOnlyDictionary<string, string>? AgentName, int AgentVersion, string Language,
    string Classification, string Mode, string? Outcome, IReadOnlyList<string> Reasons, string Status, IReadOnlyDictionary<string, CaseMetric> Metrics,
    IReadOnlyDictionary<string, StoredVerdict> Judge, IReadOnlyDictionary<string, ReviewedVerdict> Verdicts, string? Note, Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt, ProductionContent? Content, string? ContentWithheld, Guid? PromotedDatasetId, int? PromotedVersion,
    IReadOnlyList<DatasetSummary> Datasets, bool CanPromote, DateTimeOffset CreatedAt);

/// <summary>The reviewer's decision: a verdict per judged metric (left out keeps the judge's) and an optional note.</summary>
public sealed record ReviewDecisionRequest(Dictionary<string, string>? Verdicts, string? Note);

/// <summary>Promotes the reviewed case to a dataset of the agent; the texts given are redacted again before they are kept.</summary>
public sealed record ReviewPromoteRequest(Guid? DatasetId, string? Input, string? ExpectedAnswer, List<string>? KeyFacts, string? Severity);

public static class ReviewReasons
{
    public const string LowScore = "low_score";
    public const string NegativeFeedback = "negative_feedback";
    public const string JudgeUncertain = "judge_uncertain";
    public const string PolicyDenial = "policy_denial";
    public static readonly IReadOnlyList<string> All = [LowScore, NegativeFeedback, JudgeUncertain, PolicyDenial];
}

public static class ReviewStatuses
{
    public const string Open = "open";
    public const string Reviewed = "reviewed";
}

public static class ContentWithheld
{
    public const string NotRetained = "not_retained";
    public const string NotPermitted = "not_permitted";
}

/// <summary>
/// The rules of the review queue (spec 034 US8 scenarios 2 and 3), kept pure so each is tested on its own: why a scored
/// sample is flagged, and how text is redacted before it becomes a case or a calibration label.
/// </summary>
public static partial class ReviewRules
{
    /// <summary>A judge score in this band is a judge that was not sure, whatever verdict it gave.</summary>
    public const double UncertainLow = 0.35;
    public const double UncertainHigh = 0.65;

    /// <summary>The judged metrics a reviewer can confirm or overturn.</summary>
    public static readonly IReadOnlyList<string> JudgedMetrics = ["groundedness", "relevance"];

    public const string ReviewedTag = "production_review";
    public const int MaxNote = 1000;
    public const int ListLimit = 200;
    public const int TaskLimit = 50;

    public static IReadOnlyList<string> Reasons(IReadOnlyDictionary<string, CaseMetric> metrics, IReadOnlyDictionary<string, StoredVerdict> judge, int denials,
        string? outcome, bool negativeFeedback)
    {
        var reasons = new List<string>();
        var low = ProductionSampling.ContentMetrics.Any(key =>
            MetricCatalog.Find(key) is { HigherIsBetter: true } && metrics.TryGetValue(key, out var m) && m is { State: CaseMetricStates.Scored, Value: < 0.5 });
        if (low)
        {
            reasons.Add(ReviewReasons.LowScore);
        }
        if (negativeFeedback)
        {
            reasons.Add(ReviewReasons.NegativeFeedback);
        }
        if (judge.Values.Any(v => v.Score is >= UncertainLow and <= UncertainHigh || (v.Verdict == "pass") != (v.Score >= 0.5)))
        {
            reasons.Add(ReviewReasons.JudgeUncertain);
        }
        var attempts = metrics.TryGetValue("unauthorized_attempts", out var a) && a is { State: CaseMetricStates.Scored, Value: > 0 };
        if (denials > 0 || attempts || outcome == "policy_blocked")
        {
            reasons.Add(ReviewReasons.PolicyDenial);
        }
        return reasons;
    }

    /// <summary>
    /// Removes personal data a real conversation can carry before it is kept as a case or a label (US1 scenario 5): email
    /// addresses, phone and ID numbers (runs of six or more digits, spaces and dashes allowed) and IBANs. The reviewer
    /// edits anything else before promoting.
    /// </summary>
    public static string Redact(string text)
    {
        var redacted = Email().Replace(text, "[email]");
        redacted = Iban().Replace(redacted, "[iban]");
        return Number().Replace(redacted, "[number]");
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){3,7}(?: ?[A-Z0-9]{1,4})?\b")]
    private static partial Regex Iban();

    [GeneratedRegex(@"\+?\d(?:[\s\-]?\d){5,}")]
    private static partial Regex Number();
}

/// <summary>
/// Puts flagged production samples in the review queue (spec 034 US8 scenario 2): when a sample is scored and when the
/// person later says the answer did not help. One item per sample; reasons found later are added while it is open.
/// </summary>
public sealed class ReviewQueue(IDbSession session, IOutbox outbox, IClock clock) : IMessageHandler<AgentRunFeedbackGiven>
{
    public async Task HandleAsync(AgentRunFeedbackGiven message, CancellationToken ct)
    {
        if (message.Helpful)
        {
            return;
        }
        var rows = await session.QueryAsync(
            "update evaluation.production_sample set negative_feedback = true where agent_run_id = @run returning id, status",
            new { run = message.RunId }, r => (Id: r.GetGuid(0), Scored: r.GetString(1) == "scored"), ct);
        // A sample still queued is flagged when it is scored.
        if (rows.Count == 1 && rows[0].Scored)
        {
            await FlagAsync(rows[0].Id, ct);
        }
    }

    public async Task FlagAsync(Guid sampleId, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select agent_id, agent_version, org_unit_id, language, outcome, denials, metrics::text, judge::text, negative_feedback
            from evaluation.production_sample where id = @id and status = 'scored'
            """, new { id = sampleId },
            r => new
            {
                Agent = r.GetGuid(0), Version = r.GetInt32(1), Unit = r.GetGuid(2), Language = r.GetString(3), Outcome = r.Get<string?>("outcome"),
                Denials = r.GetInt32(5), Metrics = JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(r.GetString(6), EvalJson.Options)!,
                Judge = JsonSerializer.Deserialize<Dictionary<string, StoredVerdict>>(r.GetString(7), EvalJson.Options)!, Negative = r.GetBoolean(8),
            }, ct);
        if (rows.Count == 0)
        {
            return;
        }
        var s = rows[0];
        var reasons = ReviewRules.Reasons(s.Metrics, s.Judge, s.Denials, s.Outcome, s.Negative).ToArray();
        if (reasons.Length == 0)
        {
            return;
        }
        var now = clock.UtcNow;
        var id = Ids.New();
        var created = await session.QueryAsync(
            """
            insert into evaluation.review_item (tenant_id, id, sample_id, agent_id, agent_version, org_unit_id, language, reasons, status, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @sampleId, @agent, @version, @unit, @language, @reasons, 'open', @now, @now)
            on conflict (tenant_id, sample_id) do update
              set reasons = array(select distinct unnest(evaluation.review_item.reasons || excluded.reasons) order by 1), updated_at = excluded.updated_at
              where evaluation.review_item.status = 'open'
            returning id, (xmax = 0)
            """, new { id, sampleId, agent = s.Agent, version = s.Version, unit = s.Unit, language = s.Language, reasons, now },
            r => (Id: r.GetGuid(0), Inserted: r.GetBoolean(1)), ct);
        if (created is [{ Inserted: true } item])
        {
            await outbox.EnqueueAsync(new EvaluationReviewRequested(item.Id, s.Agent, reasons), ct);
        }
    }
}

/// <summary>
/// The review queue for evaluation reviewers (spec 034 US8 scenarios 2 and 3, FR-025). A reviewer sees and decides the
/// items of agents in units where they hold <c>eval.review</c>; content shows only under their own read permissions.
/// A decision corrects the sample's scores, adds a labelled example to the rubric's calibration set so the disagreement
/// counts toward agreement, and the reviewed case can then be promoted to a dataset of the agent after redaction.
/// </summary>
public sealed class ReviewService(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements, IAgentCatalog agents,
    IEnumerable<ISearchSource> sources, DatasetService datasets, IAuditWriter audit, IClock clock)
{
    private sealed record Item(Guid Id, Guid Sample, Guid Agent, int Version, Guid Unit, string Language, string[] Reasons, string Status,
        Dictionary<string, ReviewedVerdict> Verdicts, string? Note, Guid? ReviewedBy, DateTimeOffset? ReviewedAt, Guid? PromotedDataset, int? PromotedVersion,
        DateTimeOffset CreatedAt);

    private sealed record Sample(string Classification, string Mode, string? Outcome, Dictionary<string, CaseMetric> Metrics, Dictionary<string, StoredVerdict> Judge,
        ProductionContent? Content);

    private readonly Dictionary<Guid, bool> scope = [];

    internal async Task<bool> MayReviewAsync(Guid unit, CancellationToken ct)
    {
        if (!scope.TryGetValue(unit, out var allowed))
        {
            allowed = (await authz.CheckAsync(Permissions.EvalReview, AuthzTarget.OrgUnit(unit), ct)).Allowed;
            scope[unit] = allowed;
        }
        return allowed;
    }

    public async Task<ReviewQueueView> ListAsync(string? status, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct))
        {
            return new ReviewQueueView(false, []);
        }
        var wanted = status ?? ReviewStatuses.Open;
        if (wanted is not (ReviewStatuses.Open or ReviewStatuses.Reviewed))
        {
            throw AppException.Validation("status", "validation.eval.review_status");
        }
        var items = await QueryAsync("where status = @status order by " + (wanted == ReviewStatuses.Open ? "created_at" : "reviewed_at desc") + " limit @limit",
            new { status = wanted, limit = ReviewRules.ListLimit }, ct);
        var result = new List<ReviewItemSummary>();
        var names = new Dictionary<Guid, IReadOnlyDictionary<string, string>?>();
        foreach (var item in items)
        {
            if (!await MayReviewAsync(item.Unit, ct))
            {
                continue;
            }
            if (!names.TryGetValue(item.Agent, out var name))
            {
                name = (await agents.GetAsync(item.Agent, ct))?.Name;
                names[item.Agent] = name;
            }
            result.Add(new ReviewItemSummary(item.Id, item.Agent, name, item.Version, item.Language, item.Reasons, item.Status, item.CreatedAt, item.ReviewedAt));
        }
        return new ReviewQueueView(true, result);
    }

    public async Task<ReviewItemView> GetAsync(Guid id, CancellationToken ct)
    {
        var item = await ReviewableAsync(id, ct);
        var sample = await SampleAsync(item.Sample, ct);
        return await ViewAsync(item, sample, ct);
    }

    public async Task<ReviewItemView> DecideAsync(Guid id, ReviewDecisionRequest request, CancellationToken ct)
    {
        var item = await ReviewableAsync(id, ct);
        if (item.Status != ReviewStatuses.Open)
        {
            throw AppException.Conflict("conflict.eval.review_decided");
        }
        var sample = await SampleAsync(item.Sample, ct);
        var errors = new ValidationErrors();
        var given = request.Verdicts ?? [];
        foreach (var (key, value) in given)
        {
            if (!sample.Judge.ContainsKey(key) || value is not ("pass" or "fail"))
            {
                errors.Add($"verdicts.{key}", "validation.eval.review_verdict");
            }
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > ReviewRules.MaxNote })
        {
            errors.Add("note", "validation.eval.review_note");
        }
        errors.ThrowIfAny();
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var verdicts = sample.Judge.ToDictionary(j => j.Key, j => new ReviewedVerdict(j.Value.Verdict, given.GetValueOrDefault(j.Key) ?? j.Value.Verdict),
            StringComparer.Ordinal);
        var metrics = new Dictionary<string, CaseMetric>(sample.Metrics, StringComparer.Ordinal);
        foreach (var (key, v) in verdicts.Where(v => v.Value.Overturned))
        {
            metrics[key] = new CaseMetric(CaseMetricStates.Scored, v.Reviewer == "pass" ? 1 : 0, "reviewed");
        }
        await session.ExecAsync("update evaluation.production_sample set metrics = @metrics where id = @id", new { id = item.Sample, metrics = Jsonb.From(metrics) }, ct);
        await session.ExecAsync(
            """
            update evaluation.review_item set status = 'reviewed', verdicts = @verdicts, note = @note, reviewed_by = @user, reviewed_at = @now, updated_at = @now
            where id = @id
            """, new { id, verdicts = Jsonb.From(verdicts), note, user, now }, ct);
        var labelled = await LabelAsync(item, sample, verdicts, user, now, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalReviewVerdict, "eval_review_item", id.ToString(), TargetOrgUnitId: item.Unit,
            Changes: new
            {
                sample = item.Sample, agentId = item.Agent, version = item.Version,
                verdicts = verdicts.ToDictionary(v => v.Key, v => new { judge = v.Value.Judge, reviewer = v.Value.Reviewer }),
                overturned = verdicts.Where(v => v.Value.Overturned).Select(v => v.Key).ToArray(), labelled,
            }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// The reviewer's verdicts become calibration labels of the rubric (US8 scenario 3), so the next calibration measures
    /// the judge against them. Only with content the reviewer saw, redacted, and within the label limit per locale.
    /// </summary>
    private async Task<int> LabelAsync(Item item, Sample sample, IReadOnlyDictionary<string, ReviewedVerdict> verdicts, Guid user, DateTimeOffset now,
        CancellationToken ct)
    {
        if (sample.Content is not { Answer: { Length: > 0 } answer } content || !CalibrationService.Locales.Contains(item.Language)
            || await WithheldAsync(sample, ct) is not null)
        {
            return 0;
        }
        var input = Clip(ReviewRules.Redact(content.Question.Trim()), CaseRules.MaxInput);
        var text = Clip(ReviewRules.Redact(answer.Trim()), 8000);
        if (input.Length == 0)
        {
            return 0;
        }
        var added = 0;
        foreach (var (metric, v) in verdicts)
        {
            if (await session.ScalarAsync<long>("select count(*) from evaluation.calibration_label where metric = @metric and locale = @locale",
                    new { metric, locale = item.Language }, ct) >= CalibrationService.MaxLabels)
            {
                continue;
            }
            added += await session.ExecAsync(
                """
                insert into evaluation.calibration_label (tenant_id, id, metric, locale, input, expectations, answer, label, labelled_by, labelled_at)
                values (platform.current_tenant_id(), @id, @metric, @locale, @input, '{}', @answer, @label, @user, @now)
                """, new { id = Ids.New(), metric, locale = item.Language, input, answer = text, label = v.Reviewer, user, now }, ct);
        }
        return added;
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;

    public async Task<ReviewItemView> PromoteAsync(Guid id, ReviewPromoteRequest request, CancellationToken ct)
    {
        var item = await ReviewableAsync(id, ct);
        if (item.Status != ReviewStatuses.Reviewed)
        {
            throw AppException.Conflict("conflict.eval.review_not_decided");
        }
        if (item.PromotedDataset is not null)
        {
            throw AppException.Conflict("conflict.eval.review_promoted");
        }
        var sample = await SampleAsync(item.Sample, ct);
        if (sample.Content is not { } content || await WithheldAsync(sample, ct) is not null)
        {
            throw AppException.Conflict("conflict.eval.review_no_content");
        }
        if (request.DatasetId is not { } datasetId)
        {
            throw AppException.Validation("datasetId", "validation.eval.review_dataset");
        }
        var confirmed = item.Verdicts.Count > 0 && item.Verdicts.Values.All(v => v.Reviewer == "pass");
        var expected = string.IsNullOrWhiteSpace(request.ExpectedAnswer) ? confirmed ? content.Answer : null : request.ExpectedAnswer;
        var input = new CaseInput(
            ReviewRules.Redact(string.IsNullOrWhiteSpace(request.Input) ? content.Question : request.Input),
            item.Language,
            null,
            new CaseExpectations(expected is null ? null : ReviewRules.Redact(expected), request.KeyFacts?.Select(f => ReviewRules.Redact(f ?? string.Empty)).ToList()),
            [ReviewRules.ReviewedTag],
            request.Severity ?? EvalSeverities.Warning,
            sample.Classification);
        var version = await datasets.AppendReviewedCaseAsync(datasetId, item.Agent, input, ct);
        var now = clock.UtcNow;
        await session.ExecAsync("update evaluation.review_item set promoted_dataset_id = @datasetId, promoted_version = @version, updated_at = @now where id = @id",
            new { id, datasetId, version, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalReviewPromoted, "eval_review_item", id.ToString(), TargetOrgUnitId: item.Unit,
            Changes: new { sample = item.Sample, agentId = item.Agent, datasetId, version }), ct);
        return await GetAsync(id, ct);
    }

    private async Task<Item> ReviewableAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiEvalsProduction, ct);
        var rows = await QueryAsync("where id = @id", new { id }, ct);
        // Out of scope reads as not found, so a reviewer cannot learn which other units have flagged runs.
        return rows.Count == 1 && await MayReviewAsync(rows[0].Unit, ct) ? rows[0] : throw AppException.NotFound();
    }

    private Task<List<Item>> QueryAsync(string where, object args, CancellationToken ct) =>
        session.QueryAsync(
            """
            select id, sample_id, agent_id, agent_version, org_unit_id, language, reasons, status, verdicts::text, note, reviewed_by, reviewed_at,
              promoted_dataset_id, promoted_version, created_at
            from evaluation.review_item
            """ + " " + where, args,
            r => new Item(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetInt32(3), r.GetGuid(4), r.GetString(5), r.GetFieldValue<string[]>(6), r.GetString(7),
                JsonSerializer.Deserialize<Dictionary<string, ReviewedVerdict>>(r.GetString(8), EvalJson.Options)!, r.Get<string?>("note"), r.Get<Guid?>("reviewed_by"),
                r.Get<DateTimeOffset?>("reviewed_at"), r.Get<Guid?>("promoted_dataset_id"), r.Get<int?>("promoted_version"), r.GetFieldValue<DateTimeOffset>(14)), ct);

    private async Task<Sample> SampleAsync(Guid id, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            "select classification, mode, outcome, metrics::text, judge::text, content::text from evaluation.production_sample where id = @id", new { id },
            r => new Sample(r.GetString(0), r.GetString(1), r.Get<string?>("outcome"),
                JsonSerializer.Deserialize<Dictionary<string, CaseMetric>>(r.GetString(3), EvalJson.Options)!,
                JsonSerializer.Deserialize<Dictionary<string, StoredVerdict>>(r.GetString(4), EvalJson.Options)!,
                r.Get<string?>("content") is { } c ? JsonSerializer.Deserialize<ProductionContent>(c, EvalJson.Options) : null), ct);
        return rows.Count == 1 ? rows[0] : throw AppException.NotFound();
    }

    /// <summary>
    /// Why the content is not shown to this reviewer, or null when it is: it is gone after scoring, or the reviewer may not
    /// read one of the sources the run read, asked of each source's owner as the reviewer (spec 012 FR-002).
    /// </summary>
    private async Task<string?> WithheldAsync(Sample sample, CancellationToken ct)
    {
        if (sample.Content is null)
        {
            return ContentWithheld.NotRetained;
        }
        foreach (var group in sample.Content.Read.Concat(sample.Content.Cited).GroupBy(s => s.Source, StringComparer.Ordinal))
        {
            var owner = sources.FirstOrDefault(s => s.Source == group.Key);
            var ids = group.Select(s => s.ItemId).Distinct().ToArray();
            if (owner is null)
            {
                return ContentWithheld.NotPermitted;
            }
            var readable = await owner.ReadableAsync(ids, ct);
            if (!ids.All(readable.Contains))
            {
                return ContentWithheld.NotPermitted;
            }
        }
        return null;
    }

    private async Task<ReviewItemView> ViewAsync(Item item, Sample sample, CancellationToken ct)
    {
        var withheld = await WithheldAsync(sample, ct);
        var shown = withheld is null;
        var judge = sample.Judge.ToDictionary(j => j.Key, j => shown ? j.Value : j.Value with { Reason = null }, StringComparer.Ordinal);
        var agent = await agents.GetAsync(item.Agent, ct);
        var sets = await DatasetService.SummariesAsync(session, item.Agent, ct);
        var canPromote = shown && item.Status == ReviewStatuses.Reviewed && item.PromotedDataset is null && CaseRules.Locales.Contains(item.Language);
        return new ReviewItemView(item.Id, item.Sample, item.Agent, agent?.Name, item.Version, item.Language, sample.Classification, sample.Mode, sample.Outcome,
            item.Reasons, item.Status, sample.Metrics, judge, item.Verdicts, item.Note, item.ReviewedBy, item.ReviewedAt, shown ? sample.Content : null, withheld,
            item.PromotedDataset, item.PromotedVersion, sets, canPromote, item.CreatedAt);
    }
}

/// <summary>
/// Open review items in the reviewer's inbox (spec 034 US8 scenario 2, spec 016): every reviewer in scope of the agent's
/// unit sees the item until one of them decides it.
/// </summary>
public sealed class ReviewTasks(IDbSession session, IEntitlementService entitlements, IAgentCatalog agents, ReviewService reviews) : IPersonalTaskSource
{
    public const string TaskSource = "eval_review";

    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiEvalsProduction, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync(
            "select id, agent_id, org_unit_id, created_at from evaluation.review_item where status = 'open' order by created_at limit @limit",
            new { limit = ReviewRules.ListLimit }, r => (Id: r.GetGuid(0), Agent: r.GetGuid(1), Unit: r.GetGuid(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var tasks = new List<PersonalTask>();
        foreach (var row in rows)
        {
            if (tasks.Count >= ReviewRules.TaskLimit || !await reviews.MayReviewAsync(row.Unit, ct))
            {
                continue;
            }
            var agent = await agents.GetAsync(row.Agent, ct);
            tasks.Add(new PersonalTask(TaskSource, row.Id, agent?.Name ?? new Dictionary<string, string>(), $"/admin/evaluation/review/{row.Id}", row.At, null));
        }
        return tasks;
    }
}
