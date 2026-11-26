using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Search.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public sealed record SyncLinkRequest(Guid? ListId, Guid? ConnectionId, string? ContainerId);

public sealed record SyncLinkView(Guid Id, Guid ListId, string ListName, Guid ConnectionId, string Kind, string ContainerId, string ContainerName, string Status,
    DateTimeOffset? LastRunAt, DateTimeOffset CreatedAt, int Tasks);

/// <summary>One entry of the sync history: a run with its counts and outcome, or a conflict, failure or skipped assignee on one task.</summary>
public sealed record SyncEventView(Guid Id, Guid RunId, string Kind, string? Outcome, string? TaskTitle, string? Detail, int Pulled, int Pushed, DateTimeOffset At);

/// <summary>
/// The sync page of a space for its owners: whether sync is in the package, the active links, the tracker connections a
/// list can be linked to, and the latest history.
/// </summary>
public sealed record SpaceSyncView(bool Available, IReadOnlyList<SyncLinkView> Links, IReadOnlyList<TaskTrackerConnection> Connections, IReadOnlyList<SyncEventView> History);

public static class SyncEventKinds
{
    public const string Run = "run";
    public const string Conflict = "conflict";
    public const string Failed = "failed";
    public const string SkippedAssignee = "skipped_assignee";
    public const string UnlinkedTask = "unlinked_task";
}

/// <summary>
/// Planner and Jira sync of a task list (spec 029 R3 US10, FR-023 to FR-025). An owner links one list to one plan or
/// project through a tracker connection an administrator made in spec 017. Each run reads the tracker's tasks and the
/// list's tasks and, for each pair, carries the side that changed since the last run to the other. When both changed,
/// the later change wins and the history keeps a conflict. Tracker writes happen only inside a run, never in a person's
/// request, so a slow or refusing tracker never stops work in the space; a change that could not be sent stays changed
/// and goes with the next run. Synced changes notify nobody. Deleting a task on either side ends that task's link and
/// deletes nothing. Runs every 10 minutes from the maintenance worker, or when an owner asks.
/// </summary>
public sealed class SpaceSync(
    IDbSession session,
    ITenantContext context,
    SpacesService spaces,
    SpaceMilestones milestones,
    ITaskTrackers trackers,
    IUserDirectory users,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan HistoryKept = TimeSpan.FromDays(90);
    public const int HistoryShown = 50;
    public const int LinksPerTick = 20;

    private sealed record LinkRow(Guid Id, Guid SpaceId, Guid ListId, Guid ConnectionId, string Kind, string ContainerId, string ContainerName, string Status,
        DateTimeOffset? LastRunAt, DateTimeOffset CreatedAt, Guid CreatedBy);

    private const string LinkColumns = "id, space_id, list_id, connection_id, kind, container_id, container_name, status, last_run_at, created_at, created_by";

    private static LinkRow ReadLink(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6),
        r.GetString(7), r.Get<DateTimeOffset?>("last_run_at"), r.GetFieldValue<DateTimeOffset>(9), r.GetGuid(10));

    private async Task<SpaceStanding> OwnedAsync(Guid spaceId, CancellationToken ct)
    {
        var standing = await spaces.OwnedAsync(spaceId, false, ct);
        await entitlements.DemandAsync(Capabilities.SpacesTaskSync, ct);
        return standing;
    }

    // ---- The owners' page ----

    public async Task<SpaceSyncView> GetAsync(Guid spaceId, CancellationToken ct)
    {
        await spaces.OwnedAsync(spaceId, false, ct);
        if (!await entitlements.IsEnabledAsync(Capabilities.SpacesTaskSync, ct))
        {
            return new SpaceSyncView(false, [], [], []);
        }
        var links = await session.QueryAsync(
            $"""
            select k.id, k.space_id, k.list_id, k.connection_id, k.kind, k.container_id, k.container_name, k.status, k.last_run_at, k.created_at, k.created_by, l.name,
              (select count(*)::int from spaces.sync_item i where i.link_id = k.id)
            from spaces.sync_link k join spaces.task_list l on l.id = k.list_id
            where k.space_id = @spaceId and k.status = 'active' order by l.position, k.created_at
            """, new { spaceId }, r => (Link: ReadLink(r), List: r.GetString(11), Tasks: r.GetInt32(12)), ct);
        var history = await session.QueryAsync(
            """
            select e.id, e.run_id, e.kind, e.outcome, e.task_title, e.detail, e.pulled, e.pushed, e.at from spaces.sync_event e
            join spaces.sync_link k on k.id = e.link_id where k.space_id = @spaceId order by e.at desc, e.id limit @limit
            """, new { spaceId, limit = HistoryShown },
            r => new SyncEventView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<string?>("outcome"), r.Get<string?>("task_title"), r.Get<string?>("detail"), r.GetInt32(6),
                r.GetInt32(7), r.GetFieldValue<DateTimeOffset>(8)), ct);
        return new SpaceSyncView(true,
            links.Select(l => new SyncLinkView(l.Link.Id, l.Link.ListId, l.List, l.Link.ConnectionId, l.Link.Kind, l.Link.ContainerId, l.Link.ContainerName, l.Link.Status,
                l.Link.LastRunAt, l.Link.CreatedAt, l.Tasks)).ToList(),
            await trackers.ConnectionsAsync(ct), history);
    }

    /// <summary>The plans or projects of a tracker connection, for the link form.</summary>
    public async Task<TrackerRead<TrackerContainer>> ContainersAsync(Guid spaceId, Guid connectionId, CancellationToken ct)
    {
        await OwnedAsync(spaceId, ct);
        return await trackers.ContainersAsync(connectionId, ct);
    }

    /// <summary>Links a task list to a plan or project (US10 scenario 1) and runs the first sync, which matches or creates tasks on both sides.</summary>
    public async Task<SpaceSyncView> LinkAsync(Guid spaceId, SyncLinkRequest request, CancellationToken ct)
    {
        var standing = await OwnedAsync(spaceId, ct);
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        var errors = new ValidationErrors();
        if (request.ListId is not { } listId
            || !await session.ScalarAsync<bool>("select exists(select 1 from spaces.task_list where id = @listId and space_id = @spaceId)", new { listId, spaceId }, ct))
        {
            errors.Add("listId", "validation.spaces.sync_list");
            listId = Guid.Empty;
        }
        var connection = request.ConnectionId is { } id ? (await trackers.ConnectionsAsync(ct)).FirstOrDefault(c => c.Id == id) : null;
        if (connection is null)
        {
            errors.Add("connectionId", "validation.spaces.sync_connection");
        }
        errors.ThrowIfAny();
        var containers = await trackers.ContainersAsync(connection!.Id, ct);
        if (containers.Result != TrackerResults.Ok)
        {
            throw AppException.Conflict("conflict.spaces.sync_unavailable");
        }
        if (containers.Items.FirstOrDefault(c => c.Id == request.ContainerId) is not { } container)
        {
            throw AppException.Validation("containerId", "validation.spaces.sync_container");
        }
        if (await session.ScalarAsync<bool>(
                "select exists(select 1 from spaces.sync_link where status = 'active' and (list_id = @listId or (connection_id = @connection and container_id = @container)))",
                new { listId, connection = connection.Id, container = container.Id }, ct))
        {
            throw AppException.Conflict("conflict.spaces.sync_linked");
        }
        var linkId = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            $"""
            insert into spaces.sync_link (tenant_id, id, space_id, list_id, connection_id, kind, container_id, container_name, status, next_run_at, created_by, created_at)
            values (platform.current_tenant_id(), @linkId, @spaceId, @listId, @connection, @kind, @container, @name, 'active', @now, @user, @now)
            """, new { linkId, spaceId, listId, connection = connection.Id, kind = connection.Kind, container = container.Id, name = container.Name, now, user = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesSyncLinked, "space", spaceId.ToString(),
            Changes: new { list = listId, system = connection.Kind, container = container.Id }), ct);
        await RunAsync(linkId, ct);
        return await GetAsync(spaceId, ct);
    }

    /// <summary>Stops the sync (US10 scenario 6). Tasks on both sides stay as they are; the history stays for its 90 days.</summary>
    public async Task<SpaceSyncView> UnlinkAsync(Guid spaceId, Guid linkId, CancellationToken ct)
    {
        await OwnedAsync(spaceId, ct);
        var link = await FindLinkAsync(linkId, ct);
        if (link is null || link.SpaceId != spaceId || link.Status != "active")
        {
            throw AppException.NotFound();
        }
        await session.ExecAsync("update spaces.sync_link set status = 'ended', ended_at = @now where id = @linkId", new { linkId, now = clock.UtcNow }, ct);
        await session.ExecAsync("delete from spaces.sync_item where link_id = @linkId", new { linkId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesSyncUnlinked, "space", spaceId.ToString(), Changes: new { list = link.ListId, system = link.Kind }), ct);
        return await GetAsync(spaceId, ct);
    }

    /// <summary>A run on an owner's request.</summary>
    public async Task<SpaceSyncView> RunNowAsync(Guid spaceId, Guid linkId, CancellationToken ct)
    {
        await OwnedAsync(spaceId, ct);
        var link = await FindLinkAsync(linkId, ct);
        if (link is null || link.SpaceId != spaceId || link.Status != "active")
        {
            throw AppException.NotFound();
        }
        await RunAsync(linkId, ct);
        return await GetAsync(spaceId, ct);
    }

    private Task<LinkRow?> FindLinkAsync(Guid linkId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {LinkColumns} from spaces.sync_link where id = @linkId", new { linkId }, ReadLink, ct);

    /// <summary>For one tenant, as the system: runs the links that are due and drops history older than 90 days.</summary>
    public async Task<int> SyncDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync("delete from spaces.sync_event where at < @cutoff", new { cutoff = now - HistoryKept }, ct);
        if (!await entitlements.IsEnabledAsync(Capabilities.SpacesTaskSync, ct))
        {
            return 0;
        }
        var due = await session.QueryAsync(
            """
            select k.id from spaces.sync_link k join spaces.space s on s.id = k.space_id and s.status = 'active'
            where k.status = 'active' and k.next_run_at <= @now order by k.next_run_at, k.id limit @limit
            """, new { now, limit = LinksPerTick }, r => r.GetGuid(0), ct);
        foreach (var id in due)
        {
            await RunAsync(id, ct);
        }
        return due.Count;
    }

    // ---- One run ----

    private sealed record LocalTask(Guid Id, string Title, string Description, DateOnly? DueOn, bool Done, int Version, DateTimeOffset UpdatedAt, Guid ColumnId, Guid? MilestoneId,
        Guid[] Assignees);

    private sealed record Item(Guid TaskId, string ExternalId, int LocalVersion, string ExternalVersion);

    private sealed class Run(Guid id)
    {
        public Guid Id { get; } = id;
        public int Pulled;
        public int Pushed;
        public int Failures;
    }

    private async Task RunAsync(Guid linkId, CancellationToken ct)
    {
        var link = await FindLinkAsync(linkId, ct);
        if (link is null || link.Status != "active")
        {
            return;
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update spaces.sync_link set last_run_at = @now, next_run_at = @next where id = @linkId", new { linkId, now, next = now + Interval }, ct);
        var run = new Run(Ids.New());
        var remote = await trackers.TasksAsync(link.ConnectionId, link.ContainerId, ct);
        if (remote.Result != TrackerResults.Ok)
        {
            await EventAsync(link, run, SyncEventKinds.Run, remote.Result, null, null, ct);
            return;
        }
        var workers = await WorkersAsync(link.SpaceId, ct);
        var byEmail = workers.ToDictionary(w => w.Email, w => w.Id, StringComparer.OrdinalIgnoreCase);
        var emailOf = workers.ToDictionary(w => w.Id, w => w.Email);
        var locals = (await LocalsAsync(link.ListId, ct)).ToDictionary(t => t.Id);
        var remotes = remote.Items.GroupBy(t => t.ExternalId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var items = await session.QueryAsync("select task_id, external_id, local_version, external_version from spaces.sync_item where link_id = @linkId", new { linkId },
            r => new Item(r.GetGuid(0), r.GetString(1), r.GetInt32(2), r.GetString(3)), ct);

        foreach (var item in items)
        {
            locals.TryGetValue(item.TaskId, out var local);
            remotes.TryGetValue(item.ExternalId, out var twin);
            if (local is null || twin is null)
            {
                // Deleted on one side: the pair's link ends and nothing is deleted on the other side.
                await session.ExecAsync("delete from spaces.sync_item where link_id = @linkId and task_id = @task", new { linkId, task = item.TaskId }, ct);
                await EventAsync(link, run, SyncEventKinds.UnlinkedTask, null, local?.Title ?? twin?.Title, null, ct);
                locals.Remove(item.TaskId);
                remotes.Remove(item.ExternalId);
                continue;
            }
            locals.Remove(item.TaskId);
            remotes.Remove(item.ExternalId);
            var localChanged = local.Version != item.LocalVersion;
            var remoteChanged = twin.Version != item.ExternalVersion;
            if (!localChanged && !remoteChanged)
            {
                continue;
            }
            var pull = remoteChanged && (!localChanged || twin.ModifiedAt >= local.UpdatedAt);
            if (localChanged && remoteChanged)
            {
                await EventAsync(link, run, SyncEventKinds.Conflict, pull ? "tracker_won" : "space_won", local.Title, null, ct);
            }
            if (pull)
            {
                var version = await ApplyAsync(link, run, local, twin, byEmail, ct);
                await SaveItemAsync(linkId, local.Id, twin.ExternalId, version, twin.Version, ct);
                run.Pulled++;
            }
            else
            {
                var written = await trackers.UpdateAsync(link.ConnectionId, link.ContainerId, twin.ExternalId, Input(local, emailOf), $"space-sync:{linkId:N}:{local.Id:N}:{local.Version}:{run.Id:N}", ct);
                if (written is { Result: TrackerResults.Ok, Task: { } task })
                {
                    await SaveItemAsync(linkId, local.Id, task.ExternalId, local.Version, task.Version, ct);
                    run.Pushed++;
                }
                else
                {
                    run.Failures++;
                    await EventAsync(link, run, SyncEventKinds.Failed, written.Result, local.Title, null, ct);
                }
            }
        }

        // Unpaired tasks with the same title on both sides are the same task (US10 scenario 1): they pair, and the later one's fields win.
        foreach (var twin in remotes.Values.ToList())
        {
            var same = locals.Values.Where(l => string.Equals(l.Title.Trim(), twin.Title.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(l => l.Id).FirstOrDefault();
            if (same is null)
            {
                continue;
            }
            locals.Remove(same.Id);
            remotes.Remove(twin.ExternalId);
            if (twin.ModifiedAt >= same.UpdatedAt)
            {
                var version = await ApplyAsync(link, run, same, twin, byEmail, ct);
                await SaveItemAsync(linkId, same.Id, twin.ExternalId, version, twin.Version, ct);
                run.Pulled++;
                continue;
            }
            var written = await trackers.UpdateAsync(link.ConnectionId, link.ContainerId, twin.ExternalId, Input(same, emailOf), $"space-sync:{linkId:N}:{same.Id:N}:{same.Version}:{run.Id:N}", ct);
            if (written is { Result: TrackerResults.Ok, Task: { } task })
            {
                await SaveItemAsync(linkId, same.Id, task.ExternalId, same.Version, task.Version, ct);
                run.Pushed++;
            }
            else
            {
                // Paired anyway with the tracker's old version, so the next run sends the list's fields again.
                await SaveItemAsync(linkId, same.Id, twin.ExternalId, same.Version - 1, twin.Version, ct);
                run.Failures++;
                await EventAsync(link, run, SyncEventKinds.Failed, written.Result, same.Title, null, ct);
            }
        }

        // Tracker tasks not in the list yet come in; list tasks not in the tracker yet go out.
        foreach (var twin in remotes.Values)
        {
            var created = await InsertAsync(link, run, twin, byEmail, ct);
            await SaveItemAsync(linkId, created, twin.ExternalId, await VersionAsync(created, ct), twin.Version, ct);
            run.Pulled++;
        }
        foreach (var local in locals.Values)
        {
            var written = await trackers.CreateAsync(link.ConnectionId, link.ContainerId, Input(local, emailOf), $"space-sync:{linkId:N}:{local.Id:N}:create:{local.Version}", ct);
            if (written is { Result: TrackerResults.Ok, Task: { } task })
            {
                await SaveItemAsync(linkId, local.Id, task.ExternalId, local.Version, task.Version, ct);
                run.Pushed++;
            }
            else
            {
                run.Failures++;
                await EventAsync(link, run, SyncEventKinds.Failed, written.Result, local.Title, null, ct);
            }
        }
        await EventAsync(link, run, SyncEventKinds.Run, run.Failures == 0 ? TrackerResults.Ok : "partial", null, null, ct);
    }

    private async Task<IReadOnlyList<(Guid Id, string Email)>> WorkersAsync(Guid spaceId, CancellationToken ct)
    {
        var ids = await session.QueryAsync("select user_id from spaces.space_member where space_id = @spaceId and role in ('owner', 'member')", new { spaceId }, r => r.GetGuid(0), ct);
        var found = await users.GetManyAsync(ids, ct);
        return found.Values.Where(u => !string.IsNullOrWhiteSpace(u.Email)).Select(u => (u.Id, u.Email)).ToList();
    }

    private Task<List<LocalTask>> LocalsAsync(Guid listId, CancellationToken ct) =>
        session.QueryAsync(
            """
            select t.id, t.title, t.description, t.due_on, t.status = 'done', t.row_version, t.updated_at, t.column_id, t.milestone_id,
              coalesce((select array_agg(a.user_id order by a.assigned_at, a.user_id) from spaces.task_assignee a where a.task_id = t.id), '{}')
            from spaces.task t where t.list_id = @listId
            """, new { listId },
            r => new LocalTask(r.GetGuid(0), r.GetString(1), r.GetString(2), r.Get<DateOnly?>("due_on"), r.GetBoolean(4), r.GetInt32(5), r.GetFieldValue<DateTimeOffset>(6),
                r.GetGuid(7), r.Get<Guid?>("milestone_id"), r.GetFieldValue<Guid[]>(9)), ct);

    private static TrackerTaskInput Input(LocalTask t, IReadOnlyDictionary<Guid, string> emailOf) =>
        new(t.Title, t.Description.Length == 0 ? null : t.Description, t.DueOn, t.Done, t.Assignees.Where(emailOf.ContainsKey).Select(a => emailOf[a]).ToList());

    private Task<int> VersionAsync(Guid taskId, CancellationToken ct) => session.ScalarAsync<int>("select row_version from spaces.task where id = @taskId", new { taskId }, ct);

    private Task SaveItemAsync(Guid linkId, Guid taskId, string externalId, int localVersion, string externalVersion, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into spaces.sync_item (tenant_id, link_id, task_id, external_id, local_version, external_version, synced_at)
            values (platform.current_tenant_id(), @linkId, @taskId, @externalId, @localVersion, @externalVersion, @now)
            on conflict (tenant_id, link_id, task_id) do update set external_id = excluded.external_id, local_version = excluded.local_version,
              external_version = excluded.external_version, synced_at = excluded.synced_at
            """, new { linkId, taskId, externalId, localVersion, externalVersion, now = clock.UtcNow }, ct);

    /// <summary>The tracker's assignees who work in the space; the others are named in the history (US10 scenario 5).</summary>
    private async Task<Guid[]> AssigneesAsync(LinkRow link, Run run, TrackerTask twin, IReadOnlyDictionary<string, Guid> byEmail, CancellationToken ct)
    {
        var found = new List<Guid>();
        foreach (var email in twin.AssigneeEmails)
        {
            if (byEmail.TryGetValue(email, out var user))
            {
                found.Add(user);
            }
            else
            {
                await EventAsync(link, run, SyncEventKinds.SkippedAssignee, null, twin.Title, email, ct);
            }
        }
        return found.Distinct().ToArray();
    }

    private async Task<Guid> ColumnForAsync(Guid listId, bool done, CancellationToken ct) =>
        await session.ScalarAsync<Guid?>("select id from spaces.task_column where list_id = @listId and done = @done order by position, id limit 1", new { listId, done }, ct)
        ?? await session.ScalarAsync<Guid>("select id from spaces.task_column where list_id = @listId order by position, id limit 1", new { listId }, ct);

    /// <summary>Carries the tracker's fields onto the task, as the link's owner. Returns the task's new version.</summary>
    private async Task<int> ApplyAsync(LinkRow link, Run run, LocalTask local, TrackerTask twin, IReadOnlyDictionary<string, Guid> byEmail, CancellationToken ct)
    {
        var column = local.Done == twin.Done ? local.ColumnId : await ColumnForAsync(link.ListId, twin.Done, ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update spaces.task set title = @title, description = coalesce(@description, description), due_on = @due,
              start_on = case when start_on is not null and @due::date is not null and start_on > @due::date then null else start_on end,
              status = @status, completed_by = case when @done then coalesce(completed_by, @by) else null end,
              completed_at = case when @done then coalesce(completed_at, @now) else null end,
              column_id = @column, position = case when column_id = @column then position else coalesce((select max(position) + 1 from spaces.task where column_id = @column), 0) end,
              reminded_for = case when due_on is distinct from @due then null else reminded_for end, updated_at = @now, row_version = row_version + 1
            where id = @id
            """,
            new
            {
                id = local.Id, title = Title(twin.Title), description = twin.Description is null ? null : Description(twin.Description), due = twin.DueOn,
                status = twin.Done ? "done" : "open", done = twin.Done, by = link.CreatedBy, now, column,
            }, ct);
        var assignees = await AssigneesAsync(link, run, twin, byEmail, ct);
        await session.ExecAsync("delete from spaces.task_assignee where task_id = @id and not (user_id = any(@assignees))", new { id = local.Id, assignees }, ct);
        foreach (var person in assignees)
        {
            await session.ExecAsync(
                """
                insert into spaces.task_assignee (tenant_id, task_id, user_id, assigned_by, assigned_at)
                values (platform.current_tenant_id(), @id, @person, @by, @now) on conflict do nothing
                """, new { id = local.Id, person, by = link.CreatedBy, now }, ct);
        }
        await milestones.RecountAsync(link.SpaceId, local.MilestoneId, ct);
        await outbox.EnqueueAsync(new SearchItemChanged(SpaceTaskSearchSource.Name, local.Id), ct);
        return await VersionAsync(local.Id, ct);
    }

    /// <summary>A tracker task the list does not have yet comes in as a task made by the link's owner.</summary>
    private async Task<Guid> InsertAsync(LinkRow link, Run run, TrackerTask twin, IReadOnlyDictionary<string, Guid> byEmail, CancellationToken ct)
    {
        var id = Ids.New();
        var column = await ColumnForAsync(link.ListId, twin.Done, ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into spaces.task (tenant_id, id, space_id, list_id, column_id, title, description, due_on, position, status, completed_by, completed_at, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @space, @list, @column, @title, @description, @due,
              coalesce((select max(position) + 1 from spaces.task where column_id = @column), 0), @status, @by, @at, @owner, @now, @now)
            """,
            new
            {
                id, space = link.SpaceId, list = link.ListId, column, title = Title(twin.Title), description = Description(twin.Description ?? ""), due = twin.DueOn,
                status = twin.Done ? "done" : "open", by = twin.Done ? link.CreatedBy : (Guid?)null, at = twin.Done ? now : (DateTimeOffset?)null, owner = link.CreatedBy, now,
            }, ct);
        foreach (var person in await AssigneesAsync(link, run, twin, byEmail, ct))
        {
            await session.ExecAsync(
                "insert into spaces.task_assignee (tenant_id, task_id, user_id, assigned_by, assigned_at) values (platform.current_tenant_id(), @id, @person, @by, @now) on conflict do nothing",
                new { id, person, by = link.CreatedBy, now }, ct);
        }
        await outbox.EnqueueAsync(new SearchItemChanged(SpaceTaskSearchSource.Name, id), ct);
        return id;
    }

    private static string Title(string title)
    {
        var t = title.Trim();
        t = t.Length == 0 ? "-" : t;
        return t.Length > SpaceTemplates.MaxTitleLength ? t[..SpaceTemplates.MaxTitleLength] : t;
    }

    private static string Description(string text) => text.Length > SpaceTasks.MaxDescriptionLength ? text[..SpaceTasks.MaxDescriptionLength] : text;

    private Task EventAsync(LinkRow link, Run run, string kind, string? outcome, string? title, string? detail, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into spaces.sync_event (tenant_id, id, link_id, run_id, kind, outcome, task_title, detail, pulled, pushed, at)
            values (platform.current_tenant_id(), @id, @link, @run, @kind, @outcome, @title, @detail, @pulled, @pushed, @now)
            """, new { id = Ids.New(), link = link.Id, run = run.Id, kind, outcome, title, detail, pulled = run.Pulled, pushed = run.Pushed, now = clock.UtcNow }, ct);
}
