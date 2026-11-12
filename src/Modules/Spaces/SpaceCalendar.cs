using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public sealed record SpaceEventRequest(string? Title, string? Description, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, bool? AllDay, int? Version);

public sealed record SpaceEventView(Guid Id, string Title, string Description, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool AllDay, PersonRef CreatedBy, int Version);

public sealed record CalendarMilestone(Guid Id, string Name, DateOnly DueOn, bool Reached);

public sealed record CalendarTask(Guid Id, string Title, DateOnly DueOn, string Status, IReadOnlyList<PersonRef> Assignees);

/// <summary>A member away on approved leave. <see cref="End"/> is exclusive. Never the leave type (FR-009).</summary>
public sealed record CalendarAbsence(PersonRef Person, DateOnly Start, DateOnly End, bool StartHalf, bool EndHalf);

/// <summary>The team calendar between two dates, both included (US6 scenario 1).</summary>
public sealed record SpaceCalendarView(DateOnly From, DateOnly To, string TimeZone, IReadOnlyList<SpaceEventView> Events, IReadOnlyList<CalendarMilestone> Milestones,
    IReadOnlyList<CalendarTask> Tasks, IReadOnlyList<CalendarAbsence> Away, bool CanEdit);

/// <summary>One member's free and busy for a day: status blocks only, never subjects (US6 scenario 2).</summary>
public sealed record MemberAvailability(PersonRef Person, bool Shared, IReadOnlyList<AvailabilityBlock> Blocks);

/// <summary>
/// <see cref="State"/> is the viewer's Outlook state (spec 018): only <c>ok</c> carries members; otherwise the page
/// says what the viewer can do, such as connecting Outlook.
/// </summary>
public sealed record SpaceAvailabilityView(string State, DateOnly Date, IReadOnlyList<MemberAvailability> Members);

/// <summary>
/// The team calendar of a space (spec 029 US6, FR-009): the space's own events, its milestones and task due dates,
/// and the members' approved leave from spec 022 without the leave type. Free and busy comes from spec 018 as the
/// viewer, so Outlook's own sharing decides what shows. Readers see it; members and owners add events.
/// </summary>
public sealed class SpaceCalendar(
    IDbSession session,
    ITenantContext context,
    SpacesService spaces,
    IUserDirectory users,
    IHrCalendarFeed hr,
    IAvailability availability,
    IEntitlementService entitlements,
    IWorkingCalendar calendars,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxDays = 62;
    public const int MaxEvents = 1000;
    public const int MaxTitleLength = 120;
    public const int MaxDescriptionLength = 2000;
    public const int MaxEventDays = 31;
    public const int MaxAvailabilityMembers = 30;

    private const string EventColumns = "id, title, description, starts_at, ends_at, all_day, created_by, row_version";

    private sealed record EventRow(Guid Id, string Title, string Description, DateTimeOffset StartsAt, DateTimeOffset EndsAt, bool AllDay, Guid CreatedBy, int Version);

    private static EventRow ReadEvent(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
        r.GetFieldValue<DateTimeOffset>(4), r.GetBoolean(5), r.GetGuid(6), r.GetInt32(7));

    private async Task<IReadOnlyList<SpaceEventView>> ViewsAsync(IReadOnlyList<EventRow> rows, CancellationToken ct)
    {
        var names = await users.GetManyAsync(rows.Select(r => r.CreatedBy).Distinct().ToList(), ct);
        return rows.Select(r => new SpaceEventView(r.Id, r.Title, r.Description, r.StartsAt, r.EndsAt, r.AllDay,
            new PersonRef(r.CreatedBy, names.TryGetValue(r.CreatedBy, out var u) ? u.DisplayName : ""), r.Version)).ToList();
    }

    private async Task<IReadOnlyList<Guid>> MemberIdsAsync(Guid spaceId, CancellationToken ct) =>
        await session.QueryAsync("select user_id from spaces.space_member where space_id = @spaceId order by added_at", new { spaceId }, r => r.GetGuid(0), ct);

    public async Task<SpaceCalendarView> GetAsync(Guid spaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        var calendar = await calendars.ForUserAsync(context.RequireUser(), ct);
        var today = calendar.Today(clock.UtcNow);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        var end = to ?? start.AddMonths(1).AddDays(-1);
        if (end < start || end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            throw AppException.Validation("to", "validation.spaces.calendar_range");
        }
        var zone = calendar.Zone;
        var rangeStart = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(start.ToDateTime(TimeOnly.MinValue))).ToUniversalTime();
        var rangeEnd = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), zone.GetUtcOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue))).ToUniversalTime();

        var events = await session.QueryAsync(
            $"select {EventColumns} from spaces.space_event where space_id = @spaceId and starts_at < @rangeEnd and ends_at >= @rangeStart order by starts_at, id",
            new { spaceId, rangeStart, rangeEnd }, ReadEvent, ct);
        var milestones = await session.QueryAsync(
            "select id, name, due_on, reached_at is not null from spaces.milestone where space_id = @spaceId and due_on between @start and @end order by due_on, position",
            new { spaceId, start, end }, r => new CalendarMilestone(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetBoolean(3)), ct);
        var tasks = await session.QueryAsync(
            """
            select t.id, t.title, t.due_on, t.status, coalesce(array_agg(a.user_id) filter (where a.user_id is not null), '{}')
            from spaces.task t left join spaces.task_assignee a on a.task_id = t.id
            where t.space_id = @spaceId and t.due_on between @start and @end
            group by t.tenant_id, t.id order by t.due_on, t.position, t.id limit 500
            """, new { spaceId, start, end }, r => (Id: r.GetGuid(0), Title: r.GetString(1), Due: r.GetFieldValue<DateOnly>(2), Status: r.GetString(3), People: r.GetFieldValue<Guid[]>(4)), ct);

        // Approved leave of the members only; the type is dropped even where the viewer could see it (FR-009).
        var members = await MemberIdsAsync(spaceId, ct);
        var leave = (await hr.LeaveAsync(members, start, end, ct)).Where(l => l.Status == "approved").ToList();
        var names = await users.GetManyAsync(members.Concat(tasks.SelectMany(t => t.People)).Distinct().ToList(), ct);
        PersonRef Person(Guid id) => new(id, names.TryGetValue(id, out var u) ? u.DisplayName : "");
        return new SpaceCalendarView(start, end, zone.Id, await ViewsAsync(events, ct), milestones,
            tasks.Select(t => new CalendarTask(t.Id, t.Title, t.Due, t.Status, t.People.Select(Person).ToList())).ToList(),
            leave.Select(l => new CalendarAbsence(Person(l.UserId), l.Start, l.End, l.StartHalf, l.EndHalf)).ToList(), standing.Contributes);
    }

    private static (string Title, string Description, DateTimeOffset Start, DateTimeOffset End, bool AllDay) Validate(SpaceEventRequest request)
    {
        var errors = new ValidationErrors();
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 || title.Length > MaxTitleLength || title.Any(char.IsControl))
        {
            errors.Add("title", "validation.spaces.event_title");
        }
        var description = request.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength)
        {
            errors.Add("description", "validation.spaces.event_description");
        }
        if (request.StartsAt is not { } start || request.EndsAt is not { } end || end < start || (end - start).TotalDays > MaxEventDays)
        {
            errors.Add("endsAt", "validation.spaces.event_time");
        }
        errors.ThrowIfAny();
        return (title, description, request.StartsAt!.Value.ToUniversalTime(), request.EndsAt!.Value.ToUniversalTime(), request.AllDay ?? false);
    }

    public async Task<SpaceEventView> CreateAsync(Guid spaceId, SpaceEventRequest request, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        var (title, description, start, end, allDay) = Validate(request);
        if (await session.ScalarAsync<int>("select count(*)::int from spaces.space_event where space_id = @spaceId", new { spaceId }, ct) >= MaxEvents)
        {
            throw AppException.Conflict("conflict.spaces.event_limit");
        }
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into spaces.space_event (tenant_id, id, space_id, title, description, starts_at, ends_at, all_day, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @spaceId, @title, @description, @start, @end, @allDay, @user, @now, @now)
            """, new { id, spaceId, title, description, start, end, allDay, user = context.RequireUser(), now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesEventChanged, SpacesService.ResourceType, spaceId.ToString(), Changes: new { @event = id, change = "created" }), ct);
        return await EventAsync(spaceId, id, ct);
    }

    private async Task<SpaceEventView> EventAsync(Guid spaceId, Guid eventId, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync($"select {EventColumns} from spaces.space_event where id = @eventId and space_id = @spaceId",
            new { eventId, spaceId }, ReadEvent, ct) ?? throw AppException.NotFound();
        return (await ViewsAsync([row], ct))[0];
    }

    public async Task<SpaceEventView> UpdateAsync(Guid spaceId, Guid eventId, SpaceEventRequest request, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        var (title, description, start, end, allDay) = Validate(request);
        var version = await session.QuerySingleOrDefaultAsync("select row_version from spaces.space_event where id = @eventId and space_id = @spaceId for update",
            new { eventId, spaceId }, r => (int?)r.GetInt32(0), ct) ?? throw AppException.NotFound();
        if (request.Version != version)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.event_changed", null, new Dictionary<string, object?> { ["version"] = version });
        }
        await session.ExecAsync(
            """
            update spaces.space_event set title = @title, description = @description, starts_at = @start, ends_at = @end, all_day = @allDay, updated_at = @now,
              row_version = row_version + 1
            where id = @eventId
            """, new { eventId, title, description, start, end, allDay, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesEventChanged, SpacesService.ResourceType, spaceId.ToString(), Changes: new { @event = eventId, change = "changed" }), ct);
        return await EventAsync(spaceId, eventId, ct);
    }

    public async Task DeleteAsync(Guid spaceId, Guid eventId, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        if (await session.ExecAsync("delete from spaces.space_event where id = @eventId and space_id = @spaceId", new { eventId, spaceId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesEventChanged, SpacesService.ResourceType, spaceId.ToString(), Changes: new { @event = eventId, change = "deleted" }), ct);
    }

    /// <summary>
    /// The members' free and busy on one day, read as the viewer through spec 018, so a member whose Outlook does not
    /// share with the viewer shows as not shared. Status blocks only. The first members up to the limit are read.
    /// </summary>
    public async Task<SpaceAvailabilityView> AvailabilityAsync(Guid spaceId, DateOnly? date, CancellationToken ct)
    {
        await spaces.ReadableAsync(spaceId, ct);
        var viewer = context.RequireUser();
        var calendar = await calendars.ForUserAsync(viewer, ct);
        var day = date ?? calendar.Today(clock.UtcNow);
        if (!await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct))
        {
            return new SpaceAvailabilityView(M365States.TurnedOff, day, []);
        }
        var start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), calendar.Zone.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue)));
        var end = start.AddDays(1);
        var members = (await MemberIdsAsync(spaceId, ct)).Where(m => m != viewer).Take(MaxAvailabilityMembers).ToList();
        var people = await users.GetManyAsync(members, ct);
        var result = new List<MemberAvailability>();
        foreach (var member in members)
        {
            if (!people.TryGetValue(member, out var person) || string.IsNullOrEmpty(person.Email))
            {
                continue;
            }
            var answer = await availability.GetAsync(person.Email, start, end, ct);
            if (answer.State != M365States.Ok)
            {
                // The viewer's own connection decides for everyone: not connected, reconnect, or set up by an administrator.
                return new SpaceAvailabilityView(answer.State, day, []);
            }
            result.Add(new MemberAvailability(new PersonRef(member, person.DisplayName), answer.Shared, answer.Shared ? answer.Blocks : []));
        }
        return new SpaceAvailabilityView(M365States.Ok, day, result);
    }
}
