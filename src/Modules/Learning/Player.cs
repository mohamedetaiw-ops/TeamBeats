using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public static class AssignmentStatuses
{
    public const string Open = "open";
    public const string Completed = "completed";
    public const string Withdrawn = "withdrawn";
    public const string Expired = "expired";
}

public static class AssignmentSources
{
    public const string Self = "self";
    public const string Rule = "rule";
    public const string Manager = "manager";
    public const string Onboarding = "onboarding";
    public const string Policy = "policy";
}

/// <summary>Where the learner stands: not started, an open assignment, completed, or completed on a version that retraining replaced.</summary>
public static class LearnerStates
{
    public const string None = "none";
    public const string Open = "open";
    public const string Completed = "completed";
    public const string Retraining = "retraining";
}

public sealed record LearnerOption(string Key, IReadOnlyDictionary<string, string> Labels);

public sealed record LearnerQuestion(string Key, string Kind, IReadOnlyDictionary<string, string> Prompts, IReadOnlyList<LearnerOption> Options);

/// <summary>A quiz as the learner sees it: never which options are correct.</summary>
public sealed record LearnerQuiz(int PassMark, int Attempts, int AttemptsUsed, int AttemptsLeft, int? BestScore, IReadOnlyList<LearnerQuestion> Questions);

public sealed record LearnerLesson(string Key, string Kind, IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, Guid>? Sources, int? Minutes,
    bool Done, LearnerQuiz? Quiz);

public sealed record LearnerState(
    string State,
    Guid? AssignmentId,
    string? Source,
    DateOnly? DueOn,
    string? LastLessonKey,
    int Percent,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? CompletedVersion,
    Guid? CertificateId);

public sealed record LearnerCourse(
    Guid Id,
    int Version,
    string? Category,
    IReadOnlyDictionary<string, string> Titles,
    IReadOnlyDictionary<string, string> Summaries,
    int? Minutes,
    IReadOnlyList<string> Locales,
    IReadOnlyList<LearnerLesson> Lessons,
    LearnerState Me,
    bool CanEnrol,
    bool Manages);

public sealed record PositionRequest(string? LessonKey);

public sealed record AttemptResult(int Score, bool Passed, LearnerCourse Course);

public sealed record CatalogueCourse(Guid Id, IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, string> Summaries, string? Category, int? Minutes,
    int Lessons, bool OpenEnrolment, string State, int Percent);

public sealed record MyCourse(Guid CourseId, Guid AssignmentId, IReadOnlyDictionary<string, string> Titles, string? Category, int? Minutes, string Source,
    DateOnly? DueOn, string State, int Percent, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, Guid? CertificateId);

/// <summary>My learning (US3 scenario 1): assigned courses by due date, courses in progress, and completed ones with certificates.</summary>
public sealed record MyLearning(IReadOnlyList<MyCourse> Assigned, IReadOnlyList<MyCourse> InProgress, IReadOnlyList<MyCourse> Completed);

/// <summary>
/// The course player and My learning (spec 028 US3, FR-002, FR-003, FR-009, FR-010, FR-018, FR-019). Learners see the
/// latest published version of a course that targets them, that they were assigned, or that a path they follow gives
/// them; they enrol themselves in open courses. Finishing a page, document or video lesson is recorded only when the
/// learner may read its source; a quiz is scored on the server and counts toward its attempt limit, plus any extra
/// attempts a learning administrator granted. When every lesson of the current version is done the assignment is
/// completed, a certificate is issued and 022 and 025 hear about it. Completions never come from a client parameter.
/// </summary>
public sealed class Player(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAudiences audiences,
    IPublishedPages pages,
    IDocumentReferences documents,
    Courses courses,
    IAnalyticsEvents analytics,
    IOutbox outbox,
    IClock clock)
{
    internal sealed record AssignmentRow(Guid Id, Guid CourseId, Guid LearnerId, string Source, string Status, DateOnly? DueOn, int Cycle, string? LastLessonKey,
        DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int? CompletedVersion, DateTimeOffset CreatedAt);

    internal const string AssignmentColumns = "id, course_id, learner_id, source, status, due_on, cycle, last_lesson_key, started_at, completed_at, completed_version, created_at";

    internal static AssignmentRow ReadAssignment(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4),
        r.Get<DateOnly?>("due_on"), r.GetInt32(6), r.Get<string>("last_lesson_key"), r.Get<DateTimeOffset?>("started_at"), r.Get<DateTimeOffset?>("completed_at"),
        r.Get<int?>("completed_version"), r.GetFieldValue<DateTimeOffset>(11));

    private async Task<Guid> RequireLearnerAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        var user = context.RequireUser();
        await authz.DemandAsync(Permissions.LearningTake, AuthzTarget.Tenant, ct);
        return user;
    }

    /// <summary>The learner's open assignment, else their latest completed one.</summary>
    private Task<AssignmentRow?> CurrentAssignmentAsync(Guid courseId, Guid learner, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"""
            select {AssignmentColumns} from learning.assignment
            where course_id = @courseId and learner_id = @learner and status in ('open', 'completed')
            order by (status = 'open') desc, completed_at desc nulls last limit 1{(forUpdate ? " for update" : "")}
            """, new { courseId, learner }, ReadAssignment, ct);

    /// <summary>A published course the learner may open: it targets them, they have an assignment, or they manage it. Otherwise 404.</summary>
    private async Task<(Courses.CourseRow Row, CourseDefinition Definition, AssignmentRow? Assignment, bool Manages)> OpenCourseAsync(Guid id, Guid learner, bool forUpdate,
        CancellationToken ct)
    {
        var row = await courses.RowAsync(id, false, ct) ?? throw AppException.NotFound();
        if (row.Status != CourseStatuses.Published || row.PublishedVersion is not { } version)
        {
            throw AppException.NotFound();
        }
        var assignment = await CurrentAssignmentAsync(id, learner, forUpdate, ct);
        var manages = await courses.ManagesAsync(row, ct);
        if (assignment is null && !manages && !await audiences.IncludesAsync(row.AudienceValue, learner, ct) && !await PathGivesAsync(id, learner, ct))
        {
            throw AppException.NotFound();
        }
        return (row, (await courses.VersionAsync(id, version, ct))!, assignment, manages);
    }

    /// <summary>Whether a published path that targets the learner holds the course.</summary>
    private async Task<bool> PathGivesAsync(Guid courseId, Guid learner, CancellationToken ct)
    {
        var paths = await session.QueryAsync(
            """
            select p.audience::text from learning.path p join learning.path_course pc on pc.path_id = p.id
            where pc.course_id = @courseId and p.status = 'published'
            """, new { courseId }, r => LearningJson.Read<ContentAudience>(r.GetString(0)), ct);
        foreach (var audience in paths)
        {
            if (await audiences.IncludesAsync(audience, learner, ct))
            {
                return true;
            }
        }
        return false;
    }

    public async Task<LearnerCourse> GetAsync(Guid id, CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var (row, definition, assignment, manages) = await OpenCourseAsync(id, learner, false, ct);
        return await ViewAsync(row, definition, assignment, learner, manages, ct);
    }

    private async Task<HashSet<string>> DoneAsync(Guid? assignment, CancellationToken ct) => assignment is not { } a
        ? []
        : (await session.QueryAsync("select lesson_key from learning.lesson_progress where assignment_id = @a", new { a }, r => r.GetString(0), ct)).ToHashSet(StringComparer.Ordinal);

    private async Task<(int Used, int Extra, int? Best)> AttemptsAsync(Guid? assignment, string lesson, CancellationToken ct)
    {
        if (assignment is not { } a)
        {
            return (0, 0, null);
        }
        var (used, best) = await session.QuerySingleOrDefaultAsync(
            "select count(*)::int, max(score) from learning.attempt where assignment_id = @a and lesson_key = @lesson", new { a, lesson },
            r => (r.GetInt32(0), r.Get<int?>("max")), ct);
        var extra = await session.ScalarAsync<int?>("select sum(extra)::int from learning.attempt_grant where assignment_id = @a and lesson_key = @lesson", new { a, lesson }, ct) ?? 0;
        return (used, extra, best);
    }

    internal static string StateOf(AssignmentRow? assignment, int? retrainingVersion) => assignment switch
    {
        null => LearnerStates.None,
        { Status: AssignmentStatuses.Open } => LearnerStates.Open,
        { CompletedVersion: { } done } when retrainingVersion is { } r && done < r => LearnerStates.Retraining,
        _ => LearnerStates.Completed,
    };

    internal static int Percent(CourseDefinition definition, IReadOnlySet<string> done) =>
        definition.Lessons.Count == 0 ? 0 : definition.Lessons.Count(l => done.Contains(l.Key)) * 100 / definition.Lessons.Count;

    private async Task<LearnerCourse> ViewAsync(Courses.CourseRow row, CourseDefinition definition, AssignmentRow? assignment, Guid learner, bool manages, CancellationToken ct)
    {
        var state = StateOf(assignment, row.RetrainingVersion);
        // A completion replaced by retraining shows as not started: the lessons are to be taken again.
        var active = state is LearnerStates.Open or LearnerStates.Completed ? assignment : null;
        var done = await DoneAsync(active?.Id, ct);
        var lessons = new List<LearnerLesson>();
        foreach (var lesson in definition.Lessons)
        {
            LearnerQuiz? quiz = null;
            if (lesson.Quiz is { } q)
            {
                var (used, extra, best) = await AttemptsAsync(active?.Id, lesson.Key, ct);
                quiz = new LearnerQuiz(q.PassMark, q.Attempts + extra, used, Math.Max(0, q.Attempts + extra - used), best,
                    q.Questions.Select(x => new LearnerQuestion(x.Key, x.Kind, x.Prompts, x.Options.Select(o => new LearnerOption(o.Key, o.Labels)).ToList())).ToList());
            }
            lessons.Add(new LearnerLesson(lesson.Key, lesson.Kind, lesson.Titles, lesson.Sources, lesson.Minutes, done.Contains(lesson.Key), quiz));
        }
        var certificate = active is { Status: AssignmentStatuses.Completed }
            ? await session.ScalarAsync<Guid?>("select id from learning.certificate where assignment_id = @id", new { id = active.Id }, ct)
            : null;
        var me = new LearnerState(state, active?.Id, active?.Source, active?.DueOn, active?.LastLessonKey, Percent(definition, done), active?.StartedAt,
            active?.CompletedAt, active?.CompletedVersion, certificate);
        var canEnrol = state is LearnerStates.None or LearnerStates.Retraining && row.OpenEnrolment && await audiences.IncludesAsync(row.AudienceValue, learner, ct);
        return new LearnerCourse(row.Id, row.PublishedVersion!.Value, row.Category, definition.Titles, definition.Summaries, definition.Minutes, definition.Locales, lessons,
            me, canEnrol, manages);
    }

    // ---- Enrolment ----

    /// <summary>
    /// Self enrolment in an open course that targets the learner (US3 scenario 3), or in a course of a path the learner follows. An
    /// open assignment is returned as it is; after retraining a new cycle starts.
    /// </summary>
    public Task<LearnerCourse> EnrolAsync(Guid id, CancellationToken ct) => EnrolAsync(id, false, ct);

    /// <summary>With <paramref name="fromPath"/> the caller (a path) has checked the course is unlocked for the learner.</summary>
    internal async Task<LearnerCourse> EnrolAsync(Guid id, bool fromPath, CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var (row, definition, assignment, manages) = await OpenCourseAsync(id, learner, true, ct);
        var state = StateOf(assignment, row.RetrainingVersion);
        if (state is LearnerStates.Open or LearnerStates.Completed)
        {
            return await ViewAsync(row, definition, assignment, learner, manages, ct);
        }
        if (!(row.OpenEnrolment && await audiences.IncludesAsync(row.AudienceValue, learner, ct)) && !(fromPath && await PathGivesAsync(id, learner, ct)))
        {
            throw AppException.Denied("authz.denied.learning_enrolment_closed");
        }
        var created = await AssignAsync(id, learner, AssignmentSources.Self, null, ct);
        return await ViewAsync(row, definition, created, learner, manages, ct);
    }

    /// <summary>Creates the learner's open assignment of the course, in the cycle after their last one.</summary>
    internal async Task<AssignmentRow> AssignAsync(Guid courseId, Guid learner, string source, DateOnly? dueOn, CancellationToken ct)
    {
        var id = Ids.New();
        var row = await session.QuerySingleOrDefaultAsync(
            $"""
            insert into learning.assignment (tenant_id, id, course_id, learner_id, source, due_on, cycle, created_at)
            values (@tenant, @id, @courseId, @learner, @source, @dueOn,
              coalesce((select max(cycle) from learning.assignment where course_id = @courseId and learner_id = @learner), 0) + 1, @now)
            on conflict (tenant_id, course_id, learner_id) where status = 'open' do nothing
            returning {AssignmentColumns}
            """, new { tenant = context.RequireTenant(), id, courseId, learner, source, dueOn, now = clock.UtcNow }, ReadAssignment, ct);
        if (row is null)
        {
            return (await CurrentAssignmentAsync(courseId, learner, false, ct))!;
        }
        await outbox.EnqueueAsync(new CourseAssigned(row.Id, courseId, learner, source, dueOn), ct);
        return row;
    }

    private async Task<(Courses.CourseRow Row, CourseDefinition Definition, AssignmentRow Assignment, LessonDefinition Lesson)> OpenLessonAsync(Guid id, string key, Guid learner,
        CancellationToken ct)
    {
        var (row, definition, assignment, _) = await OpenCourseAsync(id, learner, true, ct);
        if (assignment is not { Status: AssignmentStatuses.Open })
        {
            throw AppException.Conflict(assignment is null || StateOf(assignment, row.RetrainingVersion) == LearnerStates.Retraining
                ? "conflict.learning.not_enrolled"
                : "conflict.learning.already_completed");
        }
        var lesson = definition.Lessons.FirstOrDefault(l => l.Key == key) ?? throw AppException.NotFound();
        return (row, definition, assignment, lesson);
    }

    private async Task StartAsync(AssignmentRow assignment, string lessonKey, CancellationToken ct)
    {
        await session.ExecAsync("update learning.assignment set last_lesson_key = @lessonKey, started_at = coalesce(started_at, @now) where id = @id",
            new { id = assignment.Id, lessonKey, now = clock.UtcNow }, ct);
        if (assignment.StartedAt is null)
        {
            await analytics.EmitAsync(AnalyticsEvents.LearningProgress, new Dictionary<string, object?> { ["courseId"] = assignment.CourseId, ["status"] = "started" }, ct);
        }
    }

    /// <summary>Where the learner is, so they resume at the last lesson on any device (US3 scenario 2).</summary>
    public async Task<LearnerCourse> PositionAsync(Guid id, PositionRequest request, CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var (row, definition, assignment, lesson) = await OpenLessonAsync(id, request.LessonKey ?? "", learner, ct);
        await StartAsync(assignment, lesson.Key, ct);
        return await ViewAsync(row, definition, await CurrentAssignmentAsync(id, learner, false, ct), learner, false, ct);
    }

    /// <summary>Marks a page, document or video lesson done. The learner must be able to read its source in one of the course's languages.</summary>
    public async Task<LearnerCourse> CompleteLessonAsync(Guid id, string key, CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var (row, definition, assignment, lesson) = await OpenLessonAsync(id, key, learner, ct);
        if (lesson.Kind == LessonKinds.Quiz)
        {
            throw AppException.Conflict("conflict.learning.quiz_needs_attempt");
        }
        var readable = false;
        foreach (var source in lesson.Sources!.Values.Distinct())
        {
            if (lesson.Kind == LessonKinds.Page ? await pages.ReadableAsync(source, ct) is not null : await documents.ReadableAsync(source, ct) is not null)
            {
                readable = true;
                break;
            }
        }
        if (!readable)
        {
            throw AppException.Conflict("conflict.learning.source_unavailable");
        }
        await StartAsync(assignment, lesson.Key, ct);
        await DoneLessonAsync(assignment, lesson.Key, ct);
        var completed = await CompleteIfDoneAsync(row, definition, assignment, ct);
        return await ViewAsync(row, definition, completed, learner, false, ct);
    }

    private async Task DoneLessonAsync(AssignmentRow assignment, string key, CancellationToken ct)
    {
        var added = await session.ExecAsync(
            "insert into learning.lesson_progress (tenant_id, assignment_id, lesson_key, completed_at) values (@tenant, @a, @key, @now) on conflict do nothing",
            new { tenant = context.RequireTenant(), a = assignment.Id, key, now = clock.UtcNow }, ct);
        if (added > 0)
        {
            await analytics.EmitAsync(AnalyticsEvents.LearningProgress,
                new Dictionary<string, object?> { ["courseId"] = assignment.CourseId, ["lesson"] = key, ["status"] = "lesson_completed" }, ct);
        }
    }

    /// <summary>
    /// Scores a quiz attempt (US1 scenario 2). An attempt beyond the limit, or on a quiz already passed, is refused. A
    /// pass finishes the lesson; the score is kept and the answers are not.
    /// </summary>
    public async Task<AttemptResult> AttemptAsync(Guid id, string key, QuizAnswers answers, CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var (row, definition, assignment, lesson) = await OpenLessonAsync(id, key, learner, ct);
        if (lesson.Quiz is not { } quiz)
        {
            throw AppException.Conflict("conflict.learning.not_a_quiz");
        }
        if ((await DoneAsync(assignment.Id, ct)).Contains(lesson.Key))
        {
            throw AppException.Conflict("conflict.learning.quiz_passed");
        }
        var (used, extra, _) = await AttemptsAsync(assignment.Id, lesson.Key, ct);
        if (used >= quiz.Attempts + extra)
        {
            throw AppException.Conflict("conflict.learning.attempts_used");
        }
        var (score, passed) = CourseModel.Score(quiz, answers);
        await StartAsync(assignment, lesson.Key, ct);
        await session.ExecAsync(
            """
            insert into learning.attempt (tenant_id, id, assignment_id, lesson_key, course_version, score, passed, created_at)
            values (@tenant, @id, @a, @key, @version, @score, @passed, @now)
            """, new { tenant = context.RequireTenant(), id = Ids.New(), a = assignment.Id, key = lesson.Key, version = row.PublishedVersion, score, passed, now = clock.UtcNow }, ct);
        await analytics.EmitAsync(AnalyticsEvents.LearningProgress,
            new Dictionary<string, object?> { ["courseId"] = id, ["lesson"] = lesson.Key, ["status"] = passed ? "quiz_passed" : "quiz_failed" }, ct);
        var current = assignment;
        if (passed)
        {
            await DoneLessonAsync(assignment, lesson.Key, ct);
            current = await CompleteIfDoneAsync(row, definition, assignment, ct);
        }
        return new AttemptResult(score, passed, await ViewAsync(row, definition, current, learner, false, ct));
    }

    /// <summary>Completes the assignment and issues the certificate once every lesson of the current version is done.</summary>
    private async Task<AssignmentRow> CompleteIfDoneAsync(Courses.CourseRow row, CourseDefinition definition, AssignmentRow assignment, CancellationToken ct)
    {
        var done = await DoneAsync(assignment.Id, ct);
        if (!definition.Lessons.All(l => done.Contains(l.Key)))
        {
            return (await CurrentAssignmentAsync(row.Id, assignment.LearnerId, false, ct))!;
        }
        var now = clock.UtcNow;
        var version = row.PublishedVersion!.Value;
        await session.ExecAsync("update learning.assignment set status = 'completed', completed_at = @now, completed_version = @version where id = @id and status = 'open'",
            new { id = assignment.Id, now, version }, ct);
        var certificate = Ids.New();
        await session.ExecAsync(
            """
            insert into learning.certificate (tenant_id, id, assignment_id, course_id, learner_id, version, code, issued_at)
            values (@tenant, @certificate, @a, @course, @learner, @version, @code, @now)
            """, new
            {
                tenant = context.RequireTenant(), certificate, a = assignment.Id, course = row.Id, learner = assignment.LearnerId, version, code = Certificates.NewCode(), now,
            }, ct);
        await analytics.EmitAsync(AnalyticsEvents.LearningProgress, new Dictionary<string, object?> { ["courseId"] = row.Id, ["status"] = "course_completed" }, ct);
        await outbox.EnqueueAsync(new CourseCompleted(assignment.Id, row.Id, assignment.LearnerId, version), ct);
        await outbox.EnqueueAsync(new CertificateIssued(certificate, row.Id, assignment.LearnerId), ct);
        return (await CurrentAssignmentAsync(row.Id, assignment.LearnerId, false, ct))!;
    }

    // ---- Catalogue and My learning ----

    /// <summary>Published courses that target the viewer (FR-018), with their own state, filtered by a search text in any language.</summary>
    public async Task<IReadOnlyList<CatalogueCourse>> CatalogueAsync(string? q, CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var rows = await session.QueryAsync($"select {Courses.Columns} from learning.course where status = 'published' order by updated_at desc, id limit 500", null, Courses.Read, ct);
        var text = q?.Trim();
        var result = new List<CatalogueCourse>();
        foreach (var row in rows)
        {
            if (!await audiences.IncludesAsync(row.AudienceValue, learner, ct))
            {
                continue;
            }
            var definition = (await courses.VersionAsync(row.Id, row.PublishedVersion!.Value, ct))!;
            if (!string.IsNullOrEmpty(text) && !definition.Titles.Values.Concat(definition.Summaries.Values).Append(row.Category ?? "")
                    .Any(t => t.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
            {
                continue;
            }
            var assignment = await CurrentAssignmentAsync(row.Id, learner, false, ct);
            var state = StateOf(assignment, row.RetrainingVersion);
            var percent = state is LearnerStates.Open or LearnerStates.Completed ? Percent(definition, await DoneAsync(assignment!.Id, ct)) : 0;
            result.Add(new CatalogueCourse(row.Id, definition.Titles, definition.Summaries, row.Category, definition.Minutes, definition.Lessons.Count, row.OpenEnrolment, state,
                percent));
        }
        return result;
    }

    public async Task<MyLearning> MineAsync(CancellationToken ct)
    {
        var learner = await RequireLearnerAsync(ct);
        var rows = await session.QueryAsync(
            $"""
            select {AssignmentColumns} from learning.assignment a
            where learner_id = @learner and status in ('open', 'completed')
            order by created_at desc limit 500
            """, new { learner }, ReadAssignment, ct);
        var assigned = new List<MyCourse>();
        var inProgress = new List<MyCourse>();
        var completed = new List<MyCourse>();
        var seen = new HashSet<Guid>();
        foreach (var assignment in rows.OrderBy(a => a.Status == AssignmentStatuses.Open ? 0 : 1))
        {
            // One line per course: the open assignment, else the latest completion.
            if (!seen.Add(assignment.CourseId) || await courses.RowAsync(assignment.CourseId, false, ct) is not { PublishedVersion: { } version } course)
            {
                continue;
            }
            var definition = (await courses.VersionAsync(course.Id, version, ct))!;
            var state = StateOf(assignment, course.RetrainingVersion);
            var done = await DoneAsync(assignment.Id, ct);
            var certificate = assignment.Status == AssignmentStatuses.Completed
                ? await session.ScalarAsync<Guid?>("select id from learning.certificate where assignment_id = @id", new { id = assignment.Id }, ct)
                : null;
            var item = new MyCourse(course.Id, assignment.Id, definition.Titles, course.Category, definition.Minutes, assignment.Source, assignment.DueOn, state,
                state == LearnerStates.Retraining ? 0 : Percent(definition, done), assignment.StartedAt, assignment.CompletedAt, certificate);
            if (assignment.Status == AssignmentStatuses.Completed)
            {
                completed.Add(item);
            }
            else if (assignment.Source != AssignmentSources.Self)
            {
                assigned.Add(item);
            }
            else
            {
                inProgress.Add(item);
            }
        }
        return new MyLearning(
            assigned.OrderBy(c => c.DueOn ?? DateOnly.MaxValue).ThenBy(c => c.AssignmentId).ToList(),
            inProgress.OrderByDescending(c => c.StartedAt ?? DateTimeOffset.MinValue).ToList(),
            completed.OrderByDescending(c => c.CompletedAt).ToList());
    }
}
