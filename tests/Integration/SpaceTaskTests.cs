using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Modules.Spaces;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 slice 2 (T004 to T007): tasks with assignees, checklists and comments, board moves that complete and
/// reopen, viewer refusal, grouped notifications, milestones and status history, My tasks, the day-before reminder,
/// CSV export, task search and the tenant boundary. Names are fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpaceTaskTests(TenantWorld world)
{
    private const string Api = "/api/v1/spaces";

    private sealed record Org(TestTenant T);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        var problem = JsonDocument.Parse(text).RootElement;
        if (problem.TryGetProperty("errors", out var errors))
        {
            return errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!;
        }
        return problem.GetProperty("detailKey").GetString()!;
    }

    /// <summary>Omar, Laila, Huda and Sara work in Gulf; Kofi in Africa.</summary>
    private async Task<Org> SetupAsync()
    {
        var t = await world.CreateTenantAsync("st-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus", "en", ["en", "ar"]);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "omar", "laila", "huda", "sara", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        foreach (var name in new[] { "omar", "laila", "huda", "sara" })
        {
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        await world.AddMembershipAsync(t, "kofi", "Africa");
        return new Org(t);
    }

    /// <summary>A project space Omar owns: Laila and Sara are members, Huda a viewer.</summary>
    private static async Task<Guid> SpaceAsync(Org o, string name, string visibility = "private")
    {
        var t = o.T;
        var body = new
        {
            name, description = $"Everything about {name}.", visibility, template = "project",
            members = new object[] { new { userId = t["laila"].Id, role = "member" }, new { userId = t["sara"].Id, role = "member" }, new { userId = t["huda"].Id, role = "viewer" } },
        };
        return (await OkAsync(t["omar"].Client.PostAsJsonAsync(Api, body), HttpStatusCode.Created)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> NewTaskAsync(HttpClient client, Guid space, string title, object? fields = null)
    {
        var body = JsonSerializer.SerializeToElement(fields ?? new { });
        var request = new Dictionary<string, object?> { ["title"] = title };
        foreach (var p in body.EnumerateObject())
        {
            request[p.Name] = p.Value;
        }
        return await OkAsync(client.PostAsJsonAsync($"{Api}/{space}/tasks", request), HttpStatusCode.Created);
    }

    private static Guid Id(JsonElement detail) => detail.GetProperty("task").GetProperty("id").GetGuid();

    private static int Version(JsonElement detail) => detail.GetProperty("task").GetProperty("version").GetInt32();

    private static Guid ColumnOf(JsonElement board, bool done) =>
        board.GetProperty("lists")[0].GetProperty("columns").EnumerateArray().First(c => c.GetProperty("done").GetBoolean() == done).GetProperty("id").GetGuid();

    private Task<long> NotificationsAsync(Org o, string user, string template) =>
        world.Database.CountInTenantAsync(o.T.Id, "select count(*) from notifications.notification where user_id = @u and template_key = @k", ("u", o.T[user].Id), ("k", template));

    private Task<long> GroupCountAsync(Org o, string user, string template) =>
        world.Database.TenantScalarAsync<long>(o.T.Id, "select coalesce(sum(group_count), 0)::bigint from notifications.notification where user_id = @u and template_key = @k",
            ("u", o.T[user].Id), ("k", template));

    private Task<long> OutboxAsync(Org o, string type) =>
        world.Database.ScalarInTenantAsync<long>(o.T.Id, "select count(*) from messaging.outbox where tenant_id = @t and message_type like @m", ("t", o.T.Id), ("m", $"%{type}%"));

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Members_assign_check_and_move_tasks_on_the_board_and_watchers_hear_in_groups()
    {
        var o = await SetupAsync();
        var space = await SpaceAsync(o, "ERP rollout");
        var omar = o.T["omar"].Client;
        var laila = o.T["laila"].Client;

        // The project template gave one list with To do, Doing and Done, and three starter tasks.
        var board = await OkAsync(omar.GetAsync($"{Api}/{space}/tasks"));
        Assert.Equal(3, board.GetProperty("lists")[0].GetProperty("columns").GetArrayLength());
        Assert.Equal(3, board.GetProperty("tasks").GetArrayLength());
        Assert.True(board.GetProperty("canEdit").GetBoolean());

        // Omar gives Laila two tasks with a due date: she hears once, in one group for the space.
        var due = Today.AddDays(10);
        var task = await NewTaskAsync(omar, space, "Map the purchase approvals", new
        {
            assignees = new[] { o.T["laila"].Id }, dueOn = due, priority = "high", labels = new[] { "finance", "Finance", "phase 1" }, description = "Every approval step in use today.",
        });
        await NewTaskAsync(omar, space, "List the vendors", new { assignees = new[] { o.T["laila"].Id } });
        Assert.Equal(new[] { "finance", "phase 1" }, task.GetProperty("task").GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).ToArray());
        Assert.Equal(1L, await NotificationsAsync(o, "laila", SpaceTasks.AssignedTemplate));
        Assert.Equal(2L, await GroupCountAsync(o, "laila", SpaceTasks.AssignedTemplate));
        Assert.Equal(0L, await NotificationsAsync(o, "omar", SpaceTasks.AssignedTemplate));
        Assert.Equal(2L, await OutboxAsync(o, "TaskAssigned"));

        // Only owners and members hold tasks: not the viewer, not someone outside the space.
        Assert.Equal("validation.spaces.task_assignee",
            await ProblemKeyAsync(await omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Viewer task", assignees = new[] { o.T["huda"].Id } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.task_assignee",
            await ProblemKeyAsync(await omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Outsider task", assignees = new[] { o.T["kofi"].Id } }), HttpStatusCode.BadRequest));
        Assert.Equal("validation.spaces.task_priority",
            await ProblemKeyAsync(await omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Odd", priority = "someday" }), HttpStatusCode.BadRequest));

        // A checklist shows the checked share (US2 scenario 4), and members comment.
        var id = Id(task);
        var withItems = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{id}/checklist", new { text = "Purchase requests" }));
        withItems = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{id}/checklist", new { text = "Purchase orders" }));
        var item = withItems.GetProperty("checklist")[0].GetProperty("id").GetGuid();
        var checkedOne = await OkAsync(laila.PutAsJsonAsync($"{Api}/{space}/tasks/{id}/checklist/{item}", new { done = true }));
        Assert.Equal((1, 2), (checkedOne.GetProperty("task").GetProperty("checklist").GetProperty("done").GetInt32(),
            checkedOne.GetProperty("task").GetProperty("checklist").GetProperty("total").GetInt32()));
        var commented = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{id}/comments", new { body = "Finance has four steps today." }));
        Assert.Equal("Finance has four steps today.", commented.GetProperty("comments")[0].GetProperty("body").GetString());

        // A milestone tracks its tasks.
        var milestones = await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/milestones", new { name = "Design signed off", dueOn = Today.AddDays(30) }));
        var milestone = milestones[0].GetProperty("id").GetGuid();
        var current = await OkAsync(omar.GetAsync($"{Api}/{space}/tasks/{id}"));
        await OkAsync(omar.PutAsJsonAsync($"{Api}/{space}/tasks/{id}", new
        {
            title = "Map the purchase approvals", description = "Every approval step in use today.", priority = "high", labels = new[] { "finance" }, dueOn = due,
            assignees = new[] { o.T["laila"].Id }, milestoneId = milestone, version = Version(current),
        }));
        // A stale version is refused.
        Assert.Equal("conflict.spaces.task_changed", await ProblemKeyAsync(await omar.PutAsJsonAsync($"{Api}/{space}/tasks/{id}", new
        {
            title = "Again", version = Version(current),
        }), HttpStatusCode.Conflict));

        // Laila drags it to Done: it is completed, the milestone is reached, and Omar (who made it) hears; Laila does not.
        var done = ColumnOf(board, true);
        var moved = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{id}:move", new { columnId = done, position = 0 }));
        Assert.Equal("done", moved.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal(1L, await NotificationsAsync(o, "omar", SpaceTasks.CompletedTemplate));
        Assert.Equal(0L, await NotificationsAsync(o, "laila", SpaceTasks.CompletedTemplate));
        var reached = (await OkAsync(omar.GetAsync($"{Api}/{space}/milestones")))[0];
        Assert.True(reached.GetProperty("reached").GetBoolean());
        Assert.Equal((1, 1), (reached.GetProperty("done").GetInt32(), reached.GetProperty("total").GetInt32()));
        Assert.Equal(1L, await OutboxAsync(o, "TaskCompleted"));
        Assert.Equal(1L, await OutboxAsync(o, "MilestoneReached"));

        // Moving it back reopens it and the milestone is open again.
        var reopened = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/tasks/{id}:move", new { columnId = ColumnOf(board, false) }));
        Assert.Equal("open", reopened.GetProperty("task").GetProperty("status").GetString());
        Assert.False((await OkAsync(omar.GetAsync($"{Api}/{space}/milestones")))[0].GetProperty("reached").GetBoolean());
        Assert.Equal("validation.spaces.task_column", await ProblemKeyAsync(
            await laila.PostAsJsonAsync($"{Api}/{space}/tasks/{id}:move", new { columnId = Guid.NewGuid() }), HttpStatusCode.BadRequest));

        // Huda is a viewer: she reads the board, and the server refuses every change whatever the page shows (US2 scenario 3).
        var huda = o.T["huda"].Client;
        Assert.False((await OkAsync(huda.GetAsync($"{Api}/{space}/tasks"))).GetProperty("canEdit").GetBoolean());
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.PostAsJsonAsync($"{Api}/{space}/tasks/{id}:move", new { columnId = done }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Mine" }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.PostAsJsonAsync($"{Api}/{space}/tasks/{id}/comments", new { body = "Hi" }), HttpStatusCode.Forbidden));
        Assert.Equal("authz.denied.spaces_contribute", await ProblemKeyAsync(await huda.DeleteAsync($"{Api}/{space}/tasks/{id}"), HttpStatusCode.Forbidden));
        // Kofi is not in the private space at all.
        Assert.Equal("authz.denied.spaces_private", await ProblemKeyAsync(await o.T["kofi"].Client.GetAsync($"{Api}/{space}/tasks"), HttpStatusCode.Forbidden));

        // A member adds a list; it comes with To do, Doing and Done.
        var lists = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/lists", new { name = "Data migration" }));
        Assert.Equal(new[] { "To do", "In progress", "Done" }, lists[1].GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray());
        await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/lists/{lists[1].GetProperty("id").GetGuid()}/columns", new { name = "Checked" }));

        // Archived, the space is read-only for tasks too.
        await OkAsync(omar.PostAsync($"{Api}/{space}:archive", null));
        Assert.Equal("conflict.spaces.archived", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Late" }), HttpStatusCode.Conflict));
    }

    [Fact]
    public async Task My_tasks_lists_open_tasks_across_spaces_by_due_date_and_lets_go_of_spaces_she_left()
    {
        var o = await SetupAsync();
        var erp = await SpaceAsync(o, "ERP rollout");
        var office = await SpaceAsync(o, "Office move");
        var omar = o.T["omar"].Client;
        var laila = o.T["laila"].Client;
        var lailaId = o.T["laila"].Id;
        var late = await NewTaskAsync(omar, erp, "Train the buyers", new { assignees = new[] { lailaId }, dueOn = Today.AddDays(20) });
        var soon = await NewTaskAsync(omar, office, "Book the movers", new { assignees = new[] { lailaId }, dueOn = Today.AddDays(2) });
        var undated = await NewTaskAsync(omar, office, "Label the boxes", new { assignees = new[] { lailaId } });
        await NewTaskAsync(omar, office, "Not hers", new { assignees = new[] { o.T["sara"].Id } });

        var mine = await OkAsync(laila.GetAsync($"{Api}/my-tasks"));
        Assert.Equal(new[] { Id(soon), Id(late), Id(undated) }, mine.EnumerateArray().Select(m => m.GetProperty("task").GetProperty("id").GetGuid()).ToArray());
        Assert.Equal("Office move", mine[0].GetProperty("spaceName").GetString());

        // She completes one there; it leaves the open list.
        await OkAsync(laila.PostAsync($"{Api}/{office}/tasks/{Id(soon)}:complete", null));
        mine = await OkAsync(laila.GetAsync($"{Api}/my-tasks"));
        Assert.DoesNotContain(Id(soon), mine.EnumerateArray().Select(m => m.GetProperty("task").GetProperty("id").GetGuid()));
        Assert.Contains(Id(soon), (await OkAsync(laila.GetAsync($"{Api}/my-tasks?done=true"))).EnumerateArray().Select(m => m.GetProperty("task").GetProperty("id").GetGuid()));

        // She leaves the ERP space: its task leaves My tasks but stays assigned until an owner reassigns it (US7 scenario 2).
        Assert.Equal(HttpStatusCode.NoContent, (await laila.PostAsync($"{Api}/{erp}:leave", null)).StatusCode);
        mine = await OkAsync(laila.GetAsync($"{Api}/my-tasks"));
        Assert.Equal(new[] { Id(undated) }, mine.EnumerateArray().Select(m => m.GetProperty("task").GetProperty("id").GetGuid()).ToArray());
        var kept = await OkAsync(omar.GetAsync($"{Api}/{erp}/tasks/{Id(late)}"));
        Assert.Equal(lailaId, kept.GetProperty("task").GetProperty("assignees")[0].GetProperty("userId").GetGuid());

        // Archived spaces are hidden from My tasks.
        await OkAsync(omar.PostAsync($"{Api}/{office}:archive", null));
        Assert.Equal(0, (await OkAsync(laila.GetAsync($"{Api}/my-tasks"))).GetArrayLength());
    }

    [Fact]
    public async Task Owners_keep_a_status_history_and_the_overview_shows_milestones_and_overdue_tasks()
    {
        var o = await SetupAsync();
        var space = await SpaceAsync(o, "Brightwater bid");
        var omar = o.T["omar"].Client;
        var laila = o.T["laila"].Client;
        await NewTaskAsync(omar, space, "Price the options", new { dueOn = Today.AddDays(-3) });
        await NewTaskAsync(omar, space, "Write the summary", new { dueOn = Today.AddDays(5) });

        var overview = await OkAsync(laila.GetAsync($"{Api}/{space}/overview"));
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("status").ValueKind);
        Assert.Equal(1, overview.GetProperty("overdueTasks").GetInt32());
        Assert.Equal("Price the options", overview.GetProperty("overdue")[0].GetProperty("title").GetString());
        Assert.False(overview.GetProperty("canSetStatus").GetBoolean());

        // Only owners set the status; members are told, the owner is not.
        Assert.Equal("authz.denied.spaces_owner", await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/status", new { status = "at_risk" }), HttpStatusCode.Forbidden));
        Assert.Equal("validation.spaces.status", await ProblemKeyAsync(await omar.PostAsJsonAsync($"{Api}/{space}/status", new { status = "fine" }), HttpStatusCode.BadRequest));
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/status", new { status = "on_track", note = "Kick-off done." }));
        var atRisk = await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/status", new { status = "at_risk", note = "Pricing is late." }));
        Assert.Equal("at_risk", atRisk.GetProperty("status").GetProperty("status").GetString());
        Assert.Equal("Pricing is late.", atRisk.GetProperty("status").GetProperty("note").GetString());
        Assert.Equal(new[] { "at_risk", "on_track" }, atRisk.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("status").GetString()!).ToArray());
        Assert.Equal(2L, await NotificationsAsync(o, "laila", SpaceMilestones.StatusTemplate));
        Assert.Equal(2L, await NotificationsAsync(o, "huda", SpaceMilestones.StatusTemplate));
        Assert.Equal(0L, await NotificationsAsync(o, "omar", SpaceMilestones.StatusTemplate));
        Assert.Equal(0L, await NotificationsAsync(o, "kofi", SpaceMilestones.StatusTemplate));
        Assert.Equal(2L, await OutboxAsync(o, "SpaceStatusChanged"));

        // Milestones show progress; a removed milestone leaves its tasks in place.
        var milestones = await OkAsync(laila.PostAsJsonAsync($"{Api}/{space}/milestones", new { name = "Bid submitted", dueOn = Today.AddDays(-1) }));
        var milestone = milestones[0].GetProperty("id").GetGuid();
        Assert.True(milestones[0].GetProperty("overdue").GetBoolean());
        var linked = await NewTaskAsync(laila, space, "Send the bid", new { milestoneId = milestone });
        Assert.Equal(1, (await OkAsync(laila.GetAsync($"{Api}/{space}/overview"))).GetProperty("milestones")[0].GetProperty("total").GetInt32());
        Assert.Equal(0, (await OkAsync(laila.DeleteAsync($"{Api}/{space}/milestones/{milestone}"))).GetArrayLength());
        Assert.Equal(JsonValueKind.Null, (await OkAsync(laila.GetAsync($"{Api}/{space}/tasks/{Id(linked)}"))).GetProperty("task").GetProperty("milestoneId").ValueKind);
        Assert.Equal("validation.spaces.milestone",
            await ProblemKeyAsync(await laila.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Lost", milestoneId = milestone }), HttpStatusCode.BadRequest));
    }

    private Task<int> RemindAsync(Org o, DateTimeOffset now) =>
        world.Factory.InScopeAsync(o.T.Id, async sp =>
        {
            sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            var sent = await sp.GetRequiredService<SpaceTasks>().RemindDueAsync(now, 1000, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return sent;
        });

    [Fact]
    public async Task Assignees_are_reminded_once_the_day_before_at_nine_in_their_time_zone()
    {
        var o = await SetupAsync();
        var space = await SpaceAsync(o, "Office move");
        var omar = o.T["omar"].Client;
        var due = Today.AddDays(6);
        await NewTaskAsync(omar, space, "Book the movers", new { assignees = new[] { o.T["laila"].Id, o.T["sara"].Id }, dueOn = due });
        await NewTaskAsync(omar, space, "Nobody's", new { dueOn = due });
        // The tenant runs on Gulf time (UTC+4): 09:00 there is 05:00 UTC.
        var admin = o.T["admin"].Client;
        var settings = await OkAsync(admin.GetAsync("/api/v1/tenant/settings"));
        await OkAsync(admin.PutAsJsonAsync("/api/v1/tenant/settings", new
        {
            defaultLocale = "en", enabledLocales = new[] { "en", "ar" }, timeZone = "Asia/Dubai", rowVersion = settings.GetProperty("rowVersion").GetInt32(),
        }));

        var dayBefore = due.AddDays(-1);
        Assert.Equal(0, await RemindAsync(o, new DateTimeOffset(dayBefore.AddDays(-1).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)));
        Assert.Equal(0, await RemindAsync(o, new DateTimeOffset(dayBefore.ToDateTime(new TimeOnly(4, 30)), TimeSpan.Zero)));
        Assert.Equal(2, await RemindAsync(o, new DateTimeOffset(dayBefore.ToDateTime(new TimeOnly(5, 30)), TimeSpan.Zero)));
        Assert.Equal(0, await RemindAsync(o, new DateTimeOffset(dayBefore.ToDateTime(new TimeOnly(7, 0)), TimeSpan.Zero)));
        Assert.Equal(1L, await NotificationsAsync(o, "laila", SpaceTasks.DueTemplate));
        Assert.Equal(1L, await NotificationsAsync(o, "sara", SpaceTasks.DueTemplate));
    }

    [Fact]
    public async Task Tasks_of_a_private_space_stay_with_its_members_in_search_export_and_other_tenants()
    {
        var o = await SetupAsync();
        var closed = await SpaceAsync(o, "Brightwater pricing");
        var omar = o.T["omar"].Client;
        var task = await NewTaskAsync(omar, closed, "Quasar discount table", new { description = "=HYPERLINK(\"x\")", labels = new[] { "+margin" } });
        await NewTaskAsync(omar, closed, "=SUM(A1:A2)");
        await world.DispatchTenantAsync(o.T.Id);

        async Task<Guid[]> FindAsync(string user) =>
            (await OkAsync(o.T[user].Client.GetAsync("/api/v1/search?q=Quasar&kind=task"))).GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("itemId").GetGuid()).ToArray();
        Assert.Equal(new[] { Id(task) }, await FindAsync("laila"));
        Assert.Empty(await FindAsync("kofi"));
        // The tenant administrator manages spaces but reads no content of a space they are not in.
        Assert.Empty(await FindAsync("admin"));

        // Members export the tasks as CSV, with spreadsheet formulas made harmless (FR-015).
        var csv = await omar.GetAsync($"{Api}/{closed}/tasks.csv");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("'=SUM(A1:A2)", text, StringComparison.Ordinal);
        Assert.Contains("'+margin", text, StringComparison.Ordinal);
        Assert.Equal("authz.denied.spaces_private", await ProblemKeyAsync(await o.T["kofi"].Client.GetAsync($"{Api}/{closed}/tasks.csv"), HttpStatusCode.Forbidden));

        // Another tenant never reaches the space or its tasks.
        var other = await world.CreateTenantAsync("sto-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        var stranger = other["admin"].Client;
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Api}/{closed}/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"{Api}/{closed}/tasks/{Id(task)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PostAsJsonAsync($"{Api}/{closed}/tasks/{Id(task)}:move", new { columnId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(0, (await OkAsync(stranger.GetAsync($"{Api}/my-tasks"))).GetArrayLength());

        // Without the package there are no tasks.
        var basic = await world.CreateTenantAsync("stb-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        Assert.Equal(HttpStatusCode.Forbidden, (await basic["admin"].Client.GetAsync($"{Api}/my-tasks")).StatusCode);
    }
}
