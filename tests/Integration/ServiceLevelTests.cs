using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 016 R2 T022 (FR-015, US5 scenarios 3 and 4): an auto-approve rule completes an approval step as the system
/// ("auto-approved by rule" on Tracking), also inside parallel paths; rules on Confidential services need a tenant
/// administrator; a service level target puts a request at risk at 75% and breached past its due time, each recorded
/// and alerted once to the queue and the owners, the breach counted for analytics; a request closed in time has met it.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceLevelTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sla-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "mona", "lina", "kofi" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        await world.AssignAsync(t, "kofi", BuiltInRoles.WorkflowDesigner, "org_subtree", dubai);
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

    private static object[] Cheap => [new { field = "boxes", @operator = "le", value = 2 }];

    private static Task<HttpResponseMessage> DraftAsync(HttpClient client, Guid id, object route) =>
        client.PutAsJsonAsync($"{Admin}/{id}/draft", new
        {
            form = new { fields = new object[] { new { key = "boxes", type = "number", labels = Names("Boxes", "علب"), required = true } } },
            route,
        });

    private static async Task<Guid> CreateAsync(TestTenant t, string classification = "internal", Guid? owner = null)
    {
        var created = await t["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("Business cards", "بطاقات العمل"), classification, ownerOrgUnitId = owner,
            audience = owner is { } unit ? new { orgUnits = new[] { unit } } : null });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        return (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ServiceAsync(TestTenant t, object route)
    {
        var id = await CreateAsync(t);
        var draft = await DraftAsync(t["admin"].Client, id, route);
        Assert.True(draft.StatusCode == HttpStatusCode.OK, await draft.Content.ReadAsStringAsync());
        var published = await t["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { });
        Assert.True(published.StatusCode == HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
        return id;
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, Guid service, int boxes)
    {
        var response = await client.PostAsJsonAsync("/api/v1/requests", new { serviceId = service, data = new { boxes } });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
    }

    private Task DispatchAsync() => world.Factory.Services.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(500, CancellationToken.None);

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid id) => await (await client.GetAsync($"/api/v1/requests/{id}")).ReadAsync<JsonElement>();

    private static async Task<string> StatusAsync(HttpClient client, Guid id) => (await DetailAsync(client, id)).GetProperty("request").GetProperty("status").GetString()!;

    private static async Task<List<(string Event, string? Step)>> TrackingAsync(HttpClient client, Guid id) =>
        (await DetailAsync(client, id)).GetProperty("tracking").EnumerateArray()
            .Select(e => (e.GetProperty("event").GetString()!, e.GetProperty("step").ValueKind == JsonValueKind.String ? e.GetProperty("step").GetString() : null)).ToList();

    private static async Task<List<Guid>> InboxAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray().Select(i => i.GetProperty("requestId").GetGuid()).ToList();

    private static async Task<Guid> TaskOfAsync(HttpClient client, Guid request) =>
        Assert.Single((await (await client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>()).EnumerateArray(), i => i.GetProperty("requestId").GetGuid() == request)
            .GetProperty("taskId").GetGuid();

    private static async Task DecideAsync(HttpClient client, Guid task, string outcome)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement[]> NotificationsAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/v1/me/notifications")).ReadAsync<JsonElement>()).GetProperty("items").EnumerateArray().ToArray();

    private Task<int> CheckAsync(TestTenant t) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var n = await sp.GetRequiredService<ServiceLevels>().CheckAsync(CancellationToken.None);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return n;
        });

    [Fact]
    public async Task An_auto_approve_rule_completes_the_step_as_the_system_and_others_wait_for_the_manager()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, new
        {
            graph = new
            {
                steps = new[] { Step("manager", "approval", new { assignee = new { kind = "line_manager" }, autoApprove = Cheap }), Step("done", "end", new { result = "approved" }) },
                links = new[] { Link("manager", "done") },
            },
        });

        var cheap = await SubmitAsync(t["sara"].Client, service, 2);
        await DispatchAsync();
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, cheap));
        Assert.Contains(("auto_approved", "manager"), await TrackingAsync(t["sara"].Client, cheap));
        Assert.DoesNotContain(await TrackingAsync(t["sara"].Client, cheap), e => e.Event == "skipped");
        Assert.DoesNotContain(cheap, await InboxAsync(t["mona"].Client));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            $"select count(*) from audit.audit_event where action = 'request.auto_approved' and target_id = '{cheap}'"));

        var many = await SubmitAsync(t["sara"].Client, service, 5);
        await DispatchAsync();
        Assert.Equal("in_approval", await StatusAsync(t["sara"].Client, many));
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, many), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, many));
        Assert.DoesNotContain(await TrackingAsync(t["sara"].Client, many), e => e.Event == "auto_approved");
    }

    [Fact]
    public async Task An_auto_approved_step_in_a_parallel_path_lets_the_join_go_on_when_the_other_path_is_done()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, new
        {
            graph = new
            {
                steps = new[]
                {
                    Step("split", "split"),
                    Step("manager", "approval", new { assignee = new { kind = "line_manager" }, autoApprove = Cheap }),
                    Step("print", "task", new { assignee = new { kind = "users", users = new[] { t["lina"].Id } } }),
                    Step("join", "join"),
                    Step("done", "end", new { result = "approved" }),
                },
                links = new[] { Link("split", "manager"), Link("split", "print"), Link("manager", "join"), Link("print", "join"), Link("join", "done") },
            },
        });
        var request = await SubmitAsync(t["sara"].Client, service, 1);
        await DispatchAsync();
        Assert.Equal("in_approval", await StatusAsync(t["sara"].Client, request));
        Assert.DoesNotContain(request, await InboxAsync(t["mona"].Client));
        await DecideAsync(t["lina"].Client, await TaskOfAsync(t["lina"].Client, request), "approve");
        Assert.Equal("approved", await StatusAsync(t["sara"].Client, request));
    }

    [Fact]
    public async Task Rules_need_workflow_automation_an_approval_step_and_a_tenant_administrator_on_confidential_services()
    {
        var t = await FreshAsync();
        var route = new
        {
            graph = new
            {
                steps = new[] { Step("manager", "approval", new { assignee = new { kind = "line_manager" }, autoApprove = Cheap }), Step("done", "end") },
                links = new[] { Link("manager", "done") },
            },
        };
        var confidential = await CreateAsync(t, "confidential", t.Units["Dubai"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(t["kofi"].Client, confidential, route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DraftAsync(t["admin"].Client, confidential, route)).StatusCode);
        var internalService = await CreateAsync(t, "internal", t.Units["Dubai"]);
        Assert.Equal(HttpStatusCode.OK, (await DraftAsync(t["kofi"].Client, internalService, route)).StatusCode);

        foreach (var (bad, key) in new (object, string)[]
        {
            (new { graph = new { steps = new[] { Step("t", "task", new { assignee = new { kind = "line_manager" }, autoApprove = Cheap }), Step("done", "end") }, links = new[] { Link("t", "done") } } },
                "validation.services.route_condition"),
            (new { graph = new { steps = new[] { Step("manager", "approval", new { assignee = new { kind = "line_manager" }, autoApprove = new[] { new { field = "colour", @operator = "eq", value = "red" } } }), Step("done", "end") },
                links = new[] { Link("manager", "done") } } }, "validation.services.route_condition"),
            (new { serviceLevelDays = 0 }, "validation.services.service_level"),
            (new { serviceLevelDays = 91 }, "validation.services.service_level"),
        })
        {
            var response = await DraftAsync(t["admin"].Client, internalService, bad);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
            Assert.Contains(key, text);
        }

        var basic = await FreshAsync("workplace");
        var plain = await CreateAsync(basic);
        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(basic["admin"].Client, plain, route)).StatusCode);
    }

    [Fact]
    public async Task A_service_level_target_goes_at_risk_then_breached_once_each_and_a_request_closed_in_time_has_met_it()
    {
        var t = await FreshAsync();
        var service = await ServiceAsync(t, new
        {
            serviceLevelDays = 3,
            graph = new
            {
                steps = new[] { Step("manager", "approval", new { assignee = new { kind = "line_manager" } }), Step("done", "end", new { result = "approved" }) },
                links = new[] { Link("manager", "done") },
            },
        });
        var request = await SubmitAsync(t["sara"].Client, service, 1);
        var level = (await DetailAsync(t["sara"].Client, request)).GetProperty("serviceLevel");
        Assert.Equal("on_track", level.GetProperty("state").GetString());
        Assert.True(level.GetProperty("dueAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(2));
        Assert.Equal(0, await CheckAsync(t));

        // Past 75% of the target: at risk, once; the manager the step waits for and the tenant administrator are told.
        await world.Database.OwnerExecInTenantAsync(t.Id, $"update services.request set at_risk_at = now() - interval '1 minute' where id = '{request}'");
        Assert.Equal(1, await CheckAsync(t));
        Assert.Equal(0, await CheckAsync(t));
        Assert.Equal("at_risk", (await DetailAsync(t["sara"].Client, request)).GetProperty("serviceLevel").GetProperty("state").GetString());
        await DispatchAsync();
        foreach (var person in new[] { "mona", "admin" })
        {
            Assert.Contains(await NotificationsAsync(t[person].Client), n => n.GetProperty("link").GetString() == $"/requests/{request}"
                && n.GetProperty("title").GetString()!.Contains("at risk", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(await NotificationsAsync(t["sara"].Client), n => n.GetProperty("title").GetString()!.Contains("at risk", StringComparison.Ordinal));

        // Past its due time: breached, once, counted for analytics without the person, and audited.
        await world.Database.OwnerExecInTenantAsync(t.Id, $"update services.request set due_at = now() - interval '1 minute' where id = '{request}'");
        Assert.Equal(1, await CheckAsync(t));
        Assert.Equal(0, await CheckAsync(t));
        Assert.Equal(["sla_at_risk", "sla_breached"], (await TrackingAsync(t["sara"].Client, request)).Select(e => e.Event).Where(e => e.StartsWith("sla_", StringComparison.Ordinal)));
        Assert.Equal("breached", (await DetailAsync(t["sara"].Client, request)).GetProperty("serviceLevel").GetProperty("state").GetString());
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, "select count(*) from analytics.event where name = 'request.sla_breached' and user_key is null"));
        Assert.Equal(1L, await world.Database.TenantScalarAsync<long>(t.Id,
            $"select count(*) from audit.audit_event where action = 'request.sla_breached' and target_id = '{request}'"));

        // Closed in time: met. Another tenant's checks never see these requests.
        var quick = await SubmitAsync(t["sara"].Client, service, 1);
        await DecideAsync(t["mona"].Client, await TaskOfAsync(t["mona"].Client, quick), "approve");
        Assert.Equal("met", (await DetailAsync(t["sara"].Client, quick)).GetProperty("serviceLevel").GetProperty("state").GetString());
        var other = await FreshAsync();
        await world.Database.OwnerExecInTenantAsync(t.Id, $"update services.request set at_risk_at = now() - interval '1 minute', service_level = null where id = '{quick}'");
        Assert.Equal(0, await CheckAsync(other));
    }
}
