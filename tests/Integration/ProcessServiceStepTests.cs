using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
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
/// Spec 016 R2 T018 on the spec 024 engine: tasks for a group or queue reach its members at check time, an overdue task
/// also goes to its escalation target, product actions run inside the product (set maps variables, notify and start
/// service go to the owning module), and the end event's result reaches the completed event. Models a module compiles
/// are checked and published through the engine contract; process model keys and other tenants' groups are refused.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ProcessServiceStepTests(TenantWorld world)
{
    private static string Model(string body) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          <bpmn:process id="p" name="Laptop request">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string Flow(string id, string from, string to) => $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"/>""";

    private async Task<TestTenant> FreshAsync()
    {
        var t = await world.CreateTenantAsync("pss-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "requester", "amal", "employee", "approver" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        return t;
    }

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

    private Task<Guid> GroupAsync(TestTenant t, string key, params string[] members) =>
        AsAsync(t, null, sp => sp.GetRequiredService<IAuthorizationProvisioning>().SyncDirectoryGroupAsync(key, key, members.Select(m => t[m].Id).ToList()));

    private Task<ModelPublishResult> PublishAsync(TestTenant t, string key, string xml) =>
        AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().PublishModelAsync(key, "service_route", xml, null));

    private Task<ProcessInstanceInfo> StartAsync(TestTenant t, string key, object? variables = null) =>
        AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().StartAsync(key, "REQ-" + Guid.NewGuid().ToString("N")[..6], null, t["requester"].Id, [t["requester"].Id],
            null, variables is null ? null : JsonSerializer.SerializeToElement(variables).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())));

    private async Task<IReadOnlyList<Guid>> TaskIdsAsync(TestTenant t, string user) =>
        (await AsAsync(t, user, sp => sp.GetRequiredService<IProcessEngine>().MyOpenTasksAsync())).Select(x => x.Id).ToList();

    private Task<long> OutboxAsync(TestTenant t, string type, string condition = "true") =>
        world.Database.CountInTenantAsync(t.Id, $"select count(*) from messaging.outbox where message_type = '{type}' and {condition}");

    [Fact]
    public async Task A_queue_task_reaches_its_members_at_check_time_and_after_the_escalation_time_also_the_target()
    {
        var t = await FreshAsync();
        var queue = await GroupAsync(t, "it-queue", "amal", "requester");
        var published = await PublishAsync(t, "service:laptop", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:userTask id="fulfil" name="Fulfil"><bpmn:extensionElements><tb:assignment groups="{queue}"/><tb:due workingDays="2"/>
              <tb:escalation afterWorkingDays="1" users="{t["approver"].Id}"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="e"/>
            {Flow("f1", "s", "fulfil")}{Flow("f2", "fulfil", "e")}
            """));
        Assert.Empty(published.Problems);
        Assert.Equal(("service:laptop", 1, "service_route"), (published.Definition!.Key, published.Definition.Version, published.Definition.Source));
        var instance = await StartAsync(t, "service:laptop");

        // Members of the queue see the task; the requester is a member but files the request, so stays out.
        var task = Assert.Single(await TaskIdsAsync(t, "amal"));
        Assert.Empty(await TaskIdsAsync(t, "requester"));
        Assert.Empty(await TaskIdsAsync(t, "employee"));
        Assert.Empty(await TaskIdsAsync(t, "approver"));

        // A membership change applies at once: no copy of the members sits on the task.
        await GroupAsync(t, "it-queue", "amal", "employee", "requester");
        Assert.Equal([task], await TaskIdsAsync(t, "employee"));

        // A calendar change recalculates the due time from the element and moves the escalation with it.
        string JobsSql(string kind) => $"select string_agg(due_at::text, ',') from workflow.engine_job where task_id = '{task}' and kind = '{kind}' and done_at is null";
        var (due, escalate) = (await world.Database.ScalarInTenantAsync<string>(t.Id, JobsSql("task_due")), await world.Database.ScalarInTenantAsync<string>(t.Id, JobsSql("task_escalate")));
        Assert.NotNull(escalate);
        await world.Database.OwnerExecInTenantAsync(t.Id, "update workflow.user_task set due_at = due_at + interval '5 days' where id = @id", ("id", task));
        await world.Database.OwnerExecInTenantAsync(t.Id, "update workflow.engine_job set due_at = due_at + interval '5 days' where task_id = @id", ("id", task));
        Assert.Equal(1, await AsAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RecalculateAsync()));
        Assert.Equal((due, escalate), (await world.Database.ScalarInTenantAsync<string>(t.Id, JobsSql("task_due")), await world.Database.ScalarInTenantAsync<string>(t.Id, JobsSql("task_escalate"))));

        // The due time reminds; the escalation time after it also hands the task to the approver, once.
        var later = DateTimeOffset.UtcNow.AddDays(30);
        Assert.Equal(2, await AsAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(later, 10)));
        Assert.Equal(0, await AsAsync(t, null, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(later, 10)));
        Assert.Equal([task], await TaskIdsAsync(t, "approver"));
        Assert.Equal([task], await TaskIdsAsync(t, "amal"));
        Assert.Equal(1, await OutboxAsync(t, nameof(UserTaskDue), $"payload->>'taskId' = '{task}'"));
        Assert.Equal(1, await OutboxAsync(t, nameof(UserTaskEscalated), $"payload->>'taskId' = '{task}' and payload->'addedUsers' ? '{t["approver"].Id}'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id,
            $"select count(*) from workflow.instance_history where instance_id = '{instance.Id}' and event = 'task_escalated'"));

        // The approver completes it; the instance ends and nothing of the task stays due.
        await AsAsync(t, "approver", sp => sp.GetRequiredService<IProcessEngine>().CompleteTaskAsync(task, TaskOutcomes.Approve, null));
        Assert.Equal("completed", (await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().GetInstanceAsync(instance.Id)))!.Status);
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from workflow.engine_job where task_id = '{task}' and done_at is null"));
    }

    [Fact]
    public async Task Product_actions_run_inside_the_product_and_the_end_result_reaches_the_completed_event()
    {
        var t = await FreshAsync();
        var published = await PublishAsync(t, "service:onboard", Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:serviceTask id="mark"><bpmn:extensionElements><tb:product action="set"/><tb:input name="value" source="&quot;ordered&quot;"/>
              <tb:output variable="status" source="result.value"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:serviceTask id="tell"><bpmn:extensionElements><tb:product action="notify"/><tb:input name="template" source="&quot;laptop_ordered&quot;"/>
              <tb:input name="laptop" source="model"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:serviceTask id="next"><bpmn:extensionElements><tb:product action="start_service"/><tb:input name="service" source="&quot;accessories&quot;"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:endEvent id="e"><bpmn:extensionElements><tb:result value="approved"/></bpmn:extensionElements></bpmn:endEvent>
            {Flow("f1", "s", "mark")}{Flow("f2", "mark", "tell")}{Flow("f3", "tell", "next")}{Flow("f4", "next", "e")}
            """));
        Assert.Empty(published.Problems);
        var instance = await StartAsync(t, "service:onboard", new { model = "X1" });

        var ended = (await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().GetInstanceAsync(instance.Id)))!;
        Assert.Equal("completed", ended.Status);
        Assert.Equal("approved", await world.Database.ScalarInTenantAsync<string>(t.Id, $"select result from workflow.process_instance where id = '{instance.Id}'"));
        // Set wrote its variable; notify's inputs went to the owning module, not into the variables.
        Assert.Equal("ordered", await world.Database.ScalarInTenantAsync<string>(t.Id, $"select variables->>'status' from workflow.process_instance where id = '{instance.Id}'"));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from workflow.process_instance where id = '{instance.Id}' and variables ? 'laptop'"));
        Assert.Equal(1, await OutboxAsync(t, nameof(ProcessActionRequested),
            $"payload->>'instanceId' = '{instance.Id}' and payload->>'action' = 'notify' and payload->'inputs'->>'laptop' = 'X1'"));
        Assert.Equal(1, await OutboxAsync(t, nameof(ProcessActionRequested),
            $"payload->>'instanceId' = '{instance.Id}' and payload->>'action' = 'start_service' and payload->'inputs'->>'service' = 'accessories'"));
        Assert.Equal(0, await OutboxAsync(t, nameof(ProcessActionRequested), $"payload->>'instanceId' = '{instance.Id}' and payload->>'action' = 'set'"));
        Assert.Equal(1, await OutboxAsync(t, nameof(ProcessInstanceCompleted), $"payload->>'instanceId' = '{instance.Id}' and payload->>'result' = 'approved'"));
    }

    [Fact]
    public async Task Compiled_models_are_checked_before_publishing_and_tenant_models_cannot_use_product_actions_or_other_tenants_groups()
    {
        var t = await FreshAsync();
        var other = await FreshAsync();
        var foreign = await GroupAsync(other, "their-queue", "amal");
        string Queue(Guid group) => Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:userTask id="fulfil"><bpmn:extensionElements><tb:assignment groups="{group}"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="e"/>
            {Flow("f1", "s", "fulfil")}{Flow("f2", "fulfil", "e")}
            """);

        // Another tenant's group does not exist here: nothing is published.
        var refused = await PublishAsync(t, "service:foreign", Queue(foreign));
        Assert.Null(refused.Definition);
        Assert.Contains(new ModelProblemInfo("fulfil", "validation.process.assignment_group"), refused.Problems);
        Assert.Null(await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().GetDefinitionAsync("service:foreign")));
        Assert.Contains(new ModelProblemInfo("fulfil", "validation.process.assignment_group"),
            await AsAsync(t, null, sp => sp.GetRequiredService<IProcessEngine>().ValidateModelAsync("service:foreign", Queue(foreign))));

        // Process model keys belong to the modeler.
        var error = await Assert.ThrowsAsync<AppException>(() => PublishAsync(t, ProcessEngine.ModelPrefix + "sneaky", Queue(foreign)));
        Assert.Equal(HttpStatusCode.BadRequest, (HttpStatusCode)error.Status);

        // A tenant-authored model may not carry product actions: the owning module would never carry them out.
        await world.AssignAsync(t, "approver", BuiltInRoles.WorkflowDesigner, "tenant", null);
        var saved = await t["approver"].Client.PutAsJsonAsync("/api/v1/workflow/processes/notify", new
        {
            bpmnXml = Model($"""
                <bpmn:startEvent id="s"/>
                <bpmn:serviceTask id="tell"><bpmn:extensionElements><tb:product action="notify"/></bpmn:extensionElements></bpmn:serviceTask>
                <bpmn:endEvent id="e"/>
                {Flow("f1", "s", "tell")}{Flow("f2", "tell", "e")}
                """),
        });
        var body = await saved.Content.ReadAsStringAsync();
        Assert.True(saved.StatusCode == HttpStatusCode.OK, body);
        Assert.Contains("validation.process.product_action", body);
    }
}
