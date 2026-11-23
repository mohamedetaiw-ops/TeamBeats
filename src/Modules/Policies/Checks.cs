using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

public sealed record CheckOptionInput(string? Key, IReadOnlyDictionary<string, string>? Texts);

public sealed record CheckQuestionInput(string? ClauseKey, IReadOnlyDictionary<string, string>? Prompts, IReadOnlyList<CheckOptionInput>? Options, IReadOnlyList<string>? Correct,
    bool? Multiple);

public sealed record CheckQuestionsRequest(IReadOnlyList<CheckQuestionInput>? Questions);

public sealed record CheckOption(string Key, IReadOnlyDictionary<string, string> Texts);

/// <summary>A question of the bank. <see cref="Correct"/> is given to the people who write the check only.</summary>
public sealed record CheckQuestionView(Guid Id, int Position, string ClauseKey, string? ClauseNumber, IReadOnlyDictionary<string, string> Prompts, IReadOnlyList<CheckOption> Options,
    bool Multiple, IReadOnlyList<string>? Correct);

/// <summary>The question bank of a version. Locked while an open campaign asks the check for that version.</summary>
public sealed record CheckQuestionsView(Guid VersionId, int? Number, string State, bool Locked, IReadOnlyList<CheckQuestionView> Questions);

/// <summary>A campaign's check: the pass mark in percent, the attempts and how many questions each attempt draws.</summary>
public sealed record CheckSettings(int? PassPercent, int? Attempts, int? Questions);

public sealed record CampaignCheck(int PassPercent, int Attempts, int Questions);

/// <summary>What the person has done of a campaign's check, for the attest panel.</summary>
public sealed record CheckDue(Guid CampaignId, int PassPercent, int Attempts, int Used, bool Passed, bool NeedsFollowUp);

public sealed record CheckAttemptView(Guid Id, int Attempt, IReadOnlyList<CheckQuestionView> Questions);

/// <summary>A section to reread after a failed attempt: never the correct answers (US6 scenario 1).</summary>
public sealed record RereadClause(string Key, string Anchor, string Number, IReadOnlyDictionary<string, string> Titles);

public sealed record CheckResult(Guid AttemptId, int Attempt, int Correct, int Total, int Score, bool Passed, int AttemptsLeft, bool NeedsFollowUp, IReadOnlyList<RereadClause> Reread);

public sealed record CheckState(Guid PolicyId, int Version, CheckDue Due, CheckAttemptView? Current, CheckResult? Last);

public sealed record CheckAnswersRequest(IReadOnlyDictionary<Guid, IReadOnlyList<string>>? Answers);

/// <summary>One answered attempt with its answers, for compliance officers only (section 7).</summary>
public sealed record MemberAttemptView(int Attempt, int Score, bool Passed, IReadOnlyDictionary<Guid, IReadOnlyList<string>> Answers, IReadOnlyList<string> Reread,
    DateTimeOffset AnsweredAt);

public sealed record MemberCheckView(PolicyPerson Person, CheckDue Due, IReadOnlyList<MemberAttemptView> Attempts, IReadOnlyList<CheckQuestionView> Questions);

internal sealed record StoredQuestion(Guid Id, int Position, string ClauseKey, Dictionary<string, string> Prompts, List<CheckOption> Options, string[] Correct, bool Multiple)
{
    public const string Columns = "id, position, clause_key, prompts::text, options::text, correct, multiple";

    public static StoredQuestion Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetInt32(1), r.GetString(2), PolicyJson.Read<Dictionary<string, string>>(r.GetString(3)),
        PolicyJson.Read<List<CheckOption>>(r.GetString(4)), r.GetFieldValue<string[]>(5), r.GetBoolean(6));
}

/// <summary>A campaign member's check: the campaign's settings, the member's extra attempts and follow-up.</summary>
internal sealed record CheckMember(Guid CampaignId, Guid PolicyId, int Version, int PassPercent, int Attempts, int Questions, int ExtraAttempts, DateTimeOffset? FollowUpAt,
    string Status)
{
    public int Allowed => Attempts + ExtraAttempts;
}

/// <summary>
/// Comprehension checks (spec 025 US6, FR-013). The owner or a compliance officer writes a question bank per version,
/// each question linked to a clause; a campaign may then ask the check with a pass mark, a number of attempts and how
/// many questions each attempt draws. A failed attempt names the sections to reread, never the answers; after the last
/// attempt the person needs follow-up and the owner hears of it. A person attests only after passing, and the
/// passing score is kept with the attestation. Answers are read by compliance officers only, and each read is audited.
/// Needs <c>policy.comprehension</c>.
/// </summary>
public sealed partial class PolicyChecks(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IEntitlementService entitlements,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxQuestions = 50;
    public const int MinOptions = 2;
    public const int MaxOptions = 6;
    public const int MaxPromptLength = 500;
    public const int MaxOptionLength = 300;
    public const int DefaultPassPercent = 80;
    public const int DefaultAttempts = 3;
    public const string FollowUpTemplate = "policies.check_follow_up";

    [GeneratedRegex("^[a-z0-9]{1,10}$")]
    private static partial Regex KeyPattern();

    public static string CheckLink(Guid policyId) => $"/policies/{policyId}/check";

    // ---- The question bank ----

    private async Task<(PolicyRow Policy, VersionRow Version)> BankAsync(Guid id, Guid versionId, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyComprehension, ct);
        var policy = await register.VisibleAsync(id, forUpdate, ct);
        if (!await register.EditsAsync(policy, ct) && !await register.ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        var version = await register.VersionAsync(versionId, false, ct);
        if (version is null || version.PolicyId != id)
        {
            throw AppException.NotFound();
        }
        return (policy, version);
    }

    private async Task<List<StoredQuestion>> QuestionsOfAsync(Guid versionId, CancellationToken ct) =>
        (await session.QueryAsync($"select {StoredQuestion.Columns} from policies.check_question where version_id = @versionId order by position", new { versionId },
            StoredQuestion.Read, ct)).ToList();

    private static CheckQuestionView View(StoredQuestion q, IReadOnlyDictionary<string, ClauseView> clauses, bool withAnswers) =>
        new(q.Id, q.Position, q.ClauseKey, clauses.GetValueOrDefault(q.ClauseKey)?.Number, q.Prompts, q.Options, q.Multiple, withAnswers ? q.Correct : null);

    private static Dictionary<string, ClauseView> ClausesOf(VersionRow version) => ClauseRules.Views(version.Clauses).ToDictionary(c => c.Key, StringComparer.Ordinal);

    /// <summary>Whether an open campaign asks the check of this version, so its bank may not change.</summary>
    private async Task<bool> LockedAsync(VersionRow version, CancellationToken ct) =>
        version.Number is { } number && await session.ScalarAsync<int>(
            "select count(*)::int from policies.campaign where policy_id = @policy and version = @number and status = 'open' and check_pass_percent is not null",
            new { policy = version.PolicyId, number }, ct) > 0;

    public async Task<CheckQuestionsView> QuestionsAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        var (_, version) = await BankAsync(id, versionId, false, ct);
        var clauses = ClausesOf(version);
        return new CheckQuestionsView(version.Id, version.Number, version.State, await LockedAsync(version, ct),
            (await QuestionsOfAsync(version.Id, ct)).Select(q => View(q, clauses, true)).ToList());
    }

    /// <summary>Replaces the bank of a version. Every prompt and option is given in every language of the version.</summary>
    public async Task<CheckQuestionsView> SaveQuestionsAsync(Guid id, Guid versionId, CheckQuestionsRequest request, CancellationToken ct)
    {
        var (policy, version) = await BankAsync(id, versionId, true, ct);
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        if (version.State == VersionStates.Superseded)
        {
            throw AppException.Conflict("conflict.policies.superseded");
        }
        if (await LockedAsync(version, ct))
        {
            throw AppException.Conflict("conflict.policies.check_in_use");
        }
        var clauses = ClausesOf(version);
        var input = request.Questions ?? [];
        var errors = new ValidationErrors();
        if (input.Count > MaxQuestions)
        {
            errors.Add("questions", "validation.policies.check_questions");
        }
        var stored = new List<StoredQuestion>();
        for (var i = 0; i < input.Count && i < MaxQuestions; i++)
        {
            var field = $"questions[{i}]";
            var q = input[i];
            if (q is null)
            {
                errors.Add(field, "validation.policies.check_question");
                continue;
            }
            if (q.ClauseKey is not { } key || !clauses.ContainsKey(key))
            {
                errors.Add($"{field}.clauseKey", "validation.policies.check_clause");
            }
            var prompts = Texts(q.Prompts, version.Locales, MaxPromptLength);
            if (prompts is null)
            {
                errors.Add($"{field}.prompts", "validation.policies.check_prompt");
            }
            var options = new List<CheckOption>();
            var given = q.Options ?? [];
            if (given.Count is < MinOptions or > MaxOptions)
            {
                errors.Add($"{field}.options", "validation.policies.check_options");
            }
            for (var j = 0; j < given.Count && j < MaxOptions; j++)
            {
                var o = given[j];
                var optionKey = string.IsNullOrEmpty(o?.Key) ? ((char)('a' + j)).ToString() : o.Key;
                var texts = Texts(o?.Texts, version.Locales, MaxOptionLength);
                if (!KeyPattern().IsMatch(optionKey) || texts is null || options.Any(x => x.Key == optionKey))
                {
                    errors.Add($"{field}.options", "validation.policies.check_options");
                    break;
                }
                options.Add(new CheckOption(optionKey, texts));
            }
            var correct = (q.Correct ?? []).Distinct(StringComparer.Ordinal).ToArray();
            var multiple = q.Multiple ?? false;
            if (correct.Length == 0 || correct.Any(c => options.All(o => o.Key != c)) || (!multiple && correct.Length != 1) || correct.Length == options.Count)
            {
                errors.Add($"{field}.correct", "validation.policies.check_correct");
            }
            stored.Add(new StoredQuestion(Ids.New(), i + 1, q.ClauseKey ?? string.Empty, prompts ?? [], options, correct, multiple));
        }
        errors.ThrowIfAny();
        await session.ExecAsync("delete from policies.check_question where version_id = @versionId", new { versionId }, ct);
        var now = clock.UtcNow;
        foreach (var q in stored)
        {
            await session.ExecAsync(
                """
                insert into policies.check_question (tenant_id, id, policy_id, version_id, position, clause_key, prompts, options, correct, multiple, created_at)
                values (platform.current_tenant_id(), @Id, @policy, @versionId, @Position, @ClauseKey, @prompts, @options, @Correct, @Multiple, @now)
                """,
                new
                {
                    q.Id, policy = id, versionId, q.Position, q.ClauseKey, prompts = new Jsonb(PolicyJson.Write(q.Prompts)), options = new Jsonb(PolicyJson.Write(q.Options)), q.Correct,
                    q.Multiple, now,
                }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCheckChanged, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = versionId, number = version.Number, questions = stored.Count }), ct);
        return await QuestionsAsync(id, versionId, ct);
    }

    private static Dictionary<string, string>? Texts(IReadOnlyDictionary<string, string>? texts, IReadOnlyList<string> locales, int max)
    {
        if (texts is null || locales.Any(l => string.IsNullOrWhiteSpace(texts.GetValueOrDefault(l))) || texts.Keys.Any(l => !locales.Contains(l))
            || texts.Values.Any(t => t.Trim().Length > max))
        {
            return null;
        }
        return texts.ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);
    }

    // ---- Campaign settings ----

    /// <summary>
    /// The check a campaign asks, from the launch request: needs <c>policy.comprehension</c> and a bank for the version in
    /// force with at least as many questions as each attempt draws (all of them by default). Null when none is asked.
    /// </summary>
    internal async Task<CampaignCheck?> SettingsAsync(VersionRow version, CheckSettings? settings, ValidationErrors errors, CancellationToken ct)
    {
        if (settings is null)
        {
            return null;
        }
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyComprehension, ct))
        {
            throw AppException.NotInPackage(Capabilities.PolicyComprehension);
        }
        var bank = await session.ScalarAsync<int>("select count(*)::int from policies.check_question where version_id = @id", new { id = version.Id }, ct);
        var pass = settings.PassPercent ?? DefaultPassPercent;
        var attempts = settings.Attempts ?? DefaultAttempts;
        var count = settings.Questions ?? bank;
        if (pass is < 1 or > 100)
        {
            errors.Add("check.passPercent", "validation.policies.check_pass");
        }
        if (attempts is < 1 or > 10)
        {
            errors.Add("check.attempts", "validation.policies.check_attempts");
        }
        if (bank == 0 || count < 1 || count > bank)
        {
            errors.Add("check.questions", "validation.policies.check_bank");
        }
        return new CampaignCheck(pass, attempts, count);
    }

    internal async Task<CampaignCheck?> CampaignCheckAsync(Guid campaignId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            "select check_pass_percent, check_attempts, check_questions from policies.campaign where id = @campaignId and check_pass_percent is not null",
            new { campaignId }, r => new CampaignCheck(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)), ct);

    // ---- The person's check ----

    private async Task<CheckMember?> MemberAsync(Guid campaignId, Guid user, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"""
            select c.id, c.policy_id, c.version, c.check_pass_percent, c.check_attempts, c.check_questions, m.extra_attempts, m.follow_up_at, m.status
            from policies.campaign_member m join policies.campaign c on c.id = m.campaign_id
            where m.campaign_id = @campaignId and m.user_id = @user and c.check_pass_percent is not null{(forUpdate ? " for update of m" : string.Empty)}
            """, new { campaignId, user },
            r => new CheckMember(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.Get<DateTimeOffset?>("follow_up_at"),
                r.GetString(8)), ct);

    /// <summary>The campaign with a check the person owes on the policy: open, and they are still asked.</summary>
    private async Task<CheckMember?> OwedAsync(Guid policyId, Guid user, bool forUpdate, CancellationToken ct)
    {
        var campaign = await session.QuerySingleOrDefaultAsync<Guid?>(
            """
            select c.id from policies.campaign_member m join policies.campaign c on c.id = m.campaign_id
            where c.policy_id = @policyId and m.user_id = @user and c.status = 'open' and m.status in ('open', 'overdue') and c.check_pass_percent is not null
            order by m.due_at, c.id limit 1
            """, new { policyId, user }, r => r.GetGuid(0), ct);
        return campaign is { } id ? await MemberAsync(id, user, forUpdate, ct) : null;
    }

    private sealed record AttemptRow(Guid Id, int Attempt, Guid[] Questions, Dictionary<Guid, List<string>>? Answers, int? Correct, int Total, int? Score, bool? Passed, string[] Reread,
        DateTimeOffset? AnsweredAt);

    private async Task<List<AttemptRow>> AttemptsAsync(Guid campaignId, Guid user, CancellationToken ct) =>
        (await session.QueryAsync(
            "select id, attempt, questions, answers::text, correct, total, score, passed, reread, answered_at from policies.check_attempt where campaign_id = @campaignId and user_id = @user order by attempt",
            new { campaignId, user },
            r => new AttemptRow(r.GetGuid(0), r.GetInt32(1), r.GetFieldValue<Guid[]>(2), r.Get<string>("answers") is { } a ? PolicyJson.Read<Dictionary<Guid, List<string>>>(a) : null,
                r.Get<int?>("correct"), r.GetInt32(5), r.Get<int?>("score"), r.Get<bool?>("passed"), r.GetFieldValue<string[]>(8), r.Get<DateTimeOffset?>("answered_at")), ct)).ToList();

    private static CheckDue Due(CheckMember m, IReadOnlyList<AttemptRow> attempts) =>
        new(m.CampaignId, m.PassPercent, m.Allowed, attempts.Count(a => a.AnsweredAt is not null), attempts.Any(a => a.Passed == true), m.FollowUpAt is not null);

    /// <summary>The check state of a campaign member, for the attest panel; null when the campaign asks no check.</summary>
    internal async Task<CheckDue?> DueAsync(Guid campaignId, Guid user, CancellationToken ct) =>
        await MemberAsync(campaignId, user, false, ct) is { } m ? Due(m, await AttemptsAsync(campaignId, user, ct)) : null;

    /// <summary>The passing attempt the attestation keeps (FR-007), or null when the person has not passed.</summary>
    internal async Task<object?> PassedAsync(Guid campaignId, Guid user, CancellationToken ct) =>
        (await AttemptsAsync(campaignId, user, ct)).FirstOrDefault(a => a.Passed == true) is { } a
            ? new { attempt = a.Attempt, a.Correct, a.Total, a.Score, passed = true, attemptId = a.Id }
            : null;

    private async Task<VersionRow> CampaignVersionAsync(CheckMember m, CancellationToken ct) =>
        await register.VersionByNumberAsync(m.PolicyId, m.Version, ct) ?? throw AppException.NotFound();

    private static CheckResult Result(AttemptRow a, CheckMember m, int used, VersionRow version)
    {
        var clauses = ClausesOf(version);
        var reread = a.Reread.Where(clauses.ContainsKey).Select(k => clauses[k]).OrderBy(c => version.Clauses.FindIndex(x => x.Key == c.Key))
            .Select(c => new RereadClause(c.Key, c.Anchor, c.Number, c.Titles)).ToList();
        return new CheckResult(a.Id, a.Attempt, a.Correct ?? 0, a.Total, a.Score ?? 0, a.Passed == true, Math.Max(0, m.Allowed - used), m.FollowUpAt is not null, reread);
    }

    /// <summary>The person's check on a policy: settings, attempts used, the attempt under way and the last result.</summary>
    public async Task<CheckState> MineAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyComprehension, ct);
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var m = await OwedAsync(policy.Id, user, false, ct) ?? throw AppException.NotFound();
        return await StateAsync(m, user, ct);
    }

    private async Task<CheckState> StateAsync(CheckMember m, Guid user, CancellationToken ct)
    {
        var attempts = await AttemptsAsync(m.CampaignId, user, ct);
        var version = await CampaignVersionAsync(m, ct);
        var open = attempts.FirstOrDefault(a => a.AnsweredAt is null);
        CheckAttemptView? current = null;
        if (open is not null)
        {
            var bank = (await QuestionsOfAsync(version.Id, ct)).ToDictionary(q => q.Id);
            var clauses = ClausesOf(version);
            current = new CheckAttemptView(open.Id, open.Attempt, open.Questions.Where(bank.ContainsKey).Select(q => View(bank[q], clauses, false)).ToList());
        }
        var last = attempts.LastOrDefault(a => a.AnsweredAt is not null);
        var used = attempts.Count(a => a.AnsweredAt is not null);
        return new CheckState(m.PolicyId, m.Version, Due(m, attempts), current, last is null ? null : Result(last, m, used, version));
    }

    /// <summary>Starts an attempt, drawing its questions from the bank, or returns the one under way.</summary>
    public async Task<CheckState> StartAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyComprehension, ct);
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var m = await OwedAsync(policy.Id, user, true, ct) ?? throw AppException.NotFound();
        var attempts = await AttemptsAsync(m.CampaignId, user, ct);
        if (attempts.Any(a => a.AnsweredAt is null))
        {
            return await StateAsync(m, user, ct);
        }
        if (attempts.Any(a => a.Passed == true))
        {
            throw AppException.Conflict("conflict.policies.check_passed");
        }
        if (m.FollowUpAt is not null || attempts.Count >= m.Allowed)
        {
            throw AppException.Conflict("conflict.policies.check_no_attempts");
        }
        var version = await CampaignVersionAsync(m, ct);
        var bank = await QuestionsOfAsync(version.Id, ct);
        if (bank.Count == 0)
        {
            throw AppException.Conflict("conflict.policies.check_bank");
        }
        var drawn = bank.Select(q => q.Id).ToArray();
        RandomNumberGenerator.Shuffle(drawn.AsSpan());
        drawn = drawn.Take(Math.Min(m.Questions, drawn.Length)).ToArray();
        await session.ExecAsync(
            """
            insert into policies.check_attempt (tenant_id, id, campaign_id, user_id, attempt, questions, total, started_at)
            values (platform.current_tenant_id(), @id, @campaign, @user, @attempt, @drawn, @total, @now)
            """, new { id = Ids.New(), campaign = m.CampaignId, user, attempt = attempts.Count + 1, drawn, total = drawn.Length, now = clock.UtcNow }, ct);
        return await StateAsync(m, user, ct);
    }

    /// <summary>
    /// Answers the attempt under way (US6 scenario 1). A question is right when exactly its correct options are chosen.
    /// Below the pass mark the result names the clauses of the questions missed; after the last attempt the person
    /// needs follow-up and the owner is told (scenario 2).
    /// </summary>
    public async Task<CheckResult> AnswerAsync(Guid id, Guid attemptId, CheckAnswersRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyComprehension, ct);
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var m = await OwedAsync(policy.Id, user, true, ct) ?? throw AppException.NotFound();
        var attempts = await AttemptsAsync(m.CampaignId, user, ct);
        var attempt = attempts.FirstOrDefault(a => a.Id == attemptId) ?? throw AppException.NotFound();
        if (attempt.AnsweredAt is not null)
        {
            throw AppException.Conflict("conflict.policies.check_answered");
        }
        var version = await CampaignVersionAsync(m, ct);
        var bank = (await QuestionsOfAsync(version.Id, ct)).ToDictionary(q => q.Id);
        var answers = request.Answers ?? new Dictionary<Guid, IReadOnlyList<string>>();
        var errors = new ValidationErrors();
        if (answers.Keys.Any(k => !attempt.Questions.Contains(k)) || attempt.Questions.Any(q => !answers.ContainsKey(q) || answers[q] is not { Count: > 0 }))
        {
            errors.Add("answers", "validation.policies.check_answers");
        }
        foreach (var (question, chosen) in answers)
        {
            if (bank.TryGetValue(question, out var q) && chosen is not null && (chosen.Any(c => q.Options.All(o => o.Key != c)) || (!q.Multiple && chosen.Count > 1)))
            {
                errors.Add("answers", "validation.policies.check_answers");
                break;
            }
        }
        errors.ThrowIfAny();
        var right = 0;
        var missed = new List<string>();
        foreach (var qid in attempt.Questions)
        {
            if (!bank.TryGetValue(qid, out var q))
            {
                continue;
            }
            if (answers[qid].ToHashSet(StringComparer.Ordinal).SetEquals(q.Correct))
            {
                right++;
            }
            else if (!missed.Contains(q.ClauseKey))
            {
                missed.Add(q.ClauseKey);
            }
        }
        var total = attempt.Questions.Length;
        var score = total == 0 ? 0 : (int)Math.Floor(right * 100.0 / total);
        var passed = score >= m.PassPercent;
        var now = clock.UtcNow;
        var stored = answers.ToDictionary(a => a.Key, a => a.Value.Distinct(StringComparer.Ordinal).ToList());
        await session.ExecAsync(
            """
            update policies.check_attempt set answers = @answers, correct = @right, score = @score, passed = @passed, reread = @reread, answered_at = @now
            where id = @attemptId
            """, new { attemptId, answers = new Jsonb(PolicyJson.Write(stored)), right, score, passed, reread = passed ? Array.Empty<string>() : missed.ToArray(), now }, ct);
        var used = attempts.Count(a => a.AnsweredAt is not null) + 1;
        if (!passed && used >= m.Allowed)
        {
            await session.ExecAsync("update policies.campaign_member set follow_up_at = @now where campaign_id = @campaign and user_id = @user",
                new { now, campaign = m.CampaignId, user }, ct);
            m = m with { FollowUpAt = now };
            await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCheckFailed, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
                Changes: new { campaign = m.CampaignId, attempts = used, score }), ct);
            await notifications.SendAsync(new NotificationRequest([policy.Owner], NotificationCategories.AttestationDue, FollowUpTemplate, $"/policies/{policy.Id}/compliance?campaign={m.CampaignId}",
                DedupeKey: $"policies.check:{m.CampaignId:N}:{user:N}:{used}"), ct);
        }
        var row = attempt with { Correct = right, Score = score, Passed = passed, Reread = passed ? [] : missed.ToArray(), AnsweredAt = now };
        return Result(row, m, used, version);
    }

    // ---- Compliance officers ----

    private async Task<(PolicyRow Policy, CheckMember Member)> FollowedAsync(Guid id, Guid campaignId, Guid userId, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyComprehension, ct);
        var policy = await register.VisibleAsync(id, false, ct);
        // Answers are for compliance officers only: never a line manager, nor the owner as such (section 7).
        if (!await register.ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        var member = await MemberAsync(campaignId, userId, forUpdate, ct);
        if (member is null || member.PolicyId != id)
        {
            throw AppException.NotFound();
        }
        return (policy, member);
    }

    /// <summary>A member's attempts with their answers, for compliance officers; the read is audited.</summary>
    public async Task<MemberCheckView> MemberViewAsync(Guid id, Guid campaignId, Guid userId, CancellationToken ct)
    {
        var (policy, member) = await FollowedAsync(id, campaignId, userId, false, ct);
        var attempts = await AttemptsAsync(campaignId, userId, ct);
        var version = await CampaignVersionAsync(member, ct);
        var clauses = ClausesOf(version);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCheckViewed, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { campaign = campaignId, person = userId }), ct);
        return new MemberCheckView(await register.PersonAsync(userId, ct), Due(member, attempts),
            attempts.Where(a => a.AnsweredAt is not null).Select(a => new MemberAttemptView(a.Attempt, a.Score ?? 0, a.Passed == true,
                (a.Answers ?? []).ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value), a.Reread, a.AnsweredAt!.Value)).ToList(),
            (await QuestionsOfAsync(version.Id, ct)).Select(q => View(q, clauses, true)).ToList());
    }

    /// <summary>After the follow-up a compliance officer gives the person the campaign's attempts again.</summary>
    public async Task<MemberCheckView> ResetAsync(Guid id, Guid campaignId, Guid userId, CancellationToken ct)
    {
        var (policy, member) = await FollowedAsync(id, campaignId, userId, true, ct);
        if (member.FollowUpAt is null)
        {
            throw AppException.Conflict("conflict.policies.check_not_followed");
        }
        var extra = member.ExtraAttempts + member.Attempts;
        if (extra > 90)
        {
            throw AppException.Conflict("conflict.policies.check_no_attempts");
        }
        await session.ExecAsync("update policies.campaign_member set follow_up_at = null, extra_attempts = @extra where campaign_id = @campaignId and user_id = @userId",
            new { extra, campaignId, userId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCheckReset, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { campaign = campaignId, person = userId, attempts = member.Attempts }), ct);
        return await MemberViewAsync(id, campaignId, userId, ct);
    }
}
