using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public sealed record QuizStanding(string LessonKey, int Used, int Allowed, int? BestScore, bool Passed);

public sealed record CourseLearner(Guid AssignmentId, CertificatePerson Learner, string Source, string Status, DateOnly? DueOn, int Percent, DateTimeOffset? CompletedAt,
    int? CompletedVersion, IReadOnlyList<QuizStanding> Quizzes);

public sealed record GrantAttemptsRequest(string? LessonKey, int? Extra);

/// <summary>
/// The people taking a course, for the learning administrators who manage it (spec 028 US1 scenario 2, FR-019): their
/// assignment, progress and quiz attempts, and extra attempts for someone who used them all, with audit.
/// </summary>
public sealed class CourseLearners(IDbSession session, ITenantContext context, Courses courses, IUserDirectory users, IAuditWriter audit, IClock clock)
{
    public const int MaxExtra = 10;

    private async Task<(Courses.CourseRow Row, CourseDefinition Definition)> ManagedAsync(Guid courseId, CancellationToken ct)
    {
        var detail = await courses.GetAsync(courseId, ct);
        var row = (await courses.RowAsync(courseId, false, ct))!;
        var definition = detail.PublishedVersion is { } v ? (await courses.VersionAsync(courseId, v, ct))! : detail.Draft;
        return (row, definition);
    }

    public async Task<IReadOnlyList<CourseLearner>> ListAsync(Guid courseId, CancellationToken ct)
    {
        var (_, definition) = await ManagedAsync(courseId, ct);
        var rows = await session.QueryAsync(
            $"select {Player.AssignmentColumns} from learning.assignment where course_id = @courseId and status in ('open', 'completed') order by created_at desc limit 1000",
            new { courseId }, Player.ReadAssignment, ct);
        var people = await users.GetManyAsync(rows.Select(r => r.LearnerId).Distinct().ToList(), ct);
        var result = new List<CourseLearner>();
        foreach (var row in rows)
        {
            var done = (await session.QueryAsync("select lesson_key from learning.lesson_progress where assignment_id = @a", new { a = row.Id }, r => r.GetString(0), ct))
                .ToHashSet(StringComparer.Ordinal);
            var quizzes = new List<QuizStanding>();
            foreach (var lesson in definition.Lessons.Where(l => l.Quiz is not null))
            {
                var (used, best) = await session.QuerySingleOrDefaultAsync(
                    "select count(*)::int, max(score) from learning.attempt where assignment_id = @a and lesson_key = @key", new { a = row.Id, key = lesson.Key },
                    r => (r.GetInt32(0), r.Get<int?>("max")), ct);
                var extra = await session.ScalarAsync<int?>("select sum(extra)::int from learning.attempt_grant where assignment_id = @a and lesson_key = @key",
                    new { a = row.Id, key = lesson.Key }, ct) ?? 0;
                quizzes.Add(new QuizStanding(lesson.Key, used, lesson.Quiz!.Attempts + extra, best, done.Contains(lesson.Key)));
            }
            var name = people.TryGetValue(row.LearnerId, out var person) ? person.DisplayName : "";
            result.Add(new CourseLearner(row.Id, new CertificatePerson(row.LearnerId, name), row.Source, row.Status, row.DueOn, Player.Percent(definition, done),
                row.CompletedAt, row.CompletedVersion, quizzes));
        }
        return result;
    }

    /// <summary>Gives a learner more attempts at a quiz of the course (FR-019). Audited with the learner, the course and the number.</summary>
    public async Task<IReadOnlyList<CourseLearner>> GrantAsync(Guid courseId, Guid assignmentId, GrantAttemptsRequest request, CancellationToken ct)
    {
        var (row, definition) = await ManagedAsync(courseId, ct);
        var assignment = await session.QuerySingleOrDefaultAsync(
            $"select {Player.AssignmentColumns} from learning.assignment where id = @assignmentId and course_id = @courseId for update", new { assignmentId, courseId },
            Player.ReadAssignment, ct) ?? throw AppException.NotFound();
        if (assignment.Status != AssignmentStatuses.Open)
        {
            throw AppException.Conflict("conflict.learning.assignment_closed");
        }
        var errors = new ValidationErrors();
        if (definition.Lessons.FirstOrDefault(l => l.Key == request.LessonKey && l.Quiz is not null) is null)
        {
            errors.Add("lessonKey", "validation.learning.grant_lesson");
        }
        if (request.Extra is not (>= 1 and <= MaxExtra))
        {
            errors.Add("extra", "validation.learning.grant_extra");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into learning.attempt_grant (tenant_id, id, assignment_id, lesson_key, extra, granted_by, created_at)
            values (@tenant, @id, @assignmentId, @key, @extra, @user, @now)
            """, new { tenant = context.RequireTenant(), id = Ids.New(), assignmentId, key = request.LessonKey, extra = request.Extra, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningAttemptsGranted, "learning_assignment", assignmentId.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { course = courseId, learner = assignment.LearnerId, lesson = request.LessonKey, extra = request.Extra }), ct);
        return await ListAsync(courseId, ct);
    }
}
