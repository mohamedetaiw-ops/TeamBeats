using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 R2 T021: start triggers (FR-014, US8 scenario 2). A product event starts one request per event for the
/// person it is about, in their and their manager's lists; a start service step starts the other service's request;
/// a loop of services stops past chain depth 5 with an alert, and a service cannot start itself; a schedule starts one
/// request per period for its system requester; a requester who leaves has open requests cancelled unless the service
/// continues after exit; triggers need workflow.automation, and a requester from another tenant is refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceTriggerTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("trg-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "mona", "lina" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        Assert.Equal(HttpStatusCode.OK, (await t["admin"].Client.PutAsJsonAsync($"/api/v1/people/{t["sara"].Id}/manager", new { managerId = t["mona"].Id })).StatusCode);
        return t;
    }

    private static object Step(string id, string type, object? extra = null)
    {
        var step = new Dictionary<string, object?> { ["id"] = id, ["type"] = type, ["names"] = Names(id, id) };
        foreach (var p in (extra ?? new { }).GetType().GetProperties())
        {
            step[p.Name] = p.GetValue(extra);
        }
        return step;
    }

    private static object Link(string from, string to) => new { from, to };

    /// <summary>The manager approves, then the route ends.</summary>
    private static object[] ManagerSteps => [Step("manager", "approval", new { assignee = new { kind = "line_manager" } }), Step("done", "end", new { result = "approved" })];

    private static object[] ManagerLinks => [Link("manager", "done")];

    private static Task<HttpResponseMessage> DraftAsync(HttpClient admin, Guid id, object route) =>
        admin.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "note", type = "text", labels = Names("Note", "ملاحظة") } } },
            route,
        });

    private static async Task<Guid> CreateAsync(TestTenant t, string name)
    {
        var created = await t["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names(name, name) });
        return (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ServiceAsync(TestTenant t, object route, Guid? existing = null, string name = "Onboarding")
    {
        var id = existing ?? await CreateAsync(t, name);
        var draft = await DraftAsync(t["admin"].Client, id, route);
        Assert.True(draft.StatusCode == HttpStatusCode.OK, await draft.Content.ReadAsStringAsync());
        var published = await t["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { });
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        return id;
    }

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private Task HandleAsync<TEvent>(TestTenant t, TEvent message) where TEvent : TeamBeats.Primitives.IIntegrationEvent =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<ServiceTriggers>().HandleAsync((dynamic)message, CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });

    private static async Task<JsonElement[]> ListAsync(HttpClient client, string path = "/api/v1/requests") =>
        (await (await client.GetAsync(path)).ReadAsync<JsonElement>()).EnumerateArray().ToArray();

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid id) => await (await client.GetAsync($"/api/v1/requests/{id}")).ReadAsync<JsonElement>();

    private static async Task<string> StatusAsync(HttpClient client, Guid id) => (await DetailAsync(client, id)).GetProperty("request").GetProperty("status").GetString()!;

    private static async Task<List<string>> TrackingAsync(HttpClient client, Guid id) =>
        (await DetailAsync(client, id)).GetProperty("tracking").EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToList();

    private static Guid IdOf(JsonElement summary) => summary.GetProperty("id").GetGuid();

    private Task<long> CountAsync(TestTenant t, Guid service) =>
        world.Database.TenantScalarAsync<long>(t.Id, $"select count(*) from services.request where service_id = '{service}'");

    [Fact]
    public async Task A_product_event_starts_one_request_for_the_person_in_their_and_their_managers_lists()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, new
        {
            graph = new { steps = ManagerSteps, links = ManagerLinks },
            triggers = new[] { new { kind = "event", @event = "user_provisioned" } },
        });

        // Delivered twice through the outbox and once more by hand: one request, for Sara, waiting on her manager.
        await world.Factory.InScopeAsync(t.Id, async sp =>
        {
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserProvisioned(t["sara"].Id, "directory"));
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new UserProvisioned(t["sara"].Id, "directory"));
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return 0;
        });
        await DispatchAsync();
        await HandleAsync(t, new UserProvisioned(t["sara"].Id, "directory"));
        Assert.Equal(1L, await CountAsync(t, service));
        var request = IdOf(Assert.Single(await ListAsync(t["sara"].Client), r => r.GetProperty("serviceId").GetGuid() == service));
        Assert.Contains(await ListAsync(t["mona"].Client, "/api/v1/requests/team"), r => IdOf(r) == request);
        Assert.Equal("in_approval", await StatusAsync(t["sara"].Client, request));
        Assert.Equal(["triggered"], (await TrackingAsync(t["sara"].Client, request)).Take(1));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            $"select count(*) from audit.audit_event where action = 'request.started_by_trigger' and target_id = '{request}'"));

        // Workplace has no triggers: the same event starts nothing there.
        var basic = await FreshAsync("workplace");
        await HandleAsync(basic, new UserProvisioned(basic["sara"].Id, "directory"));
        Assert.Equal(0L, await world.Database.TenantScalarAsync<long>(basic.Id, "select count(*) from services.request"));

        // Another event, or a unit change outside the trigger's unit, starts nothing more.
        await HandleAsync(t, new UserProvisioned(t["lina"].Id, "directory"));
        Assert.Equal(2L, await CountAsync(t, service));
        var unitBound = await ServiceAsync(t, new
        {
            triggers = new[] { new { kind = "event", @event = "membership_changed", orgUnitId = t.Units["Holding"] } },
        }, name: "Unit welcome");
        await HandleAsync(t, new OrganizationMembershipChanged(t["lina"].Id, t.Units["Dubai"], "added"));
        Assert.Equal(0L, await CountAsync(t, unitBound));
        await HandleAsync(t, new OrganizationMembershipChanged(t["lina"].Id, t.Units["Holding"], "added"));
        await HandleAsync(t, new OrganizationMembershipChanged(t["lina"].Id, t.Units["Holding"], "added"));
        Assert.Equal(1L, await CountAsync(t, unitBound));
    }

    [Fact]
    public async Task A_start_service_step_starts_the_other_service_and_a_loop_stops_past_depth_five()
    {
        var t = await FreshAsync();
        var laptop = await ServiceAsync(t, new { graph = new { steps = ManagerSteps, links = ManagerLinks } }, name: "Laptop");
        var onboarding = await ServiceAsync(t, new
        {
            graph = new { steps = new[] { Step("laptop", "start_service", new { serviceId = laptop }), Step("done", "end") }, links = new[] { Link("laptop", "done") } },
        });
        var submitted = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = onboarding, data = new { note = "first day" } });
        Assert.True(submitted.StatusCode == HttpStatusCode.Created, await submitted.Content.ReadAsStringAsync());
        var request = (await submitted.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
        await DispatchAsync();
        await DispatchAsync();
        var started = IdOf(Assert.Single(await ListAsync(t["sara"].Client), r => r.GetProperty("serviceId").GetGuid() == laptop));
        Assert.Equal("in_approval", await StatusAsync(t["sara"].Client, started));
        Assert.Contains("service_started", await TrackingAsync(t["sara"].Client, request));
        Assert.Equal(1, await world.Database.TenantScalarAsync<int>(t.Id, $"select chain_depth from services.request where id = '{started}'"));

        // A service cannot start itself, by a step or by its own completion.
        var self = await DraftAsync(t["admin"].Client, laptop, new
        {
            graph = new { steps = new[] { Step("again", "start_service", new { serviceId = laptop }), Step("done", "end") }, links = new[] { Link("again", "done") } },
            triggers = new[] { new { kind = "event", @event = "request_completed", serviceId = laptop } },
        });
        var text = await self.Content.ReadAsStringAsync();
        Assert.True(self.StatusCode == HttpStatusCode.BadRequest, text);
        Assert.Contains("route.graph.steps[0]", text);
        Assert.Contains("route.triggers[0]", text);
        Assert.Contains("validation.services.route_cycle", text);

        // Ping and pong start each other when one completes: the chain stops at depth 5 with an alert on the last request.
        var ping = await CreateAsync(t, "Ping");
        var pong = await CreateAsync(t, "Pong");
        var end = new { steps = new[] { Step("tell", "notify", new { notify = new { requester = true } }), Step("done", "end") }, links = new[] { Link("tell", "done") } };
        await ServiceAsync(t, new { graph = end, triggers = new[] { new { kind = "event", @event = "request_completed", serviceId = pong } } }, ping);
        await ServiceAsync(t, new { graph = end, triggers = new[] { new { kind = "event", @event = "request_completed", serviceId = ping, status = "completed" } } }, pong);
        var first = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = ping, data = new { note = "go" } });
        Assert.True(first.StatusCode == HttpStatusCode.Created, await first.Content.ReadAsStringAsync());
        for (var i = 0; i < 20; i++)
        {
            await DispatchAsync();
        }
        Assert.Equal(6L, await CountAsync(t, ping) + await CountAsync(t, pong));
        var last = await world.Database.TenantScalarAsync<Guid>(t.Id,
            $"select id from services.request where service_id in ('{ping}', '{pong}') and chain_depth = 5");
        Assert.Equal("completed", await StatusAsync(t["sara"].Client, last));
        Assert.Contains("chain_stopped", await TrackingAsync(t["sara"].Client, last));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            $"select count(*) from audit.audit_event where action = 'request.chain_stopped' and target_id = '{last}'"));
    }

    [Fact]
    public async Task A_schedule_starts_one_request_per_period_for_its_requester_and_never_for_the_past()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, new
        {
            triggers = new[] { new { kind = "schedule", frequency = "monthly", day = 1, requesterId = t["lina"].Id } },
        }, name: "Monthly report");
        Task<int> RunAsync() => world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ServiceTriggers>().RunSchedulesAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });

        // Published today: this month's day 1 is not caught up (unless today is the 1st).
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(today.Day == 1 ? 1 : 0, await RunAsync());

        // Published long ago: this month's request starts once, for Lina, however often the schedule runs.
        await world.Database.OwnerExecInTenantAsync(t.Id, $"update services.service_version set published_at = now() - interval '90 days' where service_id = '{service}'");
        await RunAsync();
        Assert.Equal(0, await RunAsync());
        var request = IdOf(Assert.Single(await ListAsync(t["lina"].Client), r => r.GetProperty("serviceId").GetGuid() == service));
        Assert.Equal("submitted", await StatusAsync(t["lina"].Client, request));
        Assert.Equal(1L, await CountAsync(t, service));
    }

    [Fact]
    public void Schedule_periods_are_iso_weeks_months_and_quarters()
    {
        Assert.Equal(("2026-W46", new DateOnly(2026, 11, 13)), ServiceTriggers.Period("weekly", 5, new DateOnly(2026, 11, 13)));
        Assert.Equal(("2026-W46", new DateOnly(2026, 11, 15)), ServiceTriggers.Period("weekly", 7, new DateOnly(2026, 11, 9)));
        Assert.Equal(("2026-11", new DateOnly(2026, 11, 28)), ServiceTriggers.Period("monthly", 28, new DateOnly(2026, 11, 13)));
        Assert.Equal(("2026-Q4", new DateOnly(2026, 10, 3)), ServiceTriggers.Period("quarterly", 3, new DateOnly(2026, 12, 31)));
        Assert.Null(ServiceTriggers.Period("monthly", 31, new DateOnly(2026, 11, 13)));
    }

    [Fact]
    public async Task A_requester_who_leaves_has_open_requests_cancelled_unless_the_service_continues_after_exit()
    {
        var t = await FreshAsync();
        var training = await ServiceAsync(t, new { graph = new { steps = ManagerSteps, links = ManagerLinks } }, name: "Training");
        var settlement = await ServiceAsync(t, new { graph = new { steps = ManagerSteps, links = ManagerLinks }, continueAfterExit = true }, name: "Settlement");
        var exit = await ServiceAsync(t, new
        {
            graph = new { steps = ManagerSteps, links = ManagerLinks },
            triggers = new[] { new { kind = "event", @event = "user_deprovisioned" } },
        }, name: "Exit interview");
        async Task<Guid> SubmitAsync(Guid service)
        {
            var response = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { note = "x" } });
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            return (await response.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
        }
        var open = await SubmitAsync(training);
        var kept = await SubmitAsync(settlement);

        // The exit trigger runs too; the request it starts for the leaver is not cancelled by the same leaving.
        await HandleAsync(t, new UserDeprovisioned(t["sara"].Id, "directory"));
        await HandleAsync(t, new UserDeprovisioned(t["sara"].Id, "directory"));
        Assert.Equal("cancelled", await StatusAsync(t["admin"].Client, open));
        Assert.Contains("requester_left", await TrackingAsync(t["admin"].Client, open));
        Assert.Equal("in_approval", await StatusAsync(t["admin"].Client, kept));
        var exitRequest = await world.Database.TenantScalarAsync<Guid>(t.Id, $"select id from services.request where service_id = '{exit}'");
        Assert.Equal("in_approval", await StatusAsync(t["admin"].Client, exitRequest));
        var inbox = (await (await t["mona"].Client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().Select(i => i.GetProperty("requestId").GetGuid()).ToList();
        Assert.DoesNotContain(open, inbox);
        Assert.Contains(kept, inbox);
        Assert.Contains(exitRequest, inbox);
    }

    [Fact]
    public async Task Triggers_need_workflow_automation_valid_settings_and_a_requester_of_the_same_tenant()
    {
        var basic = await FreshAsync("workplace");
        var service = await CreateAsync(basic, "Onboarding");
        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(basic["admin"].Client, service,
            new { triggers = new[] { new { kind = "event", @event = "user_provisioned" } } })).StatusCode);

        var t = await FreshAsync();
        var other = await FreshAsync();
        var id = await CreateAsync(t, "Onboarding");
        foreach (var trigger in new object[]
        {
            new { kind = "schedule", frequency = "monthly", day = 1, requesterId = other["lina"].Id },
            new { kind = "schedule", frequency = "weekly", day = 8, requesterId = t["lina"].Id },
            new { kind = "schedule", frequency = "yearly", day = 1, requesterId = t["lina"].Id },
            new { kind = "event", @event = "user_renamed" },
            new { kind = "event", @event = "request_completed" },
            new { kind = "event", @event = "user_provisioned", orgUnitId = t.Units["Dubai"] },
        })
        {
            var response = await DraftAsync(t["admin"].Client, id, new { triggers = new[] { trigger } });
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
            Assert.Contains("validation.services.trigger", text);
        }
    }
}
