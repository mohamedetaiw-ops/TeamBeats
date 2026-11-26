using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

public static class SessionStatuses
{
    public const string Scheduled = "scheduled";
    public const string Cancelled = "cancelled";
}

public static class SeatStatuses
{
    public const string Booked = "booked";
    public const string Waiting = "waiting";
    public const string Cancelled = "cancelled";
    public const string Attended = "attended";
    public const string Absent = "absent";
}

public sealed record SessionRequest(string? LessonKey, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Place, string? Link, int? Capacity, Guid? FacilitatorId);

public sealed record SessionPerson(Guid Id, string Name);

/// <summary>
/// A classroom session. <see cref="MySeat"/> is the viewer's seat status, with <see cref="MyPosition"/> their place on
/// the waiting list; <see cref="CanManage"/>: they may change or cancel it; <see cref="CanTakeAttendance"/>: they may
/// record attendance now.
/// </summary>
public sealed record SessionView(Guid Id, Guid CourseId, string LessonKey, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Place, string? Link, int Capacity,
    SessionPerson Facilitator, string Status, int Booked, int Waiting, string? MySeat, int? MyPosition, bool CanManage, bool CanTakeAttendance,
    IReadOnlyDictionary<string, string>? CourseTitles = null);

public sealed record RosterSeat(Guid LearnerId, string Name, string Status);

public sealed record RosterView(SessionView Session, IReadOnlyList<RosterSeat> Seats);

public sealed record AttendanceEntry(Guid LearnerId, bool Attended);

public sealed record AttendanceRequest(IReadOnlyList<AttendanceEntry>? Entries);

/// <summary>
/// Classroom sessions (spec 028 R3 US10, FR-026 to FR-030). Learning administrators of a course add sessions to its
/// classroom lessons; learners with the course open hold one seat per lesson, or wait for one when a session is full,
/// and cancel before the start, which gives the seat to the first person waiting. The day before, booked learners are
/// reminded. After the start the facilitator, or an administrator of the course, records who attended; attending
/// finishes the lesson like any other, so the course, its certificate and the obligations behind it follow.
/// Everything needs <c>learning.classroom</c>.
/// </summary>
public sealed class Sessions(
    IDbSession session,
    ITenantContext context,
    IEntitlementService entitlements,
    Courses courses,
    Player player,
    IUserDirectory users,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxCapacity = 500;
    public const int MaxPlace = 200;
    public const int MaxLink = 500;
    public static readonly TimeSpan MaxLength = TimeSpan.FromHours(12);
    public static readonly TimeSpan CorrectionWindow = TimeSpan.FromDays(14);
    public const string ReminderTemplate = "learning.session.reminder";
    public const string SeatTemplate = "learning.session.seat";
    public const string CancelledTemplate = "learning.session.cancelled";

    private sealed record Row(Guid Id, Guid CourseId, string LessonKey, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Place, string? Link, int Capacity,
        Guid FacilitatorId, string Status);

    private const string Columns = "id, course_id, lesson_key, starts_at, ends_at, place, link, capacity, facilitator_id, status";

    private static Row Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
        r.Get<string>("place"), r.Get<string>("link"), r.GetInt32(7), r.GetGuid(8), r.GetString(9));

    private Task<Row?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from learning.session where id = @id{(forUpdate ? " for update" : "")}", new { id }, Read, ct);

    private sealed record SeatRow(Guid Id, Guid SessionId, Guid AssignmentId, Guid LearnerId, string Status, DateTimeOffset CreatedAt);

    private const string SeatColumns = "id, session_id, assignment_id, learner_id, status, created_at";

    private static SeatRow ReadSeat(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));

    private Task RequireAsync(CancellationToken ct) => entitlements.DemandAsync(Capabilities.LearningClassroom, ct);

    /// <summary>The course's classroom lesson in its draft or its published version; a key of another kind is refused.</summary>
    private async Task<IReadOnlyDictionary<string, string>> ClassroomLessonAsync(Courses.CourseRow row, string? key, CancellationToken ct)
    {
        var published = row.PublishedVersion is { } v ? await courses.VersionAsync(row.Id, v, ct) : null;
        var lesson = row.DraftDefinition.Lessons.FirstOrDefault(l => l.Key == key) ?? published?.Lessons.FirstOrDefault(l => l.Key == key);
        if (lesson is not { Kind: LessonKinds.Classroom })
        {
            throw AppException.Validation("lessonKey", "validation.learning.session_lesson");
        }
        return lesson.Titles;
    }

    private async Task<(DateTimeOffset Starts, DateTimeOffset Ends, string? Place, string? Link, int Capacity, Guid Facilitator)> CleanAsync(SessionRequest request,
        CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var now = clock.UtcNow;
        if (request.StartsAt is not { } starts || starts <= now)
        {
            errors.Add("startsAt", "validation.learning.session_start");
            starts = now;
        }
        if (request.EndsAt is not { } ends || ends <= starts || ends - starts > MaxLength)
        {
            errors.Add("endsAt", "validation.learning.session_end");
            ends = starts.AddHours(1);
        }
        var place = string.IsNullOrWhiteSpace(request.Place) ? null : request.Place.Trim();
        var link = string.IsNullOrWhiteSpace(request.Link) ? null : request.Link.Trim();
        if (place is { Length: > MaxPlace })
        {
            errors.Add("place", "validation.learning.session_place");
        }
        if (link is not null && (link.Length > MaxLink || !Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("link", "validation.learning.session_link");
        }
        if (place is null && link is null)
        {
            errors.Add("place", "validation.learning.session_where");
        }
        var capacity = request.Capacity ?? 0;
        if (capacity is < 1 or > MaxCapacity)
        {
            errors.Add("capacity", "validation.learning.session_capacity");
        }
        var facilitator = request.FacilitatorId ?? Guid.Empty;
        if (facilitator == Guid.Empty || await users.GetAsync(facilitator, ct) is not { Status: "active" })
        {
            errors.Add("facilitatorId", "validation.learning.session_facilitator");
        }
        errors.ThrowIfAny();
        return (starts, ends, place, link, capacity, facilitator);
    }

    // ---- Managing sessions ----

    public async Task<IReadOnlyList<SessionView>> ListAsync(Guid courseId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await courses.ManagedAsync(courseId, false, ct);
        var rows = await session.QueryAsync($"select {Columns} from learning.session where course_id = @courseId order by starts_at, id limit 500",
            new { courseId = row.Id }, Read, ct);
        return await ViewsAsync(rows, true, ct);
    }

    public async Task<SessionView> CreateAsync(Guid courseId, SessionRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await courses.ManagedAsync(courseId, true, ct);
        if (row.Status == CourseStatuses.Retired || row.ExternalId is not null)
        {
            throw AppException.Conflict(row.Status == CourseStatuses.Retired ? "conflict.learning.course_retired" : "conflict.learning.external_course");
        }
        await ClassroomLessonAsync(row, request.LessonKey, ct);
        var (starts, ends, place, link, capacity, facilitator) = await CleanAsync(request, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into learning.session (tenant_id, id, course_id, lesson_key, starts_at, ends_at, place, link, capacity, facilitator_id, created_by, created_at, updated_at)
            values (@tenant, @id, @courseId, @key, @starts, @ends, @place, @link, @capacity, @facilitator, @user, @now, @now)
            """, new
            {
                tenant = context.RequireTenant(), id, courseId, key = request.LessonKey, starts, ends, place, link, capacity, facilitator, user = context.RequireUser(), now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningSessionChanged, "session", id.ToString(), TargetOrgUnitId: row.OrgUnitId,
            Changes: new { course = courseId, change = "added", starts, capacity }), ct);
        return (await ViewsAsync([(await RowAsync(id, false, ct))!], true, ct))[0];
    }

    /// <summary>The session as the caller may manage it: 404 when it is not theirs to manage, so it is not revealed.</summary>
    private async Task<(Row Session, Courses.CourseRow Course)> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        var course = await courses.ManagedAsync(row.CourseId, false, ct);
        return (row, course);
    }

    public async Task<SessionView> UpdateAsync(Guid id, SessionRequest request, CancellationToken ct)
    {
        var (row, course) = await ManagedAsync(id, true, ct);
        if (row.Status == SessionStatuses.Cancelled || row.StartsAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.learning.session_closed");
        }
        var (starts, ends, place, link, capacity, facilitator) = await CleanAsync(request with { LessonKey = row.LessonKey }, ct);
        var booked = await CountAsync(id, SeatStatuses.Booked, ct);
        if (capacity < booked)
        {
            throw AppException.Validation("capacity", "validation.learning.session_capacity_booked");
        }
        await session.ExecAsync(
            """
            update learning.session set starts_at = @starts, ends_at = @ends, place = @place, link = @link, capacity = @capacity, facilitator_id = @facilitator, updated_at = @now
            where id = @id
            """, new { id, starts, ends, place, link, capacity, facilitator, now = clock.UtcNow }, ct);
        // More seats: the people waiting move up in the order they came.
        await PromoteAsync(id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningSessionChanged, "session", id.ToString(), TargetOrgUnitId: course.OrgUnitId,
            Changes: new { course = row.CourseId, change = "changed", starts, capacity }), ct);
        return (await ViewsAsync([(await RowAsync(id, false, ct))!], true, ct))[0];
    }

    /// <summary>Cancels the session; everyone booked or waiting is told and may book another one (FR-028).</summary>
    public async Task<SessionView> CancelAsync(Guid id, CancellationToken ct)
    {
        var (row, course) = await ManagedAsync(id, true, ct);
        if (row.Status == SessionStatuses.Cancelled || row.StartsAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.learning.session_closed");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update learning.session set status = 'cancelled', updated_at = @now where id = @id", new { id, now }, ct);
        var learners = await session.QueryAsync(
            "update learning.seat set status = 'cancelled', updated_at = @now where session_id = @id and status in ('booked', 'waiting') returning learner_id",
            new { id, now }, r => r.GetGuid(0), ct);
        if (learners.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(learners.Distinct().ToList(), NotificationCategories.RequestUpdate, CancelledTemplate,
                Courses.CoursePath(row.CourseId), await ValuesAsync(row, ct), DedupeKey: $"learning.session:{id:N}:cancelled"), ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningSessionChanged, "session", id.ToString(), TargetOrgUnitId: course.OrgUnitId,
            Changes: new { course = row.CourseId, change = "cancelled", seats = learners.Count }), ct);
        return (await ViewsAsync([(await RowAsync(id, false, ct))!], true, ct))[0];
    }

    // ---- Seats ----

    /// <summary>The sessions of a classroom lesson a learner may book: scheduled ones still to start, and any they hold a seat in.</summary>
    public async Task<IReadOnlyList<SessionView>> LessonAsync(Guid courseId, string key, CancellationToken ct)
    {
        await RequireAsync(ct);
        var learner = await player.RequireLearnerAsync(ct);
        await player.GetAsync(courseId, ct);
        var rows = await session.QueryAsync(
            $"""
            select {Columns} from learning.session s
            where course_id = @courseId and lesson_key = @key
              and ((status = 'scheduled' and starts_at > @now)
                or exists (select 1 from learning.seat x where x.session_id = s.id and x.learner_id = @learner and x.status <> 'cancelled'))
            order by starts_at, id limit 100
            """, new { courseId, key, now = clock.UtcNow, learner }, Read, ct);
        return await ViewsAsync(rows, false, ct);
    }

    /// <summary>
    /// Takes a seat (FR-027): the learner needs the course open and the lesson not yet done, and holds at most one live
    /// seat per lesson. A full session puts them on its waiting list.
    /// </summary>
    public async Task<SessionView> BookAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var learner = await player.RequireLearnerAsync(ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        await player.GetAsync(row.CourseId, ct);
        if (row.Status == SessionStatuses.Cancelled || row.StartsAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.learning.session_closed");
        }
        var assignment = await player.CurrentAssignmentAsync(row.CourseId, learner, true, ct);
        if (assignment is not { Status: AssignmentStatuses.Open })
        {
            throw AppException.Conflict("conflict.learning.not_enrolled");
        }
        var course = (await courses.RowAsync(row.CourseId, false, ct))!;
        var published = await courses.VersionAsync(row.CourseId, course.PublishedVersion!.Value, ct);
        if (published?.Lessons.Any(l => l.Key == row.LessonKey && l.Kind == LessonKinds.Classroom) != true)
        {
            throw AppException.Conflict("conflict.learning.session_closed");
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from learning.lesson_progress where assignment_id = @a and lesson_key = @key)",
                new { a = assignment.Id, key = row.LessonKey }, ct))
        {
            throw AppException.Conflict("conflict.learning.lesson_done");
        }
        if (await session.ScalarAsync<bool>(
                "select exists (select 1 from learning.seat where learner_id = @learner and course_id = @course and lesson_key = @key and status in ('booked', 'waiting'))",
                new { learner, course = row.CourseId, key = row.LessonKey }, ct))
        {
            throw AppException.Conflict("conflict.learning.seat_held");
        }
        var status = await CountAsync(id, SeatStatuses.Booked, ct) < row.Capacity ? SeatStatuses.Booked : SeatStatuses.Waiting;
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into learning.seat (tenant_id, id, session_id, course_id, lesson_key, assignment_id, learner_id, status, created_at, updated_at)
            values (@tenant, @seat, @id, @course, @key, @a, @learner, @status, @now, @now)
            """, new { tenant = context.RequireTenant(), seat = Ids.New(), id, course = row.CourseId, key = row.LessonKey, a = assignment.Id, learner, status, now }, ct);
        return (await ViewsAsync([row], false, ct))[0];
    }

    /// <summary>Gives the seat back before the start; a booked seat goes to the first person waiting, who is told (FR-027).</summary>
    public async Task<SessionView> CancelSeatAsync(Guid id, CancellationToken ct)
    {
        await RequireAsync(ct);
        var learner = await player.RequireLearnerAsync(ct);
        var row = await RowAsync(id, true, ct) ?? throw AppException.NotFound();
        var seat = await session.QuerySingleOrDefaultAsync(
            $"select {SeatColumns} from learning.seat where session_id = @id and learner_id = @learner and status in ('booked', 'waiting') for update",
            new { id, learner }, ReadSeat, ct) ?? throw AppException.NotFound();
        if (row.StartsAt <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.learning.session_closed");
        }
        await session.ExecAsync("update learning.seat set status = 'cancelled', updated_at = @now where id = @seat", new { seat = seat.Id, now = clock.UtcNow }, ct);
        if (seat.Status == SeatStatuses.Booked)
        {
            await PromoteAsync(id, ct);
        }
        return (await ViewsAsync([row], false, ct))[0];
    }

    /// <summary>Fills free seats of a scheduled session from its waiting list, first come first served, and tells each person.</summary>
    private async Task PromoteAsync(Guid id, CancellationToken ct)
    {
        var row = (await RowAsync(id, false, ct))!;
        var free = row.Capacity - await CountAsync(id, SeatStatuses.Booked, ct);
        if (free <= 0 || row.Status != SessionStatuses.Scheduled)
        {
            return;
        }
        var promoted = await session.QueryAsync(
            """
            update learning.seat set status = 'booked', updated_at = @now
            where id in (select id from learning.seat where session_id = @id and status = 'waiting' order by created_at, id limit @free)
            returning learner_id
            """, new { id, free, now = clock.UtcNow }, r => r.GetGuid(0), ct);
        if (promoted.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(promoted.ToList(), NotificationCategories.RequestUpdate, SeatTemplate,
                Courses.CoursePath(row.CourseId), await ValuesAsync(row, ct), DedupeKey: $"learning.session:{id:N}:seat:{clock.UtcNow.ToUnixTimeMilliseconds()}"), ct);
        }
    }

    private Task<int> CountAsync(Guid id, string status, CancellationToken ct) =>
        session.ScalarAsync<int>("select count(*)::int from learning.seat where session_id = @id and status = @status", new { id, status }, ct);

    // ---- Facilitating and attendance ----

    /// <summary>Sessions the caller facilitates, from 14 days ago on.</summary>
    public async Task<IReadOnlyList<SessionView>> FacilitatingAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = context.RequireUser();
        var rows = await session.QueryAsync($"select {Columns} from learning.session where facilitator_id = @user and ends_at > @from order by starts_at, id limit 200",
            new { user, from = clock.UtcNow - CorrectionWindow }, Read, ct);
        return await ViewsAsync(rows, false, ct);
    }

    /// <summary>The facilitator, or a learning administrator of the course; anyone else gets not found.</summary>
    private async Task<(Row Row, Courses.CourseRow Course)> FacilitatedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await RequireAsync(ct);
        var user = context.RequireUser();
        var row = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        var course = (await courses.RowAsync(row.CourseId, false, ct))!;
        if (row.FacilitatorId != user && !await courses.ManagesAsync(course, ct))
        {
            throw AppException.NotFound();
        }
        return (row, course);
    }

    public async Task<RosterView> RosterAsync(Guid id, CancellationToken ct)
    {
        var (row, _) = await FacilitatedAsync(id, false, ct);
        return await RosterOfAsync(row, ct);
    }

    private async Task<RosterView> RosterOfAsync(Row row, CancellationToken ct)
    {
        var seats = await session.QueryAsync(
            $"select {SeatColumns} from learning.seat where session_id = @id and status <> 'cancelled' order by status <> 'waiting', created_at, id",
            new { id = row.Id }, ReadSeat, ct);
        var names = await users.GetManyAsync(seats.Select(s => s.LearnerId).Distinct().ToList(), ct);
        var view = (await ViewsAsync([row], false, ct))[0];
        return new RosterView(view, seats
            .Select(s => new RosterSeat(s.LearnerId, names.TryGetValue(s.LearnerId, out var u) ? u.DisplayName : "", s.Status))
            .OrderBy(s => s.Status == SeatStatuses.Waiting).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
    }

    /// <summary>
    /// Records attendance (FR-029) from the start until 14 days after the end. Attended finishes the lesson for the
    /// learner; absent leaves it open so they may book again. Only seats taken (not waiting) can be marked.
    /// </summary>
    public async Task<RosterView> AttendanceAsync(Guid id, AttendanceRequest request, CancellationToken ct)
    {
        var (row, course) = await FacilitatedAsync(id, true, ct);
        var now = clock.UtcNow;
        if (row.Status == SessionStatuses.Cancelled || now < row.StartsAt || now > row.EndsAt + CorrectionWindow)
        {
            throw AppException.Conflict(now < row.StartsAt ? "conflict.learning.attendance_early" : "conflict.learning.session_closed");
        }
        var entries = request.Entries ?? [];
        var seats = (await session.QueryAsync(
            $"select {SeatColumns} from learning.seat where session_id = @id and status in ('booked', 'attended', 'absent') for update", new { id }, ReadSeat, ct))
            .ToDictionary(s => s.LearnerId);
        if (entries.Count == 0 || entries.Any(e => !seats.ContainsKey(e.LearnerId)))
        {
            throw AppException.Validation("entries", "validation.learning.attendance_seat");
        }
        var user = context.RequireUser();
        var (attended, absent) = (0, 0);
        foreach (var entry in entries.DistinctBy(e => e.LearnerId))
        {
            var seat = seats[entry.LearnerId];
            await session.ExecAsync("update learning.seat set status = @status, recorded_by = @user, updated_at = @now where id = @seat",
                new { seat = seat.Id, status = entry.Attended ? SeatStatuses.Attended : SeatStatuses.Absent, user, now }, ct);
            if (entry.Attended)
            {
                attended++;
                await player.RecordAttendedAsync(seat.AssignmentId, row.LessonKey, ct);
            }
            else
            {
                absent++;
            }
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.LearningAttendanceRecorded, "session", id.ToString(), TargetOrgUnitId: course.OrgUnitId,
            Changes: new { course = row.CourseId, attended, absent }), ct);
        return await RosterOfAsync(row, ct);
    }

    // ---- Reminders ----

    /// <summary>The day-before reminder (FR-028), once per booked seat, for sessions that start within a day.</summary>
    public async Task<int> RemindDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.LearningClassroom, ct))
        {
            return 0;
        }
        var due = await session.QueryAsync(
            $"""
            select {string.Join(", ", Columns.Split(", ").Select(c => "s." + c))}, x.id, x.learner_id
            from learning.seat x join learning.session s on s.id = x.session_id
            where x.status = 'booked' and x.reminded_at is null and s.status = 'scheduled' and s.starts_at > @now and s.starts_at <= @until
            order by s.starts_at limit 1000
            """, new { now, until = now.AddDays(1) }, r => (Session: Read(r), Seat: r.GetGuid(10), Learner: r.GetGuid(11)), ct);
        foreach (var group in due.GroupBy(d => d.Session.Id))
        {
            var row = group.First().Session;
            await notifications.SendAsync(new NotificationRequest(group.Select(g => g.Learner).Distinct().ToList(), NotificationCategories.RequestUpdate, ReminderTemplate,
                Courses.CoursePath(row.CourseId), await ValuesAsync(row, ct), DedupeKey: $"learning.session:{row.Id:N}:reminder"), ct);
            await session.ExecAsync("update learning.seat set reminded_at = @now where id = any(@ids)", new { now, ids = group.Select(g => g.Seat).ToArray() }, ct);
        }
        return due.Count;
    }

    // ---- Views ----

    private async Task<Dictionary<string, string>> ValuesAsync(Row row, CancellationToken ct)
    {
        var course = (await courses.RowAsync(row.CourseId, false, ct))!;
        var definition = course.PublishedVersion is { } v ? (await courses.VersionAsync(row.CourseId, v, ct))! : course.DraftDefinition;
        return new Dictionary<string, string>
        {
            ["course"] = await courses.TitleAsync(definition, ct),
            ["start"] = row.StartsAt.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            ["place"] = row.Place ?? row.Link ?? "",
        };
    }

    private async Task<IReadOnlyList<SessionView>> ViewsAsync(IReadOnlyList<Row> rows, bool manages, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        var user = context.RequireUser();
        var ids = rows.Select(r => r.Id).ToArray();
        var counts = (await session.QueryAsync(
                "select session_id, status, count(*)::int from learning.seat where session_id = any(@ids) and status in ('booked', 'waiting', 'attended', 'absent') group by session_id, status",
                new { ids }, r => (Session: r.GetGuid(0), Status: r.GetString(1), Count: r.GetInt32(2)), ct))
            .ToLookup(c => c.Session);
        var mine = (await session.QueryAsync(
                $"select {SeatColumns} from learning.seat where session_id = any(@ids) and learner_id = @user and status <> 'cancelled'", new { ids, user }, ReadSeat, ct))
            .ToDictionary(s => s.SessionId);
        var waiting = (await session.QueryAsync("select session_id, learner_id from learning.seat where session_id = any(@ids) and status = 'waiting' order by created_at, id",
                new { ids }, r => (Session: r.GetGuid(0), Learner: r.GetGuid(1)), ct))
            .ToLookup(w => w.Session);
        var facilitators = await users.GetManyAsync(rows.Select(r => r.FacilitatorId).Distinct().ToList(), ct);
        var titles = new Dictionary<Guid, IReadOnlyDictionary<string, string>>();
        var manageable = new Dictionary<Guid, bool>();
        foreach (var courseId in rows.Select(r => r.CourseId).Distinct())
        {
            var course = (await courses.RowAsync(courseId, false, ct))!;
            titles[courseId] = (course.PublishedVersion is { } v ? (await courses.VersionAsync(courseId, v, ct))! : course.DraftDefinition).Titles;
            manageable[courseId] = manages || await courses.ManagesAsync(course, ct);
        }
        var now = clock.UtcNow;
        return rows.Select(r =>
        {
            var c = counts[r.Id].ToDictionary(x => x.Status, x => x.Count);
            var booked = c.GetValueOrDefault(SeatStatuses.Booked) + c.GetValueOrDefault(SeatStatuses.Attended) + c.GetValueOrDefault(SeatStatuses.Absent);
            var seat = mine.GetValueOrDefault(r.Id);
            int? position = seat?.Status == SeatStatuses.Waiting ? waiting[r.Id].Select(w => w.Learner).ToList().IndexOf(user) + 1 : null;
            var canManage = manageable[r.CourseId] && r.Status == SessionStatuses.Scheduled && r.StartsAt > now;
            var canAttend = (r.FacilitatorId == user || manageable[r.CourseId]) && r.Status == SessionStatuses.Scheduled && now >= r.StartsAt && now <= r.EndsAt + CorrectionWindow;
            return new SessionView(r.Id, r.CourseId, r.LessonKey, r.StartsAt, r.EndsAt, r.Place, r.Link, r.Capacity,
                new SessionPerson(r.FacilitatorId, facilitators.TryGetValue(r.FacilitatorId, out var f) ? f.DisplayName : ""), r.Status, booked,
                c.GetValueOrDefault(SeatStatuses.Waiting), seat?.Status, position, canManage, canAttend, titles[r.CourseId]);
        }).ToList();
    }
}
