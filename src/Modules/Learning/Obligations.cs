using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Learning;

/// <summary>
/// Training other modules ask for (spec 028 US4, FR-011): an onboarding step (022) or a policy version (025) gives
/// courses with a due date. Each becomes an assignment like a rule's, collapsing with any open one to the earlier date
/// (FR-017), and an obligation row that is done when the person completes the course. A completion that still counts
/// settles it at once. Cancelling the source withdraws only the assignments nothing else asks for.
/// </summary>
public sealed class LearningObligations(
    IDbSession session,
    IEntitlementService entitlements,
    IWorkingCalendar calendars,
    IClock clock,
    Courses courses,
    Player player,
    AssignmentRules rules) : ILearningObligations
{
    public const int MaxSourceKeyLength = 200;
    public const int MaxCourses = 10;
    public static readonly IReadOnlyList<string> Sources = [AssignmentSources.Onboarding, AssignmentSources.Policy];

    public async Task<int> RequireAsync(IReadOnlyCollection<Guid> userIds, IReadOnlyCollection<Guid> courseIds, string source, string sourceKey, DateOnly dueOn,
        DateTimeOffset? completedSince = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sourceKey) || sourceKey.Length > MaxSourceKeyLength)
        {
            throw new ArgumentException("A source key of 1 to 200 characters is required.", nameof(sourceKey));
        }
        if (!Sources.Contains(source))
        {
            throw new ArgumentException("The source is onboarding or policy.", nameof(source));
        }
        if (!await entitlements.IsEnabledAsync(Capabilities.LearningMandatory, ct))
        {
            return 0;
        }
        var now = clock.UtcNow;
        foreach (var courseId in courseIds.Distinct().Take(MaxCourses))
        {
            if (await courses.RowAsync(courseId, false, ct) is not { Status: CourseStatuses.Published } course)
            {
                continue;
            }
            foreach (var user in userIds.Distinct())
            {
                var calendar = await calendars.ForUserAsync(user, ct);
                var due = AssignmentRules.NextWorkingDay(calendar, dueOn);
                var current = await player.CurrentAssignmentAsync(course.Id, user, false, ct);
                var counts = current is { Status: AssignmentStatuses.Completed } && Player.StateOf(current, course.RetrainingVersion) == LearnerStates.Completed;
                var settled = counts && (completedSince is null || current!.CompletedAt >= completedSince);
                var inserted = await session.ExecAsync(
                    """
                    insert into learning.obligation (tenant_id, id, user_id, course_id, source, source_key, due_on, status, created_at, done_at)
                    values (platform.current_tenant_id(), @id, @user, @course, @source, @sourceKey, @due, @status, @now, @doneAt)
                    on conflict do nothing
                    """, new { id = Ids.New(), user, course = course.Id, source, sourceKey, due, status = settled ? "done" : "open", now, doneAt = settled ? now : (DateTimeOffset?)null },
                    ct);
                if (inserted > 0 && !settled)
                {
                    await rules.GiveAsync(course, user, due, 0, source, ct, again: counts);
                }
            }
        }
        return await OpenCountAsync(sourceKey, ct);
    }

    public async Task<int> OpenCountAsync(string sourceKey, CancellationToken ct = default) =>
        (int)await session.ScalarAsync<long>(
            """
            select count(*) from learning.obligation o join learning.course c on c.id = o.course_id
            where o.source_key = @sourceKey and o.status = 'open' and c.status = 'published'
            """, new { sourceKey }, ct);

    public async Task CancelAsync(string sourceKey, CancellationToken ct = default)
    {
        var cancelled = await session.QueryAsync(
            "update learning.obligation set status = 'cancelled' where source_key = @sourceKey and status = 'open' returning user_id, course_id, source",
            new { sourceKey }, r => (User: r.GetGuid(0), Course: r.GetGuid(1), Source: r.GetString(2)), ct);
        foreach (var o in cancelled)
        {
            // Withdrawn only when nothing else asks for it: no other open obligation and no rule covering the person.
            await session.ExecAsync(
                """
                update learning.assignment a set status = 'withdrawn', ended_at = @now
                where a.course_id = @course and a.learner_id = @user and a.status = 'open' and a.source = @source
                  and not exists (select 1 from learning.obligation o where o.course_id = @course and o.user_id = @user and o.status = 'open')
                  and not exists (select 1 from learning.rule_target t join learning.assignment_rule r on r.id = t.rule_id
                    where r.course_id = @course and r.status = 'active' and t.learner_id = @user)
                """, new { course = o.Course, user = o.User, source = o.Source, now = clock.UtcNow }, ct);
        }
    }

    public async Task<IReadOnlySet<Guid>> PublishedAsync(IReadOnlyCollection<Guid> courseIds, CancellationToken ct = default) =>
        (await session.QueryAsync("select id from learning.course where id = any(@ids) and status = 'published'", new { ids = courseIds.Distinct().ToArray() },
            r => r.GetGuid(0), ct)).ToHashSet();

    public async Task<bool> CompletedAsync(Guid userId, Guid courseId, DateTimeOffset? since, CancellationToken ct = default)
    {
        if (await courses.RowAsync(courseId, false, ct) is not { } course)
        {
            return false;
        }
        var current = await player.CurrentAssignmentAsync(courseId, userId, false, ct);
        return current is { Status: AssignmentStatuses.Completed, CompletedAt: { } at } && (since is null || at >= since)
            && Player.StateOf(current, course.RetrainingVersion) == LearnerStates.Completed;
    }
}
