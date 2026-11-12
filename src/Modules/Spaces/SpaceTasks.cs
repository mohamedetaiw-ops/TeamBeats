using System.Text;
using Npgsql;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public static class TaskPriorities
{
    public const string Normal = "normal";

    public static readonly IReadOnlyList<string> All = ["low", Normal, "high", "urgent"];
}

public sealed record TaskRequest(
    Guid? ListId,
    Guid? ColumnId,
    string? Title,
    string? Description,
    string? Priority,
    IReadOnlyList<string>? Labels,
    DateOnly? DueOn,
    IReadOnlyList<Guid>? Assignees,
    Guid? MilestoneId,
    int? Version);

public sealed record MoveRequest(Guid? ColumnId, int? Position);

public sealed record ChecklistRequest(string? Text, bool? Done);

public sealed record CommentRequest(string? Body);

public sealed record ListRequest(string? Name);

public sealed record ColumnRequest(string? Name, bool? Done);

public sealed record ChecklistProgress(int Done, int Total);

/// <summary>A task on the list and the board.</summary>
public sealed record TaskCard(
    Guid Id,
    Guid ListId,
    Guid ColumnId,
    string Title,
    string Priority,
    IReadOnlyList<string> Labels,
    DateOnly? DueOn,
    string Status,
    bool Overdue,
    IReadOnlyList<PersonRef> Assignees,
    Guid? MilestoneId,
    ChecklistProgress Checklist,
    int Comments,
    int Position,
    int Version);

public sealed record ChecklistItemView(Guid Id, string Text, bool Done);

public sealed record CommentView(Guid Id, PersonRef Author, string Body, DateTimeOffset CreatedAt);

/// <summary>A library file linked to a task, as the viewer gets it: the version readers get now (US5 scenario 3).</summary>
public sealed record TaskFileView(Guid DocumentId, string Name, string Version, string FileName, string ContentType, long Bytes);

public sealed record TaskFileRequest(Guid? DocumentId);

/// <summary>A task with everything on its page. Files lists only the linked files the viewer may open.</summary>
public sealed record TaskDetail(TaskCard Task, string Description, PersonRef CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt,
    IReadOnlyList<ChecklistItemView> Checklist, IReadOnlyList<CommentView> Comments, bool CanEdit, IReadOnlyList<TaskFileView>? Files = null);

public sealed record ColumnView(Guid Id, string Name, bool Done, int Position);

public sealed record ListView(Guid Id, string Name, int Position, IReadOnlyList<ColumnView> Columns);

/// <summary>A space's lists, their columns and every task in them: the list view and the board read the same data.</summary>
public sealed record TaskBoard(IReadOnlyList<ListView> Lists, IReadOnlyList<TaskCard> Tasks, bool CanEdit);

/// <summary>A task in My tasks, with the space it is in (US7).</summary>
public sealed record MyTask(TaskCard Task, Guid SpaceId, string SpaceName);

/// <summary>
/// Tasks of a space (spec 029 US2, US7, FR-004, FR-010, FR-011, FR-015): lists and board columns, assignees, due
/// dates, priority, labels, checklists and comments. Members and owners change tasks; viewers and people who only
/// read a public space are refused by the server whatever the page shows. Moving a task into a done column completes
/// it and moving it out reopens it. People are told about assignments and completions in one group per space.
/// </summary>
public sealed class SpaceTasks(
    IDbSession session,
    ITenantContext context,
    SpacesService spaces,
    SpaceMilestones milestones,
    IDocumentReferences documents,
    IUserDirectory users,
    IWorkingCalendar calendars,
    INotificationSender notifications,
    IOutbox outbox,
    IClock clock)
{
    public const int MaxTasksPerSpace = 5000;
    public const int MaxLists = 20;
    public const int MaxColumns = 12;
    public const int MaxLabels = 10;
    public const int MaxLabelLength = 30;
    public const int MaxAssignees = 20;
    public const int MaxDescriptionLength = 10_000;
    public const int MaxChecklistItems = 100;
    public const int MaxChecklistText = 300;
    public const int MaxCommentLength = 4000;
    public const int MaxFiles = 20;
    public const int MyTasksMax = 500;
    public const string AssignedTemplate = "spaces.task.assigned";
    public const string CompletedTemplate = "spaces.task.completed";
    public const string DueTemplate = "spaces.task.due";

    /// <summary>The day-before reminder goes at 09:00 in the assignee's time zone (section 6).</summary>
    public static readonly TimeOnly ReminderTime = new(9, 0);

    public static string Link(Guid spaceId, Guid taskId) => $"/spaces/{spaceId}/tasks/{taskId}";

    /// <summary>One group per space and kind of news, because a folded notification keeps the first one's text.</summary>
    private static string GroupKey(Guid spaceId, string template) => $"spaces:{spaceId:N}:{template}";

    // ---- Reading ----

    private const string CardColumns =
        """
        t.id, t.list_id, t.column_id, t.title, t.priority, t.labels, t.due_on, t.status, t.milestone_id, t.position, t.row_version,
        (select count(*)::int from spaces.checklist_item c where c.task_id = t.id and c.done),
        (select count(*)::int from spaces.checklist_item c where c.task_id = t.id),
        (select count(*)::int from spaces.task_comment m where m.task_id = t.id),
        coalesce((select array_agg(a.user_id order by a.assigned_at, a.user_id) from spaces.task_assignee a where a.task_id = t.id), '{}'),
        t.space_id
        """;

    private sealed record CardRow(Guid Id, Guid ListId, Guid ColumnId, string Title, string Priority, string[] Labels, DateOnly? DueOn, string Status, Guid? MilestoneId,
        int Position, int Version, int ChecklistDone, int ChecklistTotal, int Comments, Guid[] Assignees, Guid SpaceId);

    private static CardRow ReadCard(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetFieldValue<string[]>(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.GetString(7), r.Get<Guid?>("milestone_id"), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12),
        r.GetInt32(13), r.GetFieldValue<Guid[]>(14), r.GetGuid(15));

    private async Task<DateOnly> TodayAsync(CancellationToken ct) => (await calendars.ForUserAsync(context.RequireUser(), ct)).Today(clock.UtcNow);

    private async Task<IReadOnlyList<TaskCard>> CardsAsync(IReadOnlyList<CardRow> rows, CancellationToken ct)
    {
        var names = await users.GetManyAsync(rows.SelectMany(r => r.Assignees).Distinct().ToList(), ct);
        var today = await TodayAsync(ct);
        return rows.Select(r => new TaskCard(r.Id, r.ListId, r.ColumnId, r.Title, r.Priority, r.Labels, r.DueOn, r.Status,
            r.Status == "open" && r.DueOn is { } due && due < today,
            r.Assignees.Select(a => new PersonRef(a, names.TryGetValue(a, out var u) ? u.DisplayName : "")).ToList(),
            r.MilestoneId, new ChecklistProgress(r.ChecklistDone, r.ChecklistTotal), r.Comments, r.Position, r.Version)).ToList();
    }

    private Task<CardRow?> CardRowAsync(Guid spaceId, Guid taskId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {CardColumns} from spaces.task t where t.id = @taskId and t.space_id = @spaceId", new { spaceId, taskId }, ReadCard, ct);

    private async Task<IReadOnlyList<ListView>> ListsAsync(Guid spaceId, CancellationToken ct)
    {
        var lists = await session.QueryAsync("select id, name, position from spaces.task_list where space_id = @spaceId order by position, id", new { spaceId },
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Position: r.GetInt32(2)), ct);
        var columns = await session.QueryAsync(
            "select c.list_id, c.id, c.name, c.done, c.position from spaces.task_column c join spaces.task_list l on l.id = c.list_id where l.space_id = @spaceId order by c.position, c.id",
            new { spaceId }, r => (List: r.GetGuid(0), Column: new ColumnView(r.GetGuid(1), r.GetString(2), r.GetBoolean(3), r.GetInt32(4))), ct);
        return lists.Select(l => new ListView(l.Id, l.Name, l.Position, columns.Where(c => c.List == l.Id).Select(c => c.Column).ToList())).ToList();
    }

    /// <summary>The lists, columns and tasks of a space, for the list view and the board. Anyone who reads the space sees them.</summary>
    public async Task<TaskBoard> BoardAsync(Guid spaceId, Guid? listId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        var rows = await session.QueryAsync(
            $"select {CardColumns} from spaces.task t where t.space_id = @spaceId and (@listId::uuid is null or t.list_id = @listId) order by t.list_id, t.position, t.id",
            new { spaceId, listId }, ReadCard, ct);
        return new TaskBoard(await ListsAsync(spaceId, ct), await CardsAsync(rows, ct), standing.Contributes);
    }

    public async Task<TaskDetail> GetAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var standing = await spaces.ReadableAsync(spaceId, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    private async Task<TaskDetail> DetailAsync(SpaceStanding standing, Guid taskId, CancellationToken ct)
    {
        var row = await CardRowAsync(standing.Space.Id, taskId, ct) ?? throw AppException.NotFound();
        var card = (await CardsAsync([row], ct))[0];
        var extra = await session.QuerySingleOrDefaultAsync("select description, created_by, created_at, completed_at from spaces.task where id = @taskId", new { taskId },
            r => (Description: r.GetString(0), CreatedBy: r.GetGuid(1), CreatedAt: r.GetFieldValue<DateTimeOffset>(2), CompletedAt: r.Get<DateTimeOffset?>("completed_at")), ct);
        var checklist = await session.QueryAsync("select id, text, done from spaces.checklist_item where task_id = @taskId order by position, id", new { taskId },
            r => new ChecklistItemView(r.GetGuid(0), r.GetString(1), r.GetBoolean(2)), ct);
        var comments = await session.QueryAsync("select id, author_id, body, created_at from spaces.task_comment where task_id = @taskId order by created_at, id", new { taskId },
            r => (Id: r.GetGuid(0), Author: r.GetGuid(1), Body: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var names = await users.GetManyAsync(comments.Select(c => c.Author).Append(extra.CreatedBy).Distinct().ToList(), ct);
        PersonRef Person(Guid id) => new(id, names.TryGetValue(id, out var u) ? u.DisplayName : "");
        var files = new List<TaskFileView>();
        foreach (var document in await session.QueryAsync("select document_id from spaces.task_file where task_id = @taskId order by added_at, document_id", new { taskId },
                     r => r.GetGuid(0), ct))
        {
            if (await documents.ReadableAsync(document, ct) is { } f)
            {
                files.Add(new TaskFileView(f.DocumentId, f.Name, f.Label, f.FileName, f.ContentType, f.Bytes));
            }
        }
        return new TaskDetail(card, extra.Description, Person(extra.CreatedBy), extra.CreatedAt, extra.CompletedAt, checklist,
            comments.Select(c => new CommentView(c.Id, Person(c.Author), c.Body, c.At)).ToList(), standing.Contributes, files);
    }

    // ---- Lists and columns ----

    private static string Name(string? value, string field, int max, ValidationErrors errors)
    {
        var name = value?.Trim() ?? "";
        if (name.Length == 0 || name.Length > max)
        {
            errors.Add(field, "validation.spaces.short_name");
        }
        return name;
    }

    /// <summary>Adds a task list with To do, Doing and Done columns in the tenant's words.</summary>
    public async Task<IReadOnlyList<ListView>> AddListAsync(Guid spaceId, ListRequest request, IReadOnlyList<(string Name, bool Done)> columns, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        var errors = new ValidationErrors();
        var name = Name(request.Name, "name", SpaceTemplates.MaxNameLength, errors);
        errors.ThrowIfAny();
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.task_list where space_id = @spaceId", new { spaceId }, ct);
        if (count >= MaxLists)
        {
            throw AppException.Validation("lists", "validation.spaces.template_lists");
        }
        var listId = Ids.New();
        await session.ExecAsync("insert into spaces.task_list (tenant_id, id, space_id, name, position) values (platform.current_tenant_id(), @listId, @spaceId, @name, @count)",
            new { listId, spaceId, name, count }, ct);
        for (var c = 0; c < columns.Count; c++)
        {
            await session.ExecAsync(
                "insert into spaces.task_column (tenant_id, id, list_id, name, position, done) values (platform.current_tenant_id(), @id, @listId, @name, @c, @done)",
                new { id = Ids.New(), listId, name = columns[c].Name, c, done = columns[c].Done }, ct);
        }
        return await ListsAsync(spaceId, ct);
    }

    public async Task<IReadOnlyList<ListView>> AddColumnAsync(Guid spaceId, Guid listId, ColumnRequest request, CancellationToken ct)
    {
        await spaces.ContributableAsync(spaceId, ct);
        if (!await session.ScalarAsync<bool>("select exists(select 1 from spaces.task_list where id = @listId and space_id = @spaceId)", new { listId, spaceId }, ct))
        {
            throw AppException.NotFound();
        }
        var errors = new ValidationErrors();
        var name = Name(request.Name, "name", SpaceTemplates.MaxNameLength, errors);
        errors.ThrowIfAny();
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.task_column where list_id = @listId", new { listId }, ct);
        if (count >= MaxColumns)
        {
            throw AppException.Validation("columns", "validation.spaces.template_columns");
        }
        await session.ExecAsync(
            "insert into spaces.task_column (tenant_id, id, list_id, name, position, done) values (platform.current_tenant_id(), @id, @listId, @name, @count, @done)",
            new { id = Ids.New(), listId, name, count, done = request.Done ?? false }, ct);
        return await ListsAsync(spaceId, ct);
    }

    // ---- Changing tasks ----

    private sealed record Fields(string Title, string Description, string Priority, string[] Labels, DateOnly? DueOn, Guid[] Assignees, Guid? MilestoneId);

    private async Task<Fields> ValidateAsync(Guid spaceId, TaskRequest request, ValidationErrors errors, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > SpaceTemplates.MaxTitleLength)
        {
            errors.Add("title", "validation.spaces.task_title");
        }
        var description = request.Description?.Trim() ?? "";
        if (description.Length > MaxDescriptionLength)
        {
            errors.Add("description", "validation.spaces.task_description");
        }
        var priority = request.Priority ?? TaskPriorities.Normal;
        if (!TaskPriorities.All.Contains(priority))
        {
            errors.Add("priority", "validation.spaces.task_priority");
        }
        var labels = (request.Labels ?? []).Select(l => l?.Trim() ?? "").Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (labels.Length > MaxLabels || labels.Any(l => l.Length > MaxLabelLength))
        {
            errors.Add("labels", "validation.spaces.task_labels");
        }
        var assignees = (request.Assignees ?? []).Distinct().ToArray();
        if (assignees.Length > MaxAssignees)
        {
            errors.Add("assignees", "validation.spaces.task_assignees");
        }
        else if (assignees.Length > 0)
        {
            // Only people who can work in the space hold its tasks: owners and members, guests among them (section 17).
            var workers = await session.QueryAsync("select user_id from spaces.space_member where space_id = @spaceId and user_id = any(@assignees) and role in ('owner', 'member')",
                new { spaceId, assignees }, r => r.GetGuid(0), ct);
            if (workers.Count != assignees.Length)
            {
                errors.Add("assignees", "validation.spaces.task_assignee");
            }
        }
        if (request.MilestoneId is { } milestone
            && !await session.ScalarAsync<bool>("select exists(select 1 from spaces.milestone where id = @milestone and space_id = @spaceId)", new { milestone, spaceId }, ct))
        {
            errors.Add("milestoneId", "validation.spaces.milestone");
        }
        return new Fields(title, description, priority, labels, request.DueOn, assignees, request.MilestoneId);
    }

    private async Task<(Guid List, Guid Column, bool Done)?> ColumnAsync(Guid spaceId, Guid? listId, Guid? columnId, CancellationToken ct)
    {
        if (columnId is { } column)
        {
            var found = await session.QuerySingleOrDefaultAsync(
                "select c.list_id, c.id, c.done from spaces.task_column c join spaces.task_list l on l.id = c.list_id where c.id = @column and l.space_id = @spaceId",
                new { column, spaceId }, r => (r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2)), ct);
            return found == default || (listId is { } l && found.Item1 != l) ? null : found;
        }
        // No column: the first column of the list, or of the first list.
        var first = await session.QuerySingleOrDefaultAsync(
            """
            select c.list_id, c.id, c.done from spaces.task_column c join spaces.task_list l on l.id = c.list_id
            where l.space_id = @spaceId and (@listId::uuid is null or l.id = @listId) order by l.position, l.id, c.position, c.id limit 1
            """, new { spaceId, listId }, r => (r.GetGuid(0), r.GetGuid(1), r.GetBoolean(2)), ct);
        return first == default ? null : first;
    }

    public async Task<TaskDetail> CreateAsync(Guid spaceId, TaskRequest request, CancellationToken ct)
    {
        var standing = await spaces.ContributableAsync(spaceId, ct);
        var errors = new ValidationErrors();
        var fields = await ValidateAsync(spaceId, request, errors, ct);
        var place = await ColumnAsync(spaceId, request.ListId, request.ColumnId, ct);
        if (place is null)
        {
            errors.Add("columnId", "validation.spaces.task_column");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<int>("select count(*)::int from spaces.task where space_id = @spaceId", new { spaceId }, ct) >= MaxTasksPerSpace)
        {
            throw AppException.Conflict("conflict.spaces.task_limit");
        }
        var (list, column, done) = place!.Value;
        var id = Ids.New();
        var user = context.RequireUser();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into spaces.task (tenant_id, id, space_id, list_id, column_id, title, description, priority, labels, due_on, milestone_id, position, status,
              completed_by, completed_at, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @spaceId, @list, @column, @title, @description, @priority, @labels, @due, @milestone,
              coalesce((select max(position) + 1 from spaces.task where column_id = @column), 0), @status, @by, @at, @user, @now, @now)
            """,
            new
            {
                id, spaceId, list, column, title = fields.Title, description = fields.Description, priority = fields.Priority, labels = fields.Labels, due = fields.DueOn,
                milestone = fields.MilestoneId, status = done ? "done" : "open", by = done ? user : (Guid?)null, at = done ? now : (DateTimeOffset?)null, user, now,
            }, ct);
        await AssignAsync(standing.Space, id, fields.Title, fields.DueOn, [], fields.Assignees, ct);
        await milestones.RecountAsync(spaceId, fields.MilestoneId, ct);
        await outbox.EnqueueAsync(new SearchItemChanged(SpaceTaskSearchSource.Name, id), ct);
        return await DetailAsync(standing, id, ct);
    }

    private async Task AssignAsync(SpaceRow space, Guid taskId, string title, DateOnly? due, IReadOnlyCollection<Guid> before, IReadOnlyCollection<Guid> after, CancellationToken ct)
    {
        var added = after.Except(before).ToList();
        var removed = before.Except(after).ToArray();
        if (removed.Length > 0)
        {
            await session.ExecAsync("delete from spaces.task_assignee where task_id = @taskId and user_id = any(@removed)", new { taskId, removed }, ct);
        }
        var user = context.RequireUser();
        var now = clock.UtcNow;
        foreach (var person in added)
        {
            await session.ExecAsync(
                "insert into spaces.task_assignee (tenant_id, task_id, user_id, assigned_by, assigned_at) values (platform.current_tenant_id(), @taskId, @person, @user, @now) on conflict do nothing",
                new { taskId, person, user, now }, ct);
            await outbox.EnqueueAsync(new TaskAssigned(space.Id, taskId, person, due), ct);
        }
        await TellAsync(space, taskId, added, AssignedTemplate, title, $"{taskId:N}:assigned:{now.ToUnixTimeMilliseconds()}", ct);
    }

    /// <summary>Tells people who can read the space, never the person who acted, grouped per space (FR-003, FR-011).</summary>
    private async Task TellAsync(SpaceRow space, Guid taskId, IReadOnlyCollection<Guid> people, string template, string title, string dedupe, CancellationToken ct)
    {
        var recipients = new List<Guid>();
        foreach (var person in people.Where(p => p != context.ActorId).Distinct())
        {
            if (await spaces.CanReadAsync(space, person, ct))
            {
                recipients.Add(person);
            }
        }
        if (recipients.Count == 0)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SpaceActivity, template, Link(space.Id, taskId),
            new Dictionary<string, string> { ["space"] = space.Name, ["task"] = title }, DedupeKey: dedupe, GroupKey: GroupKey(space.Id, template)), ct);
    }

    private async Task<(SpaceStanding Standing, CardRow Row)> ChangeableAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var standing = await spaces.ContributableAsync(spaceId, ct);
        var row = await CardRowAsync(spaceId, taskId, ct) ?? throw AppException.NotFound();
        return (standing, row);
    }

    public async Task<TaskDetail> UpdateAsync(Guid spaceId, Guid taskId, TaskRequest request, CancellationToken ct)
    {
        var (standing, row) = await ChangeableAsync(spaceId, taskId, ct);
        if (request.Version != row.Version)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.spaces.task_changed", null, new Dictionary<string, object?> { ["version"] = row.Version });
        }
        var errors = new ValidationErrors();
        var fields = await ValidateAsync(spaceId, request, errors, ct);
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update spaces.task set title = @title, description = @description, priority = @priority, labels = @labels, due_on = @due, milestone_id = @milestone,
              reminded_for = case when due_on is distinct from @due then null else reminded_for end, updated_at = @now, row_version = row_version + 1
            where id = @taskId
            """,
            new
            {
                taskId, title = fields.Title, description = fields.Description, priority = fields.Priority, labels = fields.Labels, due = fields.DueOn,
                milestone = fields.MilestoneId, now = clock.UtcNow,
            }, ct);
        await AssignAsync(standing.Space, taskId, fields.Title, fields.DueOn, row.Assignees, fields.Assignees, ct);
        await milestones.RecountAsync(spaceId, row.MilestoneId, ct);
        if (fields.MilestoneId != row.MilestoneId)
        {
            await milestones.RecountAsync(spaceId, fields.MilestoneId, ct);
        }
        await outbox.EnqueueAsync(new SearchItemChanged(SpaceTaskSearchSource.Name, taskId), ct);
        return await DetailAsync(standing, taskId, ct);
    }

    /// <summary>
    /// Moves a task to a column at a position (the board, with a keyboard "move to" menu as the alternative). A done
    /// column completes the task and tells its watchers; leaving one reopens it (US2 scenario 2).
    /// </summary>
    public async Task<TaskDetail> MoveAsync(Guid spaceId, Guid taskId, MoveRequest request, CancellationToken ct)
    {
        var (standing, row) = await ChangeableAsync(spaceId, taskId, ct);
        if (request.ColumnId is null || await ColumnAsync(spaceId, null, request.ColumnId, ct) is not { } place)
        {
            throw AppException.Validation("columnId", "validation.spaces.task_column");
        }
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.task where column_id = @column and id <> @taskId", new { column = place.Column, taskId }, ct);
        var position = Math.Clamp(request.Position ?? count, 0, count);
        await session.ExecAsync(
            """
            update spaces.task set position = position + 1 where column_id = @column and id <> @taskId and position >= @position;
            update spaces.task set list_id = @list, column_id = @column, position = @position, updated_at = @now, row_version = row_version + 1 where id = @taskId
            """, new { list = place.List, column = place.Column, taskId, position, now = clock.UtcNow }, ct);
        await SetDoneAsync(standing.Space, row, place.Done, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    /// <summary>Completes or reopens a task, from its page or My tasks: it moves to the list's first done, or first open, column.</summary>
    public async Task<TaskDetail> CompleteAsync(Guid spaceId, Guid taskId, bool done, CancellationToken ct)
    {
        var (standing, row) = await ChangeableAsync(spaceId, taskId, ct);
        var column = await session.QuerySingleOrDefaultAsync(
            "select id from spaces.task_column where list_id = @list and done = @done order by position, id limit 1", new { list = row.ListId, done }, r => r.GetGuid(0), ct);
        if (column != Guid.Empty && column != row.ColumnId)
        {
            await session.ExecAsync(
                """
                update spaces.task set column_id = @column, position = coalesce((select max(position) + 1 from spaces.task where column_id = @column), 0),
                  updated_at = @now, row_version = row_version + 1 where id = @taskId
                """, new { column, taskId, now = clock.UtcNow }, ct);
        }
        await SetDoneAsync(standing.Space, row, done, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    private async Task SetDoneAsync(SpaceRow space, CardRow row, bool done, CancellationToken ct)
    {
        if (done == (row.Status == "done"))
        {
            return;
        }
        var user = context.RequireUser();
        var now = clock.UtcNow;
        await session.ExecAsync(
            "update spaces.task set status = @status, completed_by = @by, completed_at = @at, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id = row.Id, status = done ? "done" : "open", by = done ? user : (Guid?)null, at = done ? now : (DateTimeOffset?)null, now }, ct);
        if (done)
        {
            await outbox.EnqueueAsync(new TaskCompleted(space.Id, row.Id, user), ct);
            // The task's watchers are its assignees and the person who made it.
            var creator = await session.ScalarAsync<Guid>("select created_by from spaces.task where id = @id", new { id = row.Id }, ct);
            await TellAsync(space, row.Id, row.Assignees.Append(creator).ToList(), CompletedTemplate, row.Title, $"{row.Id:N}:done:{now.ToUnixTimeMilliseconds()}", ct);
        }
        await milestones.RecountAsync(space.Id, row.MilestoneId, ct);
    }

    public async Task DeleteAsync(Guid spaceId, Guid taskId, CancellationToken ct)
    {
        var (_, row) = await ChangeableAsync(spaceId, taskId, ct);
        await session.ExecAsync("delete from spaces.task where id = @taskId", new { taskId }, ct);
        await milestones.RecountAsync(spaceId, row.MilestoneId, ct);
        await outbox.EnqueueAsync(new SearchItemChanged(SpaceTaskSearchSource.Name, taskId), ct);
    }

    // ---- Checklist and comments ----

    private async Task TouchAsync(Guid taskId, CancellationToken ct) =>
        await session.ExecAsync("update spaces.task set updated_at = @now, row_version = row_version + 1 where id = @taskId", new { taskId, now = clock.UtcNow }, ct);

    public async Task<TaskDetail> AddChecklistItemAsync(Guid spaceId, Guid taskId, ChecklistRequest request, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        var text = request.Text?.Trim() ?? "";
        if (text.Length is 0 or > MaxChecklistText)
        {
            throw AppException.Validation("text", "validation.spaces.checklist_text");
        }
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.checklist_item where task_id = @taskId", new { taskId }, ct);
        if (count >= MaxChecklistItems)
        {
            throw AppException.Validation("text", "validation.spaces.checklist_count");
        }
        await session.ExecAsync(
            "insert into spaces.checklist_item (tenant_id, id, task_id, text, done, position) values (platform.current_tenant_id(), @id, @taskId, @text, @done, @count)",
            new { id = Ids.New(), taskId, text, done = request.Done ?? false, count }, ct);
        await TouchAsync(taskId, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    public async Task<TaskDetail> SetChecklistItemAsync(Guid spaceId, Guid taskId, Guid itemId, ChecklistRequest request, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        var text = request.Text?.Trim();
        if (text is { Length: 0 or > MaxChecklistText })
        {
            throw AppException.Validation("text", "validation.spaces.checklist_text");
        }
        var changed = await session.ExecAsync(
            "update spaces.checklist_item set text = coalesce(@text, text), done = coalesce(@done, done) where id = @itemId and task_id = @taskId",
            new { itemId, taskId, text, done = request.Done }, ct);
        if (changed == 0)
        {
            throw AppException.NotFound();
        }
        await TouchAsync(taskId, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    public async Task<TaskDetail> RemoveChecklistItemAsync(Guid spaceId, Guid taskId, Guid itemId, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        if (await session.ExecAsync("delete from spaces.checklist_item where id = @itemId and task_id = @taskId", new { itemId, taskId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await TouchAsync(taskId, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    public async Task<TaskDetail> CommentAsync(Guid spaceId, Guid taskId, CommentRequest request, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        var body = request.Body?.Trim() ?? "";
        if (body.Length is 0 or > MaxCommentLength)
        {
            throw AppException.Validation("body", "validation.spaces.comment");
        }
        await session.ExecAsync(
            "insert into spaces.task_comment (tenant_id, id, task_id, author_id, body, created_at) values (platform.current_tenant_id(), @id, @taskId, @user, @body, @now)",
            new { id = Ids.New(), taskId, user = context.RequireUser(), body, now = clock.UtcNow }, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    /// <summary>
    /// Links a library file to the task (US5 scenario 3). The caller must be able to open the file now; readers of the
    /// task see it only if they can open it too, and always at the version in force.
    /// </summary>
    public async Task<TaskDetail> LinkFileAsync(Guid spaceId, Guid taskId, TaskFileRequest request, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        if (request.DocumentId is not { } document || await documents.ReadableAsync(document, ct) is null)
        {
            throw AppException.Validation("documentId", "validation.spaces.task_file");
        }
        var count = await session.ScalarAsync<int>("select count(*)::int from spaces.task_file where task_id = @taskId", new { taskId }, ct);
        if (count >= MaxFiles)
        {
            throw AppException.Validation("documentId", "validation.spaces.task_file_count");
        }
        await session.ExecAsync(
            """
            insert into spaces.task_file (tenant_id, task_id, document_id, added_by, added_at)
            values (platform.current_tenant_id(), @taskId, @document, @user, @now)
            on conflict do nothing
            """, new { taskId, document, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await TouchAsync(taskId, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    public async Task<TaskDetail> UnlinkFileAsync(Guid spaceId, Guid taskId, Guid documentId, CancellationToken ct)
    {
        var (standing, _) = await ChangeableAsync(spaceId, taskId, ct);
        if (await session.ExecAsync("delete from spaces.task_file where task_id = @taskId and document_id = @documentId", new { taskId, documentId }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await TouchAsync(taskId, ct);
        return await DetailAsync(standing, taskId, ct);
    }

    // ---- My tasks and export ----

    /// <summary>
    /// The viewer's open tasks across active spaces they are still in, by due date (US7, FR-010). A space they left
    /// keeps the task assigned to them until an owner reassigns it, but it leaves My tasks.
    /// </summary>
    public async Task<IReadOnlyList<MyTask>> MineAsync(bool includeDone, CancellationToken ct)
    {
        await spaces.RequireAsync(ct);
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            $"""
            select {CardColumns}, s.name from spaces.task t
            join spaces.task_assignee a on a.task_id = t.id and a.user_id = @user
            join spaces.space s on s.id = t.space_id and s.status = 'active'
            join spaces.space_member m on m.space_id = t.space_id and m.user_id = @user
            where (@includeDone or t.status = 'open')
            order by t.status desc, t.due_on nulls last, t.created_at, t.id
            limit @limit
            """, new { user, includeDone, limit = MyTasksMax }, r => (Card: ReadCard(r), Space: r.GetString(16)), ct);
        var cards = await CardsAsync(rows.Select(r => r.Card).ToList(), ct);
        return cards.Select((c, i) => new MyTask(c, rows[i].Card.SpaceId, rows[i].Space)).ToList();
    }

    /// <summary>The space's tasks as CSV for its readers (FR-015), protected against spreadsheet formulas.</summary>
    public async Task<(string FileName, byte[] Content)> ExportAsync(Guid spaceId, CancellationToken ct)
    {
        var board = await BoardAsync(spaceId, null, ct);
        var lists = board.Lists.ToDictionary(l => l.Id);
        var csv = new StringBuilder("list,column,title,status,priority,due_on,assignees,labels,checklist\n");
        foreach (var t in board.Tasks)
        {
            var list = lists[t.ListId];
            var column = list.Columns.FirstOrDefault(c => c.Id == t.ColumnId)?.Name;
            csv.Append(Csv(list.Name)).Append(',').Append(Csv(column)).Append(',').Append(Csv(t.Title)).Append(',').Append(t.Status).Append(',').Append(t.Priority).Append(',')
                .Append(t.DueOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(Csv(string.Join("; ", t.Assignees.Select(a => a.DisplayName)))).Append(',').Append(Csv(string.Join("; ", t.Labels))).Append(',')
                .Append(t.Checklist.Total == 0 ? "" : $"{t.Checklist.Done}/{t.Checklist.Total}").Append('\n');
        }
        return ($"tasks-{spaceId:N}.csv", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray());
    }

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

    // ---- Reminders ----

    /// <summary>
    /// For one tenant, as the system: the day before a task is due, at 09:00 in each assignee's time zone, the assignee
    /// is reminded once (US2 scenario 1). Returns how many people were reminded.
    /// </summary>
    public async Task<int> RemindDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var utcToday = DateOnly.FromDateTime(now.UtcDateTime);
        var due = await session.QueryAsync(
            """
            select t.id, t.space_id, t.title, t.due_on, coalesce((select array_agg(a.user_id) from spaces.task_assignee a where a.task_id = t.id), '{}')
            from spaces.task t join spaces.space s on s.id = t.space_id and s.status = 'active'
            where t.status = 'open' and t.due_on between @start and @end and t.reminded_for is distinct from t.due_on
            order by t.due_on, t.id limit @limit
            """, new { start = utcToday, end = utcToday.AddDays(2), limit },
            r => (Id: r.GetGuid(0), Space: r.GetGuid(1), Title: r.GetString(2), Due: r.GetFieldValue<DateOnly>(3), People: r.GetFieldValue<Guid[]>(4)), ct);
        var zones = new Dictionary<Guid, WorkingCalendarSnapshot>();
        var spaceRows = new Dictionary<Guid, SpaceRow?>();
        var reminded = 0;
        foreach (var task in due)
        {
            if (!spaceRows.TryGetValue(task.Space, out var space))
            {
                spaceRows[task.Space] = space = await spaces.RowAsync(task.Space, ct);
            }
            var waiting = 0;
            var ready = new List<Guid>();
            foreach (var person in task.People)
            {
                if (!zones.TryGetValue(person, out var calendar))
                {
                    zones[person] = calendar = await calendars.ForUserAsync(person, ct);
                }
                var local = TimeZoneInfo.ConvertTime(now, calendar.Zone);
                var today = DateOnly.FromDateTime(local.DateTime);
                if (today < task.Due.AddDays(-1) || today == task.Due.AddDays(-1) && TimeOnly.FromDateTime(local.DateTime) < ReminderTime)
                {
                    waiting++;
                }
                else if (today < task.Due)
                {
                    ready.Add(person);
                }
            }
            if (space is not null && ready.Count > 0)
            {
                foreach (var person in ready)
                {
                    if (await spaces.CanReadAsync(space, person, ct))
                    {
                        var sent = await notifications.SendAsync(new NotificationRequest([person], NotificationCategories.SpaceActivity, DueTemplate, Link(space.Id, task.Id),
                            new Dictionary<string, string> { ["space"] = space.Name, ["task"] = task.Title }, DedupeKey: $"{task.Id:N}:due:{task.Due:yyyyMMdd}:{person:N}"), ct);
                        reminded += sent.Created + sent.Grouped;
                    }
                }
            }
            if (waiting == 0)
            {
                await session.ExecAsync("update spaces.task set reminded_for = due_on where id = @id", new { id = task.Id }, ct);
            }
        }
        return reminded;
    }
}
