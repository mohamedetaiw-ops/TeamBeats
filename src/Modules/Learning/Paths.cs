using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public sealed record PathCourseInput(Guid? CourseId, bool RequiresPrevious);

public sealed record PathRequest(
    int? RowVersion,
    Guid? OrgUnitId,
    IReadOnlyDictionary<string, string>? Titles,
    IReadOnlyDictionary<string, string>? Summaries,
    ContentAudience? Audience,
    IReadOnlyList<PathCourseInput>? Courses);

public sealed record PathCourseView(Guid CourseId, IReadOnlyDictionary<string, string> Titles, string Status, bool RequiresPrevious, string State, int Percent, bool Locked);

public sealed record PathView(
    Guid Id,
    Guid? OrgUnitId,
    string Status,
    IReadOnlyDictionary<string, string> Titles,
    IReadOnlyDictionary<string, string> Summaries,
    ContentAudience Audience,
    IReadOnlyList<PathCourseView> Courses,
    int RowVersion,
    bool Manages);

public sealed record PathSummary(Guid Id, IReadOnlyDictionary<string, string> Titles, IReadOnlyDictionary<string, string> Summaries, string Status, int Courses, int Completed);

/// <summary>
/// Learning paths (spec 028 FR-004): courses in order, each of which may need the one before it completed. Learning
/// administrators build a path in their scope and publish it once every course in it is published; a published path
/// shows to its audience, and a learner starts its courses from it, also courses that are not open for enrolment on
/// their own. A course that needs the one before it stays locked until that one is completed.
/// </summary>
public sealed class Paths(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    IAudiences audiences,
    Courses courses,
    Player player,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxCourses = 30;
    public const int MaxSummary = 2000;

    private sealed record PathRow(Guid Id, Guid? OrgUnitId, string Status, string Titles, string Summaries, string Audience, int RowVersion)
    {
        public AuthzTarget Scope => OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

        public ContentAudience AudienceValue => LearningJson.Read<ContentAudience>(Audience);
    }

    private const string Columns = "id, org_unit_id, status, titles::text, summaries::text, audience::text, row_version";

    private static PathRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.Get<Guid?>("org_unit_id"), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.GetInt32(6));

    private Task<PathRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from learning.path where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    private async Task<bool> ManagesAsync(PathRow row, CancellationToken ct) => (await authz.CheckAsync(Permissions.LearningManage, row.Scope, ct)).Allowed;

    private async Task<Guid> RequireAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        var user = context.RequireUser();
        await authz.DemandAsync(Permissions.LearningTake, AuthzTarget.Tenant, ct);
        return user;
    }

    private async Task<PathRow> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        context.RequireUser();
        var row = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        return await ManagesAsync(row, ct) ? row : throw AppException.NotFound();
    }

    private async Task<List<(Guid CourseId, bool RequiresPrevious)>> CoursesOfAsync(Guid pathId, CancellationToken ct) =>
        await session.QueryAsync("select course_id, requires_previous from learning.path_course where path_id = @pathId order by position", new { pathId },
            r => (r.GetGuid(0), r.GetBoolean(1)), ct);

    private async Task<PathView> ViewAsync(PathRow row, Guid user, bool manages, CancellationToken ct)
    {
        var items = new List<PathCourseView>();
        var previousDone = true;
        foreach (var (courseId, requiresPrevious) in await CoursesOfAsync(row.Id, ct))
        {
            var course = (await courses.RowAsync(courseId, false, ct))!;
            var definition = course.PublishedVersion is { } v ? (await courses.VersionAsync(courseId, v, ct))! : course.DraftDefinition;
            var assignment = await session.QuerySingleOrDefaultAsync(
                $"""
                select {Player.AssignmentColumns} from learning.assignment
                where course_id = @courseId and learner_id = @user and status in ('open', 'completed')
                order by (status = 'open') desc, completed_at desc nulls last limit 1
                """, new { courseId, user }, Player.ReadAssignment, ct);
            var state = Player.StateOf(assignment, course.RetrainingVersion);
            var percent = state is LearnerStates.Open or LearnerStates.Completed
                ? Player.Percent(definition, (await session.QueryAsync("select lesson_key from learning.lesson_progress where assignment_id = @a", new { a = assignment!.Id },
                    r => r.GetString(0), ct)).ToHashSet(StringComparer.Ordinal))
                : 0;
            var locked = requiresPrevious && !previousDone && state == LearnerStates.None;
            items.Add(new PathCourseView(courseId, definition.Titles, course.Status, requiresPrevious, state, percent, locked));
            previousDone = state == LearnerStates.Completed;
        }
        return new PathView(row.Id, row.OrgUnitId, row.Status, LearningJson.Read<Dictionary<string, string>>(row.Titles),
            LearningJson.Read<Dictionary<string, string>>(row.Summaries), row.AudienceValue, items, row.RowVersion, manages);
    }

    // ---- Building ----

    private async Task<(Dictionary<string, string> Titles, Dictionary<string, string> Summaries, ContentAudience Audience, List<PathCourseInput> Courses)> CleanAsync(
        PathRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (locale, text) in request.Titles ?? new Dictionary<string, string>())
        {
            var clean = text?.Trim() ?? "";
            if (clean.Length == 0)
            {
                continue;
            }
            if (!enabled.Contains(locale) || clean.Length > CourseModel.MaxTitle)
            {
                errors.Add("titles", "validation.learning.path_title");
            }
            titles[locale] = clean;
        }
        if (titles.Count == 0)
        {
            errors.Add("titles", "validation.learning.path_title");
        }
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (locale, text) in request.Summaries ?? new Dictionary<string, string>())
        {
            var clean = text?.Trim() ?? "";
            if (clean.Length == 0)
            {
                continue;
            }
            if (!titles.ContainsKey(locale) || clean.Length > MaxSummary)
            {
                errors.Add("summaries", "validation.learning.path_summary");
            }
            summaries[locale] = clean;
        }
        var audience = request.Audience ?? ContentAudience.Everyone;
        audiences.Validate(audience, "audience", errors);
        var list = request.Courses ?? [];
        if (list.Count is 0 or > MaxCourses || list.Select(c => c.CourseId).Distinct().Count() != list.Count)
        {
            errors.Add("courses", "validation.learning.path_courses");
        }
        foreach (var item in list)
        {
            if (item.CourseId is not { } id || await courses.RowAsync(id, false, ct) is not { Status: not CourseStatuses.Retired })
            {
                errors.Add("courses", "validation.learning.path_course_unknown");
                break;
            }
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.learning.unit");
        }
        errors.ThrowIfAny();
        return (titles, summaries, audience, list.ToList());
    }

    private async Task DemandScopeAsync(Guid? unit, CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.LearningManage, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied("authz.denied.learning_scope");
        }
    }

    private async Task WriteCoursesAsync(Guid pathId, IReadOnlyList<PathCourseInput> list, CancellationToken ct)
    {
        await session.ExecAsync("delete from learning.path_course where path_id = @pathId", new { pathId }, ct);
        for (var i = 0; i < list.Count; i++)
        {
            await session.ExecAsync(
                "insert into learning.path_course (tenant_id, path_id, position, course_id, requires_previous) values (@tenant, @pathId, @i, @course, @requires)",
                new { tenant = context.RequireTenant(), pathId, i, course = list[i].CourseId!.Value, requires = i > 0 && list[i].RequiresPrevious }, ct);
        }
    }

    public async Task<PathView> CreateAsync(PathRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        var user = context.RequireUser();
        var (titles, summaries, audience, list) = await CleanAsync(request, ct);
        await DemandScopeAsync(request.OrgUnitId, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into learning.path (tenant_id, id, org_unit_id, titles, summaries, audience, created_by, created_at, updated_at)
            values (@tenant, @id, @unit, @titles, @summaries, @audience, @user, @now, @now)
            """, new
            {
                tenant = context.RequireTenant(), id, unit = request.OrgUnitId, titles = LearningJson.Jsonb(titles), summaries = LearningJson.Jsonb(summaries),
                audience = LearningJson.Jsonb(audience), user, now,
            }, ct);
        await WriteCoursesAsync(id, list, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, user, true, ct);
    }

    public async Task<PathView> UpdateAsync(Guid id, PathRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (request.RowVersion != row.RowVersion)
        {
            throw AppException.Conflict("conflict.learning.path_revision");
        }
        if (row.Status == CourseStatuses.Retired)
        {
            throw AppException.Conflict("conflict.learning.path_retired");
        }
        var (titles, summaries, audience, list) = await CleanAsync(request, ct);
        if (request.OrgUnitId != row.OrgUnitId)
        {
            await DemandScopeAsync(request.OrgUnitId, ct);
        }
        if (row.Status == CourseStatuses.Published)
        {
            await DemandPublishedCoursesAsync(list.Select(c => c.CourseId!.Value), ct);
        }
        await session.ExecAsync(
            """
            update learning.path set org_unit_id = @unit, titles = @titles, summaries = @summaries, audience = @audience, row_version = row_version + 1, updated_at = @now
            where id = @id
            """, new
            {
                id, unit = request.OrgUnitId, titles = LearningJson.Jsonb(titles), summaries = LearningJson.Jsonb(summaries), audience = LearningJson.Jsonb(audience),
                now = clock.UtcNow,
            }, ct);
        await WriteCoursesAsync(id, list, ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, context.RequireUser(), true, ct);
    }

    private async Task DemandPublishedCoursesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        foreach (var courseId in ids)
        {
            if (await courses.RowAsync(courseId, false, ct) is not { Status: CourseStatuses.Published })
            {
                throw AppException.Validation("courses", "validation.learning.path_course_unpublished");
            }
        }
    }

    public async Task<PathView> PublishAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (row.Status == CourseStatuses.Retired)
        {
            throw AppException.Conflict("conflict.learning.path_retired");
        }
        var list = await CoursesOfAsync(id, ct);
        await DemandPublishedCoursesAsync(list.Select(c => c.CourseId), ct);
        await session.ExecAsync("update learning.path set status = 'published', row_version = row_version + 1, updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningPathPublished, "learning_path", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { courses = list.Count }), ct);
        return await ViewAsync((await RowAsync(id, false, ct))!, context.RequireUser(), true, ct);
    }

    public async Task<IReadOnlyList<PathSummary>> ManagedListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        context.RequireUser();
        var rows = await session.QueryAsync($"select {Columns} from learning.path order by updated_at desc, id limit 500", null, Read, ct);
        var result = new List<PathSummary>();
        foreach (var row in rows)
        {
            if (await ManagesAsync(row, ct))
            {
                result.Add(new PathSummary(row.Id, LearningJson.Read<Dictionary<string, string>>(row.Titles), LearningJson.Read<Dictionary<string, string>>(row.Summaries),
                    row.Status, (await CoursesOfAsync(row.Id, ct)).Count, 0));
            }
        }
        return result;
    }

    // ---- Learning ----

    private async Task<(PathRow Row, bool Manages)> VisibleAsync(Guid id, Guid user, CancellationToken ct)
    {
        var row = await RowAsync(id, false, ct) ?? throw AppException.NotFound();
        var manages = await ManagesAsync(row, ct);
        if (!manages && (row.Status != CourseStatuses.Published || !await audiences.IncludesAsync(row.AudienceValue, user, ct)))
        {
            throw AppException.NotFound();
        }
        return (row, manages);
    }

    public async Task<PathView> GetAsync(Guid id, CancellationToken ct)
    {
        var user = await RequireAsync(ct);
        var (row, manages) = await VisibleAsync(id, user, ct);
        return await ViewAsync(row, user, manages, ct);
    }

    /// <summary>Published paths that target the viewer, with how many of their courses the viewer completed.</summary>
    public async Task<IReadOnlyList<PathSummary>> CatalogueAsync(CancellationToken ct)
    {
        var user = await RequireAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from learning.path where status = 'published' order by updated_at desc, id limit 200", null, Read, ct);
        var result = new List<PathSummary>();
        foreach (var row in rows)
        {
            if (await audiences.IncludesAsync(row.AudienceValue, user, ct))
            {
                var view = await ViewAsync(row, user, false, ct);
                result.Add(new PathSummary(row.Id, view.Titles, view.Summaries, row.Status, view.Courses.Count, view.Courses.Count(c => c.State == LearnerStates.Completed)));
            }
        }
        return result;
    }

    /// <summary>Starts a course of the path. A locked course (FR-004) is refused until the one before it is completed.</summary>
    public async Task<LearnerCourse> StartCourseAsync(Guid id, Guid courseId, CancellationToken ct)
    {
        var user = await RequireAsync(ct);
        var (row, _) = await VisibleAsync(id, user, ct);
        if (row.Status != CourseStatuses.Published)
        {
            throw AppException.NotFound();
        }
        var view = await ViewAsync(row, user, false, ct);
        var item = view.Courses.FirstOrDefault(c => c.CourseId == courseId) ?? throw AppException.NotFound();
        if (item.Locked)
        {
            throw AppException.Conflict("conflict.learning.path_locked");
        }
        return await player.EnrolAsync(courseId, true, ct);
    }
}
