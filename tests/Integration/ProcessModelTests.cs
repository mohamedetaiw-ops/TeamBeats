using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Webhooks;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 024 R2 T009, T011 and T013: process models are saved as drafts with their problems, published as immutable
/// versions, and run as graph instances on the engine: user tasks in the inbox, gateways, call activities, messages with
/// a buffer, timers and incidents an operator resolves. Owners, operators, package and tenant boundaries hold.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ProcessModelTests(TenantWorld world)
{
    private const string Api = "/api/v1/workflow/processes";

    private sealed record Org(TestTenant T, Guid Gulf, Guid Africa);

    private async Task<Org> FreshAsync(string package = "workplace_plus")
    {
        var t = await world.CreateTenantAsync("pm-" + Guid.NewGuid().ToString("N")[..8], package);
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "owner", "operator", "amal", "approver", "employee", "gulf_owner" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "owner", BuiltInRoles.WorkflowDesigner, "tenant", null);
        await world.AssignAsync(t, "gulf_owner", BuiltInRoles.WorkflowDesigner, "org_unit", gulf);
        await world.AssignAsync(t, "operator", BuiltInRoles.ProcessOperator, "tenant", null);
        await world.AssignAsync(t, "approver", BuiltInRoles.Approver, "org_unit", gulf);
        return new Org(t, gulf, africa);
    }

    private static string Model(string body, string definitions = "", string name = "Purchase") => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          {definitions}
          <bpmn:process id="p" name="{name}">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string Flow(string id, string from, string to, string? condition = null) => condition is null
        ? $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"/>"""
        : $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"><bpmn:conditionExpression>{System.Security.SecurityElement.Escape(condition)}</bpmn:conditionExpression></bpmn:sequenceFlow>""";

    private static string UserTask(string id, string assignment, string name = "Review", string extra = "") =>
        $"""<bpmn:userTask id="{id}" name="{name}"><bpmn:extensionElements><tb:assignment {assignment}/><tb:name xml:lang="ar">مراجعة</tb:name></bpmn:extensionElements>{extra}</bpmn:userTask>""";

    /// <summary>Review by Amal; approved purchases go to an approver at the instance's unit, rejected ones end.</summary>
    private static string Purchase(TestTenant t) => Model($"""
        <bpmn:startEvent id="s"/>
        {UserTask("review", $"users=\"{t["amal"].Id}\"")}
        <bpmn:exclusiveGateway id="x" default="f4"/>
        {UserTask("approve", "permission=\"workflow.approve\"", "Approve")}
        <bpmn:endEvent id="done"/><bpmn:endEvent id="rejected"/>
        {Flow("f1", "s", "review")}{Flow("f2", "review", "x")}{Flow("f3", "x", "approve", "outcome = \"approve\"")}{Flow("f4", "x", "rejected")}{Flow("f5", "approve", "done")}
        """);

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string> ProblemKeyAsync(Task<HttpResponseMessage> call, HttpStatusCode expected)
    {
        var problem = await OkAsync(call, expected);
        return problem.TryGetProperty("errors", out var errors)
            ? errors.EnumerateObject().First().Value[0].GetProperty("key").GetString()!
            : problem.GetProperty("detailKey").GetString()!;
    }

    private static Task<JsonElement> SaveAsync(TestTenant t, string user, string key, string xml, Guid? owner = null) =>
        OkAsync(t[user].Client.PutAsJsonAsync($"{Api}/{key}", new { bpmnXml = xml, ownerOrgUnitId = owner }));

    private static Task<JsonElement> PublishAsync(TestTenant t, string user, string key) => OkAsync(t[user].Client.PostAsync($"{Api}/{key}:publish", null));

    private static async Task<Guid> StartAsync(TestTenant t, string key, object? variables = null, Guid? unit = null, string? businessKey = null) =>
        (await OkAsync(t["operator"].Client.PostAsJsonAsync($"{Api}/{key}/instances", new { businessKey, orgUnitId = unit, variables }), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();

    private Task<T> AsAsync<T>(TestTenant t, string? user, Func<IServiceProvider, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            if (user is not null)
            {
                sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.User, t[user].Id);
            }
            var result = await action(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private Task<IReadOnlyList<UserTaskInfo>> TasksAsync(TestTenant t, string user) => AsAsync(t, user, sp => sp.GetRequiredService<IProcessEngine>().MyOpenTasksAsync());

    private Task<UserTaskInfo> CompleteAsync(TestTenant t, string user, Guid task, string outcome = TaskOutcomes.Approve) =>
        AsAsync(t, user, sp => sp.GetRequiredService<IProcessEngine>().CompleteTaskAsync(task, outcome, null));

    private Task<ProcessInstanceInfo?> InstanceAsync(TestTenant t, Guid id) => AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().GetInstanceAsync(id));

    private Task<MessageResult> MessageAsync(TestTenant t, string name, string? key, string id, object? payload = null) =>
        AsAsync(t, null, sp => sp.GetRequiredService<ProcessEngine>().CorrelateAsync(name, key, id,
            payload is null ? null : JsonSerializer.SerializeToNode(payload) as JsonObject, "event", null));

    private async Task<string> VariablesAsync(TestTenant t, Guid instance) =>
        JsonNode.Parse(await world.Database.ScalarInTenantAsync<string>(t.Id, $"select variables::text from workflow.process_instance where id = '{instance}'") ?? "{}")!.ToJsonString();

    [Fact]
    public async Task A_draft_keeps_its_problems_publishing_makes_immutable_versions_and_owners_act_in_their_scope()
    {
        var o = await FreshAsync();
        var t = o.T;

        // A draft with problems is saved and lists them by element; publishing it is refused.
        var broken = Model($"""<bpmn:startEvent id="s"/>{UserTask("review", "")}<bpmn:endEvent id="e"/>{Flow("f1", "s", "review")}{Flow("f2", "review", "e")}""");
        var draft = await SaveAsync(t, "owner", "purchase", broken);
        Assert.True(draft.GetProperty("problems").EnumerateArray().Any(p => p.GetProperty("elementId").GetString() == "review"
            && p.GetProperty("key").GetString() == "validation.process.assignment"), draft.ToString());
        Assert.Equal("validation.process.assignment", await ProblemKeyAsync(t["owner"].Client.PostAsync($"{Api}/purchase:publish", null), HttpStatusCode.BadRequest));

        // A call to a process that is not published is a publish problem.
        await SaveAsync(t, "owner", "caller", Model($"""
            <bpmn:startEvent id="s"/><bpmn:callActivity id="c" calledElement="nowhere"/><bpmn:endEvent id="e"/>{Flow("f1", "s", "c")}{Flow("f2", "c", "e")}
            """));
        Assert.Equal("validation.process.called_missing", await ProblemKeyAsync(t["owner"].Client.PostAsync($"{Api}/caller:publish", null), HttpStatusCode.BadRequest));
        var caller = await OkAsync(t["owner"].Client.GetAsync($"{Api}/caller"));
        Assert.Contains(caller.GetProperty("problems").EnumerateArray(), p => p.GetProperty("key").GetString() == "validation.process.called_missing");

        // Fixed, it publishes version 1; the same model again stays version 1; a change makes version 2.
        await SaveAsync(t, "owner", "purchase", Purchase(t));
        var v1 = await PublishAsync(t, "owner", "purchase");
        Assert.Equal(1, v1.GetProperty("versions")[0].GetProperty("version").GetInt32());
        Assert.Equal(0, v1.GetProperty("problems").GetArrayLength());
        Assert.Equal(1, (await PublishAsync(t, "owner", "purchase")).GetProperty("versions").GetArrayLength());
        await SaveAsync(t, "owner", "purchase", Purchase(t).Replace("name=\"Purchase\"", "name=\"Purchase request\""));
        var v2 = await PublishAsync(t, "owner", "purchase");
        Assert.Equal([2, 1], v2.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("version").GetInt32()));
        Assert.Equal("Purchase request", v2.GetProperty("names").GetProperty("en").GetString());
        var xml = await (await t["owner"].Client.GetAsync($"{Api}/purchase/versions/1/bpmn")).Content.ReadAsStringAsync();
        Assert.Contains("name=\"Purchase\"", xml);
        Assert.Equal(2, await world.Database.AuditCountAsync(t.Id, AuditActions.WorkflowDefinitionPublished));
        Assert.True(await world.Database.AuditCountAsync(t.Id, AuditActions.ProcessModelSaved) >= 4);

        // A process owner of Gulf owns Gulf models only; employees do not see models; a bad key is refused.
        await SaveAsync(t, "gulf_owner", "gulf-travel", Purchase(t), o.Gulf);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gulf_owner"].Client.PutAsJsonAsync($"{Api}/africa-travel", new { bpmnXml = Purchase(t), ownerOrgUnitId = o.Africa })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await t["gulf_owner"].Client.PutAsJsonAsync($"{Api}/purchase", new { bpmnXml = Purchase(t), ownerOrgUnitId = o.Gulf })).StatusCode);
        var gulfList = await OkAsync(t["gulf_owner"].Client.GetAsync(Api));
        Assert.Equal(["gulf-travel"], gulfList.EnumerateArray().Select(m => m.GetProperty("key").GetString()));
        Assert.Equal(3, (await OkAsync(t["owner"].Client.GetAsync(Api))).GetArrayLength());
        Assert.Empty((await OkAsync(t["employee"].Client.GetAsync(Api))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await t["employee"].Client.GetAsync($"{Api}/purchase")).StatusCode);
        Assert.Equal("validation.process.key", await ProblemKeyAsync(t["owner"].Client.PutAsJsonAsync($"{Api}/Bad Key", new { bpmnXml = Purchase(t) }), HttpStatusCode.BadRequest));

        // Another tenant sees nothing of it, and Workplace has no process models.
        var other = await FreshAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.T["owner"].Client.GetAsync($"{Api}/purchase")).StatusCode);
        Assert.Empty((await OkAsync(other.T["owner"].Client.GetAsync(Api))).EnumerateArray());
        var basic = await FreshAsync("workplace");
        var refused = await basic.T["owner"].Client.PutAsJsonAsync($"{Api}/purchase", new { bpmnXml = Purchase(basic.T) });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("entitlement.not_in_package", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_instance_moves_through_user_tasks_and_an_exclusive_gateway_with_candidates_at_its_unit()
    {
        var o = await FreshAsync();
        var t = o.T;
        await SaveAsync(t, "owner", "purchase", Purchase(t));
        await PublishAsync(t, "owner", "purchase");

        // Only process operators start instances from the API.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PostAsJsonAsync($"{Api}/purchase/instances", new { })).StatusCode);
        var approved = await StartAsync(t, "purchase", new { amount = 1200 }, o.Gulf, "PO-1");
        var review = Assert.Single(await TasksAsync(t, "amal"));
        Assert.Equal(("review", "Review"), (review.ElementId, review.Name));
        Assert.Equal(ProcessEngine.ModelPrefix + "purchase", review.DefinitionKey);
        Assert.Empty(await TasksAsync(t, "approver"));

        // The inbox lists it with its names per locale.
        var inbox = await OkAsync(t["amal"].Client.GetAsync("/api/v1/tasks"));
        var item = inbox.EnumerateArray().Single(i => i.GetProperty("taskId").GetGuid() == review.Id);
        Assert.Equal("مراجعة", item.GetProperty("title").GetProperty("ar").GetString());

        await CompleteAsync(t, "amal", review.Id);
        var approve = Assert.Single(await TasksAsync(t, "approver"));
        Assert.Equal("approve", approve.ElementId);
        Assert.Equal(HttpStatusCode.Forbidden, (await AssertDeniedAsync(t, "employee", approve.Id)));
        await CompleteAsync(t, "approver", approve.Id);
        var done = await InstanceAsync(t, approved);
        Assert.Equal(("completed", "completed"), (done!.Status, done.Outcome));
        Assert.Contains("\"tasks\"", await VariablesAsync(t, approved));
        Assert.Contains("\"amount\":1200", await VariablesAsync(t, approved));

        // Rejected at review, it ends at the default flow; an approver of another unit never sees Africa's task.
        var rejected = await StartAsync(t, "purchase", null, o.Gulf, "PO-2");
        await CompleteAsync(t, "amal", (await TasksAsync(t, "amal")).Single().Id, TaskOutcomes.Reject);
        Assert.Equal("completed", (await InstanceAsync(t, rejected))!.Status);
        var history = await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().HistoryAsync(rejected));
        Assert.Contains(history, h => h.ElementId == "rejected");
        Assert.DoesNotContain(history, h => h.ElementId == "approve");

        var africa = await StartAsync(t, "purchase", null, o.Africa, "PO-3");
        await CompleteAsync(t, "amal", (await TasksAsync(t, "amal")).Single().Id);
        Assert.Empty(await TasksAsync(t, "approver"));
        Assert.Equal("active", (await InstanceAsync(t, africa))!.Status);

        // The same business key returns the same instance; another tenant cannot complete or read it.
        Assert.Equal(africa, await StartAsync(t, "purchase", null, o.Africa, "PO-3"));
        var other = await FreshAsync();
        Assert.Null(await InstanceAsync(other.T, africa));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ProcessInstanceStarted, approved.ToString()));
    }

    private async Task<HttpStatusCode> AssertDeniedAsync(TestTenant t, string user, Guid task)
    {
        try
        {
            await CompleteAsync(t, user, task);
            return HttpStatusCode.OK;
        }
        catch (TeamBeats.Primitives.AppException e)
        {
            return (HttpStatusCode)e.Status;
        }
    }

    [Fact]
    public async Task Parallel_branches_join_and_a_call_activity_waits_for_its_child_and_passes_variables_back()
    {
        var o = await FreshAsync();
        var t = o.T;
        await SaveAsync(t, "owner", "check", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:userTask id="verify" name="Verify"><bpmn:extensionElements><tb:assignment users="{t["approver"].Id}"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="e"/>{Flow("f1", "s", "verify")}{Flow("f2", "verify", "e")}
            """, name: "Check"));
        Assert.Equal(HttpStatusCode.NotFound, (await t["owner"].Client.PostAsync($"{Api}/missing:publish", null)).StatusCode);
        await PublishAsync(t, "owner", "check");
        await SaveAsync(t, "owner", "onboard", Model($"""
            <bpmn:startEvent id="s"/><bpmn:parallelGateway id="split"/>
            {UserTask("laptop", $"users=\"{t["amal"].Id}\"", "Laptop")}
            <bpmn:callActivity id="vetting" calledElement="check"><bpmn:extensionElements><tb:output variable="vetted" source="result.tasks.verify.outcome"/></bpmn:extensionElements></bpmn:callActivity>
            <bpmn:parallelGateway id="join"/><bpmn:endEvent id="e"/>
            {Flow("f1", "s", "split")}{Flow("f2", "split", "laptop")}{Flow("f3", "split", "vetting")}{Flow("f4", "laptop", "join")}{Flow("f5", "vetting", "join")}{Flow("f6", "join", "e")}
            """, name: "Onboarding"));
        await PublishAsync(t, "owner", "onboard");

        var parent = await StartAsync(t, "onboard", new { person = "Sara" }, o.Gulf);
        var child = await world.Database.ScalarInTenantAsync<Guid>(t.Id, $"select id from workflow.process_instance where parent_instance_id = '{parent}'");
        Assert.Contains("\"person\":\"Sara\"", await VariablesAsync(t, child));
        await CompleteAsync(t, "amal", (await TasksAsync(t, "amal")).Single().Id);
        Assert.Equal("active", (await InstanceAsync(t, parent))!.Status);

        await CompleteAsync(t, "approver", (await TasksAsync(t, "approver")).Single().Id);
        Assert.Equal("completed", (await InstanceAsync(t, child))!.Status);
        Assert.Equal("completed", (await InstanceAsync(t, parent))!.Status);
        Assert.Contains("\"vetted\":\"approve\"", await VariablesAsync(t, parent));

        // Withdrawing a parent ends its running child with it.
        var second = await StartAsync(t, "onboard", null, o.Gulf);
        var secondChild = await world.Database.ScalarInTenantAsync<Guid>(t.Id, $"select id from workflow.process_instance where parent_instance_id = '{second}'");
        await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().WithdrawAsync(second));
        Assert.Equal("cancelled", (await InstanceAsync(t, secondChild))!.Status);
        Assert.Empty(await TasksAsync(t, "approver"));
        Assert.Empty(await TasksAsync(t, "amal"));
    }

    [Fact]
    public async Task Messages_correlate_by_name_and_key_once_and_wait_in_the_buffer_when_they_come_first()
    {
        var o = await FreshAsync();
        var t = o.T;
        await SaveAsync(t, "owner", "delivery", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:intermediateCatchEvent id="arrived"><bpmn:extensionElements><tb:correlation key="po"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:intermediateCatchEvent>
            {UserTask("check", $"users=\"{t["amal"].Id}\"", "Check delivery")}<bpmn:endEvent id="e"/>
            {Flow("f1", "s", "arrived")}{Flow("f2", "arrived", "check")}{Flow("f3", "check", "e")}
            """, """<bpmn:message id="m" name="delivery_arrived"/>""", "Delivery"));
        await PublishAsync(t, "owner", "delivery");

        var waiting = await StartAsync(t, "delivery", new { po = "PO-7" }, o.Gulf);
        Assert.Empty(await TasksAsync(t, "amal"));
        Assert.Equal(MessageResults.Buffered, (await MessageAsync(t, "delivery_arrived", "PO-8", "msg-1", new { boxes = 2 })).Result);
        var hit = await MessageAsync(t, "delivery_arrived", "PO-7", "msg-2", new { boxes = 3 });
        Assert.Equal((MessageResults.Correlated, waiting), (hit.Result, hit.InstanceId!.Value));
        Assert.Equal(MessageResults.Duplicate, (await MessageAsync(t, "delivery_arrived", "PO-7", "msg-2")).Result);
        Assert.Single(await TasksAsync(t, "amal"));
        Assert.Contains("\"boxes\":3", await VariablesAsync(t, waiting));

        // The message for PO-8 came first and is taken when that instance reaches the catch event.
        var late = await StartAsync(t, "delivery", new { po = "PO-8" }, o.Gulf);
        Assert.Equal(2, (await TasksAsync(t, "amal")).Count);
        Assert.Contains("\"boxes\":2", await VariablesAsync(t, late));

        // A message of the same name and key in another tenant reaches nothing here.
        var other = await FreshAsync();
        var third = await StartAsync(t, "delivery", new { po = "PO-9" }, o.Gulf);
        Assert.Equal(MessageResults.Buffered, (await MessageAsync(other.T, "delivery_arrived", "PO-9", "msg-3")).Result);
        Assert.Equal(2, (await TasksAsync(t, "amal")).Count);
        Assert.Equal("active", (await InstanceAsync(t, third))!.Status);
    }

    [Fact]
    public async Task A_timer_boundary_moves_an_overdue_task_on_and_a_task_with_nobody_to_do_it_raises_an_incident_an_operator_resolves()
    {
        var o = await FreshAsync();
        var t = o.T;
        await SaveAsync(t, "owner", "escalate", Model($"""
            <bpmn:startEvent id="s"/>
            {UserTask("review", $"users=\"{t["amal"].Id}\"")}
            <bpmn:boundaryEvent id="late" attachedToRef="review"><bpmn:timerEventDefinition><bpmn:timeDuration>PT2H</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:boundaryEvent>
            {UserTask("head", "assignee=\"headOfUnit\"", "Head review")}
            <bpmn:endEvent id="e"/><bpmn:endEvent id="e2"/>
            {Flow("f1", "s", "review")}{Flow("f2", "review", "e")}{Flow("f3", "late", "head")}{Flow("f4", "head", "e2")}
            """, name: "Escalation"));
        await PublishAsync(t, "owner", "escalate");

        var instance = await StartAsync(t, "escalate", new { headOfUnit = t["approver"].Id.ToString() }, o.Gulf);
        Assert.Single(await TasksAsync(t, "amal"));
        var engineJobs = (Func<DateTimeOffset, Task<int>>)(at => AsAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(at, 100)));
        Assert.Equal(0, await engineJobs(DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Equal(1, await engineJobs(DateTimeOffset.UtcNow.AddHours(3)));
        Assert.Empty(await TasksAsync(t, "amal"));
        var head = Assert.Single(await TasksAsync(t, "approver"));
        Assert.Equal("head", head.ElementId);
        await CompleteAsync(t, "approver", head.Id);
        Assert.Equal("completed", (await InstanceAsync(t, instance))!.Status);

        // Nobody resolves from the expression: an incident on the step. Skipping it with a reason moves the instance on.
        var stuck = await StartAsync(t, "escalate", new { headOfUnit = (string?)null }, o.Gulf);
        Assert.Equal(1, await engineJobs(DateTimeOffset.UtcNow.AddHours(3)));
        var incident = Assert.Single(await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().IncidentsAsync(stuck)));
        Assert.Equal(("head", "no_assignee", "open"), (incident.ElementId, incident.ErrorClass, incident.Status));
        Assert.Equal("Head review", incident.StepName);
        var resolved = await AsAsync(t, "operator", sp => sp.GetRequiredService<IProcessEngine>().ResolveIncidentAsync(incident.Id, IncidentResolutions.Skipped, "Handled by phone"));
        Assert.Equal(("resolved", IncidentResolutions.Skipped), (resolved.Status, resolved.Resolution));
        Assert.Equal("completed", (await InstanceAsync(t, stuck))!.Status);
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.ProcessIncidentSkipped, stuck.ToString()));
        await Assert.ThrowsAsync<TeamBeats.Primitives.AppException>(() =>
            AsAsync(t, "operator", sp => sp.GetRequiredService<IProcessEngine>().ResolveIncidentAsync(incident.Id, IncidentResolutions.Retried, "Again")));
    }

    [Fact]
    public async Task A_timer_start_event_starts_one_instance_per_cycle_of_the_current_version_and_a_message_start_starts_one_per_message()
    {
        var o = await FreshAsync();
        var t = o.T;
        await SaveAsync(t, "owner", "weekly", Model($"""
            <bpmn:startEvent id="tick"><bpmn:timerEventDefinition><bpmn:timeCycle>R2/PT24H</bpmn:timeCycle></bpmn:timerEventDefinition></bpmn:startEvent>
            {UserTask("report", $"users=\"{t["amal"].Id}\"", "Weekly report")}<bpmn:endEvent id="e"/>{Flow("f1", "tick", "report")}{Flow("f2", "report", "e")}
            """, name: "Weekly"));
        await PublishAsync(t, "owner", "weekly");
        Assert.Equal(HttpStatusCode.Conflict, (await t["operator"].Client.PostAsJsonAsync($"{Api}/weekly/instances", new { })).StatusCode);

        var run = (Func<DateTimeOffset, Task<int>>)(at => AsAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(at, 100)));
        // A missed tick fires once, and the next one is scheduled from now.
        Assert.Equal(1, await run(DateTimeOffset.UtcNow.AddDays(10)));
        Assert.Single(await TasksAsync(t, "amal"));
        Assert.Equal(0, await run(DateTimeOffset.UtcNow.AddHours(23)));
        Assert.Equal(1, await run(DateTimeOffset.UtcNow.AddDays(2)));
        Assert.Equal(2, (await TasksAsync(t, "amal")).Count);
        Assert.Equal(0, await run(DateTimeOffset.UtcNow.AddDays(30)));

        await SaveAsync(t, "owner", "intake", Model($"""
            <bpmn:startEvent id="s"><bpmn:messageEventDefinition messageRef="m"/></bpmn:startEvent>
            {UserTask("triage", $"users=\"{t["approver"].Id}\"", "Triage")}<bpmn:endEvent id="e"/>{Flow("f1", "s", "triage")}{Flow("f2", "triage", "e")}
            """, """<bpmn:message id="m" name="ticket_opened"/>""", "Intake"));
        await PublishAsync(t, "owner", "intake");
        var started = await MessageAsync(t, "ticket_opened", null, "ticket-1", new { subject = "Printer" });
        Assert.Equal(MessageResults.Started, started.Result);
        Assert.Equal(MessageResults.Duplicate, (await MessageAsync(t, "ticket_opened", null, "ticket-1")).Result);
        Assert.Single(await TasksAsync(t, "approver"));
        Assert.Contains("\"subject\":\"Printer\"", await VariablesAsync(t, started.InstanceId!.Value));
    }

    private static string Bands(string headRule = "> 1000") => $"""
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/" id="defs" name="Bands">
          <decision id="band" name="Approval band">
            <decisionTable id="table" hitPolicy="UNIQUE">
              <input id="amount" label="Amount"><inputExpression><text>amount</text></inputExpression></input>
              <output id="level" name="level"/>
              <rule id="r1"><inputEntry><text>&lt;= 1000</text></inputEntry><outputEntry><text>"manager"</text></outputEntry></rule>
              <rule id="r2"><inputEntry><text>{System.Security.SecurityElement.Escape(headRule)}</text></inputEntry><outputEntry><text>"head"</text></outputEntry></rule>
            </decisionTable>
          </decision>
        </definitions>
        """;

    [Fact]
    public async Task Decision_owners_test_and_publish_tables_and_a_business_rule_task_routes_on_the_current_version()
    {
        var o = await FreshAsync();
        var t = o.T;
        const string Decisions = "/api/v1/workflow/decisions";

        // An overlap under UNIQUE is a problem on the rule; test rows run against the draft.
        var draft = await OkAsync(t["owner"].Client.PutAsJsonAsync($"{Decisions}/band", new { dmnXml = Bands(">= 1000") }));
        Assert.Contains(draft.GetProperty("problems").EnumerateArray(), p => p.GetProperty("elementId").GetString() == "r2"
            && p.GetProperty("key").GetString() == "validation.decision.overlap");
        Assert.Equal("validation.decision.overlap", await ProblemKeyAsync(t["owner"].Client.PostAsync($"{Decisions}/band:publish", null), HttpStatusCode.BadRequest));
        await OkAsync(t["owner"].Client.PutAsJsonAsync($"{Decisions}/band", new { dmnXml = Bands() }));
        var tested = await OkAsync(t["owner"].Client.PostAsJsonAsync($"{Decisions}/band:test", new { rows = new object[] { new { amount = 500 }, new { amount = 5000 }, new { amount = (int?)null } } }));
        Assert.Equal(["manager", "head", null], tested.EnumerateArray().Select(r => r.GetProperty("result").ValueKind == JsonValueKind.Null ? null : r.GetProperty("result").GetString()));
        Assert.Equal([2], tested[1].GetProperty("rules").EnumerateArray().Select(r => r.GetInt32()));
        var published = await OkAsync(t["owner"].Client.PostAsync($"{Decisions}/band:publish", null));
        Assert.Equal(1, published.GetProperty("versions")[0].GetProperty("version").GetInt32());
        Assert.Equal(1, (await OkAsync(t["owner"].Client.PostAsync($"{Decisions}/band:publish", null))).GetProperty("versions").GetArrayLength());
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.DecisionPublished));

        // Employees and process operators do not edit decisions; another tenant does not see them.
        Assert.Equal(HttpStatusCode.Forbidden, (await t["employee"].Client.PutAsJsonAsync($"{Decisions}/other", new { dmnXml = Bands() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await t["operator"].Client.GetAsync($"{Decisions}/band")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await (await FreshAsync()).T["owner"].Client.GetAsync($"{Decisions}/band")).StatusCode);

        await SaveAsync(t, "owner", "spend", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:businessRuleTask id="decide"><bpmn:extensionElements><tb:decision key="band" resultVariable="level"/></bpmn:extensionElements></bpmn:businessRuleTask>
            <bpmn:exclusiveGateway id="x" default="f4"/>
            {UserTask("head", $"users=\"{t["approver"].Id}\"", "Head approval")}{UserTask("manager", $"users=\"{t["amal"].Id}\"", "Manager approval")}
            <bpmn:endEvent id="e"/><bpmn:endEvent id="e2"/>
            {Flow("f1", "s", "decide")}{Flow("f2", "decide", "x")}{Flow("f3", "x", "head", "level = \"head\"")}{Flow("f4", "x", "manager")}{Flow("f5", "head", "e")}{Flow("f6", "manager", "e2")}
            """, name: "Spend"));
        await PublishAsync(t, "owner", "spend");
        var big = await StartAsync(t, "spend", new { amount = 5000 }, o.Gulf);
        Assert.Equal("head", Assert.Single(await TasksAsync(t, "approver")).ElementId);
        Assert.Contains("\"level\":\"head\"", await VariablesAsync(t, big));
        var detail = await world.Database.ScalarInTenantAsync<string>(t.Id, $"select detail::text from workflow.instance_history where instance_id = '{big}' and event = 'decided'");
        Assert.Contains("\"rules\": [2]", detail);
        await StartAsync(t, "spend", new { amount = 50 }, o.Gulf);
        Assert.Equal("manager", Assert.Single(await TasksAsync(t, "amal")).ElementId);

        // A missing decision is an incident on the step.
        await SaveAsync(t, "owner", "lost", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:businessRuleTask id="decide"><bpmn:extensionElements><tb:decision key="nowhere" resultVariable="level"/></bpmn:extensionElements></bpmn:businessRuleTask>
            <bpmn:endEvent id="e"/>{Flow("f1", "s", "decide")}{Flow("f2", "decide", "e")}
            """, name: "Lost"));
        await PublishAsync(t, "owner", "lost");
        var lost = await StartAsync(t, "lost", null, o.Gulf);
        var incident = Assert.Single(await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().IncidentsAsync(lost)));
        Assert.Equal(("decide", "decision"), (incident.ElementId, incident.ErrorClass));
    }

    private string Secret(TestTenant t, string name, string value)
    {
        var reference = SecretReferences.For(t.Id, name);
        world.Factory.Secrets.Set(reference, value);
        return reference;
    }

    private static async Task<Guid> WebhookConnectionAsync(TestTenant t, object settings)
    {
        var created = await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/pack-connections", new { kind = "webhook", displayName = "Supplier", settings }),
            HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        await OkAsync(t["admin"].Client.PostAsync($"/api/v1/integrations/pack-connections/{id}:enable", null));
        return id;
    }

    /// <summary>Hands the outbox messages of one type to a handler, as the worker does.</summary>
    private async Task DispatchAsync<T>(TestTenant t, Func<IServiceProvider, T, Task> handle)
    {
        var json = await world.Database.OwnerScalarAsync<string>(
            "select coalesce(json_agg(payload order by seq), '[]')::text from messaging.outbox where tenant_id = @t and message_type = @m", ("t", t.Id), ("m", typeof(T).Name));
        foreach (var e in JsonSerializer.Deserialize<List<T>>(json!, JsonDefaults.Options)!)
        {
            await AsAsync(t, null, async sp =>
            {
                await handle(sp, e);
                return 0;
            });
        }
    }

    [Fact]
    public async Task A_signed_inbound_webhook_correlates_a_message_once_and_a_webhook_step_posts_to_an_allowlisted_host()
    {
        var o = await FreshAsync();
        var t = o.T;
        var host = $"proc-{Guid.NewGuid():N}"[..20] + ".rest.test";
        await OkAsync(t["admin"].Client.PostAsJsonAsync("/api/v1/integrations/allowlist", new { host }));
        world.Factory.Rest.On(host, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var outbound = await WebhookConnectionAsync(t, new
        {
            url = $"https://{host}/orders",
            auth = new { type = "hmac_sha256", secretRef = Secret(t, "supplier-out", "fictional-outbound-value") },
        });
        var inbound = await WebhookConnectionAsync(t, new
        {
            auth = new { type = "hmac_sha256", secretRef = Secret(t, "supplier-in", "fictional-inbound-value") },
            inbound = new { target = "message", name = "delivery_arrived", correlationPath = "$.po", fields = new Dictionary<string, string> { ["boxes"] = "$.boxes" }, windowSeconds = 300 },
        });
        await SaveAsync(t, "owner", "supply", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:serviceTask id="order"><bpmn:extensionElements><tb:webhook id="{outbound}"/><tb:input name="po" source="po"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:intermediateCatchEvent id="arrived"><bpmn:extensionElements><tb:correlation key="po"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:intermediateCatchEvent>
            {UserTask("check", $"users=\"{t["amal"].Id}\"", "Check delivery")}<bpmn:endEvent id="e"/>
            {Flow("f1", "s", "order")}{Flow("f2", "order", "arrived")}{Flow("f3", "arrived", "check")}{Flow("f4", "check", "e")}
            """, """<bpmn:message id="m" name="delivery_arrived"/>""", "Supply"));
        await PublishAsync(t, "owner", "supply");
        var instance = await StartAsync(t, "supply", new { po = "PO-1" }, o.Gulf);

        // The webhook step posts its inputs, signed, and moves on when the delivery succeeds.
        await AsAsync(t, null, sp => sp.GetRequiredService<TeamBeats.Modules.Integrations.Runtime.ConnectorRuntime>().RunDueAsync(DateTimeOffset.UtcNow.AddSeconds(1), 50));
        var sent = Assert.Single(world.Factory.Rest.SentTo(host));
        using (var body = JsonDocument.Parse(sent.Body!))
        {
            Assert.Equal("PO-1", body.RootElement.GetProperty("data").GetProperty("po").GetString());
        }
        await DispatchAsync<ConnectorActionCompleted>(t, (sp, e) => sp.GetRequiredService<ProcessEngine>().OnConnectorCompletedAsync(e));
        Assert.Equal("succeeded", await world.Database.ScalarInTenantAsync<string>(t.Id, $"select status from workflow.service_task where instance_id = '{instance}'"));
        Assert.Empty(await TasksAsync(t, "amal"));

        // A signed delivery to the inbound connection correlates by the configured key, once per delivery ID.
        async Task<HttpResponseMessage> DeliverAsync(string body, string secret, string deliveryId)
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/integrations/inbound/{inbound:D}") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader, ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation(WebhookSignature.DeliveryHeader, deliveryId);
            request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader, WebhookSignature.Compute(secret, ts, body));
            return await world.Factory.TenantClient(t.Key).SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await DeliverAsync("""{"po":"PO-1","boxes":"4"}""", "fictional-wrong-value", "d-0")).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await DeliverAsync("""{"po":"PO-1","boxes":"4"}""", "fictional-inbound-value", "d-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DeliverAsync("""{"po":"PO-1","boxes":"5"}""", "fictional-inbound-value", "d-1")).StatusCode);
        await DispatchAsync<InboundWebhookReceived>(t, (sp, e) => ActivatorUtilities.CreateInstance<InboundMessageReceived>(sp).HandleAsync(e, CancellationToken.None));
        // A redelivered event (the outbox delivers at least once) does not correlate twice.
        await DispatchAsync<InboundWebhookReceived>(t, (sp, e) => ActivatorUtilities.CreateInstance<InboundMessageReceived>(sp).HandleAsync(e, CancellationToken.None));
        Assert.Single(await TasksAsync(t, "amal"));
        Assert.Contains("\"boxes\":\"4\"", await VariablesAsync(t, instance));

        // The same delivery in another tenant's process reaches nothing here, and a package without BPMN ignores it.
        var basic = await world.CreateTenantAsync("pmb-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        await AsAsync(basic, null, async sp =>
        {
            await ActivatorUtilities.CreateInstance<InboundMessageReceived>(sp)
                .HandleAsync(new InboundWebhookReceived(inbound, "d-9", "message", "delivery_arrived", "PO-1", new Dictionary<string, string?> { ["boxes"] = "9" }), CancellationToken.None);
            return 0;
        });
        Assert.Equal(0, await world.Database.CountInTenantAsync(basic.Id, "select count(*) from workflow.message_buffer"));
        Assert.Contains("\"boxes\":\"4\"", await VariablesAsync(t, instance));
    }
}
