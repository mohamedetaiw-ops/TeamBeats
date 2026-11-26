using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Ticketing;
using TeamBeats.Modules.Spaces;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 029 R3 T016 (US10, FR-023 to FR-025): a task list kept in step with a Jira project or a Planner plan. The first
/// sync matches tasks by title and creates the rest on both sides; later changes flow both ways; when both sides changed
/// the later change wins and the history keeps the conflict; an unavailable or refusing tracker leaves the space working
/// and the change goes with a later run; assignees outside the space are left out and named; unlinking keeps every task.
/// Owners only, the package, and the tenant boundary hold. Jira and Graph are fake hosts; every name is fictional.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class SpaceSyncTests(TenantWorld world)
{
    private const string Api = "/api/v1/spaces";
    private const string Packs = "/api/v1/integrations/pack-connections";

    private FakeRestApi Rest => world.Factory.Rest;

    private static string Host(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20] + ".rest.test";

    private static string JiraTime(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'+0000'", CultureInfo.InvariantCulture);

    /// <summary>A small Jira project: issues with summary, description, due date, done, one assignee and an updated time.</summary>
    private sealed class FakeJira
    {
        public sealed record Issue(string Summary, string? Description, string? Due, bool Done, string? Assignee, DateTimeOffset Updated);

        public ConcurrentDictionary<string, Issue> Issues { get; } = new();
        public HashSet<string> People { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Down;
        public bool RefuseWrites;
        private int next = 100;

        public void Set(string key, Issue issue) => Issues[key] = issue;

        private static object Json(string key, Issue i) => new
        {
            key,
            fields = new
            {
                summary = i.Summary,
                description = i.Description is null ? null : new { type = "doc", version = 1, content = new[] { new { type = "paragraph", content = new[] { new { type = "text", text = i.Description } } } } },
                duedate = i.Due,
                status = new { statusCategory = new { key = i.Done ? "done" : "new" } },
                assignee = i.Assignee is null ? null : new { emailAddress = i.Assignee },
                updated = JiraTime(i.Updated),
            },
        };

        private static HttpResponseMessage Ok(object body) => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            var path = s.Uri.AbsolutePath;
            if (path == "/rest/api/3/myself")
            {
                return Ok(new { accountId = "integration" });
            }
            if (Down)
            {
                return FakeRestApi.Json(HttpStatusCode.ServiceUnavailable, "{}");
            }
            if (path == "/rest/api/3/project/search")
            {
                return Ok(new { values = new[] { new { key = "ERP", name = "ERP rollout" }, new { key = "OPS", name = "Operations" } } });
            }
            if (path == "/rest/api/3/search/jql")
            {
                return Ok(new { issues = Issues.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => Json(i.Key, i.Value)).ToArray(), isLast = true });
            }
            if (path == "/rest/api/3/user/search")
            {
                var email = Uri.UnescapeDataString(s.Uri.Query).Split("query=")[1].Split('&')[0];
                return Ok(People.Contains(email) ? new object[] { new { accountId = "acc-" + email.Split('@')[0], emailAddress = email } } : []);
            }
            if (s.Method != "GET" && RefuseWrites)
            {
                return FakeRestApi.Json(HttpStatusCode.BadRequest, """{"errorMessages":["Field 'summary' cannot be set."]}""");
            }
            if (path == "/rest/api/3/issue" && s.Method == "POST")
            {
                var key = $"ERP-{Interlocked.Increment(ref next)}";
                Issues[key] = Apply(new Issue("", null, null, false, null, DateTimeOffset.UtcNow), s.Body!);
                return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(new { id = "10" + key[4..], key }));
            }
            var parts = path.Split('/');
            var issueKey = parts.Length > 5 ? parts[5] : "";
            if (!Issues.TryGetValue(issueKey, out var issue))
            {
                return FakeRestApi.Json(HttpStatusCode.NotFound, """{"errorMessages":["Issue does not exist"]}""");
            }
            if (path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                if (s.Method == "GET")
                {
                    return Ok(new { transitions = new[] { new { id = "31", to = new { statusCategory = new { key = "done" } } }, new { id = "11", to = new { statusCategory = new { key = "new" } } } } });
                }
                var id = JsonDocument.Parse(s.Body!).RootElement.GetProperty("transition").GetProperty("id").GetString();
                Issues[issueKey] = issue with { Done = id == "31", Updated = DateTimeOffset.UtcNow };
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            if (s.Method == "PUT")
            {
                Issues[issueKey] = Apply(issue, s.Body!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Ok(Json(issueKey, issue));
        }

        private Issue Apply(Issue issue, string body)
        {
            var f = JsonDocument.Parse(body).RootElement.GetProperty("fields");
            var description = f.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object
                ? string.Join("\n\n", d.GetProperty("content").EnumerateArray().Select(p => p.GetProperty("content")[0].GetProperty("text").GetString()))
                : null;
            string? assignee = null;
            if (f.TryGetProperty("assignee", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                var account = a.GetProperty("accountId").GetString()!;
                assignee = People.First(p => "acc-" + p.Split('@')[0] == account);
            }
            return issue with
            {
                Summary = f.GetProperty("summary").GetString()!, Description = description,
                Due = f.TryGetProperty("duedate", out var due) && due.ValueKind == JsonValueKind.String ? due.GetString() : null, Assignee = assignee, Updated = DateTimeOffset.UtcNow,
            };
        }
    }

    /// <summary>A small Planner group: one plan, tasks with etags that change on every write, and users by id and mail.</summary>
    private sealed class FakePlanner
    {
        public const string Group = "6f1c3a52-0d1e-4c0b-9a51-2f6a8a4f0c11";
        public sealed record Item(string Title, string? Due, int Percent, string[] Assigned, string Etag);

        public ConcurrentDictionary<string, Item> Tasks { get; } = new();
        public Dictionary<string, string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentQueue<string> IfMatch { get; } = new();
        private int etag;

        public string Tag() => $"W/\"etag-{Interlocked.Increment(ref etag)}\"";

        private static object Json(string id, Item i) => new Dictionary<string, object?>
        {
            ["id"] = id, ["title"] = i.Title, ["dueDateTime"] = i.Due, ["percentComplete"] = i.Percent, ["@odata.etag"] = i.Etag,
            ["assignments"] = i.Assigned.ToDictionary(a => a, _ => (object)new { orderHint = "x" }),
        };

        private static HttpResponseMessage Ok(object body) => FakeRestApi.Json(HttpStatusCode.OK, JsonSerializer.Serialize(body));

        public HttpResponseMessage Respond(FakeRestApi.Sent s)
        {
            var path = Uri.UnescapeDataString(s.Uri.AbsolutePath);
            if (path == $"/groups/{Group}/planner/plans")
            {
                return Ok(new { value = new[] { new { id = "plan-erp", title = "ERP plan" } } });
            }
            if (path == "/planner/plans/plan-erp/tasks")
            {
                return Ok(new { value = Tasks.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => Json(t.Key, t.Value)).ToArray() });
            }
            if (path.StartsWith("/users/", StringComparison.Ordinal))
            {
                var who = path["/users/".Length..];
                if (Users.TryGetValue(who, out var mail))
                {
                    return Ok(new { id = who, mail });
                }
                var byMail = Users.FirstOrDefault(u => string.Equals(u.Value, who, StringComparison.OrdinalIgnoreCase));
                return byMail.Key is null ? FakeRestApi.Json(HttpStatusCode.NotFound, "{}") : Ok(new { id = byMail.Key });
            }
            if (path == "/planner/tasks" && s.Method == "POST")
            {
                var id = "task-" + Guid.NewGuid().ToString("N")[..8];
                var body = JsonDocument.Parse(s.Body!).RootElement;
                Tasks[id] = Apply(new Item("", null, 0, [], ""), body);
                return FakeRestApi.Json(HttpStatusCode.Created, JsonSerializer.Serialize(Json(id, Tasks[id])));
            }
            var taskId = path.Split('/')[^1];
            if (!Tasks.TryGetValue(taskId, out var task))
            {
                return FakeRestApi.Json(HttpStatusCode.NotFound, "{}");
            }
            if (s.Method == "PATCH")
            {
                var match = s.Headers.TryGetValue("If-Match", out var m) ? m : "";
                IfMatch.Enqueue(match);
                if (match != task.Etag)
                {
                    return FakeRestApi.Json(HttpStatusCode.PreconditionFailed, "{}");
                }
                Tasks[taskId] = Apply(task, JsonDocument.Parse(s.Body!).RootElement);
                return Ok(Json(taskId, Tasks[taskId]));
            }
            return Ok(Json(taskId, task));
        }

        private Item Apply(Item item, JsonElement body)
        {
            var assigned = item.Assigned.ToHashSet(StringComparer.Ordinal);
            if (body.TryGetProperty("assignments", out var a))
            {
                foreach (var p in a.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Null)
                    {
                        assigned.Remove(p.Name);
                    }
                    else
                    {
                        assigned.Add(p.Name);
                    }
                }
            }
            return new Item(body.GetProperty("title").GetString()!, body.TryGetProperty("dueDateTime", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                body.GetProperty("percentComplete").GetInt32(), assigned.Order(StringComparer.Ordinal).ToArray(), Tag());
        }
    }

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

    /// <summary>Omar owns a project space; Laila is a member and Huda a viewer.</summary>
    private async Task<(TestTenant T, Guid Space, Guid List)> SetupAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sy-" + Guid.NewGuid().ToString("N")[..8], package, "en", ["en", "ar"]);
        await world.CreateUnitAsync(t, "Gulf", null, "organization");
        foreach (var name in new[] { "omar", "laila", "huda" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Gulf");
        }
        var body = new
        {
            name = "ERP rollout", description = "Moving to the new ERP.", visibility = "private", template = "project",
            members = new object[] { new { userId = t["laila"].Id, role = "member" }, new { userId = t["huda"].Id, role = "viewer" } },
        };
        var space = (await OkAsync(t["omar"].Client.PostAsJsonAsync(Api, body), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var board = await OkAsync(t["omar"].Client.GetAsync($"{Api}/{space}/tasks"));
        // The template's starter tasks go, so each test starts from an empty list.
        foreach (var starter in board.GetProperty("tasks").EnumerateArray())
        {
            Assert.Equal(HttpStatusCode.NoContent, (await t["omar"].Client.DeleteAsync($"{Api}/{space}/tasks/{starter.GetProperty("id").GetGuid()}")).StatusCode);
        }
        return (t, space, board.GetProperty("lists")[0].GetProperty("id").GetGuid());
    }

    private static string Email(TestTenant t, string name) => $"{name}@{t.Key}.example";

    private async Task<Guid> ConnectAsync(TestTenant t, string kind, string host, object settings)
    {
        var admin = t["admin"].Client;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host })).StatusCode);
        world.Factory.Secrets.Set(SecretReferences.For(t.Id, "tracker-key"), "fictional-tracker-key");
        var created = await OkAsync(admin.PostAsJsonAsync(Packs, new { kind, displayName = kind == JiraTaskPack.KindName ? "Jira" : "Planner", settings }), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var test = await OkAsync(admin.PostAsync($"{Packs}/{id}:test", null));
        Assert.True(test.GetProperty("status").GetString() == "ok", test.GetRawText());
        await OkAsync(admin.PostAsync($"{Packs}/{id}:enable", null));
        return id;
    }

    private static object JiraSettings(TestTenant t, string host) =>
        new { baseUrl = $"https://{host}", auth = new { type = "basic", username = "tb.integration@example.test", secretRef = SecretReferences.For(t.Id, "tracker-key") } };

    private static async Task<JsonElement> TaskOfAsync(HttpClient client, Guid space, Guid task) =>
        (await OkAsync(client.GetAsync($"{Api}/{space}/tasks/{task}"))).GetProperty("task");

    private static async Task<IReadOnlyList<JsonElement>> TasksAsync(HttpClient client, Guid space) =>
        (await OkAsync(client.GetAsync($"{Api}/{space}/tasks"))).GetProperty("tasks").EnumerateArray().ToList();

    private static Task<JsonElement> RunAsync(HttpClient client, Guid space, Guid link) => OkAsync(client.PostAsync($"{Api}/{space}/sync/links/{link}:run", null));

    private static IEnumerable<JsonElement> History(JsonElement view, string kind) =>
        view.GetProperty("history").EnumerateArray().Where(e => e.GetProperty("kind").GetString() == kind);

    [Fact]
    public async Task A_list_and_a_Jira_project_stay_in_step_both_ways_with_conflicts_failures_and_unlinking_kept_in_history()
    {
        var (t, space, list) = await SetupAsync();
        var omar = t["omar"].Client;
        var host = Host("jira");
        var jira = new FakeJira();
        jira.People.Add(Email(t, "laila"));
        jira.People.Add("outsider@contractor.example");
        var earlier = DateTimeOffset.UtcNow.AddHours(-2);
        jira.Set("ERP-1", new FakeJira.Issue("Design data model", "Tables and keys.", "2026-12-10", false, Email(t, "laila"), earlier));
        jira.Set("ERP-2", new FakeJira.Issue("Migrate customer data", null, null, true, "outsider@contractor.example", earlier));
        Rest.On(host, jira.Respond);
        var connection = await ConnectAsync(t, JiraTaskPack.KindName, host, JiraSettings(t, host));

        // The space already has two tasks: one with the same title as ERP-1, one Jira does not have.
        var design = (await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "design data model" }), HttpStatusCode.Created)).GetProperty("task").GetProperty("id").GetGuid();
        var train = (await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/tasks", new { title = "Train finance team", dueOn = "2026-12-20", assignees = new[] { t["laila"].Id } }),
            HttpStatusCode.Created)).GetProperty("task").GetProperty("id").GetGuid();

        var page = await OkAsync(omar.GetAsync($"{Api}/{space}/sync"));
        Assert.True(page.GetProperty("available").GetBoolean());
        Assert.Equal(JiraTaskPack.KindName, Assert.Single(page.GetProperty("connections").EnumerateArray()).GetProperty("kind").GetString());
        var containers = await OkAsync(omar.GetAsync($"{Api}/{space}/sync/containers?connection={connection}"));
        Assert.Equal(["ERP", "OPS"], containers.GetProperty("items").EnumerateArray().Select(c => c.GetProperty("id").GetString()!).ToArray());
        Assert.Equal("validation.spaces.sync_container", await ProblemKeyAsync(
            await omar.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "NOPE" }), HttpStatusCode.BadRequest));

        // Linking runs the first sync: the same title pairs (the space's task is newer, so it goes out), the rest is created on both sides.
        var linked = await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "ERP" }));
        var link = Assert.Single(linked.GetProperty("links").EnumerateArray());
        var linkId = link.GetProperty("id").GetGuid();
        Assert.Equal(("ERP", "ERP rollout", 3), (link.GetProperty("containerId").GetString(), link.GetProperty("containerName").GetString(), link.GetProperty("tasks").GetInt32()));
        Assert.Equal("design data model", jira.Issues["ERP-1"].Summary);
        var pushed = Assert.Single(jira.Issues, i => i.Value.Summary == "Train finance team");
        Assert.Equal(("2026-12-20", Email(t, "laila")), (pushed.Value.Due, pushed.Value.Assignee));
        var tasks = await TasksAsync(omar, space);
        var migrated = Assert.Single(tasks, x => x.GetProperty("title").GetString() == "Migrate customer data");
        Assert.Equal("done", migrated.GetProperty("status").GetString());
        // The assignee outside the space is left out and named in the history (scenario 5).
        Assert.Equal(0, migrated.GetProperty("assignees").GetArrayLength());
        Assert.Contains(History(linked, SyncEventKinds.SkippedAssignee), e => e.GetProperty("detail").GetString() == "outsider@contractor.example");
        var firstRun = History(linked, SyncEventKinds.Run).First();
        Assert.Equal(("ok", 1, 2), (firstRun.GetProperty("outcome").GetString(), firstRun.GetProperty("pulled").GetInt32(), firstRun.GetProperty("pushed").GetInt32()));

        // The same list or project cannot be linked twice.
        Assert.Equal("conflict.spaces.sync_linked", await ProblemKeyAsync(
            await omar.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "OPS" }), HttpStatusCode.Conflict));

        // A change in Jira comes in, description and assignee included (scenario 2). The first sync sent the space's fields, so Jira had no assignee.
        jira.Set("ERP-1", jira.Issues["ERP-1"] with { Summary = "Design the data model", Description = "Tables, keys and indexes.", Due = "2026-12-12", Assignee = Email(t, "laila"), Updated = DateTimeOffset.UtcNow });
        await RunAsync(omar, space, linkId);
        var pulled = await OkAsync(omar.GetAsync($"{Api}/{space}/tasks/{design}"));
        Assert.Equal(("Design the data model", "2026-12-12", "Tables, keys and indexes."), (pulled.GetProperty("task").GetProperty("title").GetString(),
            pulled.GetProperty("task").GetProperty("dueOn").GetString(), pulled.GetProperty("description").GetString()));
        Assert.Equal(t["laila"].Id, Assert.Single(pulled.GetProperty("task").GetProperty("assignees").EnumerateArray()).GetProperty("userId").GetGuid());

        // A change in the space goes out: completing the task moves the issue to done.
        await OkAsync(omar.PostAsync($"{Api}/{space}/tasks/{train}:complete", null));
        await RunAsync(omar, space, linkId);
        Assert.True(jira.Issues[pushed.Key].Done);

        // Both sides changed: the later change (the space's) wins and the history keeps the conflict (scenario 3).
        jira.Set("ERP-1", jira.Issues["ERP-1"] with { Summary = "Old idea from Jira", Updated = DateTimeOffset.UtcNow.AddMinutes(-30) });
        var current = await TaskOfAsync(omar, space, design);
        await OkAsync(omar.PutAsJsonAsync($"{Api}/{space}/tasks/{design}", new { title = "Design the data model v2", version = current.GetProperty("version").GetInt32() }));
        var conflict = await RunAsync(omar, space, linkId);
        Assert.Equal("Design the data model v2", jira.Issues["ERP-1"].Summary);
        Assert.Contains(History(conflict, SyncEventKinds.Conflict), e => e.GetProperty("outcome").GetString() == "space_won");

        // Jira is down: the space carries on, the run says why, and the change goes with the next run (scenario 4).
        jira.Down = true;
        current = await TaskOfAsync(omar, space, design);
        await OkAsync(omar.PutAsJsonAsync($"{Api}/{space}/tasks/{design}", new { title = "Design while Jira is down", version = current.GetProperty("version").GetInt32() }));
        var down = await RunAsync(omar, space, linkId);
        Assert.Equal("unavailable", History(down, SyncEventKinds.Run).First().GetProperty("outcome").GetString());
        jira.Down = false;
        // A refused change is kept for the next run too.
        jira.RefuseWrites = true;
        var refused = await RunAsync(omar, space, linkId);
        Assert.Contains(History(refused, SyncEventKinds.Failed), e => e.GetProperty("outcome").GetString() == "refused" && e.GetProperty("taskTitle").GetString() == "Design while Jira is down");
        Assert.Equal("partial", History(refused, SyncEventKinds.Run).First().GetProperty("outcome").GetString());
        jira.RefuseWrites = false;
        // The scheduled run (every 10 minutes) carries it.
        Assert.Equal(1, await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var due = await sp.GetRequiredService<SpaceSync>().SyncDueAsync(DateTimeOffset.UtcNow.AddMinutes(11), CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return due;
        }));
        Assert.Equal("Design while Jira is down", jira.Issues["ERP-1"].Summary);

        // An issue deleted in Jira ends that task's link and deletes nothing in the space.
        jira.Issues.TryRemove("ERP-2", out _);
        var gone = await RunAsync(omar, space, linkId);
        Assert.Contains(History(gone, SyncEventKinds.UnlinkedTask), e => e.GetProperty("taskTitle").GetString() == "Migrate customer data");
        Assert.Contains(await TasksAsync(omar, space), x => x.GetProperty("title").GetString() == "Migrate customer data");
        Assert.Equal(2, Assert.Single(gone.GetProperty("links").EnumerateArray()).GetProperty("tasks").GetInt32());

        // Unlinking stops the sync and keeps the tasks on both sides (scenario 6); the history stays.
        var unlinked = await OkAsync(omar.DeleteAsync($"{Api}/{space}/sync/links/{linkId}"));
        Assert.Equal(0, unlinked.GetProperty("links").GetArrayLength());
        Assert.NotEmpty(unlinked.GetProperty("history").EnumerateArray());
        Assert.Equal(3, (await TasksAsync(omar, space)).Count);
        Assert.Equal(2, jira.Issues.Count);
        Assert.Equal(HttpStatusCode.NotFound, (await omar.PostAsync($"{Api}/{space}/sync/links/{linkId}:run", null)).StatusCode);
        foreach (var action in new[] { AuditActions.SpacesSyncLinked, AuditActions.SpacesSyncUnlinked })
        {
            Assert.Equal(1L, await world.Database.CountInTenantAsync(t.Id, "select count(*) from audit.audit_event where action = @a and target_id = @s",
                ("a", action), ("s", space.ToString())));
        }
        // The list can be linked again afterwards.
        await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "ERP" }));
    }

    [Fact]
    public async Task A_Planner_plan_syncs_with_etags_and_done_tasks_land_in_the_done_column()
    {
        var (t, space, list) = await SetupAsync();
        var omar = t["omar"].Client;
        var host = Host("graph");
        var planner = new FakePlanner();
        planner.Users["u-laila"] = Email(t, "laila");
        planner.Tasks["task-1"] = new FakePlanner.Item("Order servers", "2026-12-05T00:00:00Z", 100, ["u-laila"], planner.Tag());
        Rest.On(host, planner.Respond);
        var settings = new { baseUrl = $"https://{host}", auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "tracker-key") }, groupId = FakePlanner.Group };
        // A Planner connection names its group.
        var admin = t["admin"].Client;
        await OkAsync(admin.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host }));
        Assert.Equal("validation.tasks.planner_group", await ProblemKeyAsync(await admin.PostAsJsonAsync(Packs, new
        {
            kind = PlannerTaskPack.KindName, displayName = "Planner", settings = new { baseUrl = $"https://{host}", auth = new { type = "bearer", secretRef = SecretReferences.For(t.Id, "tracker-key") } },
        }), HttpStatusCode.BadRequest));
        var connection = await ConnectAsync(t, PlannerTaskPack.KindName, host, settings);

        var linked = await OkAsync(omar.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "plan-erp" }));
        Assert.Equal("ERP plan", Assert.Single(linked.GetProperty("links").EnumerateArray()).GetProperty("containerName").GetString());
        var board = await OkAsync(omar.GetAsync($"{Api}/{space}/tasks"));
        var doneColumn = board.GetProperty("lists")[0].GetProperty("columns").EnumerateArray().First(c => c.GetProperty("done").GetBoolean()).GetProperty("id").GetGuid();
        var order = Assert.Single(board.GetProperty("tasks").EnumerateArray());
        Assert.Equal(("Order servers", "done", "2026-12-05", doneColumn), (order.GetProperty("title").GetString(), order.GetProperty("status").GetString(),
            order.GetProperty("dueOn").GetString(), order.GetProperty("columnId").GetGuid()));
        Assert.Equal(t["laila"].Id, Assert.Single(order.GetProperty("assignees").EnumerateArray()).GetProperty("userId").GetGuid());

        // A change in the space goes to Planner with the etag Planner gave, and the assignee is taken off.
        var id = order.GetProperty("id").GetGuid();
        var etag = planner.Tasks["task-1"].Etag;
        await OkAsync(omar.PutAsJsonAsync($"{Api}/{space}/tasks/{id}", new { title = "Order two servers", assignees = Array.Empty<Guid>(), version = order.GetProperty("version").GetInt32() }));
        await RunAsync(omar, space, Assert.Single(linked.GetProperty("links").EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(etag, Assert.Single(planner.IfMatch));
        Assert.Equal(("Order two servers", 0), (planner.Tasks["task-1"].Title, planner.Tasks["task-1"].Assigned.Length));
    }

    [Fact]
    public async Task Sync_is_for_owners_in_the_package_and_stays_inside_the_tenant()
    {
        var (t, space, list) = await SetupAsync();
        var host = Host("jira");
        var jira = new FakeJira();
        Rest.On(host, jira.Respond);
        var connection = await ConnectAsync(t, JiraTaskPack.KindName, host, JiraSettings(t, host));

        // Members and viewers do not manage sync, whatever they send.
        foreach (var name in new[] { "laila", "huda" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await t[name].Client.GetAsync($"{Api}/{space}/sync")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await t[name].Client.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "ERP" })).StatusCode);
        }
        // A list of another space is refused.
        Assert.Equal("validation.spaces.sync_list", await ProblemKeyAsync(
            await t["omar"].Client.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = Guid.NewGuid(), connectionId = connection, containerId = "ERP" }), HttpStatusCode.BadRequest));

        // Another tenant cannot reach the space, and the first tenant's connection is not one of its own.
        var (other, otherSpace, otherList) = await SetupAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other["omar"].Client.GetAsync($"{Api}/{space}/sync")).StatusCode);
        Assert.Equal("validation.spaces.sync_connection", await ProblemKeyAsync(
            await other["omar"].Client.PostAsJsonAsync($"{Api}/{otherSpace}/sync/links", new { listId = otherList, connectionId = connection, containerId = "ERP" }), HttpStatusCode.BadRequest));
        Assert.Equal(0, (await OkAsync(other["omar"].Client.GetAsync($"{Api}/{otherSpace}/sync"))).GetProperty("connections").GetArrayLength());

        // Without spaces.task_sync the page says so and linking is refused.
        await world.SetCapabilityAsync(t, Capabilities.SpacesTaskSync, new { enabled = false });
        var page = await OkAsync(t["omar"].Client.GetAsync($"{Api}/{space}/sync"));
        Assert.False(page.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["omar"].Client.PostAsJsonAsync($"{Api}/{space}/sync/links", new { listId = list, connectionId = connection, containerId = "ERP" })).StatusCode);
    }
}
