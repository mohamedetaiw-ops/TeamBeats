using System.Globalization;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Engagement.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Engagement;

public sealed record SurveyQuestionRequest(string? Kind, string? Prompt, bool Required, IReadOnlyList<string>? Options);

public sealed record SurveyRequest(Guid? OrgUnitId, string? Title, string? Intro, bool Anonymous, int? Threshold, DateTimeOffset? ClosesAt, IReadOnlyList<SurveyQuestionRequest>? Questions);

public sealed record SurveyAnswerRequest(Guid QuestionId, IReadOnlyList<Guid>? OptionIds, int? Value, string? Text);

public sealed record SurveyResponseRequest(IReadOnlyList<SurveyAnswerRequest>? Answers);

public sealed record SurveySettingsRequest(int? Threshold);

public sealed record SurveySettingsView(int Threshold, int Version);

public sealed record SurveyOptionView(Guid Id, string Label);

public sealed record SurveyQuestionView(Guid Id, string Kind, string Prompt, bool Required, IReadOnlyList<SurveyOptionView> Options);

public sealed record SurveyView(
    Guid Id,
    Guid OrgUnitId,
    IReadOnlyDictionary<string, string> UnitNames,
    Person Author,
    string Title,
    string? Intro,
    bool Anonymous,
    int Threshold,
    string Status,
    DateTimeOffset? ClosesAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<SurveyQuestionView> Questions,
    bool Responded,
    bool CanRespond,
    bool Manages,
    bool CanEdit);

public sealed record SurveyList(bool CanCreate, int DefaultThreshold, IReadOnlyList<SurveyView> ToAnswer, IReadOnlyList<SurveyView> Managed);

public sealed record SurveyOptionCount(Guid Id, string Label, int Count);

/// <summary>One question's aggregate. Scale holds the counts for 1 to 5. Texts never carry who wrote them.</summary>
public sealed record SurveyQuestionResult(Guid Id, string Kind, string Prompt, int Answered, IReadOnlyList<SurveyOptionCount> Options, IReadOnlyList<int>? Scale,
    double? Average, IReadOnlyList<string>? Texts);

/// <summary>A group is the unit directly below the survey's unit (or the unit itself); Responses is null while it is hidden.</summary>
public sealed record SurveyGroupResult(Guid UnitId, IReadOnlyDictionary<string, string> Names, int? Responses, bool Visible);

public sealed record SurveyResults(Guid SurveyId, bool Anonymous, int Threshold, string Status, Guid? Group, int Responses, bool Visible,
    IReadOnlyList<SurveyGroupResult> Groups, IReadOnlyList<SurveyQuestionResult> Questions);

public sealed record SurveyNamedAnswer(Guid QuestionId, IReadOnlyList<Guid> OptionIds, int? Value, string? Text);

public sealed record SurveyNamedResponse(Person Person, DateTimeOffset SubmittedAt, IReadOnlyList<SurveyNamedAnswer> Answers);

public static class SurveyKinds
{
    public const string Single = "single";
    public const string Multiple = "multiple";
    public const string Scale = "scale";
    public const string Text = "text";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Single, Multiple, Scale, Text };

    public static bool HasOptions(string kind) => kind is Single or Multiple;
}

public static class SurveyStatuses
{
    public const string Draft = "draft";
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>
/// Surveys (spec 023 US3 scenarios 2 to 5, FR-005, FR-009, FR-010). A survey author with <c>engagement.survey_publish</c>
/// on an org unit builds a survey for that unit and everything below it, anonymous or named, and publishes it with an
/// end date. People in the audience answer once. An anonymous response is stored with no user and no time, only the
/// group it counts in; "has responded" is kept apart and deleted at close. Anonymous results show only after close,
/// and only for groups that reach the threshold: a group under it is hidden everywhere, and when hiding one group would
/// let it be worked out from the others, the next smallest is hidden too. In a named survey the author and publishers
/// on the unit see who answered what. The author and publishers export aggregated results as CSV.
/// </summary>
public sealed class Surveys(
    IDbSession session,
    EngagementActivity activity,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const int DefaultThreshold = 5;
    public const int MinThreshold = 3;
    public const int MaxThreshold = 50;
    public const int MaxQuestions = 50;
    public const int MaxOptions = 20;
    public const int MaxTitle = 200;
    public const int MaxIntro = 2000;
    public const int MaxPrompt = 300;
    public const int MaxLabel = 120;
    public const int MaxText = 2000;
    public static readonly TimeSpan MaxOpen = TimeSpan.FromDays(365);

    private sealed record SurveyRow(Guid Id, Guid OrgUnitId, Guid AuthorId, string Title, string? Intro, bool Anonymous, int Threshold, string Status,
        DateTimeOffset? ClosesAt, DateTimeOffset? PublishedAt, DateTimeOffset? ClosedAt, DateTimeOffset CreatedAt);

    private const string Columns = "id, org_unit_id, author_id, title, intro, anonymous, threshold, status, closes_at, published_at, closed_at, created_at";

    private static SurveyRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.Get<string?>("intro"), r.GetBoolean(5),
        r.GetInt32(6), r.GetString(7), r.Get<DateTimeOffset?>("closes_at"), r.Get<DateTimeOffset?>("published_at"), r.Get<DateTimeOffset?>("closed_at"),
        r.GetFieldValue<DateTimeOffset>(11));

    private sealed record Question(Guid Id, string Kind, string Prompt, bool Required, List<SurveyOptionView> Options);

    private async Task RequireAsync(CancellationToken ct) => await entitlements.DemandAsync(Capabilities.EngagementSurveys, ct);

    private async Task<bool> PublishesAsync(Guid unit, CancellationToken ct) => (await authz.CheckAsync(Permissions.EngagementSurveyPublish, AuthzTarget.OrgUnit(unit), ct)).Allowed;

    private async Task<bool> ManagesAsync(SurveyRow row, CancellationToken ct) => row.AuthorId == context.RequireUser() || await PublishesAsync(row.OrgUnitId, ct);

    // ---- Settings ----

    public static string SettingsETag(int version) => $"\"s{version}\"";

    private async Task<SurveySettingsView> SettingsAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select threshold, version from engagement.survey_setting", null, r => (Threshold: r.GetInt32(0), Version: r.GetInt32(1)), ct);
        return row == default ? new SurveySettingsView(DefaultThreshold, 0) : new SurveySettingsView(row.Threshold, row.Version);
    }

    public async Task<SurveySettingsView> GetSettingsAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        return await SettingsAsync(ct);
    }

    public async Task<SurveySettingsView> SetSettingsAsync(SurveySettingsRequest request, string? ifMatch, CancellationToken ct)
    {
        await RequireAsync(ct);
        await authz.DemandAsync(Permissions.EngagementManage, AuthzTarget.Tenant, ct);
        var current = await SettingsAsync(ct);
        if (ifMatch != SettingsETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.survey_settings_changed", null,
                new Dictionary<string, object?> { ["etag"] = SettingsETag(current.Version) });
        }
        if (request.Threshold is not (>= MinThreshold and <= MaxThreshold) || request.Threshold is not { } threshold)
        {
            throw AppException.Validation("threshold", "validation.engagement.survey_threshold");
        }
        var version = current.Version + 1;
        await session.ExecAsync(
            """
            insert into engagement.survey_setting (tenant_id, threshold, version, updated_by, updated_at) values (platform.current_tenant_id(), @threshold, @version, @user, @now)
            on conflict (tenant_id) do update set threshold = excluded.threshold, version = excluded.version, updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """, new { threshold, version, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementSurveyThresholdChanged, "survey_setting", null,
            Changes: new { version, from = current.Threshold, to = threshold }), ct);
        return new SurveySettingsView(threshold, version);
    }

    // ---- Audience ----

    /// <summary>
    /// Whether the person is in the survey's audience, and the group they count in: the unit directly below the
    /// survey's unit on the path to their membership, or the survey's unit itself. The primary membership decides
    /// when several fall inside the audience.
    /// </summary>
    private async Task<(bool In, Guid Group)> AudienceAsync(Guid user, Guid surveyUnit, CancellationToken ct)
    {
        var memberships = (await hierarchy.GetActiveMembershipsAsync(user, ct)).OrderByDescending(m => m.IsPrimary).ToList();
        if (memberships.Count == 0)
        {
            return (false, Guid.Empty);
        }
        var paths = await hierarchy.GetPathsAsync(memberships.Select(m => m.OrgUnitId), ct);
        foreach (var m in memberships)
        {
            if (!paths.TryGetValue(m.OrgUnitId, out var path))
            {
                continue;
            }
            var list = path.ToList();
            var at = list.IndexOf(surveyUnit);
            if (at >= 0)
            {
                return (true, at + 1 < list.Count ? list[at + 1] : surveyUnit);
            }
        }
        return (false, Guid.Empty);
    }

    private async Task<HashSet<Guid>> MyUnitsAndAboveAsync(CancellationToken ct)
    {
        var memberships = await hierarchy.GetActiveMembershipsAsync(context.RequireUser(), ct);
        var paths = await hierarchy.GetPathsAsync(memberships.Select(m => m.OrgUnitId), ct);
        return paths.Values.SelectMany(p => p).ToHashSet();
    }

    // ---- Reading ----

    public async Task<SurveyList> ListAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.EngagementSurveyPublish, ct);
        var mine = await MyUnitsAndAboveAsync(ct);
        var open = await session.QueryAsync($"select {Columns} from engagement.survey where status = 'open' order by closes_at limit 200", null, Read, ct);
        var toAnswer = new List<SurveyView>();
        foreach (var row in open)
        {
            if (!mine.Contains(row.OrgUnitId))
            {
                continue;
            }
            var current = await CloseIfDueAsync(row, ct);
            if (current.Status == SurveyStatuses.Open)
            {
                toAnswer.Add(await ViewAsync(current, ct));
            }
        }
        var managed = new List<SurveyView>();
        if (coverage.Any || await session.ScalarAsync<bool>("select exists (select 1 from engagement.survey where author_id = @user)", new { user }, ct))
        {
            var rows = await session.QueryAsync($"select {Columns} from engagement.survey order by created_at desc limit 500", null, Read, ct);
            foreach (var row in rows.Where(r => r.AuthorId == user || coverage.Covers(r.OrgUnitId)).Take(100))
            {
                managed.Add(await ViewAsync(await CloseIfDueAsync(row, ct), ct));
            }
        }
        return new SurveyList(coverage.Any, (await SettingsAsync(ct)).Threshold, toAnswer, managed);
    }

    public async Task<SurveyView> GetAsync(Guid id, CancellationToken ct)
    {
        var (row, _) = await VisibleAsync(id, ct);
        return await ViewAsync(row, ct);
    }

    /// <summary>A survey the viewer may open: people in the audience once it is published, and those who manage it. Anything else is not found.</summary>
    private async Task<(SurveyRow Row, bool Manages)> VisibleAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync($"select {Columns} from engagement.survey where id = @id", new { id }, Read, ct) ?? throw AppException.NotFound();
        var manages = await ManagesAsync(row, ct);
        if (!manages && (row.Status == SurveyStatuses.Draft || !(await AudienceAsync(context.RequireUser(), row.OrgUnitId, ct)).In))
        {
            throw AppException.NotFound();
        }
        return (await CloseIfDueAsync(row, ct), manages);
    }

    private async Task<(SurveyRow Row, bool Manages)> ManagedAsync(Guid id, CancellationToken ct)
    {
        var (row, manages) = await VisibleAsync(id, ct);
        return manages ? (row, manages) : throw AppException.Denied();
    }

    /// <summary>An open survey past its end date closes the first time anyone looks at it.</summary>
    private async Task<SurveyRow> CloseIfDueAsync(SurveyRow row, CancellationToken ct) =>
        row.Status == SurveyStatuses.Open && row.ClosesAt is { } closes && closes <= clock.UtcNow ? await CloseRowAsync(row, closes, "end_date", ct) : row;

    private async Task<SurveyRow> CloseRowAsync(SurveyRow row, DateTimeOffset at, string reason, CancellationToken ct)
    {
        var changed = await session.ExecAsync("update engagement.survey set status = 'closed', closed_at = @at where id = @id and status = 'open'", new { id = row.Id, at }, ct);
        // The "has responded" marks go at close (FR-010), so nothing is left that says who answered an anonymous survey.
        await session.ExecAsync("delete from engagement.survey_responded where survey_id = @id", new { id = row.Id }, ct);
        if (changed > 0)
        {
            await outbox.EnqueueAsync(new SurveyClosed(row.Id, row.OrgUnitId), ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.EngagementSurveyClosed, "survey", row.Id.ToString(), TargetOrgUnitId: row.OrgUnitId,
                Changes: new { audience = row.OrgUnitId, reason }), ct);
        }
        return row with { Status = SurveyStatuses.Closed, ClosedAt = at };
    }

    private async Task<List<Question>> QuestionsAsync(Guid surveyId, CancellationToken ct)
    {
        var questions = await session.QueryAsync("select id, kind, prompt, required from engagement.survey_question where survey_id = @surveyId order by position",
            new { surveyId }, r => new Question(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), []), ct);
        var options = await session.QueryAsync(
            """
            select o.question_id, o.id, o.label from engagement.survey_option o
            join engagement.survey_question q on q.tenant_id = o.tenant_id and q.id = o.question_id
            where q.survey_id = @surveyId order by o.position
            """, new { surveyId }, r => (Question: r.GetGuid(0), Option: new SurveyOptionView(r.GetGuid(1), r.GetString(2))), ct);
        var byQuestion = options.ToLookup(o => o.Question, o => o.Option);
        foreach (var q in questions)
        {
            q.Options.AddRange(byQuestion[q.Id]);
        }
        return questions;
    }

    private async Task<bool> RespondedAsync(SurveyRow row, Guid user, CancellationToken ct) => row.Anonymous
        ? await session.ScalarAsync<bool>("select exists (select 1 from engagement.survey_responded where survey_id = @id and user_id = @user)", new { id = row.Id, user }, ct)
        : await session.ScalarAsync<bool>("select exists (select 1 from engagement.survey_response where survey_id = @id and user_id = @user)", new { id = row.Id, user }, ct);

    private async Task<SurveyView> ViewAsync(SurveyRow row, CancellationToken ct)
    {
        var user = context.RequireUser();
        var manages = await ManagesAsync(row, ct);
        var responded = row.Status != SurveyStatuses.Draft && await RespondedAsync(row, user, ct);
        var canRespond = row.Status == SurveyStatuses.Open && !responded
            && (await authz.CheckAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct)).Allowed
            && (await AudienceAsync(user, row.OrgUnitId, ct)).In;
        var names = await hierarchy.GetNamesAsync([row.OrgUnitId], ct);
        var author = await users.GetManyAsync([row.AuthorId], ct);
        var person = author.TryGetValue(row.AuthorId, out var a) ? new Person(row.AuthorId, a.DisplayName, a.Status == "active") : new Person(row.AuthorId, "", false);
        var questions = await QuestionsAsync(row.Id, ct);
        return new SurveyView(row.Id, row.OrgUnitId, names.TryGetValue(row.OrgUnitId, out var n) ? n : new Dictionary<string, string>(), person, row.Title, row.Intro,
            row.Anonymous, row.Threshold, row.Status, row.ClosesAt, row.PublishedAt, row.ClosedAt, row.CreatedAt,
            questions.Select(q => new SurveyQuestionView(q.Id, q.Kind, q.Prompt, q.Required, q.Options)).ToList(),
            responded, canRespond, manages, manages && row.Status == SurveyStatuses.Draft);
    }

    // ---- Building ----

    private sealed record Built(Guid Unit, string Title, string? Intro, int Threshold, DateTimeOffset? ClosesAt, List<(string Kind, string Prompt, bool Required, List<string> Options)> Questions);

    private async Task<Built> ValidateAsync(SurveyRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.OrgUnitId is not { } unit || !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.engagement.survey_unit");
            unit = Guid.Empty;
        }
        else if (!await PublishesAsync(unit, ct))
        {
            throw AppException.Denied();
        }
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > MaxTitle || title.Any(char.IsControl))
        {
            errors.Add("title", "validation.engagement.survey_title");
        }
        var intro = string.IsNullOrWhiteSpace(request.Intro) ? null : request.Intro.Trim();
        if (intro is { Length: > MaxIntro })
        {
            errors.Add("intro", "validation.engagement.survey_intro");
        }
        var floor = (await SettingsAsync(ct)).Threshold;
        var threshold = request.Threshold ?? floor;
        if (threshold < floor || threshold > MaxThreshold)
        {
            errors.Add("threshold", "validation.engagement.survey_threshold");
        }
        var now = clock.UtcNow;
        if (request.ClosesAt is { } closes && (closes <= now || closes > now + MaxOpen))
        {
            errors.Add("closesAt", "validation.engagement.survey_closes");
        }
        var questions = new List<(string, string, bool, List<string>)>();
        var given = request.Questions ?? [];
        if (given.Count > MaxQuestions)
        {
            errors.Add("questions", "validation.engagement.survey_questions");
        }
        foreach (var q in given.Take(MaxQuestions))
        {
            var kind = q?.Kind ?? "";
            var prompt = q?.Prompt?.Trim() ?? "";
            var options = (q?.Options ?? []).Select(o => (o ?? "").Trim()).ToList();
            if (!SurveyKinds.All.Contains(kind))
            {
                errors.Add("questions", "validation.engagement.survey_question_kind");
                continue;
            }
            if (prompt.Length is 0 or > MaxPrompt || prompt.Any(char.IsControl))
            {
                errors.Add("questions", "validation.engagement.survey_prompt");
            }
            if (SurveyKinds.HasOptions(kind))
            {
                if (options.Count is < 2 or > MaxOptions || options.Any(o => o.Length is 0 or > MaxLabel || o.Any(char.IsControl))
                    || options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != options.Count)
                {
                    errors.Add("questions", "validation.engagement.survey_options");
                }
            }
            else if (options.Count > 0)
            {
                errors.Add("questions", "validation.engagement.survey_options");
            }
            questions.Add((kind, prompt, q!.Required, options));
        }
        errors.ThrowIfAny();
        return new Built(unit, title, intro, threshold, request.ClosesAt, questions);
    }

    private async Task SaveQuestionsAsync(Guid surveyId, Built built, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            delete from engagement.survey_option o using engagement.survey_question q
            where q.tenant_id = o.tenant_id and q.id = o.question_id and q.survey_id = @surveyId
            """, new { surveyId }, ct);
        await session.ExecAsync("delete from engagement.survey_question where survey_id = @surveyId", new { surveyId }, ct);
        for (var i = 0; i < built.Questions.Count; i++)
        {
            var (kind, prompt, required, options) = built.Questions[i];
            var questionId = Ids.New();
            await session.ExecAsync(
                """
                insert into engagement.survey_question (tenant_id, id, survey_id, position, kind, prompt, required)
                values (platform.current_tenant_id(), @questionId, @surveyId, @i, @kind, @prompt, @required)
                """, new { questionId, surveyId, i, kind, prompt, required }, ct);
            for (var j = 0; j < options.Count; j++)
            {
                await session.ExecAsync(
                    "insert into engagement.survey_option (tenant_id, id, question_id, position, label) values (platform.current_tenant_id(), @id, @questionId, @j, @label)",
                    new { id = Ids.New(), questionId, j, label = options[j] }, ct);
            }
        }
    }

    public async Task<SurveyView> CreateAsync(SurveyRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var built = await ValidateAsync(request, ct);
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into engagement.survey (tenant_id, id, org_unit_id, author_id, title, intro, anonymous, threshold, closes_at, created_at)
            values (platform.current_tenant_id(), @id, @unit, @user, @title, @intro, @anonymous, @threshold, @closes, @now)
            """, new { id, unit = built.Unit, user = context.RequireUser(), title = built.Title, intro = built.Intro, anonymous = request.Anonymous, threshold = built.Threshold,
                closes = built.ClosesAt, now = clock.UtcNow }, ct);
        await SaveQuestionsAsync(id, built, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>A draft changes as a whole; once published, the questions and audience are fixed so every answer means the same.</summary>
    public async Task<SurveyView> UpdateAsync(Guid id, SurveyRequest request, CancellationToken ct)
    {
        var (row, _) = await ManagedAsync(id, ct);
        if (row.Status != SurveyStatuses.Draft)
        {
            throw AppException.Conflict("conflict.engagement.survey_published");
        }
        var built = await ValidateAsync(request, ct);
        await session.ExecAsync(
            """
            update engagement.survey set org_unit_id = @unit, title = @title, intro = @intro, anonymous = @anonymous, threshold = @threshold, closes_at = @closes
            where id = @id and status = 'draft'
            """, new { id, unit = built.Unit, title = built.Title, intro = built.Intro, anonymous = request.Anonymous, threshold = built.Threshold, closes = built.ClosesAt }, ct);
        await SaveQuestionsAsync(id, built, ct);
        return await GetAsync(id, ct);
    }

    public async Task<SurveyView> PublishAsync(Guid id, CancellationToken ct)
    {
        var (row, _) = await ManagedAsync(id, ct);
        if (row.Status != SurveyStatuses.Draft)
        {
            throw AppException.Conflict("conflict.engagement.survey_published");
        }
        // Whoever publishes must still hold the permission on the audience.
        if (!await PublishesAsync(row.OrgUnitId, ct))
        {
            throw AppException.Denied();
        }
        var errors = new ValidationErrors();
        var now = clock.UtcNow;
        if (row.ClosesAt is not { } closes || closes <= now || closes > now + MaxOpen)
        {
            errors.Add("closesAt", "validation.engagement.survey_closes");
        }
        if ((await QuestionsAsync(id, ct)).Count == 0)
        {
            errors.Add("questions", "validation.engagement.survey_questions");
        }
        errors.ThrowIfAny();
        await session.ExecAsync("update engagement.survey set status = 'open', published_at = @now where id = @id and status = 'draft'", new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EngagementSurveyPublished, "survey", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { audience = row.OrgUnitId, anonymous = row.Anonymous, threshold = row.Threshold, closesAt = row.ClosesAt }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<SurveyView> CloseAsync(Guid id, CancellationToken ct)
    {
        var (row, _) = await ManagedAsync(id, ct);
        if (row.Status != SurveyStatuses.Open)
        {
            throw AppException.Conflict("conflict.engagement.survey_not_open");
        }
        return await ViewAsync(await CloseRowAsync(row, clock.UtcNow, "closed", ct), ct);
    }

    // ---- Answering ----

    public async Task<SurveyView> RespondAsync(Guid id, SurveyResponseRequest request, CancellationToken ct)
    {
        var (row, _) = await VisibleAsync(id, ct);
        var user = context.RequireUser();
        if (!(await authz.CheckAsync(Permissions.EngagementParticipate, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var (inAudience, group) = await AudienceAsync(user, row.OrgUnitId, ct);
        if (!inAudience)
        {
            throw AppException.Denied();
        }
        if (row.Status != SurveyStatuses.Open)
        {
            throw AppException.Conflict("conflict.engagement.survey_not_open");
        }
        var questions = (await QuestionsAsync(id, ct)).ToDictionary(q => q.Id);
        var rows = new List<(Guid Question, Guid? Option, int? Value, string? Text)>();
        var errors = new ValidationErrors();
        var answers = request.Answers ?? [];
        if (answers.Select(a => a?.QuestionId).Distinct().Count() != answers.Count)
        {
            errors.Add("answers", "validation.engagement.survey_answer");
        }
        foreach (var a in answers)
        {
            if (a is null || !questions.TryGetValue(a.QuestionId, out var q))
            {
                errors.Add("answers", "validation.engagement.survey_answer");
                continue;
            }
            var chosen = (a.OptionIds ?? []).Distinct().ToList();
            switch (q.Kind)
            {
                case SurveyKinds.Single or SurveyKinds.Multiple:
                    if (chosen.Count == 0 || (q.Kind == SurveyKinds.Single && chosen.Count > 1) || chosen.Any(o => q.Options.All(x => x.Id != o)) || a.Value is not null || a.Text is not null)
                    {
                        errors.Add("answers", "validation.engagement.survey_answer");
                        break;
                    }
                    rows.AddRange(chosen.Select(o => (q.Id, (Guid?)o, (int?)null, (string?)null)));
                    break;
                case SurveyKinds.Scale:
                    if (a.Value is not (>= 1 and <= 5) || chosen.Count > 0 || a.Text is not null)
                    {
                        errors.Add("answers", "validation.engagement.survey_answer");
                        break;
                    }
                    rows.Add((q.Id, null, a.Value, null));
                    break;
                default:
                    var text = a.Text?.Trim() ?? "";
                    if (text.Length is 0 or > MaxText || chosen.Count > 0 || a.Value is not null)
                    {
                        errors.Add("answers", "validation.engagement.survey_answer");
                        break;
                    }
                    rows.Add((q.Id, null, null, text));
                    break;
            }
        }
        var answered = rows.Select(r => r.Question).ToHashSet();
        if (questions.Values.Any(q => q.Required && !answered.Contains(q.Id)))
        {
            errors.Add("answers", "validation.engagement.survey_required");
        }
        if (rows.Count == 0)
        {
            errors.Add("answers", "validation.engagement.survey_empty");
        }
        errors.ThrowIfAny();

        var already = new AppException(ProblemCodes.Conflict, 409, "conflict.engagement.already_responded", null);
        // A random id, not a time-ordered one, so the order of responses says nothing about when or by whom.
        var responseId = Guid.NewGuid();
        if (row.Anonymous)
        {
            // The mark and the response are written apart; nothing joins them, and the mark goes at close.
            var marked = await session.ExecAsync(
                "insert into engagement.survey_responded (tenant_id, survey_id, user_id) values (platform.current_tenant_id(), @id, @user) on conflict do nothing", new { id, user }, ct);
            if (marked == 0)
            {
                throw already;
            }
            await session.ExecAsync(
                "insert into engagement.survey_response (tenant_id, id, survey_id, group_unit_id) values (platform.current_tenant_id(), @responseId, @id, @group)",
                new { responseId, id, group }, ct);
        }
        else
        {
            if (await RespondedAsync(row, user, ct))
            {
                throw already;
            }
            await session.ExecAsync(
                """
                insert into engagement.survey_response (tenant_id, id, survey_id, group_unit_id, user_id, submitted_at)
                values (platform.current_tenant_id(), @responseId, @id, @group, @user, @now)
                """, new { responseId, id, group, user, now = clock.UtcNow }, ct);
        }
        foreach (var r in rows)
        {
            await session.ExecAsync(
                """
                insert into engagement.survey_answer (tenant_id, id, response_id, question_id, option_id, value, text)
                values (platform.current_tenant_id(), @answerId, @responseId, @question, @option, @value, @text)
                """, new { answerId = Guid.NewGuid(), responseId, question = r.Question, option = r.Option, value = r.Value, text = r.Text }, ct);
        }
        if (row.Anonymous)
        {
            await activity.AnonymousAsync("survey_response", ct);
        }
        else
        {
            await activity.ActedAsync("respond", null, ct);
        }
        return await ViewAsync(row, ct);
    }

    // ---- Results ----

    /// <summary>
    /// Which groups show. In an anonymous survey a group under the threshold is hidden, and while the hidden groups
    /// could still be worked out from the total (only one hidden, or together fewer than the threshold) the next
    /// smallest group is hidden too. Nothing shows before close, or when the whole survey is under the threshold.
    /// </summary>
    internal static HashSet<Guid> VisibleGroups(IReadOnlyDictionary<Guid, int> counts, int threshold, bool anonymous, bool closed)
    {
        if (!anonymous)
        {
            return counts.Keys.ToHashSet();
        }
        if (!closed || counts.Values.Sum() < threshold)
        {
            return [];
        }
        var hidden = counts.Where(c => c.Value < threshold).Select(c => c.Key).ToHashSet();
        var visible = counts.Where(c => c.Value >= threshold).OrderBy(c => c.Value).ThenBy(c => c.Key).Select(c => c.Key).ToList();
        while (hidden.Count > 0 && visible.Count > 0 && (hidden.Count == 1 || hidden.Sum(h => counts[h]) < threshold))
        {
            hidden.Add(visible[0]);
            visible.RemoveAt(0);
        }
        return visible.ToHashSet();
    }

    public async Task<SurveyResults> ResultsAsync(Guid id, Guid? group, CancellationToken ct)
    {
        var (row, _) = await ManagedAsync(id, ct);
        return await ResultsAsync(row, group, ct);
    }

    private async Task<SurveyResults> ResultsAsync(SurveyRow row, Guid? group, CancellationToken ct)
    {
        var counts = (await session.QueryAsync("select group_unit_id, count(*)::int from engagement.survey_response where survey_id = @id group by group_unit_id",
            new { id = row.Id }, r => (Group: r.GetGuid(0), Count: r.GetInt32(1)), ct)).ToDictionary(c => c.Group, c => c.Count);
        var total = counts.Values.Sum();
        var closed = row.Status == SurveyStatuses.Closed;
        var shown = VisibleGroups(counts, row.Threshold, row.Anonymous, closed);
        var overall = !row.Anonymous || (closed && total >= row.Threshold);
        var names = await hierarchy.GetNamesAsync(counts.Keys, ct);
        var groups = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key)
            .Select(c => new SurveyGroupResult(c.Key, names.TryGetValue(c.Key, out var n) ? n : new Dictionary<string, string>(), shown.Contains(c.Key) ? c.Value : null, shown.Contains(c.Key)))
            .ToList();
        var visible = group is { } g ? shown.Contains(g) : overall;
        var questions = await QuestionsAsync(row.Id, ct);
        if (!visible)
        {
            return new SurveyResults(row.Id, row.Anonymous, row.Threshold, row.Status, group, total, false, groups, []);
        }
        var answers = await session.QueryAsync(
            """
            select a.question_id, a.option_id, a.value, a.text from engagement.survey_answer a
            join engagement.survey_response r on r.tenant_id = a.tenant_id and r.id = a.response_id
            where r.survey_id = @id and (@group::uuid is null or r.group_unit_id = @group)
            """, new { id = row.Id, group }, r => (Question: r.GetGuid(0), Option: r.Get<Guid?>("option_id"), Value: r.Get<int?>("value"), Text: r.Get<string?>("text")), ct);
        var answeredBy = await session.QueryAsync(
            """
            select a.question_id, count(distinct a.response_id)::int from engagement.survey_answer a
            join engagement.survey_response r on r.tenant_id = a.tenant_id and r.id = a.response_id
            where r.survey_id = @id and (@group::uuid is null or r.group_unit_id = @group) group by a.question_id
            """, new { id = row.Id, group }, r => (Question: r.GetGuid(0), Count: r.GetInt32(1)), ct);
        var answeredCount = answeredBy.ToDictionary(a => a.Question, a => a.Count);
        var byQuestion = answers.ToLookup(a => a.Question);
        var results = questions.Select(q =>
        {
            var mine = byQuestion[q.Id].ToList();
            var options = q.Options.Select(o => new SurveyOptionCount(o.Id, o.Label, mine.Count(a => a.Option == o.Id))).ToList();
            IReadOnlyList<int>? scale = q.Kind == SurveyKinds.Scale ? Enumerable.Range(1, 5).Select(v => mine.Count(a => a.Value == v)).ToList() : null;
            double? average = q.Kind == SurveyKinds.Scale && mine.Count > 0 ? Math.Round(mine.Average(a => a.Value!.Value), 2) : null;
            // Free text comes back in alphabetical order, so the order says nothing about who wrote it or when.
            IReadOnlyList<string>? texts = q.Kind == SurveyKinds.Text ? mine.Select(a => a.Text!).Order(StringComparer.Ordinal).ToList() : null;
            return new SurveyQuestionResult(q.Id, q.Kind, q.Prompt, answeredCount.GetValueOrDefault(q.Id), options, scale, average, texts);
        }).ToList();
        return new SurveyResults(row.Id, row.Anonymous, row.Threshold, row.Status, group, total, true, groups, results);
    }

    /// <summary>Who answered what, in a named survey only, for the author and publishers on its unit (US3 scenario 4).</summary>
    public async Task<IReadOnlyList<SurveyNamedResponse>> ResponsesAsync(Guid id, CancellationToken ct)
    {
        var (row, _) = await ManagedAsync(id, ct);
        if (row.Anonymous)
        {
            throw AppException.NotFound();
        }
        var responses = await session.QueryAsync("select id, user_id, submitted_at from engagement.survey_response where survey_id = @id order by submitted_at",
            new { id }, r => (Id: r.GetGuid(0), User: r.GetGuid(1), At: r.GetFieldValue<DateTimeOffset>(2)), ct);
        var answers = (await session.QueryAsync(
            """
            select a.response_id, a.question_id, a.option_id, a.value, a.text from engagement.survey_answer a
            join engagement.survey_response r on r.tenant_id = a.tenant_id and r.id = a.response_id where r.survey_id = @id
            """, new { id }, r => (Response: r.GetGuid(0), Question: r.GetGuid(1), Option: r.Get<Guid?>("option_id"), Value: r.Get<int?>("value"), Text: r.Get<string?>("text")), ct))
            .ToLookup(a => a.Response);
        var people = await users.GetManyAsync(responses.Select(r => r.User).Distinct().ToList(), ct);
        return responses.Select(r => new SurveyNamedResponse(
            people.TryGetValue(r.User, out var u) ? new Person(r.User, u.DisplayName, u.Status == "active") : new Person(r.User, "", false), r.At,
            answers[r.Id].GroupBy(a => a.Question).Select(g => new SurveyNamedAnswer(g.Key, g.Where(a => a.Option is not null).Select(a => a.Option!.Value).ToList(),
                g.Select(a => a.Value).FirstOrDefault(v => v is not null), g.Select(a => a.Text).FirstOrDefault(t => t is not null))).ToList())).ToList();
    }

    /// <summary>
    /// Aggregated results as CSV (US3 scenario 5): the whole survey, then each group that shows. Hidden groups never
    /// appear, and free text is listed without who wrote it. Nothing but the header when the results are hidden.
    /// </summary>
    public async Task<(string Name, byte[] Content)> ExportAsync(Guid id, CancellationToken ct)
    {
        var (row, _) = await ManagedAsync(id, ct);
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var csv = new StringBuilder("group,question,answer,count\n");
        var overall = await ResultsAsync(row, null, ct);
        if (overall.Visible)
        {
            Append(csv, "", overall, includeTexts: true);
            foreach (var g in overall.Groups.Where(g => g.Visible))
            {
                var name = g.Names.TryGetValue(locale, out var n) ? n : g.Names.Values.FirstOrDefault() ?? g.UnitId.ToString();
                Append(csv, name, await ResultsAsync(row, g.UnitId, ct), includeTexts: false);
            }
        }
        return ($"survey-{id}.csv", new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray());
    }

    private static void Append(StringBuilder csv, string group, SurveyResults results, bool includeTexts)
    {
        foreach (var q in results.Questions)
        {
            foreach (var o in q.Options)
            {
                csv.Append(CultureInfo.InvariantCulture, $"{Csv(group)},{Csv(q.Prompt)},{Csv(o.Label)},{o.Count}\n");
            }
            if (q.Scale is { } scale)
            {
                for (var v = 0; v < scale.Count; v++)
                {
                    csv.Append(CultureInfo.InvariantCulture, $"{Csv(group)},{Csv(q.Prompt)},{v + 1},{scale[v]}\n");
                }
            }
            if (includeTexts && q.Texts is { } texts)
            {
                foreach (var text in texts)
                {
                    csv.Append(CultureInfo.InvariantCulture, $"{Csv(group)},{Csv(q.Prompt)},{Csv(text)},\n");
                }
            }
        }
    }

    /// <summary>Quotes a value, and neutralises a leading formula character so a spreadsheet never runs it.</summary>
    internal static string Csv(string value)
    {
        var safe = value.Length > 0 && "=+-@\t\r".Contains(value[0]) ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    }
}
