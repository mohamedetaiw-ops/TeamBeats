using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Runtime;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 017 T008/T009 and spec 016 US8 scenario 5: connector steps in service routes. Only mapped fields are sent, a
/// timeout on an idempotent action is retried with the same key, a declared business error follows its path, and an
/// unknown outcome stops the request with "automation failed" until the service owner retries, skips or cancels.
/// Package and tenant boundaries hold.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ServiceAutomationTests(TenantWorld world)
{
    private const string Admin = "/api/v1/services/admin";

    private FakeTicketing Ticketing => world.Factory.Ticketing;

    private static Dictionary<string, string> Names(string en, string ar) => new() { ["en"] = en, ["ar"] = ar };

    private async Task<TestTenant> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("sa-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var dubai = await world.CreateUnitAsync(t, "Dubai", holding, "department");
        foreach (var name in new[] { "sara", "omar" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
            await world.AddMembershipAsync(t, name, "Dubai");
        }
        await world.AssignAsync(t, "omar", BuiltInRoles.Approver, "org_subtree", dubai);
        return t;
    }

    private async Task<Guid> ConnectionAsync(TestTenant t)
    {
        var id = Guid.NewGuid();
        await world.Database.OwnerExecInTenantAsync(t.Id,
            """
            insert into integrations.connection (tenant_id, id, kind, display_name, status, enabled, rate_limit_per_minute)
            values (@t, @id, 'test_ticketing', 'Service desk', 'healthy', true, 600)
            """, ("t", t.Id), ("id", id));
        return id;
    }

    private static object Form() => new
    {
        fields = new object[]
        {
            new { key = "device", type = "text", labels = Names("Device", "الجهاز"), required = true },
            new { key = "category", type = "text", labels = Names("Category", "الفئة"), required = true },
            new { key = "reason", type = "text", labels = Names("Private reason", "سبب خاص") },
        },
    };

    private static object Step(Guid connection, string action = "create_incident_keyed", Dictionary<string, string>? inputs = null,
        Dictionary<string, string>? onBusinessError = null) => new
    {
        names = Names("Open the IT ticket", "فتح تذكرة تقنية المعلومات"),
        connectionId = connection,
        action,
        inputs = inputs ?? new Dictionary<string, string> { ["summary"] = "form.device", ["category"] = "form.category", ["requester"] = "requester.email" },
        onBusinessError,
    };

    private static object Route(Guid? approver, params object[] actions) => new
    {
        approval = approver is { } a ? new { names = Names("Manager approval", "موافقة المدير"), approvers = new { kind = "users", users = new[] { a } } } : null,
        actions,
    };

    private static async Task<HttpResponseMessage> DraftAsync(TestTenant t, Guid service, object route) =>
        await t["admin"].Client.PutAsJsonAsync($"{Admin}/{service}/draft", new { form = Form(), route });

    private static async Task<Guid> CreateAsync(TestTenant t)
    {
        var created = await t["admin"].Client.PostAsJsonAsync(Admin, new { kind = "form", names = Names("New laptop", "حاسوب جديد") });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.ReadAsync<JsonElement>()).GetProperty("service").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> ServiceAsync(TestTenant t, object route)
    {
        var id = await CreateAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await DraftAsync(t, id, route)).StatusCode);
        var published = await t["admin"].Client.PostAsJsonAsync($"{Admin}/{id}/publish", new { });
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return id;
    }

    private static async Task<Guid> SubmitAsync(TestTenant t, Guid service, string category = "hardware")
    {
        var response = await t["sara"].Client.PostAsJsonAsync("/api/v1/requests",
            new { serviceId = service, data = new { device = "Laptop 14 inch", category, reason = "Medical, do not share" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.ReadAsync<JsonElement>()).GetProperty("request").GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, Guid request) =>
        await (await client.GetAsync($"/api/v1/requests/{request}")).ReadAsync<JsonElement>();

    private static string Status(JsonElement detail) => detail.GetProperty("request").GetProperty("status").GetString()!;

    private static string[] Events(JsonElement detail) => detail.GetProperty("tracking").EnumerateArray().Select(e => e.GetProperty("event").GetString()!).ToArray();

    private static async Task ApproveAsync(TestTenant t)
    {
        var inbox = await (await t["omar"].Client.GetAsync("/api/v1/tasks")).ReadAsync<JsonElement>();
        var task = inbox.EnumerateArray().Single().GetProperty("taskId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await t["omar"].Client.PostAsJsonAsync($"/api/v1/tasks/{task}/decision", new { outcome = "approve" })).StatusCode);
    }

    /// <summary>Runs due connector jobs and delivers the events they cause, a few rounds, as the background workers would.</summary>
    private async Task DriveAsync(TestTenant t, int rounds = 3)
    {
        var dispatcher = world.Factory.Services.GetRequiredService<OutboxDispatcher>();
        for (var i = 0; i < rounds; i++)
        {
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
            await world.Factory.InScopeAsync(t.Id, async sp =>
            {
                var n = await sp.GetRequiredService<ConnectorJobs>().RunDueAsync(DateTimeOffset.UtcNow.AddHours(1 + i), 50, CancellationToken.None);
                await sp.GetRequiredService<IDbSession>().CommitAsync();
                return n;
            });
            await dispatcher.DispatchBatchAsync(500, CancellationToken.None);
        }
    }

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client, Guid request, Guid incident, string resolution, string? reason = "Checked with IT") =>
        client.PostAsJsonAsync($"/api/v1/requests/{request}/automation/incidents/{incident}:resolve", new { resolution, reason });

    private static Guid OpenIncident(JsonElement detail) =>
        detail.GetProperty("automation").GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("incidentId"))
            .Single(i => i.ValueKind == JsonValueKind.String).GetGuid();

    [Fact]
    public async Task After_approval_the_step_sends_only_the_mapped_fields_and_the_request_completes_with_the_ticket_number()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        var service = await ServiceAsync(t, Route(t["omar"].Id, Step(c)));
        var request = await SubmitAsync(t, service);
        Assert.Empty(Ticketing.CallsFor(c));

        await ApproveAsync(t);
        // Approved with an automatic step left: the requester can no longer cancel (spec 016 section 6).
        Assert.Equal("approved", Status(await DetailAsync(t["sara"].Client, request)));
        Assert.Equal(HttpStatusCode.Conflict, (await t["sara"].Client.PostAsync($"/api/v1/requests/{request}/cancel", null)).StatusCode);

        await DriveAsync(t);
        var call = Assert.Single(Ticketing.CallsFor(c));
        Assert.Equal("create_incident_keyed", call.Action);
        using var input = JsonDocument.Parse(call.Input);
        Assert.Equal(["category", "requester", "summary"], input.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("Laptop 14 inch", input.RootElement.GetProperty("summary").GetString());
        Assert.Equal(t["sara"].Email, input.RootElement.GetProperty("requester").GetString());
        Assert.DoesNotContain("Medical", call.Input);

        var detail = await DetailAsync(t["sara"].Client, request);
        Assert.Equal("completed", Status(detail));
        Assert.Contains("automation_done", Events(detail));
        var step = detail.GetProperty("automation").GetProperty("steps").EnumerateArray().Single();
        Assert.Equal("succeeded", step.GetProperty("status").GetString());
        Assert.StartsWith("INC", step.GetProperty("externalReference").GetString());
        Assert.False(detail.GetProperty("automation").GetProperty("canOperate").GetBoolean());
    }

    [Fact]
    public async Task A_timeout_on_an_idempotent_action_is_retried_with_the_same_key_and_acts_once()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));
        var service = await ServiceAsync(t, Route(null, Step(c)));
        var request = await SubmitAsync(t, service);

        await DriveAsync(t, 4);
        var calls = Ticketing.CallsFor(c);
        Assert.Equal(2, calls.Count);
        Assert.Single(calls.Select(x => x.IdempotencyKey).Distinct());
        Assert.Equal("completed", Status(await DetailAsync(t["sara"].Client, request)));
    }

    [Fact]
    public async Task A_declared_business_error_follows_its_path_to_reject_or_continue()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        var rejecting = await ServiceAsync(t, Route(null, Step(c, onBusinessError: new() { ["invalid_category"] = "reject" })));
        var continuing = await ServiceAsync(t, Route(null, Step(c, onBusinessError: new() { ["invalid_category"] = "continue" })));

        Ticketing.Script(c, PackCallResult.Business("invalid_category"));
        var first = await SubmitAsync(t, rejecting, "toys");
        await DriveAsync(t);
        Ticketing.Script(c, PackCallResult.Business("invalid_category"));
        var second = await SubmitAsync(t, continuing, "toys");
        await DriveAsync(t);

        var rejected = await DetailAsync(t["sara"].Client, first);
        Assert.Equal("rejected", Status(rejected));
        Assert.Equal("business_error", rejected.GetProperty("automation").GetProperty("steps")[0].GetProperty("status").GetString());
        Assert.Equal("completed", Status(await DetailAsync(t["sara"].Client, second)));
        // A business error with a path is not retried.
        Assert.Equal(2, Ticketing.CallsFor(c).Count);
    }

    [Fact]
    public async Task An_unknown_outcome_pauses_the_request_until_the_owner_retries_skips_or_cancels()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        var service = await ServiceAsync(t, Route(null, Step(c, action: "create_incident")));

        // Retry: the same key runs again and the request completes.
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));
        var retried = await SubmitAsync(t, service);
        await DriveAsync(t);
        var paused = await DetailAsync(t["sara"].Client, retried);
        Assert.Equal("submitted", Status(paused));
        Assert.Contains("automation_failed", Events(paused));
        var step = paused.GetProperty("automation").GetProperty("steps")[0];
        Assert.Equal("incident", step.GetProperty("status").GetString());
        Assert.Equal("outcome_unknown", step.GetProperty("errorClass").GetString());
        var incident = OpenIncident(paused);

        Assert.Equal(HttpStatusCode.Forbidden, (await ResolveAsync(t["sara"].Client, retried, incident, "retried")).StatusCode);
        var noReason = await ResolveAsync(t["admin"].Client, retried, incident, "retried", reason: " ");
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        Assert.True((await DetailAsync(t["admin"].Client, retried)).GetProperty("automation").GetProperty("canOperate").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(t["admin"].Client, retried, incident, "retried")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await ResolveAsync(t["admin"].Client, retried, incident, "skipped")).StatusCode);
        await DriveAsync(t);
        var done = await DetailAsync(t["sara"].Client, retried);
        Assert.Equal("completed", Status(done));
        Assert.Contains("automation_retried", Events(done));
        var keys = Ticketing.CallsFor(c).Select(x => x.IdempotencyKey).Distinct().ToList();
        Assert.Single(keys);

        // Skip: the request moves on without the step.
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));
        var skipped = await SubmitAsync(t, service);
        await DriveAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(t["admin"].Client, skipped, OpenIncident(await DetailAsync(t["admin"].Client, skipped)), "skipped")).StatusCode);
        await DriveAsync(t);
        var afterSkip = await DetailAsync(t["sara"].Client, skipped);
        Assert.Equal("completed", Status(afterSkip));
        Assert.Equal("skipped", afterSkip.GetProperty("automation").GetProperty("steps")[0].GetProperty("status").GetString());

        // Cancel: the request ends as cancelled.
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));
        var cancelled = await SubmitAsync(t, service);
        await DriveAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(t["admin"].Client, cancelled, OpenIncident(await DetailAsync(t["admin"].Client, cancelled)), "cancelled")).StatusCode);
        await DriveAsync(t);
        Assert.Equal("cancelled", Status(await DetailAsync(t["sara"].Client, cancelled)));

        // The incident of one request is not reachable through another.
        Assert.Equal(HttpStatusCode.NotFound, (await ResolveAsync(t["admin"].Client, skipped, incident, "skipped")).StatusCode);
    }

    [Fact]
    public async Task Publishing_checks_each_step_against_the_actions_declared_fields_and_errors()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        var service = await CreateAsync(t);

        async Task<string[]> PublishErrorsAsync(object step)
        {
            Assert.Equal(HttpStatusCode.OK, (await DraftAsync(t, service, Route(null, step))).StatusCode);
            var response = await t["admin"].Client.PostAsJsonAsync($"{Admin}/{service}/publish", new { });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
            return errors.EnumerateObject().SelectMany(p => p.Value.EnumerateArray().Select(e => e.GetProperty("key").GetString()!)).ToArray();
        }

        Assert.Contains("validation.workflow.action_input_undeclared", await PublishErrorsAsync(Step(c, inputs: new()
        {
            ["summary"] = "form.device", ["category"] = "form.category", ["password"] = "form.reason",
        })));
        Assert.Contains("validation.workflow.action_input_missing", await PublishErrorsAsync(Step(c, inputs: new() { ["summary"] = "form.device" })));
        Assert.Contains("validation.workflow.action_error_unknown", await PublishErrorsAsync(Step(c, onBusinessError: new() { ["out_of_stock"] = "continue" })));
        Assert.Contains("validation.workflow.action_unknown", await PublishErrorsAsync(Step(c, action: "delete_everything")));
        Assert.Contains("validation.workflow.action_unknown", await PublishErrorsAsync(Step(Guid.NewGuid())));
    }

    [Fact]
    public async Task Automatic_steps_need_the_package_and_another_tenants_connection_or_request_is_not_found()
    {
        var basic = await FreshAsync("workplace");
        var basicConnection = await ConnectionAsync(basic);
        var basicService = await CreateAsync(basic);
        Assert.Equal(HttpStatusCode.Forbidden, (await DraftAsync(basic, basicService, Route(null, Step(basicConnection)))).StatusCode);

        var t = await FreshAsync();
        var other = await FreshAsync();
        var otherConnection = await ConnectionAsync(other);
        var service = await CreateAsync(t);
        Assert.Equal(HttpStatusCode.OK, (await DraftAsync(t, service, Route(null, Step(otherConnection)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await t["admin"].Client.PostAsJsonAsync($"{Admin}/{service}/publish", new { })).StatusCode);

        var c = await ConnectionAsync(t);
        Ticketing.Script(c, PackCallResult.Technical(ConnectorErrorClasses.NotResponding, mayHaveActed: true));
        var live = await ServiceAsync(t, Route(null, Step(c, action: "create_incident")));
        var request = await SubmitAsync(t, live);
        await DriveAsync(t);
        var incident = OpenIncident(await DetailAsync(t["admin"].Client, request));
        Assert.Equal(HttpStatusCode.NotFound, (await ResolveAsync(other["admin"].Client, request, incident, "skipped")).StatusCode);
        Assert.Empty(Ticketing.CallsFor(otherConnection));
    }

    [Fact]
    public async Task The_designer_lists_actions_that_declare_their_fields_only_to_owners_and_only_with_the_package()
    {
        var t = await FreshAsync();
        var c = await ConnectionAsync(t);
        var view = await (await t["admin"].Client.GetAsync($"{Admin}/connector-actions")).ReadAsync<JsonElement>();
        Assert.True(view.GetProperty("available").GetBoolean());
        var mine = view.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("connectionId").GetGuid() == c).ToList();
        Assert.Equal(["create_incident", "create_incident_keyed", "get_status"], mine.Select(a => a.GetProperty("action").GetString()!).Order().ToArray());
        var create = mine.Single(a => a.GetProperty("action").GetString() == "create_incident");
        Assert.Equal(["category", "requester", "summary"], create.GetProperty("inputs").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).Order().ToArray());
        Assert.DoesNotContain("settings", view.GetRawText());
        Assert.Equal(HttpStatusCode.Forbidden, (await t["sara"].Client.GetAsync($"{Admin}/connector-actions")).StatusCode);

        var basic = await FreshAsync("workplace");
        var none = await (await basic["admin"].Client.GetAsync($"{Admin}/connector-actions")).ReadAsync<JsonElement>();
        Assert.False(none.GetProperty("available").GetBoolean());
        Assert.Empty(none.GetProperty("actions").EnumerateArray());
    }
}
