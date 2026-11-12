using System.Globalization;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

/// <summary>Where a person stands on a course in a report: given and open, overdue, completed, or expired.</summary>
public static class ReportStates
{
    public const string Assigned = "assigned";
    public const string Overdue = "overdue";
    public const string Completed = "completed";
    public const string Expired = "expired";
}

public sealed record ReportCounts(int Assigned, int Completed, int Overdue, int Expired);

/// <summary>One unit of a course report: its people and the units below it.</summary>
public sealed record UnitReport(Guid OrgUnitId, IReadOnlyDictionary<string, string> Names, Guid? ParentId, ReportCounts Counts);

public sealed record ReportCourse(Guid Id, IReadOnlyDictionary<string, string> Titles, string Status, string? Category);

public sealed record CourseReport(Guid CourseId, IReadOnlyDictionary<string, string> Titles, bool WholeTenant, ReportCounts Total, IReadOnlyList<UnitReport> Units);

/// <summary>A person in a report or the evidence export (FR-014): version, score, time and certificate ID once completed.</summary>
public sealed record ReportPerson(RulePerson Person, string State, string Source, DateOnly? DueOn, DateTimeOffset? CompletedAt, int? Version, int? Score,
    string? CertificateCode, Guid? CertificateId);

public sealed record TeamCourse(Guid CourseId, IReadOnlyDictionary<string, string> Titles, string State, string Source, DateOnly? DueOn, DateTimeOffset? CompletedAt);

public sealed record TeamMember(RulePerson Person, IReadOnlyList<TeamCourse> Courses);

public sealed record TeamAssignRequest(Guid? CourseId, DateOnly? DueOn);

/// <summary>
/// Learning reports (spec 028 US5, FR-013, FR-014). Compliance officers with <c>learning.report</c> see, for a course,
/// how many people are assigned, completed, overdue and expired per unit in their scope, a parent unit counting the
/// units below it, with drill down to people, and export the evidence as CSV (audited). Line managers see their direct
/// reports only, and may give them published catalogue courses that the report can take.
/// </summary>
public sealed class LearningReports(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAudiences audiences,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    IReportingLine reportingLine,
    IWorkingCalendar calendars,
    IAuditWriter audit,
    IClock clock,
    Courses courses,
    AssignmentRules rules)
{
    public const int MaxPeople = 10_000;
    public const int MaxDueDays = 365;

    private sealed record Standing(Guid Learner, Guid AssignmentId, string State, string Source, DateOnly? DueOn, DateTimeOffset? CompletedAt, int? Version);

    private async Task<(Courses.CourseRow Course, OrgCoverage Coverage)> ReportableAsync(Guid courseId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.LearningReport, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied("authz.denied.learning_report");
        }
        var course = await courses.RowAsync(courseId, false, ct) ?? throw AppException.NotFound();
        return (course, coverage);
    }

    private async Task<IReadOnlyDictionary<string, string>> TitlesAsync(Courses.CourseRow course, CancellationToken ct) =>
        course.PublishedVersion is { } v && await courses.VersionAsync(course.Id, v, ct) is { } definition ? definition.Titles : course.DraftDefinition.Titles;

    /// <summary>Each learner's latest assignment of the course that was not withdrawn, as a report state.</summary>
    private async Task<Dictionary<Guid, Standing>> StandingsAsync(Guid courseId, CancellationToken ct)
    {
        var today = (await calendars.TenantDefaultAsync(ct)).Today(clock.UtcNow);
        var rows = await session.QueryAsync(
            """
            select distinct on (learner_id) learner_id, id, status, source, due_on, grace_days, completed_at, completed_version from learning.assignment
            where course_id = @courseId and status <> 'withdrawn'
            order by learner_id, created_at desc, id
            """, new { courseId },
            r => (Learner: r.GetGuid(0), Id: r.GetGuid(1), Status: r.GetString(2), Source: r.GetString(3), Due: r.Get<DateOnly?>("due_on"), Grace: r.GetInt32(5),
                CompletedAt: r.Get<DateTimeOffset?>("completed_at"), Version: r.Get<int?>("completed_version")), ct);
        return rows.ToDictionary(r => r.Learner, r => new Standing(r.Learner, r.Id, r.Status switch
        {
            AssignmentStatuses.Completed => ReportStates.Completed,
            AssignmentStatuses.Expired => ReportStates.Expired,
            _ => r.Due is { } due && due.AddDays(r.Grace) < today ? ReportStates.Overdue : ReportStates.Assigned,
        }, r.Source, r.Due, r.CompletedAt, r.Version));
    }

    private static ReportCounts Count(IEnumerable<Standing> standings)
    {
        var list = standings.ToList();
        return new ReportCounts(list.Count, list.Count(s => s.State == ReportStates.Completed), list.Count(s => s.State == ReportStates.Overdue),
            list.Count(s => s.State == ReportStates.Expired));
    }

    /// <summary>The units the viewer reports on, and for each the people of the unit and every unit below it.</summary>
    private async Task<Dictionary<Guid, HashSet<Guid>>> PeopleByUnitAsync(OrgCoverage coverage, CancellationToken ct)
    {
        var memberships = await hierarchy.ListActiveMembershipsAsync(ct);
        var paths = await hierarchy.GetPathsAsync(memberships.Select(m => m.OrgUnitId).Distinct(), ct);
        var byUnit = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var m in memberships)
        {
            foreach (var unit in paths.GetValueOrDefault(m.OrgUnitId) ?? [m.OrgUnitId])
            {
                if (coverage.Covers(unit))
                {
                    (byUnit.TryGetValue(unit, out var set) ? set : byUnit[unit] = []).Add(m.UserId);
                }
            }
        }
        return byUnit;
    }

    /// <summary>The courses a report holder can pick: every published or retired course, newest first.</summary>
    public async Task<IReadOnlyList<ReportCourse>> CoursesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        context.RequireUser();
        if (!(await authz.CoverageAsync(Permissions.LearningReport, ct)).Any)
        {
            throw AppException.Denied("authz.denied.learning_report");
        }
        var rows = await session.QueryAsync($"select {Courses.Columns} from learning.course where status <> 'draft' order by updated_at desc, id limit 500", null, Courses.Read, ct);
        var list = new List<ReportCourse>();
        foreach (var row in rows)
        {
            list.Add(new ReportCourse(row.Id, await TitlesAsync(row, ct), row.Status, row.Category));
        }
        return list;
    }

    /// <summary>Counts per unit in the viewer's scope (US5 scenario 1). People in no covered unit are only in a whole tenant total.</summary>
    public async Task<CourseReport> CourseAsync(Guid courseId, CancellationToken ct)
    {
        var (course, coverage) = await ReportableAsync(courseId, ct);
        var standings = await StandingsAsync(course.Id, ct);
        var byUnit = await PeopleByUnitAsync(coverage, ct);
        var names = await hierarchy.GetNamesAsync(byUnit.Keys, ct);
        var paths = await hierarchy.GetPathsAsync(byUnit.Keys, ct);
        var units = byUnit.Select(u =>
            {
                var path = paths.GetValueOrDefault(u.Key) ?? [];
                // The path runs from the root to the unit; its parent is the one before it.
                Guid? parent = path.Count > 1 ? path[^2] : null;
                return new UnitReport(u.Key, names.GetValueOrDefault(u.Key) ?? new Dictionary<string, string>(), parent,
                    Count(u.Value.Where(standings.ContainsKey).Select(p => standings[p])));
            })
            .OrderBy(u => (paths.GetValueOrDefault(u.OrgUnitId) ?? []).Count).ThenBy(u => u.Names.Values.FirstOrDefault(), StringComparer.CurrentCulture).ToList();
        var inScope = coverage.WholeTenant ? standings.Values : byUnit.Values.SelectMany(p => p).Distinct().Where(standings.ContainsKey).Select(p => standings[p]);
        return new CourseReport(course.Id, await TitlesAsync(course, ct), coverage.WholeTenant, Count(inScope), units);
    }

    /// <summary>The people behind a report, for one unit (with the units below it) or the whole scope.</summary>
    public async Task<IReadOnlyList<ReportPerson>> PeopleAsync(Guid courseId, Guid? unitId, CancellationToken ct)
    {
        var (course, coverage) = await ReportableAsync(courseId, ct);
        return await PeopleOfAsync(course, coverage, unitId, ct);
    }

    private async Task<IReadOnlyList<ReportPerson>> PeopleOfAsync(Courses.CourseRow course, OrgCoverage coverage, Guid? unitId, CancellationToken ct)
    {
        if (unitId is { } unit && !coverage.Covers(unit))
        {
            throw AppException.Denied("authz.denied.learning_report");
        }
        var standings = await StandingsAsync(course.Id, ct);
        IEnumerable<Guid> people;
        if (unitId is null && coverage.WholeTenant)
        {
            people = standings.Keys;
        }
        else
        {
            var byUnit = await PeopleByUnitAsync(coverage, ct);
            people = unitId is { } u ? byUnit.GetValueOrDefault(u) ?? [] : byUnit.Values.SelectMany(p => p).Distinct();
        }
        var chosen = people.Where(standings.ContainsKey).Select(p => standings[p]).Take(MaxPeople).ToList();
        var ids = chosen.Select(s => s.AssignmentId).ToArray();
        var scores = (await session.QueryAsync(
            "select assignment_id, max(score) from learning.attempt where assignment_id = any(@ids) group by assignment_id",
            new { ids }, r => (Id: r.GetGuid(0), Score: r.GetInt32(1)), ct)).ToDictionary(s => s.Id, s => s.Score);
        var certificates = (await session.QueryAsync("select assignment_id, id, code from learning.certificate where assignment_id = any(@ids)",
            new { ids }, r => (Assignment: r.GetGuid(0), Id: r.GetGuid(1), Code: r.GetString(2)), ct)).ToDictionary(c => c.Assignment);
        var directory = await users.GetManyAsync(chosen.Select(s => s.Learner).ToList(), ct);
        return chosen
            .Select(s =>
            {
                var hasCertificate = certificates.TryGetValue(s.AssignmentId, out var c);
                return new ReportPerson(new RulePerson(s.Learner, directory.GetValueOrDefault(s.Learner)?.DisplayName ?? ""), s.State, s.Source, s.DueOn, s.CompletedAt,
                    s.Version, scores.TryGetValue(s.AssignmentId, out var score) ? score : null, hasCertificate ? c.Code : null, hasCertificate ? c.Id : null);
            })
            .OrderBy(p => p.State switch { ReportStates.Overdue => 0, ReportStates.Assigned => 1, ReportStates.Expired => 2, _ => 3 })
            .ThenBy(p => p.Person.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>The evidence pack (US5 scenario 2): one CSV row per person, audited with the course, the scope and the count.</summary>
    public async Task<(string FileName, byte[] Content)> EvidenceAsync(Guid courseId, Guid? unitId, CancellationToken ct)
    {
        var (course, coverage) = await ReportableAsync(courseId, ct);
        var people = await PeopleOfAsync(course, coverage, unitId, ct);
        var csv = new StringBuilder("person,status,source,due_on,completed_at,version,score,certificate_id\n");
        foreach (var p in people)
        {
            csv.Append(Csv(p.Person.DisplayName)).Append(',').Append(p.State).Append(',').Append(p.Source).Append(',')
                .Append(p.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(p.CompletedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',')
                .Append(p.Version?.ToString(CultureInfo.InvariantCulture)).Append(',').Append(p.Score?.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(p.CertificateCode)).Append('\n');
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningEvidenceExported, "course", course.Id.ToString(), TargetOrgUnitId: unitId,
            Changes: new { course = course.Id, scope = unitId?.ToString() ?? (coverage.WholeTenant ? "tenant" : "scope"), rows = people.Count }), ct);
        // UTF-8 with a byte order mark so spreadsheet tools read Arabic names.
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return ($"evidence-{course.Id:N}.csv", bytes);
    }

    /// <summary>CSV escaping with formula-injection protection for spreadsheet tools.</summary>
    internal static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }
        return value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    // ---- Team (line managers) ----

    /// <summary>The viewer's direct reports and their courses (US5 scenario 3): never anyone further down or across.</summary>
    public async Task<IReadOnlyList<TeamMember>> TeamAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        var me = context.RequireUser();
        var reports = await reportingLine.DirectReportsAsync(me, ct);
        if (reports.Count == 0)
        {
            return [];
        }
        var today = (await calendars.TenantDefaultAsync(ct)).Today(clock.UtcNow);
        var rows = await session.QueryAsync(
            """
            select distinct on (a.learner_id, a.course_id) a.learner_id, a.course_id, a.status, a.source, a.due_on, a.grace_days, a.completed_at
            from learning.assignment a join learning.course c on c.id = a.course_id
            where a.learner_id = any(@reports) and a.status <> 'withdrawn' and c.status <> 'draft'
            order by a.learner_id, a.course_id, a.created_at desc, a.id
            """, new { reports = reports.ToArray() },
            r => (Learner: r.GetGuid(0), Course: r.GetGuid(1), Status: r.GetString(2), Source: r.GetString(3), Due: r.Get<DateOnly?>("due_on"), Grace: r.GetInt32(5),
                CompletedAt: r.Get<DateTimeOffset?>("completed_at")), ct);
        var titles = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        foreach (var id in rows.Select(r => r.Course).Distinct())
        {
            if (await courses.RowAsync(id, false, ct) is { } row)
            {
                titles[id] = await TitlesAsync(row, ct);
            }
        }
        var directory = await users.GetManyAsync(reports.ToList(), ct);
        return reports
            .Select(p => new TeamMember(new RulePerson(p, directory.GetValueOrDefault(p)?.DisplayName ?? ""),
                rows.Where(r => r.Learner == p && titles.ContainsKey(r.Course))
                    .Select(r => new TeamCourse(r.Course, titles[r.Course], r.Status switch
                    {
                        AssignmentStatuses.Completed => ReportStates.Completed,
                        AssignmentStatuses.Expired => ReportStates.Expired,
                        _ => r.Due is { } due && due.AddDays(r.Grace) < today ? ReportStates.Overdue : ReportStates.Assigned,
                    }, r.Source, r.Due, r.CompletedAt))
                    .OrderBy(c => c.State == ReportStates.Completed).ThenBy(c => c.DueOn ?? DateOnly.MaxValue).ToList()))
            .OrderBy(m => m.Person.DisplayName, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// A line manager gives a direct report a published course the report can take (role table: assign catalogue courses
    /// to direct reports), due on a date within a year, moved to the report's next working day. Audited.
    /// </summary>
    public async Task<IReadOnlyList<TeamMember>> AssignAsync(Guid userId, TeamAssignRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.LearningCourses, ct);
        var me = context.RequireUser();
        if (!await reportingLine.IsManagerOfAsync(me, userId, ct) || !(await reportingLine.DirectReportsAsync(me, ct)).Contains(userId))
        {
            throw AppException.Denied("authz.denied.learning_team");
        }
        var errors = new ValidationErrors();
        var course = request.CourseId is { } id ? await courses.RowAsync(id, false, ct) : null;
        if (course is not { Status: CourseStatuses.Published } || !await audiences.IncludesAsync(course.AudienceValue, userId, ct))
        {
            errors.Add("courseId", "validation.learning.team_course");
        }
        var calendar = await calendars.ForUserAsync(userId, ct);
        var today = calendar.Today(clock.UtcNow);
        var dueOn = request.DueOn ?? today.AddDays(AssignmentRules.DefaultDueDays);
        if (dueOn <= today || dueOn > today.AddDays(MaxDueDays))
        {
            errors.Add("dueOn", "validation.learning.team_due");
        }
        errors.ThrowIfAny();
        var due = AssignmentRules.NextWorkingDay(calendar, dueOn);
        await rules.GiveAsync(course!, userId, due, 0, AssignmentSources.Manager, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningAssignmentCreated, "course", course!.Id.ToString(),
            Changes: new { course = course.Id, learner = userId, source = AssignmentSources.Manager, dueOn = due }), ct);
        return await TeamAsync(ct);
    }
}
