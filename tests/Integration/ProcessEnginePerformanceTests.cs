using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow;
using TeamBeats.Persistence;
using TeamBeats.Testing;
using Xunit;
using Xunit.Abstractions;

namespace TeamBeats.Integration.Tests;

/// <summary>
/// Spec 024 R2 T017: message correlation stays under 1 second at p95 with many instances waiting (section 15), and a
/// worker that dies while it holds a due job neither loses it nor runs it twice: the lock keeps other workers off, its
/// transaction rolls back, and the next worker runs the job once (FR-012).
/// </summary>
[Collection(WorldCollection.Name)]
public sealed class ProcessEnginePerformanceTests(TenantWorld world, ITestOutputHelper output)
{
    private const string Api = "/api/v1/workflow/processes";
    private const int Waiting = 300;
    private const int Measured = 100;

    private static string Model(string body, string definitions = "") => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          {definitions}
          <bpmn:process id="p" name="Load">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string Flow(string id, string from, string to) => $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"/>""";

    private static string UserTask(string id, string assignment) =>
        $"""<bpmn:userTask id="{id}" name="{id}"><bpmn:extensionElements><tb:assignment {assignment}/></bpmn:extensionElements></bpmn:userTask>""";

    private static async Task<JsonElement> OkAsync(Task<HttpResponseMessage> call, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await call;
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<TestTenant> FreshAsync(string key, Func<TestTenant, string> xml)
    {
        var t = await world.CreateTenantAsync("pep-" + Guid.NewGuid().ToString("N")[..8], "workplace_plus");
        foreach (var name in new[] { "owner", "operator", "amal", "approver" })
        {
            await world.SeedUserAsync(t, name, MembershipTypes.Employee);
        }
        await world.AssignAsync(t, "owner", BuiltInRoles.WorkflowDesigner, "tenant", null);
        await world.AssignAsync(t, "operator", BuiltInRoles.ProcessOperator, "tenant", null);
        await OkAsync(t["owner"].Client.PutAsJsonAsync($"{Api}/{key}", new { bpmnXml = xml(t) }));
        await OkAsync(t["owner"].Client.PostAsync($"{Api}/{key}:publish", null));
        return t;
    }

    private static async Task<Guid> StartAsync(TestTenant t, string key, object variables) =>
        (await OkAsync(t["operator"].Client.PostAsJsonAsync($"{Api}/{key}/instances", new { variables }), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private Task<T> CommittedAsync<T>(TestTenant t, Func<IServiceProvider, Task<T>> action) =>
        world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var result = await action(sp);
            await sp.GetRequiredService<IDbSession>().CommitAsync();
            return result;
        });

    [Fact]
    public async Task A_message_correlates_with_one_of_hundreds_of_waiting_instances_in_under_one_second_at_p95()
    {
        var t = await FreshAsync("delivery", _ => Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:intermediateCatchEvent id="arrived"><bpmn:extensionElements><tb:correlation key="po"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:intermediateCatchEvent>
            <bpmn:endEvent id="e"/>
            {Flow("f1", "s", "arrived")}{Flow("f2", "arrived", "e")}
            """, """<bpmn:message id="m" name="delivery_arrived"/>"""));
        var instances = new Dictionary<string, Guid>();
        for (var i = 0; i < Waiting; i++)
        {
            instances[$"PO-{i:D4}"] = await StartAsync(t, "delivery", new { po = $"PO-{i:D4}" });
        }

        var timings = new List<double>();
        for (var i = 0; i < Measured; i++)
        {
            // Spread across the waiting set, so the newest and the oldest subscriptions are both found by key.
            var po = $"PO-{i * (Waiting / Measured):D4}";
            var watch = Stopwatch.StartNew();
            var result = await CommittedAsync(t, sp => sp.GetRequiredService<ProcessEngine>().CorrelateAsync("delivery_arrived", po, $"msg-{po}", null, "event", null));
            watch.Stop();
            timings.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal((MessageResults.Correlated, instances[po]), (result.Result, result.InstanceId!.Value));
        }
        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        output.WriteLine($"Correlation with {Waiting} waiting: p50 {timings[timings.Count / 2]:F1} ms, p95 {p95:F1} ms.");
        Assert.True(p95 < 1000, $"p95 {p95:F1} ms");

        // Each correlated instance ended; the others still wait.
        Assert.Equal(Measured, await world.Database.CountInTenantAsync(t.Id, "select count(*) from workflow.process_instance where status = 'completed'"));
        Assert.Equal(Waiting - Measured, await world.Database.CountInTenantAsync(t.Id, "select count(*) from workflow.process_instance where status = 'active'"));
    }

    [Fact]
    public async Task A_worker_that_dies_holding_a_due_timer_loses_nothing_and_the_next_worker_runs_it_once()
    {
        var t = await FreshAsync("escalate", x => Model($"""
            <bpmn:startEvent id="s"/>
            {UserTask("review", $"users=\"{x["amal"].Id}\"")}
            <bpmn:boundaryEvent id="late" attachedToRef="review"><bpmn:timerEventDefinition><bpmn:timeDuration>PT2H</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:boundaryEvent>
            {UserTask("head", $"users=\"{x["approver"].Id}\"")}
            <bpmn:endEvent id="e"/><bpmn:endEvent id="e2"/>
            {Flow("f1", "s", "review")}{Flow("f2", "review", "e")}{Flow("f3", "late", "head")}{Flow("f4", "head", "e2")}
            """));
        var instance = await StartAsync(t, "escalate", new { });
        var later = DateTimeOffset.UtcNow.AddHours(3);

        // Worker A claims the due timer and runs it, then dies before it commits. While it holds the row, worker B
        // finds nothing to run.
        var claimed = new TaskCompletionSource();
        var die = new TaskCompletionSource();
        var workerA = world.Factory.InScopeAsync(t.Id, async sp =>
        {
            var ran = await sp.GetRequiredService<EngineJobs>().RunDueAsync(later, 10);
            claimed.SetResult();
            await die.Task;
            if (ran > 0)
            {
                throw new OperationCanceledException("worker A was killed");
            }
            return ran;
        });
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, await CommittedAsync(t, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(later, 10)));
        die.SetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => workerA);

        // Nothing of worker A's run stayed: the review task is still open and the job is still due.
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from workflow.user_task where instance_id = '{instance}' and element_id = 'review' and status = 'open'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from workflow.engine_job where instance_id = '{instance}' and done_at is null"));

        // Worker B runs it once; a further run finds nothing, so the escalation happened exactly once.
        Assert.Equal(1, await CommittedAsync(t, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(later, 10)));
        Assert.Equal(0, await CommittedAsync(t, sp => sp.GetRequiredService<EngineJobs>().RunDueAsync(later.AddHours(1), 10)));
        Assert.Equal(0, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from workflow.user_task where instance_id = '{instance}' and element_id = 'review' and status = 'open'"));
        Assert.Equal(1, await world.Database.CountInTenantAsync(t.Id, $"select count(*) from workflow.user_task where instance_id = '{instance}' and element_id = 'head'"));
    }
}
