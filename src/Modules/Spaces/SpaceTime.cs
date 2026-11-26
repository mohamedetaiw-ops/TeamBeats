using System.Globalization;
using System.Text;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public sealed record TimeEntryRequest(DateOnly? WorkedOn, int? Minutes, string? Note);

/// <summary>A time entry as the viewer gets it. CanChange: the viewer wrote it, or owns the space (removal only).</summary>
public sealed record TimeEntryView(Guid Id, PersonRef Person, DateOnly WorkedOn, int Minutes, string Note, bool CanEdit, bool CanRemove);

/// <summary>Time on one task (US9 scenario 1): its estimate, the total logged, and the entries, newest day first.</summary>
public sealed record TaskTime(Guid TaskId, int? EstimateMinutes, int LoggedMinutes, IReadOnlyList<TimeEntryView> Entries, bool CanLog);

public sealed record PersonTime(PersonRef Person, int Minutes);

public sealed record TaskTimeTotal(Guid TaskId, string Title, int? EstimateMinutes, int Minutes);

/// <summary>Time logged in a space between two days, by person and by task, for its owners (US9 scenario 3).</summary>
public sealed record TimeTotals(DateOnly From, DateOnly To, int Minutes, IReadOnlyList<PersonTime> People, IReadOnlyList<TaskTimeTotal> Tasks);

/// <summary>
/// Time tracking on tasks (spec 029 R3 US9, FR-021, FR-022). Members and owners log time on tasks of an active space;
/// only the author changes an entry, and an owner may remove any. Owners see totals for a date range and export them.
/// This is not a time sheet: there is no approval or billing (section 2).
/// </summary>
public sealed class SpaceTime(IDbSession session, ITenantContext context, SpacesService spaces, IUserDirectory users, IWorkingCalendar calendars, IClock clock)
{
    public const int MaxNoteLength = 500;
    public const int MaxDaysBack = 365;
    public const int MaxRangeDays = 366;
    public const int DefaultRangeDays = 30;
    public const int MaxEntriesShown = 200;

    private async Task<DateOnly> TodayAsync(CancellationToken ct) => (await calendars.ForUserAsync(context.RequireUser(), ct)).Today(clock.UtcNow);

    public async Task<TaskTime> GetAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        return await TaskTimeAsync(standing, taskId, ct);
    }

    private async Task<TaskTime> TaskTimeAsync(SpaceStanding standing, Guid taskId, CancellationToken ct)
    {
        var estimate = await session.QuerySingleOrDefaultAsync("select estimate_minutes from spaces.task where id = @taskId and space_id = @spaceId",
            new { taskId, spaceId = standing.Space.Id }, r => (Found: true, Estimate: r.Get<int?>("estimate_minutes")), ct);
        if (!estimate.Found)
        {
            throw AppException.NotFound();
        }
        var total = await session.ScalarAsync<int>("select coalesce(sum(minutes), 0)::int from spaces.time_entry where task_id = @taskId", new { taskId }, ct);
        var rows = await session.QueryAsync(
            "select id, user_id, worked_on, minutes, note from spaces.time_entry where task_id = @taskId order by worked_on desc, created_at desc, id limit @limit",
            new { taskId, limit = MaxEntriesShown },
            r => (Id: r.GetGuid(0), User: r.GetGuid(1), On: r.GetFieldValue<DateOnly>(2), Minutes: r.GetInt32(3), Note: r.GetString(4)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.User).Distinct().ToList(), ct);
        var me = context.RequireUser();
        var active = standing.Space.Active;
        return new TaskTime(taskId, estimate.Estimate, total,
            rows.Select(r => new TimeEntryView(r.Id, new PersonRef(r.User, names.TryGetValue(r.User, out var u) ? u.DisplayName : ""), r.On, r.Minutes, r.Note,
                active && r.User == me && standing.Contributes, active && (r.User == me && standing.Contributes || standing.Owns))).ToList(),
            standing.Contributes);
    }

    private async Task<(DateOnly WorkedOn, int Minutes, string Note)> ValidateAsync(TimeEntryRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var today = await TodayAsync(ct);
        if (request.WorkedOn is not { } on || on > today || on < today.AddDays(-MaxDaysBack))
        {
            errors.Add("workedOn", "validation.spaces.time_day");
        }
        if (request.Minutes is not (>= 1 and <= 1440))
        {
            errors.Add("minutes", "validation.spaces.time_minutes");
        }
        var note = request.Note?.Trim() ?? "";
        if (note.Length > MaxNoteLength)
        {
            errors.Add("note", "validation.spaces.time_note");
        }
        errors.ThrowIfAny();
        return (request.WorkedOn!.Value, request.Minutes!.Value, note);
    }

    private async Task<SpaceStanding> TaskInSpaceAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var standing = await spaces.ContributableAsync(spaceId, ct);
        return await session.ScalarAsync<bool>("select exists(select 1 from spaces.task where id = @taskId and space_id = @spaceId)", new { taskId, spaceId }, ct)
            ? standing
            : throw AppException.NotFound();
    }

    public async Task<TaskTime> LogAsync(Guid spaceId, Guid taskId, TimeEntryRequest request, CancellationToken ct)
    {
        var standing = await TaskInSpaceAsync(spaceId, taskId, ct);
        var (on, minutes, note) = await ValidateAsync(request, ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into spaces.time_entry (tenant_id, id, space_id, task_id, user_id, worked_on, minutes, note, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @spaceId, @taskId, @user, @on, @minutes, @note, @now, @now)
            """, new { id = Ids.New(), spaceId, taskId, user = context.RequireUser(), on, minutes, note, now }, ct);
        return await TaskTimeAsync(standing, taskId, ct);
    }

    /// <summary>Only the author changes an entry (US9 scenario 2).</summary>
    public async Task<TaskTime> UpdateAsync(Guid spaceId, Guid taskId, Guid entryId, TimeEntryRequest request, CancellationToken ct)
    {
        var standing = await TaskInSpaceAsync(spaceId, taskId, ct);
        var author = await AuthorAsync(taskId, entryId, ct);
        if (author != context.RequireUser())
        {
            throw AppException.Denied("authz.denied.spaces_time_entry");
        }
        var (on, minutes, note) = await ValidateAsync(request, ct);
        await session.ExecAsync("update spaces.time_entry set worked_on = @on, minutes = @minutes, note = @note, updated_at = @now where id = @entryId",
            new { entryId, on, minutes, note, now = clock.UtcNow }, ct);
        return await TaskTimeAsync(standing, taskId, ct);
    }

    /// <summary>The author removes their entry; an owner may remove any (US9 scenario 2).</summary>
    public async Task<TaskTime> RemoveAsync(Guid spaceId, Guid taskId, Guid entryId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        var author = await AuthorAsync(taskId, entryId, ct);
        if (!(author == context.RequireUser() && standing.Contributes) && !standing.Owns)
        {
            throw AppException.Denied("authz.denied.spaces_time_entry");
        }
        await session.ExecAsync("delete from spaces.time_entry where id = @entryId", new { entryId }, ct);
        return await TaskTimeAsync(standing, taskId, ct);
    }

    private async Task<Guid> AuthorAsync(Guid taskId, Guid entryId, CancellationToken ct) =>
        await session.ScalarAsync<Guid?>("select user_id from spaces.time_entry where id = @entryId and task_id = @taskId", new { entryId, taskId }, ct)
        ?? throw AppException.NotFound();

    /// <summary>Totals for the owners between two days, the last 30 days when none are given (US9 scenario 3).</summary>
    public async Task<TimeTotals> TotalsAsync(Guid spaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        await spaces.OwnedAsync(spaceId, false, ct);
        var end = to ?? await TodayAsync(ct);
        var start = from ?? end.AddDays(-(DefaultRangeDays - 1));
        if (start > end || end.DayNumber - start.DayNumber >= MaxRangeDays)
        {
            throw AppException.Validation("from", "validation.spaces.time_range");
        }
        var people = await session.QueryAsync(
            "select user_id, sum(minutes)::int from spaces.time_entry where space_id = @spaceId and worked_on between @start and @end group by user_id order by 2 desc, 1",
            new { spaceId, start, end }, r => (User: r.GetGuid(0), Minutes: r.GetInt32(1)), ct);
        var tasks = await session.QueryAsync(
            """
            select t.id, t.title, t.estimate_minutes, sum(e.minutes)::int from spaces.time_entry e join spaces.task t on t.id = e.task_id
            where e.space_id = @spaceId and e.worked_on between @start and @end group by t.id, t.title, t.estimate_minutes order by 4 desc, t.title, t.id
            """, new { spaceId, start, end },
            r => new TaskTimeTotal(r.GetGuid(0), r.GetString(1), r.Get<int?>("estimate_minutes"), r.GetInt32(3)), ct);
        var names = await users.GetManyAsync(people.Select(p => p.User).ToList(), ct);
        return new TimeTotals(start, end, people.Sum(p => p.Minutes),
            people.Select(p => new PersonTime(new PersonRef(p.User, names.TryGetValue(p.User, out var u) ? u.DisplayName : ""), p.Minutes)).ToList(), tasks);
    }

    /// <summary>Every entry in the range as CSV for the owners, protected against spreadsheet formulas.</summary>
    public async Task<(string FileName, byte[] Content)> ExportAsync(Guid spaceId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var totals = await TotalsAsync(spaceId, from, to, ct);
        var rows = await session.QueryAsync(
            """
            select e.worked_on, e.user_id, t.title, e.minutes, e.note from spaces.time_entry e join spaces.task t on t.id = e.task_id
            where e.space_id = @spaceId and e.worked_on between @start and @end order by e.worked_on, e.created_at, e.id
            """, new { spaceId, start = totals.From, end = totals.To },
            r => (On: r.GetFieldValue<DateOnly>(0), User: r.GetGuid(1), Task: r.GetString(2), Minutes: r.GetInt32(3), Note: r.GetString(4)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.User).Distinct().ToList(), ct);
        var csv = new StringBuilder("date,person,task,minutes,note\n");
        foreach (var r in rows)
        {
            csv.Append(r.On.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
                .Append(SpaceTasks.Csv(names.TryGetValue(r.User, out var u) ? u.DisplayName : "")).Append(',')
                .Append(SpaceTasks.Csv(r.Task)).Append(',').Append(r.Minutes.ToString(CultureInfo.InvariantCulture)).Append(',').Append(SpaceTasks.Csv(r.Note)).Append('\n');
        }
        return ($"time-{spaceId:N}-{totals.From:yyyyMMdd}-{totals.To:yyyyMMdd}.csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray());
    }
}
