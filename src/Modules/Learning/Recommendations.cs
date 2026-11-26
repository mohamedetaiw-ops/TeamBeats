using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Learning;

/// <summary>A recommended course and the learner's skills it is tagged with.</summary>
public sealed record RecommendedCourse(Guid Id, IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, string> Summaries, string? Category, int? Minutes,
    IReadOnlyList<ListedSkill> Skills);

/// <summary><see cref="Available"/> is false while the package has no skills or the tenant has them off; then nothing is recommended.</summary>
public sealed record RecommendationsView(bool Available, IReadOnlyList<RecommendedCourse> Items);

/// <summary>
/// Courses for the skills a learner has (spec 028 US9, FR-025). Published courses of TeamBeats that are open for
/// enrolment, target the learner and are tagged with a skill they declared below expert; courses they have open or
/// completed and ones they dismissed are left out. More matching skills first, then a basic skill before a good one,
/// then the newest. No model is used.
/// </summary>
public sealed class Recommendations(Player player, Courses courses, IDbSession session, IAudiences audiences, IPersonSkills skills, TeamBeats.TenantContext.ITenantContext context, IClock clock)
{
    public const int Max = 6;

    public async Task<RecommendationsView> ListAsync(CancellationToken ct)
    {
        var learner = await player.RequireLearnerAsync(ct);
        if (await skills.DeclaredAsync(learner, ct) is not { } declared || await skills.ListedAsync(ct) is not { } listed)
        {
            return new RecommendationsView(false, []);
        }
        // Lower rank is a skill to grow: basic is 1, good is 2; expert is not used.
        var growing = declared.Where(d => d.Proficiency != "expert").ToDictionary(d => d.SkillId, d => d.Proficiency == "basic" ? 1 : 2);
        if (growing.Count == 0)
        {
            return new RecommendationsView(true, []);
        }
        var names = listed.ToDictionary(s => s.Id);
        var rows = await session.QueryAsync(
            $"""
            select {Courses.Columns} from learning.course c
            where status = 'published' and open_enrolment and external_id is null and skills && @skills
              and not exists (select 1 from learning.recommendation_dismissal d where d.course_id = c.id and d.learner_id = @learner)
            order by updated_at desc, id limit 200
            """, new { skills = growing.Keys.ToArray(), learner }, Courses.Read, ct);
        var found = new List<(RecommendedCourse Course, int Matches, int Best, DateTimeOffset At)>();
        foreach (var row in rows)
        {
            if (!await audiences.IncludesAsync(row.AudienceValue, learner, ct) || await player.CurrentAssignmentAsync(row.Id, learner, false, ct) is not null)
            {
                continue;
            }
            var matched = (row.Skills ?? []).Where(growing.ContainsKey).ToList();
            var definition = (await courses.VersionAsync(row.Id, row.PublishedVersion!.Value, ct))!;
            found.Add((new RecommendedCourse(row.Id, definition.Titles, definition.Summaries, row.Category, definition.Minutes,
                matched.Where(names.ContainsKey).Select(id => names[id]).ToList()), matched.Count, matched.Min(id => growing[id]), row.UpdatedAt));
        }
        var items = found.OrderByDescending(f => f.Matches).ThenBy(f => f.Best).ThenByDescending(f => f.At).Take(Max).Select(f => f.Course).ToList();
        return new RecommendationsView(true, items);
    }

    /// <summary>The course is not recommended to the learner again.</summary>
    public async Task DismissAsync(Guid courseId, CancellationToken ct)
    {
        var learner = await player.RequireLearnerAsync(ct);
        if (await courses.RowAsync(courseId, false, ct) is not { Status: CourseStatuses.Published })
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync(
            """
            insert into learning.recommendation_dismissal (tenant_id, learner_id, course_id, dismissed_at)
            values (@tenant, @learner, @courseId, @now) on conflict do nothing
            """, new { tenant = context.RequireTenant(), learner, courseId, now = clock.UtcNow }, ct);
    }
}
