using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Graph;
using Record = TeamBeats.Modules.Workflow.Graph.Record;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 024 T009 and T011: BPMN models read into the graph, are validated, and run with BPMN token semantics.</summary>
public sealed class ProcessGraphTests
{
    private const string User = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static string Model(string body, string definitions = "") => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          {definitions}
          <bpmn:process id="p" name="Purchase">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string Flow(string id, string from, string to, string? condition = null) => condition is null
        ? $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"/>"""
        : $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"><bpmn:conditionExpression>{System.Security.SecurityElement.Escape(condition)}</bpmn:conditionExpression></bpmn:sequenceFlow>""";

    private static string Task(string id, string name = "Review") =>
        $"""<bpmn:userTask id="{id}" name="{name}"><bpmn:extensionElements><tb:assignment users="{User}"/><tb:name xml:lang="ar">مراجعة</tb:name></bpmn:extensionElements></bpmn:userTask>""";

    private static ProcessGraph Read(string xml)
    {
        var (graph, problems) = BpmnReader.Read("purchase", xml);
        Assert.True(problems.Count == 0, string.Join(", ", problems.Select(p => $"{p.ElementId}:{p.Key}")));
        return graph!;
    }

    private static IReadOnlyList<ModelProblem> Problems(string xml) => BpmnReader.Read("purchase", xml).Problems;

    private sealed class Context : IExecutionContext
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        public (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string key, IReadOnlyDictionary<string, object?> variables)
        {
            if (key != "chain") throw new DecisionException("missing");
            var amount = variables["amount"] as decimal? ?? 0;
            return (JsonValue.Create(amount > 1000 ? "head" : "manager"), [amount > 1000 ? 2 : 1]);
        }
    }

    private sealed class Run(ProcessGraph graph)
    {
        public InstanceState State { get; } = new();
        public List<Effect> All { get; } = [];
        public string? Outcome => All.OfType<Finished>().LastOrDefault()?.Outcome;

        private IReadOnlyList<Effect> Do(Func<Executor, IReadOnlyList<Effect>> action)
        {
            var effects = action(new Executor(graph, State, new Context()));
            All.AddRange(effects);
            return effects;
        }

        public Run Start(string vars = "{}") { Do(e => e.Start(JsonNode.Parse(vars)!.AsObject())); return this; }

        public TokenState At(string element) => State.Tokens.Single(t => t.ElementId == element && t.Status != "listening");

        public IEnumerable<string> Waiting => State.Tokens.Where(t => t.Status is "waiting" or "incident").Select(t => t.ElementId).Order();

        public Run Complete(string element, string? result = null) { var t = At(element); Do(e => e.Complete(t.Id, result is null ? null : JsonNode.Parse(result)!.AsObject())); return this; }

        public Run Fire(string tokenElement, string eventElement, string? payload = null)
        {
            var t = State.Tokens.First(x => x.ElementId == tokenElement);
            Do(e => e.Fire(t.Id, eventElement, payload is null ? null : JsonNode.Parse(payload)!.AsObject()));
            return this;
        }

        public Run Error(string element, string code) { var t = At(element); Do(e => e.ThrowError(t.Id, code)); return this; }
    }

    [Fact]
    public void A_model_reads_into_the_graph_with_names_per_locale_and_assignments()
    {
        var graph = Read(Model($"""<bpmn:startEvent id="s"/>{Task("review")}<bpmn:endEvent id="e"/>{Flow("f1", "s", "review")}{Flow("f2", "review", "e")}"""));
        var review = graph.Element("review");
        Assert.Equal("Review", review.Names["en"]);
        Assert.Equal("مراجعة", review.Names["ar"]);
        Assert.Equal(Guid.Parse(User), review.Assignment!.Users![0]);
        Assert.Equal(["f1"], review.Incoming);
        var json = graph.ToJson();
        Assert.Equal(graph.Elements.Count, ProcessGraph.FromJson(json).Elements.Count);
    }

    [Fact]
    public void Validation_marks_each_problem_on_its_element()
    {
        var problems = Problems(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:userTask id="noone"/>
            <bpmn:parallelGateway id="split"/>
            <bpmn:serviceTask id="call"/>
            <bpmn:intermediateCatchEvent id="wait"><bpmn:timerEventDefinition><bpmn:timeDuration>soon</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:intermediateCatchEvent>
            <bpmn:scriptTask id="script"/>
            <bpmn:userTask id="lonely"><bpmn:extensionElements><tb:assignment users="{User}"/></bpmn:extensionElements></bpmn:userTask>
            <bpmn:endEvent id="e"/>
            {Flow("f1", "s", "noone")}{Flow("f2", "noone", "split")}{Flow("f3", "split", "call")}{Flow("f4", "split", "wait")}
            {Flow("f5", "call", "e")}{Flow("f6", "wait", "e")}{Flow("f7", "lonely", "e")}
            """));
        Assert.Contains(new ModelProblem("noone", "validation.process.assignment"), problems);
        Assert.Contains(new ModelProblem("split", "validation.process.parallel_join"), problems);
        Assert.Contains(new ModelProblem("call", "validation.process.service_binding"), problems);
        Assert.Contains(new ModelProblem("wait", "validation.process.timer"), problems);
        Assert.Contains(new ModelProblem("script", "validation.process.unsupported_element"), problems);
        Assert.Contains(new ModelProblem("lonely", "validation.process.unreachable"), problems);
    }

    [Fact]
    public void Validation_refuses_bad_expressions_cycles_under_an_hour_self_calls_and_unsafe_xml()
    {
        var problems = Problems(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:exclusiveGateway id="x"/>
            <bpmn:intermediateCatchEvent id="t"><bpmn:timerEventDefinition><bpmn:timeCycle>R/PT5M</bpmn:timeCycle></bpmn:timerEventDefinition></bpmn:intermediateCatchEvent>
            <bpmn:callActivity id="again" calledElement="purchase"/>
            <bpmn:endEvent id="e"/>
            {Flow("f1", "s", "x")}{Flow("f2", "x", "t", "amount >")}{Flow("f3", "x", "again")}{Flow("f4", "t", "e")}{Flow("f5", "again", "e")}
            """));
        Assert.Contains(new ModelProblem("f2", "validation.process.expression"), problems);
        Assert.Contains(new ModelProblem("f3", "validation.process.condition"), problems);
        Assert.Contains(new ModelProblem("t", "validation.process.timer_cycle"), problems);
        Assert.Contains(new ModelProblem("again", "validation.process.call_self"), problems);

        var entity = """<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]><bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL">&x;</bpmn:definitions>""";
        Assert.Equal([new ModelProblem("", "validation.process.xml")], Problems(entity));
    }

    [Fact]
    public void Exclusive_gateways_take_the_first_true_flow_or_the_default()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/><bpmn:exclusiveGateway id="x" default="small"/>
            {Task("head")}{Task("manager")}<bpmn:endEvent id="e"/>
            {Flow("f1", "s", "x")}{Flow("big", "x", "head", "amount > 1000")}{Flow("small", "x", "manager")}{Flow("f2", "head", "e")}{Flow("f3", "manager", "e")}
            """));
        Assert.Equal(["head"], new Run(graph).Start("""{"amount": 5000}""").Waiting);
        var run = new Run(graph).Start("""{"amount": 10}""");
        Assert.Equal(["manager"], run.Waiting);
        run.Complete("manager", """{"outcome": "approved"}""");
        Assert.Equal("completed", run.Outcome);
        Assert.Equal("approved", run.State.Variables["outcome"]!.GetValue<string>());
    }

    [Fact]
    public void Parallel_join_waits_for_every_branch()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/><bpmn:parallelGateway id="split"/>{Task("it")}{Task("cards")}<bpmn:parallelGateway id="join"/>{Task("welcome")}<bpmn:endEvent id="e"/>
            {Flow("f1", "s", "split")}{Flow("f2", "split", "it")}{Flow("f3", "split", "cards")}{Flow("f4", "it", "join")}{Flow("f5", "cards", "join")}
            {Flow("f6", "join", "welcome")}{Flow("f7", "welcome", "e")}
            """));
        var run = new Run(graph).Start();
        Assert.Equal(["cards", "it"], run.Waiting);
        run.Complete("it");
        Assert.Equal(["cards"], run.Waiting);
        run.Complete("cards");
        Assert.Equal(["welcome"], run.Waiting);
        run.Complete("welcome");
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void Inclusive_join_waits_only_for_the_branches_that_were_taken()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/><bpmn:inclusiveGateway id="split" default="none"/>{Task("legal")}{Task("finance")}{Task("it")}<bpmn:inclusiveGateway id="join"/><bpmn:endEvent id="e"/>
            {Flow("f1", "s", "split")}{Flow("toLegal", "split", "legal", "contract")}{Flow("toFinance", "split", "finance", "amount > 100")}{Flow("none", "split", "it")}
            {Flow("f2", "legal", "join")}{Flow("f3", "finance", "join")}{Flow("f4", "it", "join")}{Flow("f5", "join", "e")}
            """));
        var run = new Run(graph).Start("""{"contract": true, "amount": 500}""");
        Assert.Equal(["finance", "legal"], run.Waiting);
        run.Complete("legal");
        Assert.Null(run.Outcome);
        run.Complete("finance");
        Assert.Equal("completed", run.Outcome);

        var one = new Run(graph).Start("""{"contract": false, "amount": 1}""");
        Assert.Equal(["it"], one.Waiting);
        one.Complete("it");
        Assert.Equal("completed", one.Outcome);
    }

    [Fact]
    public void Sub_processes_and_parallel_multi_instance_with_a_completion_condition()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:subProcess id="sub">
              <bpmn:startEvent id="ss"/>{Task("vote")}<bpmn:endEvent id="se"/>
              {Flow("sf1", "ss", "vote")}{Flow("sf2", "vote", "se")}
            </bpmn:subProcess>
            <bpmn:userTask id="approve"><bpmn:extensionElements><tb:assignment assignee="approver"/><tb:loop collection="approvers" elementVariable="approver"/></bpmn:extensionElements>
              <bpmn:multiInstanceLoopCharacteristics><bpmn:completionCondition>nrOfCompletedInstances >= 2</bpmn:completionCondition></bpmn:multiInstanceLoopCharacteristics></bpmn:userTask>
            <bpmn:endEvent id="e"/>
            {Flow("f1", "s", "sub")}{Flow("f2", "sub", "approve")}{Flow("f3", "approve", "e")}
            """));
        var run = new Run(graph).Start("""{"approvers": ["a", "b", "c"]}""");
        Assert.Equal(["sub", "vote"], run.Waiting);
        run.Complete("vote");
        var bodies = run.State.Tokens.Where(t => t.ElementId == "approve" && t.Body).ToList();
        Assert.Equal(3, bodies.Count);
        var tasks = run.All.OfType<CreateUserTask>().Where(c => c.ElementId == "approve").ToList();
        Assert.Equal(["a", "b", "c"], tasks.Select(t => t.Variables["approver"]!.GetValue<string>()).Order());

        var executor = new Executor(graph, run.State, new Context());
        executor.Complete(bodies[0].Id, null);
        Assert.Null(executor.Effects.OfType<Finished>().FirstOrDefault());
        executor = new Executor(graph, run.State, new Context());
        var effects = executor.Complete(bodies[1].Id, null);
        Assert.Contains(effects, e => e is CancelWaits c && c.Token == bodies[2].Id);
        Assert.Equal("completed", effects.OfType<Finished>().Single().Outcome);
    }

    [Fact]
    public void Sequential_multi_instance_runs_one_item_at_a_time()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:manualTask id="pack"><bpmn:extensionElements><tb:loop collection="boxes" elementVariable="box"/></bpmn:extensionElements><bpmn:multiInstanceLoopCharacteristics isSequential="true"/></bpmn:manualTask>
            {Task("check")}<bpmn:endEvent id="e"/>
            {Flow("f1", "s", "pack")}{Flow("f2", "pack", "check")}{Flow("f3", "check", "e")}
            """));
        var run = new Run(graph).Start("""{"boxes": [1, 2, 3]}""");
        Assert.Equal(["check"], run.Waiting);
        Assert.Contains(run.All.OfType<Record>(), r => r.ElementId == "pack" && r.Event == "completed");
    }

    [Fact]
    public void Timer_boundaries_interrupt_or_run_beside_the_task()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>{Task("review")}
            <bpmn:boundaryEvent id="remind" attachedToRef="review" cancelActivity="false"><bpmn:extensionElements><tb:timer workingDays="2"/></bpmn:extensionElements><bpmn:timerEventDefinition/></bpmn:boundaryEvent>
            <bpmn:boundaryEvent id="escalate" attachedToRef="review"><bpmn:extensionElements><tb:timer workingDays="5"/></bpmn:extensionElements><bpmn:timerEventDefinition/></bpmn:boundaryEvent>
            <bpmn:sendTask id="nudge" messageRef="m"/>{Task("head")}<bpmn:endEvent id="e"/><bpmn:endEvent id="e2"/><bpmn:endEvent id="e3"/>
            {Flow("f1", "s", "review")}{Flow("f2", "review", "e")}{Flow("f3", "remind", "nudge")}{Flow("f4", "nudge", "e2")}{Flow("f5", "escalate", "head")}{Flow("f6", "head", "e3")}
            """, """<bpmn:message id="m" name="Reminder"/>"""));
        var run = new Run(graph).Start();
        var waits = run.All.OfType<Wait>().ToList();
        Assert.Equal(["escalate", "remind"], waits.Select(w => w.EventElementId).Order());
        Assert.Equal(2, waits.Single(w => w.EventElementId == "remind").Timer!.WorkingDays);

        run.Fire("review", "remind");
        Assert.Equal(["review"], run.Waiting);
        Assert.Contains(run.All.OfType<SendMessage>(), m => m.Name == "Reminder");
        Assert.Null(run.Outcome);

        run.Fire("review", "escalate");
        Assert.Equal(["head"], run.Waiting);
        run.Complete("head");
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void An_event_based_gateway_takes_the_first_event_and_cancels_the_other()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/><bpmn:eventBasedGateway id="g"/>
            <bpmn:intermediateCatchEvent id="confirmed"><bpmn:extensionElements><tb:correlation key="po"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:intermediateCatchEvent>
            <bpmn:intermediateCatchEvent id="late"><bpmn:timerEventDefinition><bpmn:timeDuration>P10D</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:intermediateCatchEvent>
            <bpmn:endEvent id="ok"/><bpmn:endEvent id="timeout"/>
            {Flow("f1", "s", "g")}{Flow("f2", "g", "confirmed")}{Flow("f3", "g", "late")}{Flow("f4", "confirmed", "ok")}{Flow("f5", "late", "timeout")}
            """, """<bpmn:message id="m" name="SupplierConfirmed"/>"""));
        var run = new Run(graph).Start("""{"po": "PO-7"}""");
        var message = run.All.OfType<Wait>().Single(w => w.Kind == "message");
        Assert.Equal(("SupplierConfirmed", "PO-7"), (message.MessageName, message.CorrelationKey));
        run.Fire("g", "confirmed", """{"confirmedBy": "ACME"}""");
        Assert.Equal("completed", run.Outcome);
        Assert.Contains(run.All, e => e is CancelWaits);
        Assert.Equal("ACME", run.State.Variables["confirmedBy"]!.GetValue<string>());
        Assert.Contains(run.All.OfType<Record>(), r => r.ElementId == "ok");
    }

    [Fact]
    public void Business_errors_follow_error_boundaries_and_uncaught_errors_end_the_instance()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:serviceTask id="post"><bpmn:extensionElements><tb:connector connection="{Guid.NewGuid()}" action="create_po"/><tb:input name="amount" source="amount"/></bpmn:extensionElements></bpmn:serviceTask>
            <bpmn:boundaryEvent id="budget" attachedToRef="post"><bpmn:errorEventDefinition errorRef="err"/></bpmn:boundaryEvent>
            {Task("fix")}<bpmn:endEvent id="e"/><bpmn:endEvent id="e2"/>
            {Flow("f1", "s", "post")}{Flow("f2", "post", "e")}{Flow("f3", "budget", "fix")}{Flow("f4", "fix", "e2")}
            """, """<bpmn:error id="err" errorCode="BudgetExceeded"/>"""));
        var run = new Run(graph).Start("""{"amount": 7}""");
        Assert.Equal(7m, run.All.OfType<CallService>().Single().Inputs["amount"]!.GetValue<decimal>());
        run.Error("post", "BudgetExceeded");
        Assert.Equal(["fix"], run.Waiting);

        var other = new Run(graph).Start("""{"amount": 7}""");
        other.Error("post", "SomethingElse");
        Assert.Equal("error", other.Outcome);
        Assert.Equal("SomethingElse", other.All.OfType<Finished>().Single().Code);
    }

    [Fact]
    public void An_error_end_inside_a_sub_process_is_caught_on_its_boundary()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:subProcess id="sub"><bpmn:startEvent id="ss"/><bpmn:endEvent id="fail"><bpmn:errorEventDefinition errorRef="err"/></bpmn:endEvent>{Flow("sf", "ss", "fail")}</bpmn:subProcess>
            <bpmn:boundaryEvent id="caught" attachedToRef="sub"><bpmn:errorEventDefinition/></bpmn:boundaryEvent>
            <bpmn:endEvent id="e"/><bpmn:endEvent id="handled"/>
            {Flow("f1", "s", "sub")}{Flow("f2", "sub", "e")}{Flow("f3", "caught", "handled")}
            """, """<bpmn:error id="err" errorCode="Nope"/>"""));
        var run = new Run(graph).Start();
        Assert.Equal("completed", run.Outcome);
        Assert.Contains(run.All.OfType<Record>(), r => r.ElementId == "handled");
        Assert.Empty(run.State.Scopes);
    }

    [Fact]
    public void A_message_event_sub_process_interrupts_its_scope()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>{Task("work")}<bpmn:endEvent id="e"/>
            <bpmn:subProcess id="cancelled" triggeredByEvent="true">
              <bpmn:startEvent id="cs"><bpmn:extensionElements><tb:correlation key="ref"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:startEvent>
              {Task("tidy")}<bpmn:endEvent id="ce"/>{Flow("c1", "cs", "tidy")}{Flow("c2", "tidy", "ce")}
            </bpmn:subProcess>
            {Flow("f1", "s", "work")}{Flow("f2", "work", "e")}
            """, """<bpmn:message id="m" name="Cancel"/>"""));
        var run = new Run(graph).Start("""{"ref": "R1"}""");
        Assert.Equal(["work"], run.Waiting);
        Assert.Contains(run.All.OfType<Wait>(), w => w.EventElementId == "cs" && w.CorrelationKey == "R1");
        run.Fire("cs", "cs");
        Assert.Equal(["tidy"], run.Waiting);
        run.Complete("tidy");
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void A_non_interrupting_escalation_runs_its_path_while_the_sub_process_continues()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:subProcess id="sub"><bpmn:startEvent id="ss"/><bpmn:intermediateThrowEvent id="raise"><bpmn:escalationEventDefinition escalationRef="esc"/></bpmn:intermediateThrowEvent>
              {Task("work")}<bpmn:endEvent id="se"/>{Flow("s1", "ss", "raise")}{Flow("s2", "raise", "work")}{Flow("s3", "work", "se")}</bpmn:subProcess>
            <bpmn:boundaryEvent id="tell" attachedToRef="sub" cancelActivity="false"><bpmn:escalationEventDefinition escalationRef="esc"/></bpmn:boundaryEvent>
            {Task("inform")}<bpmn:endEvent id="e"/><bpmn:endEvent id="e2"/>
            {Flow("f1", "s", "sub")}{Flow("f2", "sub", "e")}{Flow("f3", "tell", "inform")}{Flow("f4", "inform", "e2")}
            """, """<bpmn:escalation id="esc" escalationCode="Late"/>"""));
        var run = new Run(graph).Start();
        Assert.Equal(["inform", "sub", "work"], run.Waiting);
        run.Complete("work");
        Assert.Null(run.Outcome);
        run.Complete("inform");
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void Decisions_terminate_calls_and_the_loop_limit()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:businessRuleTask id="pick"><bpmn:extensionElements><tb:decision key="chain" resultVariable="level"/></bpmn:extensionElements></bpmn:businessRuleTask>
            <bpmn:callActivity id="po" calledElement="create-po"/>
            <bpmn:endEvent id="stop"><bpmn:terminateEventDefinition/></bpmn:endEvent>
            {Flow("f1", "s", "pick")}{Flow("f2", "pick", "po")}{Flow("f3", "po", "stop")}
            """));
        var run = new Run(graph).Start("""{"amount": 5000}""");
        Assert.Equal("head", run.State.Variables["level"]!.GetValue<string>());
        Assert.Contains(run.All.OfType<Record>(), r => r.Event == "decided");
        var child = run.All.OfType<StartChild>().Single();
        Assert.Equal("create-po", child.CalledKey);
        run.Complete("po", """{"poNumber": "PO-1"}""");
        Assert.Equal("terminated", run.Outcome);
        Assert.False(run.State.Variables.ContainsKey("poNumber"));

        var loop = Read(Model($"""
            <bpmn:startEvent id="s"/><bpmn:exclusiveGateway id="x" default="again"/><bpmn:manualTask id="spin"/><bpmn:endEvent id="e"/>
            {Flow("f1", "s", "x")}{Flow("out", "x", "e", "false")}{Flow("again", "x", "spin")}{Flow("f2", "spin", "x")}
            """));
        var spun = new Run(loop).Start();
        Assert.Contains(spun.All.OfType<RaiseIncident>(), i => i.ErrorClass == "loop");
        Assert.Null(spun.Outcome);
    }

    [Fact]
    public void A_failing_expression_or_decision_raises_an_incident_and_retry_runs_the_element_again()
    {
        var graph = Read(Model($"""
            <bpmn:startEvent id="s"/>
            <bpmn:businessRuleTask id="pick"><bpmn:extensionElements><tb:decision key="unknown" resultVariable="level"/></bpmn:extensionElements></bpmn:businessRuleTask>
            <bpmn:endEvent id="e"/>{Flow("f1", "s", "pick")}{Flow("f2", "pick", "e")}
            """));
        var run = new Run(graph).Start();
        var incident = run.All.OfType<RaiseIncident>().Single();
        Assert.Equal(("pick", "decision"), (incident.ElementId, incident.ErrorClass));
        Assert.Equal(["pick"], run.Waiting);
        var executor = new Executor(graph, run.State, new Context());
        Assert.Equal("completed", executor.Skip(incident.Token).OfType<Finished>().Single().Outcome);
    }
}
