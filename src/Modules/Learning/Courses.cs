using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public static class CourseStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Retired = "retired";
}

public sealed record CourseRequest(
    int? Revision,
    Guid? OrgUnitId,
    string? Category,
    ContentAudience? Audience,
    bool OpenEnrolment,
    CourseDefinitionInput? Definition);

public sealed record PublishRequest(int? Revision, bool RequiresRetraining);

/// <summary>A lesson whose page or file changed since the course was last published, in one language.</summary>
public sealed record ChangedSource(string LessonKey, string Locale);

/// <summary>What a learning administrator sees in the builder.</summary>
public sealed record CourseDetail(
    Guid Id,
    Guid? OrgUnitId,
    IReadOnlyDictionary<string, string>? UnitNames,
    string? Category,
    string Status,
    ContentAudience Audience,
    bool OpenEnrolment,
    CourseDefinition Draft,
    int Revision,
    bool DraftChanged,
    int? PublishedVersion,
    IReadOnlyList<ChangedSource> ChangedSources,
    DateTimeOffset UpdatedAt);

public sealed record CourseSummary(
    Guid Id,
    IReadOnlyDictionary<string, string> Titles,
    string? Category,
    string Status,
    Guid? OrgUnitId,
    int? PublishedVersion,
    bool DraftChanged,
    int Lessons,
    DateTimeOffset UpdatedAt);

/// <summary>What the viewer may do in learning; <paramref name="Team"/>: they have direct reports to follow.</summary>
public sealed record LearningRights(bool Take, bool Manage, bool Report, bool Team = false);

/// <summary>
/// The course builder (spec 028 US1, FR-001 to FR-003). Learning administrators with <c>learning.manage</c> on a
/// course's org unit (or the tenant for a tenant-wide course) build its draft from referenced pages, documents and
/// videos and quizzes, and publish it as a new version. Publishing checks every source is published, readable and, for
/// a video lesson, a video, and keeps the version of each source; learners see the latest published version and the
/// draft stays with the builders. "Requires retraining" on a version means earlier completions no longer count
/// (FR-012). Retiring a course withdraws its open assignments and keeps completions.
/// </summary>
public sealed class Courses(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    IAudiences audiences,
    IPublishedPages pages,
    IDocumentReferences documents,
    IOutbox outbox,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock,
    People.Contracts.IReportingLine reportingLine)
{
    public const int MaxCategory = 60;
    public const string WithdrawnTemplate = "learning.course.withdrawn";
    public const string MyLearningPath = "/learning";

    public static string CoursePath(Guid id) => $"/learning/courses/{id}";

    internal sealed record CourseRow(Guid Id, Guid? OrgUnitId, string? Category, string Status, string Draft, int DraftRevision, int? PublishedRevision,
        int? PublishedVersion, int? RetrainingVersion, string Audience, bool OpenEnrolment, DateTimeOffset UpdatedAt)
    {
        public CourseDefinition DraftDefinition => LearningJson.Read<CourseDefinition>(Draft);

        public ContentAudience AudienceValue => LearningJson.Read<ContentAudience>(Audience);

        public bool DraftChanged => PublishedRevision != DraftRevision;

        public AuthzTarget Scope => OrgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;
    }

    internal const string Columns = "id, org_unit_id, category, status, draft::text, draft_revision, published_revision, published_version, retraining_version, audience::text, open_enrolment, updated_at";

    internal static CourseRow Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.Get<Guid?>("org_unit_id"), r.Get<string>("category"), r.GetString(3), r.GetString(4),
        r.GetInt32(5), r.Get<int?>("published_revision"), r.Get<int?>("published_version"), r.Get<int?>("retraining_version"), r.GetString(9), r.GetBoolean(10),
        r.GetFieldValue<DateTimeOffset>(11));

    internal Task<CourseRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from learning.course where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    internal async Task<CourseDefinition?> VersionAsync(Guid courseId, int version, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select definition::text from learning.course_version where course_id = @courseId and version = @version",
            new { courseId, version }, r => LearningJson.Read<CourseDefinition>(r.GetString(0)), ct);

    internal Task<bool> ManagesAsync(CourseRow row, CancellationToken ct) => AllowedAsync(Permissions.LearningManage, row.Scope, ct);

    private async Task<bool> AllowedAsync(string permission, AuthzTarget target, CancellationToken ct) => (await authz.CheckAsync(permission, target, ct)).Allowed;

    public async Task<LearningRights> RightsAsync(CancellationToken ct)
    {
        context.RequireUser();
        if (!await entitlements.IsEnabledAsync(Capabilities.LearningCourses, ct))
        {
            return new LearningRights(false, false, false);
        }
        return new LearningRights(await AllowedAsync(Permissions.LearningTake, AuthzTarget.Tenant, ct), await authz.HoldsAnywhereAsync(Permissions.LearningManage, ct),
            await authz.HoldsAnywhereAsync(Permissions.LearningReport, ct), (await reportingLine.DirectReportsAsync(context.RequireUser(), ct)).Count > 0);
    }

    private async Task RequireBuilderAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.LearningManage, ct))
        {
            throw AppException.Denied();
        }
    }

    /// <summary>The row when the caller may manage the course; 404 otherwise, so a course outside their scope is not revealed.</summary>
    internal async Task<CourseRow> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await RequireBuilderAsync(ct);
        var row = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        if (!await ManagesAsync(row, ct))
        {
            throw AppException.NotFound();
        }
        return row;
    }

    public async Task<IReadOnlyList<CourseSummary>> ListAsync(CancellationToken ct)
    {
        await RequireBuilderAsync(ct);
        var rows = await session.QueryAsync($"select {Columns} from learning.course order by updated_at desc, id limit 500", null, Read, ct);
        var result = new List<CourseSummary>();
        foreach (var row in rows)
        {
            if (await ManagesAsync(row, ct))
            {
                var draft = row.DraftDefinition;
                result.Add(new CourseSummary(row.Id, draft.Titles, row.Category, row.Status, row.OrgUnitId, row.PublishedVersion, row.DraftChanged, draft.Lessons.Count, row.UpdatedAt));
            }
        }
        return result;
    }

    public async Task<CourseDetail> GetAsync(Guid id, CancellationToken ct) => await DetailAsync(await ManagedAsync(id, false, ct), ct);

    private async Task<CourseDetail> DetailAsync(CourseRow row, CancellationToken ct)
    {
        var names = row.OrgUnitId is { } unit ? (await hierarchy.GetNamesAsync([unit], ct)).GetValueOrDefault(unit) : null;
        return new CourseDetail(row.Id, row.OrgUnitId, names, row.Category, row.Status, row.AudienceValue, row.OpenEnrolment, row.DraftDefinition, row.DraftRevision,
            row.DraftChanged, row.PublishedVersion, await ChangedSourcesAsync(row, ct), row.UpdatedAt);
    }

    /// <summary>Lessons of the published version whose page or file has a newer version than the one published (US1 scenario 1).</summary>
    private async Task<IReadOnlyList<ChangedSource>> ChangedSourcesAsync(CourseRow row, CancellationToken ct)
    {
        if (row.PublishedVersion is not { } version)
        {
            return [];
        }
        var (definition, sources) = await session.QuerySingleOrDefaultAsync(
            "select definition::text, sources::text from learning.course_version where course_id = @id and version = @version", new { id = row.Id, version },
            r => (LearningJson.Read<CourseDefinition>(r.GetString(0)), LearningJson.Read<Dictionary<string, string>>(r.GetString(1))), ct);
        var changed = new List<ChangedSource>();
        foreach (var lesson in definition.Lessons.Where(l => l.Sources is not null))
        {
            foreach (var (locale, source) in lesson.Sources!)
            {
                var now = await SourceVersionAsync(lesson.Kind, source, ct);
                if (now is not null && sources.GetValueOrDefault(SourceKey(lesson.Key, locale)) != now)
                {
                    changed.Add(new ChangedSource(lesson.Key, locale));
                }
            }
        }
        return changed;
    }

    private static string SourceKey(string lesson, string locale) => $"{lesson}:{locale}";

    /// <summary>The version of a page or file the caller may read, or null when it is not readable or not of the right kind.</summary>
    private async Task<string?> SourceVersionAsync(string kind, Guid id, CancellationToken ct)
    {
        if (kind == LessonKinds.Page)
        {
            return await pages.ReadableAsync(id, ct) is { } page ? $"page:{page.Version}" : null;
        }
        if (await documents.ReadableAsync(id, ct) is not { } document)
        {
            return null;
        }
        return kind == LessonKinds.Video && !document.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? null : $"document:{document.VersionId:N}";
    }

    private async Task<(CourseDefinition Definition, ContentAudience Audience, string? Category)> CleanAsync(CourseRequest request, ValidationErrors errors, CancellationToken ct)
    {
        var locales = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        var definition = CourseModel.Clean(request.Definition, locales, errors);
        var audience = request.Audience ?? ContentAudience.Everyone;
        audiences.Validate(audience, "audience", errors);
        var category = request.Category?.Trim();
        if (category is { Length: > MaxCategory })
        {
            errors.Add("category", "validation.learning.category");
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.learning.unit");
        }
        return (definition, audience, string.IsNullOrEmpty(category) ? null : category);
    }

    /// <summary>The course title in the tenant's default language, or the first language the course has.</summary>
    internal async Task<string> TitleAsync(CourseDefinition definition, CancellationToken ct)
    {
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        return definition.Titles.TryGetValue(locale, out var title) ? title : definition.Titles.Values.FirstOrDefault() ?? "";
    }

    private async Task DemandScopeAsync(Guid? unit, CancellationToken ct)
    {
        if (!await AllowedAsync(Permissions.LearningManage, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct))
        {
            throw AppException.Denied("authz.denied.learning_scope");
        }
    }

    public async Task<CourseDetail> CreateAsync(CourseRequest request, CancellationToken ct)
    {
        await RequireBuilderAsync(ct);
        var errors = new ValidationErrors();
        var (definition, audience, category) = await CleanAsync(request, errors, ct);
        errors.ThrowIfAny();
        await DemandScopeAsync(request.OrgUnitId, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into learning.course (tenant_id, id, org_unit_id, category, draft, audience, open_enrolment, created_by, created_at, updated_at)
            values (@tenant, @id, @unit, @category, @draft, @audience, @open, @user, @now, @now)
            """, new
            {
                tenant = context.RequireTenant(), id, unit = request.OrgUnitId, category, draft = LearningJson.Jsonb(definition), audience = LearningJson.Jsonb(audience),
                open = request.OpenEnrolment, user = context.RequireUser(), now,
            }, ct);
        return await DetailAsync((await RowAsync(id, false, ct))!, ct);
    }

    public async Task<CourseDetail> UpdateAsync(Guid id, CourseRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (row.Status == CourseStatuses.Retired)
        {
            throw AppException.Conflict("conflict.learning.course_retired");
        }
        if (request.Revision != row.DraftRevision)
        {
            throw AppException.Conflict("conflict.learning.course_revision");
        }
        var errors = new ValidationErrors();
        var (definition, audience, category) = await CleanAsync(request, errors, ct);
        errors.ThrowIfAny();
        if (request.OrgUnitId != row.OrgUnitId)
        {
            await DemandScopeAsync(request.OrgUnitId, ct);
        }
        await session.ExecAsync(
            """
            update learning.course set org_unit_id = @unit, category = @category, draft = @draft, draft_revision = draft_revision + 1, audience = @audience,
              open_enrolment = @open, updated_at = @now
            where id = @id
            """, new
            {
                id, unit = request.OrgUnitId, category, draft = LearningJson.Jsonb(definition), audience = LearningJson.Jsonb(audience), open = request.OpenEnrolment,
                now = clock.UtcNow,
            }, ct);
        if (row.Status == CourseStatuses.Published)
        {
            // The category and owning unit are not versioned, so search reads them again.
            await outbox.EnqueueAsync(new SearchItemChanged(LearningSearchSource.Name, id), ct);
        }
        return await DetailAsync((await RowAsync(id, false, ct))!, ct);
    }

    public async Task<CourseDetail> PublishAsync(Guid id, PublishRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (row.Status == CourseStatuses.Retired)
        {
            throw AppException.Conflict("conflict.learning.course_retired");
        }
        if (request.Revision != row.DraftRevision)
        {
            throw AppException.Conflict("conflict.learning.course_revision");
        }
        var definition = row.DraftDefinition;
        var errors = new ValidationErrors();
        CourseModel.CheckPublishable(definition, errors);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < definition.Lessons.Count; i++)
        {
            var lesson = definition.Lessons[i];
            foreach (var (locale, source) in lesson.Sources ?? new Dictionary<string, Guid>())
            {
                if (await SourceVersionAsync(lesson.Kind, source, ct) is { } sourceVersion)
                {
                    sources[SourceKey(lesson.Key, locale)] = sourceVersion;
                }
                else
                {
                    errors.Add($"lessons[{i}].sources", lesson.Kind == LessonKinds.Video ? "validation.learning.source_not_video" : "validation.learning.source_unreadable");
                }
            }
        }
        errors.ThrowIfAny();
        var version = (row.PublishedVersion ?? 0) + 1;
        var now = clock.UtcNow;
        // The first version never asks for retraining: nobody has completed the course yet.
        var retraining = request.RequiresRetraining && row.PublishedVersion is not null;
        await session.ExecAsync(
            """
            insert into learning.course_version (tenant_id, course_id, version, definition, sources, requires_retraining, published_by, published_at)
            values (@tenant, @id, @version, @definition, @sources, @retraining, @user, @now)
            """, new
            {
                tenant = context.RequireTenant(), id, version, definition = LearningJson.Jsonb(definition), sources = LearningJson.Jsonb(sources), retraining,
                user = context.RequireUser(), now,
            }, ct);
        await session.ExecAsync(
            """
            update learning.course set status = 'published', published_version = @version, published_revision = draft_revision,
              retraining_version = case when @retraining then @version else retraining_version end, updated_at = @now
            where id = @id
            """, new { id, version, retraining, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningCoursePublished, "course", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { version, retraining }), ct);
        await outbox.EnqueueAsync(new CoursePublished(id, version, retraining), ct);
        await outbox.EnqueueAsync(new SearchItemChanged(LearningSearchSource.Name, id), ct);
        return await DetailAsync((await RowAsync(id, false, ct))!, ct);
    }

    /// <summary>Retires the course: it leaves the catalogue, open assignments are withdrawn and completions stay (edge case "course retired").</summary>
    public async Task<CourseDetail> RetireAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, true, ct);
        if (row.Status == CourseStatuses.Retired)
        {
            return await DetailAsync(row, ct);
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update learning.course set status = 'retired', updated_at = @now where id = @id", new { id, now }, ct);
        var learners = await session.QueryAsync("update learning.assignment set status = 'withdrawn', ended_at = @now where course_id = @id and status = 'open' returning learner_id",
            new { id, now }, r => r.GetGuid(0), ct);
        // Its assignment rules end with it (spec 028 US2): nobody is given a retired course again.
        await session.ExecAsync("delete from learning.rule_target t using learning.assignment_rule r where r.id = t.rule_id and r.course_id = @id", new { id }, ct);
        await session.ExecAsync("update learning.assignment_rule set status = 'ended', ended_by = @user, ended_at = @now where course_id = @id and status = 'active'",
            new { id, user = context.ActorId, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningCourseRetired, "course", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { withdrawn = learners.Count }), ct);
        await outbox.EnqueueAsync(new SearchItemChanged(LearningSearchSource.Name, id), ct);
        if (learners.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(learners, NotificationCategories.RequestUpdate, WithdrawnTemplate, MyLearningPath,
                new Dictionary<string, string> { ["course"] = await TitleAsync(row.DraftDefinition, ct) }, DedupeKey: $"learning.course:{id:N}:withdrawn"), ct);
        }
        return await DetailAsync((await RowAsync(id, false, ct))!, ct);
    }
}
