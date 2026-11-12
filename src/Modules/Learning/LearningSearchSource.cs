using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

/// <summary>
/// Published courses for search (spec 028 FR-018, spec 012 section 2). A course gives one unit per language of its
/// published version: the title, the summary, the category and the lesson titles. A person finds a course only when it
/// would show in their catalogue (its audience includes them) or they already have it, decided at query time.
/// </summary>
internal sealed class LearningSearchSource(IDbSession session, ITenantContext context, IAuthorizationService authz, IEntitlementService entitlements,
    IAudiences audiences, Courses courses) : ISearchSource
{
    public const string Name = "learning";

    public string Source => Name;

    public async Task<IReadOnlyList<SearchUnit>> UnitsAsync(Guid itemId, CancellationToken ct = default)
    {
        if (await courses.RowAsync(itemId, false, ct) is not { Status: CourseStatuses.Published, PublishedVersion: { } version } row
            || await courses.VersionAsync(itemId, version, ct) is not { } definition)
        {
            return [];
        }
        return [.. definition.Titles.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t =>
        {
            var text = string.Join("\n", new[] { definition.Summaries.GetValueOrDefault(t.Key), row.Category }
                .Concat(definition.Lessons.Select(l => l.Titles.GetValueOrDefault(t.Key)))
                .Where(x => !string.IsNullOrWhiteSpace(x)));
            return new SearchUnit(t.Key, SearchKinds.Course, t.Key, t.Value, text, Courses.CoursePath(itemId), "internal", row.OrgUnitId, version, row.UpdatedAt);
        })];
    }

    public async Task<IReadOnlyList<Guid>> ItemIdsAsync(Guid? after, int limit, CancellationToken ct = default) =>
        await session.QueryAsync(
            "select id from learning.course where status = 'published' and (@after::uuid is null or id > @after) order by id limit @limit",
            new { after, limit }, r => r.GetGuid(0), ct);

    public async Task<IReadOnlySet<Guid>> ReadableAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken ct = default)
    {
        var readable = new HashSet<Guid>();
        if (itemIds.Count == 0 || context.ActorType != ActorType.User || context.ActorId is not { } user || !await entitlements.IsEnabledAsync(Capabilities.LearningCourses, ct)
            || !(await authz.CheckAsync(Permissions.LearningTake, AuthzTarget.Tenant, ct)).Allowed)
        {
            return readable;
        }
        var rows = await session.QueryAsync(
            """
            select c.id, c.audience::text, exists (select 1 from learning.assignment a where a.course_id = c.id and a.learner_id = @user and a.status <> 'withdrawn')
            from learning.course c where c.id = any(@ids) and c.status = 'published'
            """, new { ids = itemIds.ToArray(), user }, r => (Id: r.GetGuid(0), Audience: r.GetString(1), Has: r.GetBoolean(2)), ct);
        foreach (var row in rows)
        {
            if (row.Has || await audiences.IncludesAsync(LearningJson.Read<ContentAudience>(row.Audience), user, ct))
            {
                readable.Add(row.Id);
            }
        }
        return readable;
    }
}
