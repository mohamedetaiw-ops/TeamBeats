using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 R2 T019 and T020: service routes built as step graphs run on the process engine. Steps go to their
/// assignee (a queue, the line manager, named people), conditions on form values pick the path and the steps not taken
/// show as skipped on Tracking (US5 scenario 1), parallel steps join, reject and return end the route, open requests
/// finish on the route version they started with (US5 scenario 3), a split with no join cannot be published (US8
/// scenario 7), Workplace keeps approval and task steps in sequence only, and another tenant's group is refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceRouteGraphTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("srg-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "mona", "kofi", "lina", "it" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        return t;
    }

    private Task<Guid> GroupAsync(TestTenant t, string key, params string[] members) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var id = await sp.GetRequiredService<IAuthorizationProvisioning>().SyncDirectoryGroupAsync(key, key, members.Select(m => t[m].Id).ToList());
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return id;
        });

    private static object Step(string id, string type, object? extra = null)
    {
        var step = new Dictionary<string, object?> { ["id"] = id, ["type"] = type, ["names"] = Names(id, id) };
        foreach (var p in (extra ?? new { }).GetType().GetProperties())
        {
            step[p.Name] = p.GetValue(extra);
        }
        return step;
    }

    private static object Link(string from, string to, object[]? when = null) => new { from, to, when };

    private static object Graph(object[] steps, object[] links) => new { graph = new { steps, links } };

    private static Task<HttpResponseMessage> DraftAsync(HttpClient admin, Guid id, object route) =>
        admin.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "cost", type = "number", labels = Names("Cost", "التكلفة"), required = true } } },
            route,
        });

    private static async Task<Guid> CreateAsync(TestTenant t)
    {
        var created = await t["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Training", "تدريب") });
        return (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ServiceAsync(TestTenant t, object route, Guid? existing = null)
    {
        var id = existing ?? await CreateAsync(t);
        var draft = await DraftAsync(t["admin"].Client, id, route);
        Assert.True(draft.StatusCode == HttpStatusCode.OK, await draft.Content.ReadAsStringAsync());
        var published = await t["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { });
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        return id;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, Guid service, decimal cost)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { cost } });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement[]> InboxAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    private static async Task<Guid> TaskOfAsync(HttpClient client, Guid request) =>
        Assert.Single(await InboxAsync(client), i => i.GetProperty("requestId").GetGuid() == request).GetProperty("taskId").GetGuid();

    private static async Task DecideAsync(HttpClient client, Guid task, string outcome, string? comment = null)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome, comment });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid id) => await (await client.GetAsync($"/api/v1/requests/{id}")).ReadAsync<JsonElement>();

    private static async Task<string> StatusAsync(HttpClient client, Guid id) => (await DetailAsync(client, id)).GetProperty("request").GetProperty("status").GetString()!;

    private static async Task<List<(string Event, string? Step)>> TrackingAsync(HttpClient client, Guid id) =>
        (await DetailAsync(client, id)).GetProperty("tracking").EnumerateArray()
            .Select(e => (e.GetProperty("event").GetString()!, e.GetProperty("step").ValueKind == JsonValueKind.String ? e.GetProperty("step").GetString() : null)).ToList();

    /// <summary>Training request: the training admins check the seat, the manager approves, and above 1,000 the head approves too.</summary>
    private static object Training(Guid admins, Guid head) => Graph(
        [
            Step("seat", "task", new { assignee = new { kind = "group", groups = new[] { admins } } }),
            Step("manager", "approval", new { assignee = new { kind = "line_manager" }, dueWorkingDays = 3 }),
            Step("costly", "condition"),
            Step("head", "approval", new { assignee = new { kind = "users", users = new[] { head } } }),
            Step("done", "end", new { result = "approved" }),
        ],
        [
            Link("seat", "manager"), Link("manager", "costly"),
            Link("costly", "head", [new { field = "cost", @operator = "gt", value = 1000 }]), Link("costly", "done"), Link("head", "done"),
        ]);

    [Fact]
    public async Task Each_step_goes_to_its_assignee_the_condition_picks_the_path_and_steps_not_taken_show_as_skipped()
    {
        var t = await FreshAsync();
        var admins = await GroupAsync(t, "training-admins", "lina", "sara");
        var service = await ServiceAsync(t, Training(admins, t["kofi"].Id));

        // A cheap course: the queue checks the seat (Lina holds no approver role; Sara files it, so stays out), the manager approves, done.
        var cheap = await SubmitAsync(t["sara"].Client, service, 400);
        Assert.Equal("in_approval", await StatusAsync(t["sara"].Client, cheap));
        Assert.DoesNotContain(await InboxAsync(t["sara"].Client), i => i.GetProperty("requestId").GetGuid() == cheap);
        Assert.Empty(await InboxAsync(t["mona"].Client));
        var seat = Assert.Single(await InboxAsync(t["lina"].Client));
        Assert.Equal("seat", seat.GetProperty("step").GetProperty("en").GetString());
        await DecideAsync(t["lina"].Client, seat.GetProperty("taskId").GetGuid(), "approve");
        var manager = Assert.Single(await InboxAsync(t["mona"].Client));
        Assert.Equal("manager", manager.GetProperty("step").GetProperty("en").GetString());
        Assert.NotEqual(JsonValueKind.Null, manager.GetProperty("dueAt").ValueKind);
        await DecideAsync(t["mona"].Client, manager.GetProperty("taskId").GetGuid(), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, cheap));
        var tracking = await TrackingAsync(t["sara"].Client, cheap);
        Assert.Contains(("task_done", "seat"), tracking);
        Assert.Contains(("approved", "manager"), tracking);
        Assert.Contains(("skipped", "head"), tracking);

        // A costly course also goes to the head; nothing is skipped.
        var costly = await SubmitAsync(t["sara"].Client, service, 2500);
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, costly), "approve");
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, costly), "approve");
        Assert.Equal("in_approval", await StatusAsync(t["sara"].Client, costly));
        await DecideAsync(t["kofi"].Client, await TaskOfAsync(t["kofi"].Client, costly), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, costly));
        Assert.DoesNotContain(await TrackingAsync(t["sara"].Client, costly), e => e.Event == "skipped");
    }

    [Fact]
    public async Task Parallel_steps_join_and_a_rejection_or_return_ends_the_whole_route()
    {
        var t = await FreshAsync();
        var it = await GroupAsync(t, "it", "it");
        var logistics = await GroupAsync(t, "logistics", "lina");
        var service = await ServiceAsync(t, Graph(
            [
                Step("both", "split"),
                Step("laptop", "task", new { assignee = new { kind = "group", groups = new[] { it } } }),
                Step("cards", "task", new { assignee = new { kind = "group", groups = new[] { logistics } } }),
                Step("joined", "join"),
                Step("manager", "approval", new { assignee = new { kind = "line_manager" } }),
                Step("done", "end"),
            ],
            [Link("both", "laptop"), Link("both", "cards"), Link("laptop", "joined"), Link("cards", "joined"), Link("joined", "manager"), Link("manager", "done")]));

        // Both tasks open at once; the manager's step waits for both.
        var request = await SubmitAsync(t["sara"].Client, service, 10);
        await DecideAsync(t["it"].Client, await TaskOfAsync(t["it"].Client, request), "approve");
        Assert.Empty(await InboxAsync(t["mona"].Client));
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, request), "approve");
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, request), "approve");
        Assert.Equal("completed", await StatusAsync(t["sara"].Client, request));

        // A rejected task ends the route: the other branch's open task closes with it.
        var rejected = await SubmitAsync(t["sara"].Client, service, 10);
        await DecideAsync(t["it"].Client, await TaskOfAsync(t["it"].Client, rejected), "reject", "No laptops left");
        Assert.Equal("rejected", await StatusAsync(t["sara"].Client, rejected));
        Assert.DoesNotContain(await InboxAsync(t["lina"].Client), i => i.GetProperty("requestId").GetGuid() == rejected);
        Assert.Contains(("task_rejected", "laptop"), await TrackingAsync(t["sara"].Client, rejected));

        // The manager returns it: the requester can change it and submit again.
        var returned = await SubmitAsync(t["sara"].Client, service, 10);
        await DecideAsync(t["it"].Client, await TaskOfAsync(t["it"].Client, returned), "approve");
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, returned), "approve");
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, returned), "return", "Add the cost centre");
        Assert.Equal("returned", await StatusAsync(t["sara"].Client, returned));
    }

    [Fact]
    public async Task Open_requests_finish_on_the_route_version_they_started_with()
    {
        var t = await FreshAsync();
        var admins = await GroupAsync(t, "training-admins", "lina");
        var service = await ServiceAsync(t, Training(admins, t["kofi"].Id));
        var before = await SubmitAsync(t["sara"].Client, service, 2500);

        // Version 2 drops the seat check and the head: the manager alone approves.
        await ServiceAsync(t, Graph(
            [Step("manager", "approval", new { assignee = new { kind = "line_manager" } }), Step("done", "end", new { result = "approved" })],
            [Link("manager", "done")]), service);
        var after = await SubmitAsync(t["sara"].Client, service, 2500);
        Assert.Equal(after, (await InboxAsync(t["mona"].Client)).Single().GetProperty("requestId").GetGuid());
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, after), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, after));

        // The first request still runs version 1: seat, manager, then the head.
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, before), "approve");
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, before), "approve");
        await DecideAsync(t["kofi"].Client, await TaskOfAsync(t["kofi"].Client, before), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, before));
    }

    [Fact]
    public async Task A_route_that_cannot_run_is_not_published_and_shapes_are_checked_when_saved()
    {
        var t = await FreshAsync();
        var other = await world.CreateTenantAsync("srg-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        await world.SeedUserAsync(other, "lina", MembershipTypes.Employee);
        var foreign = await GroupAsync(other, "their-queue", "lina");
        var admin = t["admin"].Client;
        var id = await CreateAsync(t);

        // A split with no join (US8 scenario 7): saved as a draft, refused at publish with the problem on the split.
        var draft = await DraftAsync(admin, id, Graph(
            [Step("both", "split"), Step("a", "approval", new { assignee = new { kind = "line_manager" } }),
             Step("b", "approval", new { assignee = new { kind = "line_manager" } }), Step("end_a", "end"), Step("end_b", "end")],
            [Link("both", "a"), Link("both", "b"), Link("a", "end_a"), Link("b", "end_b")]));
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        var refused = await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { });
        var body = await refused.Content.ReadAsStringAsync();
        Assert.True(refused.StatusCode == HttpStatusCode.BadRequest, body);
        Assert.Contains("route.graph.steps[0]", body);
        Assert.Contains("validation.process.parallel_join", body);

        // Another tenant's queue does not exist here.
        await DraftAsync(admin, id, Graph(
            [Step("pick", "task", new { assignee = new { kind = "group", groups = new[] { foreign } } }), Step("done", "end")], [Link("pick", "done")]));
        var foreignPublish = await admin.PostAsJsonAsync($"{Admin}/{id}/publish", new { });
        Assert.Equal(HttpStatusCode.BadRequest, foreignPublish.StatusCode);
        Assert.Contains("validation.process.assignment_group", await foreignPublish.Content.ReadAsStringAsync());

        // Shapes are checked on save: two first steps, a condition on a plain step, an unknown type, a mixed route.
        foreach (var (route, key) in new (object, string)[]
        {
            (Graph([Step("a", "approval", new { assignee = new { kind = "line_manager" } }), Step("b", "end"), Step("c", "end")], [Link("a", "b")]), "validation.services.route_start"),
            (Graph([Step("a", "approval", new { assignee = new { kind = "line_manager" } }), Step("b", "end")],
                [Link("a", "b", [new { field = "cost", @operator = "gt", value = 1 }])]), "validation.services.route_condition"),
            (Graph([Step("a", "script"), Step("b", "end")], [Link("a", "b")]), "validation.services.route_step"),
            (Graph([Step("a", "condition"), Step("b", "end")], [Link("a", "b")]), "validation.services.route_paths"),
            (new { approval = new { approvers = new { kind = "line_manager" } }, graph = new { steps = new[] { Step("b", "end") }, links = Array.Empty<object>() } },
                "validation.services.route_graph"),
        })
        {
            var response = await DraftAsync(admin, id, route);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
            Assert.Contains(key, text);
        }
    }

    [Fact]
    public async Task Workplace_runs_approval_and_task_steps_in_sequence_but_not_conditions_or_automatic_steps()
    {
        var t = await FreshAsync("workplace");
        var admins = await GroupAsync(t, "training-admins", "lina");
        var service = await ServiceAsync(t, Graph(
            [Step("seat", "task", new { assignee = new { kind = "group", groups = new[] { admins } } }),
             Step("manager", "approval", new { assignee = new { kind = "line_manager" } }), Step("done", "end", new { result = "approved" })],
            [Link("seat", "manager"), Link("manager", "done")]));
        var request = await SubmitAsync(t["sara"].Client, service, 10);
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, request), "approve");
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, request), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, request));

        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(t["admin"].Client, service, Training(admins, t["kofi"].Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(t["admin"].Client, service, Graph(
            [Step("tell", "notify", new { notify = new { requester = true } }), Step("done", "end")], [Link("tell", "done")]))).StatusCode);
    }

    [Fact]
    public async Task The_queue_is_told_a_request_waits_and_a_notify_step_tells_the_requester()
    {
        var t = await FreshAsync();
        var admins = await GroupAsync(t, "training-admins", "lina", "kofi");
        var service = await ServiceAsync(t, Graph(
            [Step("seat", "task", new { assignee = new { kind = "group", groups = new[] { admins } } }),
             Step("tell", "notify", new { notify = new { requester = true } }), Step("done", "end")],
            [Link("seat", "tell"), Link("tell", "done")]));
        var request = await SubmitAsync(t["sara"].Client, service, 10);
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        foreach (var member in new[] { "lina", "kofi" })
        {
            Assert.Contains(await NotificationsAsync(t[member].Client), n => n.GetProperty("link").GetString()!.StartsWith($"/requests/{request}?task=", StringComparison.Ordinal));
        }
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, request), "approve");
        await world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);
        Assert.Equal("completed", await StatusAsync(t["sara"].Client, request));
        Assert.Contains(await NotificationsAsync(t["sara"].Client), n => n.GetProperty("link").GetString() == $"/requests/{request}"
            && n.GetProperty("title").GetString()!.Contains("Update on request", StringComparison.Ordinal));
    }

    private static async Task<JsonElement[]> NotificationsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/me/notifications")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();
}
