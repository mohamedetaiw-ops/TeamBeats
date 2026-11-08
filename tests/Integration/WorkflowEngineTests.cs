using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using TeamBeats.Testing;
using Xunit;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 024 T004 and T005: approval routes on the engine core. Start, approve, reject, return and withdraw; one effect
/// per completion; no self-approval; candidates by permission at the instance's unit; versions; tenant isolation.
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class WorkflowEngineTests(TenantWorld world)
{
    private sealed record Org(TestTenant T, Guid Gulf, Guid Africa);

    private async Task<Org> FreshAsync()
    {
        var t = await world.CreateTenantAsync("wf-" + Guid.NewGuid().ToString("N")[..8], "workplace");
        var holding = await world.CreateUnitAsync(t, "Holding", null, "organization");
        var gulf = await world.CreateUnitAsync(t, "Gulf", holding, "subsidiary");
        var africa = await world.CreateUnitAsync(t, "Africa", holding, "subsidiary");
        foreach (var name in new[] { "requester", "legal", "approver_gulf", "approver_africa", "other" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "approver_gulf", "approver", "org_unit", gulf);
        await world.AssignAsync(t, "approver_africa", "approver", "org_unit", africa);
        return new Org(t, gulf, africa);
    }

    /// <summary>Runs engine calls in one transaction as a user of the tenant (or the system when no user is named).</summary>
    private Task<T> AsAsync<T>(TestTenant t, string? user, Func<IProcessEngine, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            if (user is not null)
            {
                sp.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.User, t[user].Id);
            }
            var result = await action(sp.GetRequiredService<IProcessEngine>());
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    private static RouteSpec TwoSteps(TestTenant t, bool excludeStarter = true) => new("Contract approval",
    [
        new RouteStep("legal", "Legal review", new CandidateRule(Users: [t["legal"].Id, t["requester"].Id])),
        new RouteStep("manager", "Manager approval", new CandidateRule(Permission: "workflow.approve")),
    ], excludeStarter);

    private async Task<int> StatusAsync(Func<Task> action)
    {
        try
        {
            await action();
            return 200;
        }
        catch (AppException e)
        {
            return e.Status;
        }
    }

    [Fact]
    public async Task An_approved_route_runs_its_steps_in_order_and_completes_approved_with_history_events_and_audit()
    {
        var o = await FreshAsync();
        var t = o.T;
        var definition = await AsAsync(t, "requester", e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        Assert.Equal(1, definition.Version);
        var bpmn = await AsAsync(t, null, e => e.GetBpmnAsync("service.contract", 1));
        Assert.Contains("userTask", bpmn);

        var instance = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-1", o.Gulf, t["requester"].Id));
        var first = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));
        Assert.Equal("legal", first.ElementId);
        Assert.Equal("Legal review", first.Name);
        Assert.Empty(await AsAsync(t, "approver_gulf", e => e.MyOpenTasksAsync()));

        await AsAsync(t, "legal", e => e.CompleteTaskAsync(first.Id, TaskOutcomes.Approve, "Looks fine"));
        var second = Assert.Single(await AsAsync(t, "approver_gulf", e => e.MyOpenTasksAsync()));
        Assert.Equal("manager", second.ElementId);
        Assert.Equal(1, second.StepIndex);
        await AsAsync(t, "approver_gulf", e => e.CompleteTaskAsync(second.Id, TaskOutcomes.Approve, null));

        var done = await AsAsync(t, null, e => e.GetInstanceAsync(instance.Id));
        Assert.Equal("completed", done!.Status);
        Assert.Equal(InstanceOutcomes.Approved, done.Outcome);
        var history = await AsAsync(t, null, e => e.HistoryAsync(instance.Id));
        Assert.Equal(["started", "task_created", "task_completed:approve", "task_created", "task_completed:approve", "completed"], history.Select(h => h.Event));
        Assert.Equal(t["legal"].Id, history[2].ActorId);

        Assert.Equal(2, await world.Database.OutboxCountAsync("UserTaskCreated", t.Id));
        Assert.Equal(2, await world.Database.OutboxCountAsync("UserTaskCompleted", t.Id));
        Assert.Equal(1, await world.Database.OutboxCountAsync("ProcessInstanceCompleted", t.Id));
        Assert.Equal(2, await world.Database.AuditCountAsync(t.Id, AuditActions.WorkflowTaskCompleted));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.WorkflowDefinitionPublished));
    }

    [Theory]
    [InlineData(TaskOutcomes.Reject, InstanceOutcomes.Rejected)]
    [InlineData(TaskOutcomes.Return, InstanceOutcomes.Returned)]
    public async Task Reject_or_return_at_any_step_ends_the_instance_with_that_outcome(string outcome, string expected)
    {
        var o = await FreshAsync();
        var t = o.T;
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        var instance = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-2", o.Gulf, t["requester"].Id));
        var task = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));
        await AsAsync(t, "legal", e => e.CompleteTaskAsync(task.Id, outcome, "Missing annex"));

        var ended = await AsAsync(t, null, e => e.GetInstanceAsync(instance.Id));
        Assert.Equal(expected, ended!.Outcome);
        Assert.Single(await AsAsync(t, null, e => e.InstanceTasksAsync(instance.Id)));
        Assert.Empty(await AsAsync(t, "approver_gulf", e => e.MyOpenTasksAsync()));
    }

    [Fact]
    public async Task A_second_completion_of_the_same_task_is_refused_and_creates_no_second_step()
    {
        var o = await FreshAsync();
        var t = o.T;
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        var instance = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-3", o.Gulf, t["requester"].Id));
        var task = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));

        // Two completions race; the task row lock makes the second one see the completed task.
        var results = await Task.WhenAll(
            StatusAsync(() => AsAsync(t, "legal", e => e.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null))),
            StatusAsync(() => AsAsync(t, "legal", e => e.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null))));
        Assert.Equal([200, 409], results.Order());
        Assert.Equal(2, (await AsAsync(t, null, e => e.InstanceTasksAsync(instance.Id))).Count);
        Assert.Equal(1, await world.Database.OutboxCountAsync("UserTaskCompleted", t.Id));

        // Starting again for the same business key returns the same instance.
        var again = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-3", o.Gulf, t["requester"].Id));
        Assert.Equal(instance.Id, again.Id);
    }

    [Fact]
    public async Task The_starter_never_approves_their_own_request_even_when_named()
    {
        var o = await FreshAsync();
        var t = o.T;
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-4", o.Gulf, t["requester"].Id));
        Assert.Empty(await AsAsync(t, "requester", e => e.MyOpenTasksAsync()));
        var task = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));
        Assert.Equal(403, await StatusAsync(() => AsAsync(t, "requester", e => e.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null))));

        // When the route allows it, a named starter is a candidate.
        await AsAsync(t, null, e => e.PublishRouteAsync("service.open", RouteSources.ServiceRoute, TwoSteps(t, excludeStarter: false)));
        await AsAsync(t, "requester", e => e.StartAsync("service.open", "REQ-5", o.Gulf, t["requester"].Id));
        Assert.Single(await AsAsync(t, "requester", e => e.MyOpenTasksAsync()));
    }

    [Fact]
    public async Task A_permission_step_goes_to_holders_at_the_instances_unit_and_not_to_a_sibling_units_holder()
    {
        var o = await FreshAsync();
        var t = o.T;
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        var instance = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-6", o.Africa, t["requester"].Id));
        var legal = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));
        await AsAsync(t, "legal", e => e.CompleteTaskAsync(legal.Id, TaskOutcomes.Approve, null));
        var manager = (await AsAsync(t, null, e => e.InstanceTasksAsync(instance.Id))).Single(x => x.Status == "open");

        Assert.Empty(await AsAsync(t, "approver_gulf", e => e.MyOpenTasksAsync()));
        Assert.Equal(403, await StatusAsync(() => AsAsync(t, "approver_gulf", e => e.CompleteTaskAsync(manager.Id, TaskOutcomes.Approve, null))));
        Assert.Equal(403, await StatusAsync(() => AsAsync(t, "other", e => e.CompleteTaskAsync(manager.Id, TaskOutcomes.Approve, null))));
        Assert.Single(await AsAsync(t, "approver_africa", e => e.MyOpenTasksAsync()));
        await AsAsync(t, "approver_africa", e => e.CompleteTaskAsync(manager.Id, TaskOutcomes.Approve, null));
    }

    [Fact]
    public async Task Withdrawing_cancels_the_open_task_and_only_the_starter_can_withdraw()
    {
        var o = await FreshAsync();
        var t = o.T;
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        var instance = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-7", o.Gulf, t["requester"].Id));
        var task = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));

        Assert.Equal(403, await StatusAsync(() => AsAsync(t, "legal", e => e.WithdrawAsync(instance.Id))));
        var withdrawn = await AsAsync(t, "requester", e => e.WithdrawAsync(instance.Id));
        Assert.Equal(InstanceOutcomes.Withdrawn, withdrawn.Outcome);
        Assert.Equal("cancelled", (await AsAsync(t, null, e => e.GetTaskAsync(task.Id)))!.Status);
        Assert.Empty(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));
        Assert.Equal(409, await StatusAsync(() => AsAsync(t, "legal", e => e.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null))));
        Assert.Equal(409, await StatusAsync(() => AsAsync(t, "requester", e => e.WithdrawAsync(instance.Id))));
        Assert.Equal(1, await world.Database.OutboxCountAsync("ProcessInstanceWithdrawn", t.Id));
        Assert.Equal(1, await world.Database.AuditCountAsync(t.Id, AuditActions.WorkflowInstanceWithdrawn));
    }

    [Fact]
    public async Task Running_instances_stay_on_their_version_and_republishing_the_same_route_adds_no_version()
    {
        var o = await FreshAsync();
        var t = o.T;
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        var same = await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        Assert.Equal(1, same.Version);
        var old = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-8", o.Gulf, t["requester"].Id));

        var oneStep = new RouteSpec("Contract approval", [new RouteStep("manager", "Manager approval", new CandidateRule(Permission: "workflow.approve"))]);
        Assert.Equal(2, (await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, oneStep))).Version);
        var fresh = await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-9", o.Gulf, t["requester"].Id));

        Assert.Equal(1, old.DefinitionVersion);
        Assert.Equal(2, fresh.DefinitionVersion);
        // The old instance still asks Legal first; the new one goes straight to the manager.
        Assert.Equal("legal", (await AsAsync(t, null, e => e.InstanceTasksAsync(old.Id))).Single().ElementId);
        Assert.Equal("manager", (await AsAsync(t, null, e => e.InstanceTasksAsync(fresh.Id))).Single().ElementId);
    }

    [Fact]
    public async Task Invalid_input_is_refused_with_resource_keys()
    {
        var o = await FreshAsync();
        var t = o.T;
        Assert.Equal(400, await StatusAsync(() => AsAsync(t, null, e => e.PublishRouteAsync("Bad Key", RouteSources.ServiceRoute, TwoSteps(t)))));
        Assert.Equal(400, await StatusAsync(() => AsAsync(t, null, e => e.PublishRouteAsync("service.x", "elsewhere", TwoSteps(t)))));
        Assert.Equal(404, await StatusAsync(() => AsAsync(t, "requester", e => e.StartAsync("service.none", "REQ", o.Gulf, t["requester"].Id))));
        await AsAsync(t, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(t)));
        await AsAsync(t, "requester", e => e.StartAsync("service.contract", "REQ-10", o.Gulf, t["requester"].Id));
        var task = Assert.Single(await AsAsync(t, "legal", e => e.MyOpenTasksAsync()));
        Assert.Equal(400, await StatusAsync(() => AsAsync(t, "legal", e => e.CompleteTaskAsync(task.Id, "maybe", null))));
        Assert.Equal(400, await StatusAsync(() => AsAsync(t, "legal", e => e.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, new string('x', 2001)))));
    }

    [Fact]
    public async Task Another_tenant_cannot_see_start_complete_or_withdraw_anything()
    {
        var a = await FreshAsync();
        var b = await FreshAsync();
        await AsAsync(a.T, null, e => e.PublishRouteAsync("service.contract", RouteSources.ServiceRoute, TwoSteps(a.T)));
        var instance = await AsAsync(a.T, "requester", e => e.StartAsync("service.contract", "REQ-11", a.Gulf, a.T["requester"].Id));
        var task = Assert.Single(await AsAsync(a.T, "legal", e => e.MyOpenTasksAsync()));

        Assert.Null(await AsAsync(b.T, null, e => e.GetInstanceAsync(instance.Id)));
        Assert.Null(await AsAsync(b.T, null, e => e.GetTaskAsync(task.Id)));
        Assert.Null(await AsAsync(b.T, null, e => e.GetDefinitionAsync("service.contract")));
        Assert.Empty(await AsAsync(b.T, "legal", e => e.MyOpenTasksAsync()));
        Assert.Equal(404, await StatusAsync(() => AsAsync(b.T, "legal", e => e.CompleteTaskAsync(task.Id, TaskOutcomes.Approve, null))));
        Assert.Equal(404, await StatusAsync(() => AsAsync(b.T, "requester", e => e.WithdrawAsync(instance.Id))));
        Assert.Equal(404, await StatusAsync(() => AsAsync(b.T, "requester", e => e.StartAsync("service.contract", "REQ-11", b.Gulf, b.T["requester"].Id))));
        Assert.Equal("open", (await AsAsync(a.T, null, e => e.GetTaskAsync(task.Id)))!.Status);
    }
}
