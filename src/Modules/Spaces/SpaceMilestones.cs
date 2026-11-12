using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public static class SpaceStatusValues
{
    public static readonly IReadOnlyList<string> All = ["on_track", "at_risk", "off_track", "done"];
}

public sealed record MilestoneRequest(string? Name, DateOnly? DueOn, int? Version);

public sealed record StatusRequest(string? Status, string? Note);

public sealed record MilestoneView(Guid Id, string Name, DateOnly? DueOn, int Done, int Total, bool Reached, bool Overdue, int Version);

public sealed record StatusView(Guid Id, string Status, string Note, PersonRef SetBy, DateTimeOffset SetAt);

/// <summary>What the space overview shows (US3 scenario 1): the current status, milestone progress and overdue tasks.</summary>
public sealed record SpaceOverview(StatusView? Status, IReadOnlyList<StatusView> History, IReadOnlyList<MilestoneView> Milestones, int OpenTasks, int DoneTasks,
    int OverdueTasks, IReadOnlyList<OverdueTask> Overdue, bool CanSetStatus, bool CanEdit);

public sealed record OverdueTask(Guid Id, string Title, DateOnly DueOn);

/// <summary>
/// Milestones and the space's status (spec 029 US3, FR-005). A milestone's progress is the share of its linked tasks
/// that are done; when all are done it is reached once and <see cref="MilestoneReached"/> is published. Owners set
/// the status with a note; every change is kept and the members are told.
/// </summary>
public sealed class SpaceMilestones(
    IDbSession session,
    ITenantContext context,
    SpacesService spaces,
    IUserDirectory users,
    IWorkingCalendar calendars,
    INotificationSender notifications,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxMilestones = 50;
    public const int MaxNoteLength = 2000;
    public const int HistoryShown = 20;
    public const int OverdueShown = 20;
    public const string StatusTemplate = "spaces.space.status";

    private async Task<DateOnly> TodayAsync(CancellationToken ct) => (await calendars.ForUserAsync(context.RequireUser(), ct)).Today(clock.UtcNow);

    /// <summary>Marks a milestone reached when every linked task is done, or not reached again when one reopens.</summary>
    internal async Task RecountAsync(Guid spaceId, Guid? milestoneId, CancellationToken ct)
    {
        if (milestoneId is not { } id)
        {
            return;
        }
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select (select count(*)::int from spaces.task where milestone_id = @id), (select count(*)::int from spaces.task where milestone_id = @id and status = 'done'),
              reached_at is not null
            from spaces.milestone where id = @id
            """, new { id }, r => (Total: r.GetInt32(0), Done: r.GetInt32(1), Reached: r.GetBoolean(2)), ct);
        var complete = counts.Total > 0 && counts.Done == counts.Total;
        if (complete && !counts.Reached)
        {
            await session.ExecAsync("update spaces.milestone set reached_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
            await outbox.EnqueueAsync(new MilestoneReached(spaceId, id), ct);
        }
        else if (!complete && counts.Reached)
        {
            await session.ExecAsync("update spaces.milestone set reached_at = null where id = @id", new { id }, ct);
        }
    }

    private async Task<IReadOnlyList<MilestoneView>> ListAsync(Guid spaceId, CancellationToken ct)
    {
        var today = await TodayAsync(ct);
        return await session.QueryAsync(
            """
            select m.id, m.name, m.due_on, (select count(*)::int from spaces.task t where t.milestone_id = m.id and t.status = 'done'),
              (select count(*)::int from spaces.task t where t.milestone_id = m.id), m.reached_at is not null, m.row_version
            from spaces.milestone m where m.space_id = @spaceId order by m.due_on nulls last, m.position, m.id
            """, new { spaceId },
            r =>
            {
                DateOnly? due = r.IsDBNull(2) ? null : r.GetFieldValue<DateOnly>(2);
                var reached = r.GetBoolean(5);
                return new MilestoneView(r.GetGuid(0), r.GetString(1), due, r.GetInt32(3), r.GetInt32(4), reached, !reached && due < today, r.GetInt32(6));
            }, ct);
    }

    public async Task<IReadOnlyList<MilestoneView>> MilestonesAsync(Guid spaceId, CancellationToken ct)
    {
        await spaces.ReadableAsync(spaceId, ct);
        return await ListAsync(spaceId, ct);
    }

    private static string Validate(MilestoneRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        return name.Length is 0 or > SpaceTemplates.MaxNameLength ? throw AppException.Validation("name", "validation.spaces.short_name") : name;
    }

    public async Task<IReadOnlyList<MilestoneView>> CreateAsync(Guid spaceId, MilestoneRequest request, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        var name = Validate(request);
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.milestone where space_id = @spaceId", new { spaceId }, ct);
        if (count >= MaxMilestones)
        {
            throw AppException.Validation("milestones", "validation.spaces.milestone_count");
        }
        await session.ExecAsync(
            """
            insert into spaces.milestone (tenant_id, id, space_id, name, due_on, position, created_by, created_at)
            values (platform.current_tenant_id(), @id, @spaceId, @name, @due, @count, @user, @now)
            """, new { id = Ids.New(), spaceId, name, due = request.DueOn, count, user = context.RequireUser(), now = clock.UtcNow }, ct);
        return await ListAsync(spaceId, ct);
    }

    public async Task<IReadOnlyList<MilestoneView>> UpdateAsync(Guid spaceId, Guid milestoneId, MilestoneRequest request, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        var version = await session.QuerySingleOrDefaultAsync("select row_version from spaces.milestone where id = @milestoneId and space_id = @spaceId",
            new { milestoneId, spaceId }, r => (int?)r.GetInt32(0), ct) ?? throw AppException.NotFound();
        if (request.Version != version)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.milestone_changed", null, new Dictionary<string, object?> { ["version"] = version });
        }
        var name = Validate(request);
        await session.ExecAsync("update spaces.milestone set name = @name, due_on = @due, row_version = row_version + 1 where id = @milestoneId",
            new { milestoneId, name, due = request.DueOn }, ct);
        return await ListAsync(spaceId, ct);
    }

    /// <summary>Removes a milestone; its tasks stay, without a milestone.</summary>
    public async Task<IReadOnlyList<MilestoneView>> DeleteAsync(Guid spaceId, Guid milestoneId, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        await session.ExecAsync("update spaces.task set milestone_id = null where milestone_id = @milestoneId and space_id = @spaceId", new { milestoneId, spaceId }, ct);
        if (await session.ExecAsync("delete from spaces.milestone where id = @milestoneId and space_id = @spaceId", new { milestoneId, spaceId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        return await ListAsync(spaceId, ct);
    }

    // ---- Status ----

    private async Task<IReadOnlyList<StatusView>> HistoryAsync(Guid spaceId, int limit, CancellationToken ct)
    {
        var rows = await session.QueryAsync("select id, status, note, set_by, set_at from spaces.status_entry where space_id = @spaceId order by set_at desc, id limit @limit",
            new { spaceId, limit }, r => (Id: r.GetGuid(0), Status: r.GetString(1), Note: r.GetString(2), By: r.GetGuid(3), At: r.GetFieldValue<DateTimeOffset>(4)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.By).Distinct().ToList(), ct);
        return rows.Select(r => new StatusView(r.Id, r.Status, r.Note, new PersonRef(r.By, names.TryGetValue(r.By, out var u) ? u.DisplayName : ""), r.At)).ToList();
    }

    /// <summary>An owner sets the status with a note (US3 scenario 2). Members who can read the space are told.</summary>
    public async Task<SpaceOverview> SetStatusAsync(Guid spaceId, StatusRequest request, CancellationToken ct)
    {
        var standing = await spaces.OwnedAsync(spaceId, false, ct);
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        var errors = new ValidationErrors();
        if (request.Status is not { } status || !SpaceStatusValues.All.Contains(status))
        {
            errors.Add("status", "validation.spaces.status");
            status = "";
        }
        var note = request.Note?.Trim() ?? "";
        if (note.Length > MaxNoteLength)
        {
            errors.Add("note", "validation.spaces.status_note");
        }
        errors.ThrowIfAny();
        var id = Ids.New();
        var user = context.RequireUser();
        await session.ExecAsync(
            "insert into spaces.status_entry (tenant_id, id, space_id, status, note, set_by, set_at) values (platform.current_tenant_id(), @id, @spaceId, @status, @note, @user, @now)",
            new { id, spaceId, status, note, user, now = clock.UtcNow }, ct);
        await outbox.EnqueueAsync(new SpaceStatusChanged(spaceId, status), ct);
        var members = await session.QueryAsync("select user_id from spaces.space_member where space_id = @spaceId and user_id <> @user", new { spaceId, user }, r => r.GetGuid(0), ct);
        var recipients = new List<Guid>();
        foreach (var member in members)
        {
            if (await spaces.CanReadAsync(standing.Space, member, ct))
            {
                recipients.Add(member);
            }
        }
        if (recipients.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SpaceActivity, StatusTemplate, SpacesService.Link(spaceId),
                new Dictionary<string, string> { ["space"] = standing.Space.Name, ["status"] = status }, DedupeKey: $"{id:N}:status"), ct);
        }
        return await OverviewAsync(spaceId, ct);
    }

    public async Task<SpaceOverview> OverviewAsync(Guid spaceId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        var today = await TodayAsync(ct);
        var history = await HistoryAsync(spaceId, HistoryShown, ct);
        var counts = await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where status = 'open')::int, count(*) filter (where status = 'done')::int,
              count(*) filter (where status = 'open' and due_on < @today)::int
            from spaces.task where space_id = @spaceId
            """, new { spaceId, today }, r => (Open: r.GetInt32(0), Done: r.GetInt32(1), Overdue: r.GetInt32(2)), ct);
        var overdue = await session.QueryAsync(
            "select id, title, due_on from spaces.task where space_id = @spaceId and status = 'open' and due_on < @today order by due_on, id limit @limit",
            new { spaceId, today, limit = OverdueShown }, r => new OverdueTask(r.GetGuid(0), r.GetString(1), r.GetFieldValue<DateOnly>(2)), ct);
        return new SpaceOverview(history.FirstOrDefault(), history, await ListAsync(spaceId, ct), counts.Open, counts.Done, counts.Overdue, overdue,
            standing.Owns && standing.Space.Active, standing.Contributes);
    }
}
