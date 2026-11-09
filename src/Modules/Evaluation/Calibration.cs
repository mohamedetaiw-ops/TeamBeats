using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.AiGateway.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record CalibrationLabelRequest(string? Locale, string? Input, string? ExpectedAnswer, List<string>? KeyFacts, string? Answer, string? Label);

public sealed record CalibrationLabelView(Guid Id, string Metric, string Locale, string Input, CaseExpectations Expect, string Answer, string Label, Guid LabelledBy,
    DateTimeOffset LabelledAt);

/// <summary>Agreement of the judge with people in one locale; measured needs at least <see cref="CalibrationService.MinLabels"/> judged labels.</summary>
public sealed record LocaleAgreement(string Locale, int Labels, int Judged, int Agreed, double? Value, string Status);

public sealed record CalibrationView(Guid Id, string Metric, string RubricVersion, string Status, string? Outcome, IReadOnlyList<LocaleAgreement> Agreement, Guid RequestedBy,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

/// <summary>A judged rubric in the tenant: platform calibrated, calibrated by the tenant, or demoted to warning severity.</summary>
public sealed record RubricState(string Metric, string RubricVersion, string State, IReadOnlyDictionary<string, int> Labels, CalibrationView? Latest, bool Running,
    bool CanLabel = false, bool CanCalibrate = false);

public static class RubricStates
{
    public const string Platform = "platform";
    public const string Calibrated = "calibrated";
    public const string Demoted = "demoted";
}

public static class CalibrationOutcomes
{
    public const string Calibrated = "calibrated";
    public const string Demoted = "demoted";
    public const string Insufficient = "insufficient";
}

/// <summary>
/// Judge calibration (US8 scenarios 1 and 4, FR-011). Platform rubrics ship calibrated. An Evaluation Reviewer labels
/// cases for a rubric with the verdict a person gives; the AI Policy Administrator measures the judge against them.
/// A locale with at least <see cref="MinLabels"/> judged labels is measured; when any measured locale agrees less than
/// <see cref="Target"/>, the rubric is demoted in the tenant, so its blocking thresholds count as warnings, and the
/// person who asked is told. A later calibration that reaches the target restores it.
/// </summary>
public sealed class CalibrationService(IDbSession session, ITenantContext context, IAuthorizationService authz, IOutbox outbox, IClock clock)
{
    public const int MinLabels = 50;
    public const double Target = 0.85;
    public const int MaxLabels = 500;
    public static readonly IReadOnlyList<string> Locales = ["ar", "en"];

    private async Task<bool> MayAsync(string permission, CancellationToken ct) => (await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandReadAsync(CancellationToken ct)
    {
        if (!await MayAsync(Permissions.EvalReview, ct) && !await MayAsync(Permissions.EvalPolicyManage, ct) && !await MayAsync(Permissions.EvalView, ct))
        {
            throw AppException.Denied();
        }
    }

    private static Rubric RubricOf(string metric) => Rubrics.For(metric) ?? throw AppException.NotFound();

    private const string CalibrationSql =
        "select id, metric, rubric_version, status, outcome, agreement::text, requested_by, created_at, finished_at from evaluation.calibration";

    private static CalibrationView Read(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Get<string?>("outcome"),
            JsonSerializer.Deserialize<List<LocaleAgreement>>(r.GetString(5), EvalJson.Options) ?? [], r.GetGuid(6), r.GetFieldValue<DateTimeOffset>(7),
            r.Get<DateTimeOffset?>("finished_at"));

    /// <summary>The metrics whose rubric is demoted in the tenant: the latest calibration that measured anything fell below the target.</summary>
    internal async Task<IReadOnlySet<string>> DemotedAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select distinct on (metric) metric, outcome from evaluation.calibration
            where status = 'completed' and outcome in ('calibrated', 'demoted')
            order by metric, finished_at desc
            """, null, r => (Metric: r.GetString(0), Outcome: r.GetString(1)), ct);
        return rows.Where(r => r.Outcome == CalibrationOutcomes.Demoted).Select(r => r.Metric).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A demoted rubric's blocking thresholds count as warnings (US8 scenario 4); judged metrics are never critical.</summary>
    internal static IReadOnlyList<EffectiveRule> Demote(IReadOnlyList<EffectiveRule> rules, IReadOnlySet<string> demoted) =>
        rules.Select(r => demoted.Contains(r.Key) && r.Severity == EvalSeverities.Blocking ? r with { Severity = EvalSeverities.Warning, SeverityLevel = "calibration" } : r)
            .ToList();

    public async Task<IReadOnlyList<RubricState>> RubricsAsync(CancellationToken ct)
    {
        await DemandReadAsync(ct);
        var counts = await session.QueryAsync("select metric, locale, count(*) from evaluation.calibration_label group by metric, locale", null,
            r => (Metric: r.GetString(0), Locale: r.GetString(1), Count: (int)r.GetInt64(2)), ct);
        var canLabel = await MayAsync(Permissions.EvalReview, ct);
        var canCalibrate = await MayAsync(Permissions.EvalPolicyManage, ct);
        var list = new List<RubricState>();
        foreach (var rubric in Rubrics.All)
        {
            var mine = await session.QueryAsync($"{CalibrationSql} where metric = @metric order by created_at desc limit 5", new { metric = rubric.Metric }, Read, ct);
            var decided = mine.FirstOrDefault(c => c.Outcome is CalibrationOutcomes.Calibrated or CalibrationOutcomes.Demoted);
            var state = decided?.Outcome == CalibrationOutcomes.Demoted ? RubricStates.Demoted : decided is null ? RubricStates.Platform : RubricStates.Calibrated;
            list.Add(new RubricState(rubric.Metric, rubric.Version, state,
                Locales.ToDictionary(l => l, l => counts.FirstOrDefault(c => c.Metric == rubric.Metric && c.Locale == l).Count),
                mine.FirstOrDefault(c => c.Status == "completed"), mine.Any(c => c.Status == "running"), canLabel, canCalibrate));
        }
        return list;
    }

    public async Task<IReadOnlyList<CalibrationLabelView>> LabelsAsync(string metric, CancellationToken ct)
    {
        await DemandReadAsync(ct);
        _ = RubricOf(metric);
        return await session.QueryAsync(
            "select id, metric, locale, input, expectations::text, answer, label, labelled_by, labelled_at from evaluation.calibration_label where metric = @metric order by labelled_at, id",
            new { metric }, r => new CalibrationLabelView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
                JsonSerializer.Deserialize<CaseExpectations>(r.GetString(4), EvalJson.Options)!, r.GetString(5), r.GetString(6), r.GetGuid(7), r.GetFieldValue<DateTimeOffset>(8)), ct);
    }

    public async Task<CalibrationLabelView> AddLabelAsync(string metric, CalibrationLabelRequest request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EvalReview, AuthzTarget.Tenant, ct);
        _ = RubricOf(metric);
        var errors = new ValidationErrors();
        var locale = request.Locale;
        if (locale is null || !Locales.Contains(locale))
        {
            errors.Add("locale", "validation.eval.case_locale");
        }
        var input = request.Input?.Trim() ?? string.Empty;
        if (input.Length is < 1 or > 4000)
        {
            errors.Add("input", "validation.eval.case_input");
        }
        var answer = request.Answer?.Trim() ?? string.Empty;
        if (answer.Length is < 1 or > 8000)
        {
            errors.Add("answer", "validation.eval.calibration_answer");
        }
        if (request.Label is not ("pass" or "fail"))
        {
            errors.Add("label", "validation.eval.calibration_label");
        }
        var expected = request.ExpectedAnswer?.Trim();
        if (expected is { Length: > 4000 })
        {
            errors.Add("expectedAnswer", "validation.eval.case_answer");
        }
        if (metric == "answer_correctness" && expected is not { Length: > 0 })
        {
            errors.Add("expectedAnswer", "validation.eval.calibration_expected");
        }
        var facts = (request.KeyFacts ?? []).Select(f => f.Trim()).Where(f => f.Length > 0).ToList();
        if (facts.Count > 20 || facts.Any(f => f.Length > 500))
        {
            errors.Add("keyFacts", "validation.eval.case_facts");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<long>("select count(*) from evaluation.calibration_label where metric = @metric and locale = @locale", new { metric, locale }, ct) >= MaxLabels)
        {
            throw AppException.Validation("locale", "validation.eval.calibration_too_many");
        }
        var id = Ids.New();
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var expect = new CaseExpectations(Answer: expected is { Length: > 0 } ? expected : null, KeyFacts: facts.Count > 0 ? facts : null);
        await session.ExecAsync(
            """
            insert into evaluation.calibration_label (tenant_id, id, metric, locale, input, expectations, answer, label, labelled_by, labelled_at)
            values (platform.current_tenant_id(), @id, @metric, @locale, @input, @expect, @answer, @label, @user, @now)
            """, new { id, metric, locale, input, expect = Jsonb.From(expect), answer, label = request.Label, user, now }, ct);
        return new CalibrationLabelView(id, metric, locale!, input, expect, answer, request.Label!, user, now);
    }

    public async Task DeleteLabelAsync(Guid id, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EvalReview, AuthzTarget.Tenant, ct);
        if (await session.ExecAsync("delete from evaluation.calibration_label where id = @id", new { id }, ct) == 0)
        {
            throw AppException.NotFound();
        }
    }

    /// <summary>Starts measuring the judge against the labels of a rubric; the worker finishes it.</summary>
    public async Task<CalibrationView> StartAsync(string metric, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.EvalPolicyManage, AuthzTarget.Tenant, ct);
        var rubric = RubricOf(metric);
        if (await session.ScalarAsync<long>("select count(*) from evaluation.calibration where metric = @metric and status = 'running'", new { metric }, ct) > 0)
        {
            throw AppException.Conflict("conflict.eval.calibration_running");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        var user = context.RequireUser();
        await session.ExecAsync(
            """
            insert into evaluation.calibration (tenant_id, id, metric, rubric_version, status, requested_by, created_at)
            values (platform.current_tenant_id(), @id, @metric, @version, 'running', @user, @now)
            """, new { id, metric, version = rubric.Version, user, now }, ct);
        await outbox.EnqueueAsync(new CalibrationRequested(id), ct);
        return new CalibrationView(id, metric, rubric.Version, "running", null, [], user, now, null);
    }

    public async Task<CalibrationView> GetAsync(Guid id, CancellationToken ct)
    {
        await DemandReadAsync(ct);
        var rows = await session.QueryAsync($"{CalibrationSql} where id = @id", new { id }, Read, ct);
        return rows.Count == 0 ? throw AppException.NotFound() : rows[0];
    }
}

/// <summary>
/// Measures a calibration (US8): the judge scores each labelled answer as it would in a run, through the gateway and
/// on behalf of the person who asked, and its verdict is compared with the person's label.
/// </summary>
public sealed class CalibrationRunner(IServiceScopeFactory scopes, ITenantContext context, ILogger<CalibrationRunner> logger) : IMessageHandler<CalibrationRequested>
{
    public const string DemotedTemplate = "evaluation.rubric_demoted";

    private async Task<(string Metric, string Status, Guid By)?> ReadAsync(Guid id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(context.RequireTenant());
        accessor.SetActor(ActorType.System, null);
        var rows = await scope.ServiceProvider.GetRequiredService<IDbSession>().QueryAsync("select metric, status, requested_by from evaluation.calibration where id = @id",
            new { id }, r => (Metric: r.GetString(0), Status: r.GetString(1), By: r.GetGuid(2)), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task HandleAsync(CalibrationRequested message, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var rows = await ReadAsync(message.CalibrationId, ct);
        if (rows is not { Status: "running" } found)
        {
            return;
        }
        var (metric, _, by) = found;
        var accessor = sp.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(ActorType.User, by);
        var session = sp.GetRequiredService<IDbSession>();
        var judge = sp.GetRequiredService<EvaluationJudge>();
        var labels = await session.QueryAsync("select locale, input, expectations::text, answer, label from evaluation.calibration_label where metric = @metric order by labelled_at, id",
            new { metric }, r => (Locale: r.GetString(0), Input: r.GetString(1), Expect: JsonSerializer.Deserialize<CaseExpectations>(r.GetString(2), EvalJson.Options)!,
                Answer: r.GetString(3), Label: r.GetString(4)), ct);
        var agreement = new List<LocaleAgreement>();
        foreach (var locale in CalibrationService.Locales)
        {
            var mine = labels.Where(l => l.Locale == locale).ToList();
            if (mine.Count < CalibrationService.MinLabels)
            {
                agreement.Add(new LocaleAgreement(locale, mine.Count, 0, 0, null, CalibrationOutcomes.Insufficient));
                continue;
            }
            var judged = 0;
            var agreed = 0;
            foreach (var label in mine)
            {
                var record = new EvalRunRecord(Guid.Empty, "completed", "answered", null, label.Answer, locale, null, 0, 0, 0, [], [], [], null);
                var verdicts = await judge.JudgeAsync([metric], locale, label.Input, label.Expect, record, DataClassification.Internal, ct);
                if (verdicts.GetValueOrDefault(metric) is { } verdict)
                {
                    judged++;
                    agreed += verdict.Verdict == label.Label ? 1 : 0;
                }
            }
            agreement.Add(judged < CalibrationService.MinLabels
                ? new LocaleAgreement(locale, mine.Count, judged, agreed, null, CalibrationOutcomes.Insufficient)
                : new LocaleAgreement(locale, mine.Count, judged, agreed, Math.Round(agreed / (double)judged, 4), "measured"));
        }
        var measured = agreement.Where(a => a.Value is not null).ToList();
        var outcome = measured.Count == 0 ? CalibrationOutcomes.Insufficient
            : measured.Any(a => a.Value < CalibrationService.Target) ? CalibrationOutcomes.Demoted
            : CalibrationOutcomes.Calibrated;
        await session.ExecAsync("update evaluation.calibration set status = 'completed', outcome = @outcome, agreement = @agreement, finished_at = @now where id = @id",
            new { id = message.CalibrationId, outcome, agreement = Jsonb.From(agreement), now = sp.GetRequiredService<IClock>().UtcNow }, ct);
        if (outcome != CalibrationOutcomes.Insufficient)
        {
            await sp.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(
                outcome == CalibrationOutcomes.Demoted ? AuditActions.EvalRubricDemoted : AuditActions.EvalRubricCalibrated, "eval_rubric", metric,
                Changes: new { calibration = message.CalibrationId, rubric = Rubrics.For(metric)?.Version, agreement }), ct);
        }
        if (outcome == CalibrationOutcomes.Demoted)
        {
            await sp.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest([by], NotificationCategories.System, DemotedTemplate,
                "/admin/evaluation/calibration", DedupeKey: $"{DemotedTemplate}:{message.CalibrationId:N}"), ct);
        }
        await session.CommitAsync(ct);
        logger.LogInformation("eval.calibration_completed tenant {Tenant} metric {Metric} outcome {Outcome}", tenant, metric, outcome);
    }
}
