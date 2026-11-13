using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Graph;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 024 SC-002 conformance suite: one reference model per supported element and event kind, run on every build.
/// Each case reads its model, runs a short script of signals and checks the end state. A new element type without a case
/// fails <see cref="Every_supported_element_type_has_a_reference_model"/>.
/// </summary>
public sealed class ProcessConformanceTests
{
    private const string User = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static string Model(string body, string definitions = "") => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          <bpmn:message id="m" name="Ping"/><bpmn:error id="err" errorCode="E1"/><bpmn:escalation id="esc" escalationCode="X1"/>{definitions}
          <bpmn:process id="p" name="Conformance">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string F(string id, string from, string to, string? condition = null) => condition is null
        ? $"""<bpmn:sequenceFlow id="f{id}" sourceRef="{from}" targetRef="{to}"/>"""
        : $"""<bpmn:sequenceFlow id="f{id}" sourceRef="{from}" targetRef="{to}"><bpmn:conditionExpression>{System.Security.SecurityElement.Escape(condition)}</bpmn:conditionExpression></bpmn:sequenceFlow>""";

    private static string U(string id) => $"""<bpmn:userTask id="{id}"><bpmn:extensionElements><tb:assignment users="{User}"/></bpmn:extensionElements></bpmn:userTask>""";

    private const string Start = """<bpmn:startEvent id="s"/>""";
    private const string End = """<bpmn:endEvent id="e"/>""";

    private sealed class Context : IExecutionContext
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

        public (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string key, IReadOnlyDictionary<string, object?> variables) =>
            key == "grade" ? (JsonValue.Create("gold"), [1]) : throw new DecisionException("missing");
    }

    /// <summary>A reference model, the signals sent after start, the expected outcome (null while running) and the elements still waiting.</summary>
    public sealed record Case(string Name, string[] Covers, string Xml, string[] Script, string? Outcome, string[] Waiting, string? StartAt = null, string Variables = """{"ref": "A"}""")
    {
        public override string ToString() => Name;
    }

    private static readonly Case[] Cases =
    [
        new("none start, user task, none end", ["startEvent", "userTask", "endEvent"],
            Model($"{Start}{U("t")}{End}{F("1", "s", "t")}{F("2", "t", "e")}"), ["complete:t"], "completed", []),
        new("manual task passes through", ["manualTask"],
            Model($"""{Start}<bpmn:manualTask id="m1"/>{End}{F("1", "s", "m1")}{F("2", "m1", "e")}"""), [], "completed", []),
        new("send task sends its message and moves on", ["sendTask"],
            Model($"""{Start}<bpmn:sendTask id="send" messageRef="m"/>{End}{F("1", "s", "send")}{F("2", "send", "e")}"""), [], "completed", []),
        new("receive task waits for its message", ["receiveTask"],
            Model($"""{Start}<bpmn:receiveTask id="r" messageRef="m"><bpmn:extensionElements><tb:correlation key="ref"/></bpmn:extensionElements></bpmn:receiveTask>{End}{F("1", "s", "r")}{F("2", "r", "e")}"""), ["fire:r:r"], "completed", []),
        new("service task waits for its result", ["serviceTask"],
            Model($"""{Start}<bpmn:serviceTask id="call"><bpmn:extensionElements><tb:connector connection="{Guid.NewGuid()}" action="post"/></bpmn:extensionElements></bpmn:serviceTask>{End}{F("1", "s", "call")}{F("2", "call", "e")}"""),
            ["complete:call"], "completed", []),
        new("business rule task writes the decision result", ["businessRuleTask"],
            Model($"""{Start}<bpmn:businessRuleTask id="rule"><bpmn:extensionElements><tb:decision key="grade" resultVariable="tier"/></bpmn:extensionElements></bpmn:businessRuleTask><bpmn:exclusiveGateway id="x" default="f2"/>{U("gold")}{End}{F("1", "s", "rule")}{F("0", "rule", "x")}{F("2", "x", "e")}{F("3", "x", "gold", "tier = \"gold\"")}{F("4", "gold", "e")}"""),
            [], null, ["gold"]),
        new("exclusive gateway takes the first true flow or the default", ["exclusiveGateway"],
            Model($"""{Start}<bpmn:exclusiveGateway id="x" default="f3"/>{U("big")}{End}{F("1", "s", "x")}{F("2", "x", "big", "amount > 100")}{F("3", "x", "e")}{F("4", "big", "e")}"""),
            [], "completed", [], Variables: """{"amount": 50, "ref": "A"}"""),
        new("parallel gateway splits and joins", ["parallelGateway"],
            Model($"""{Start}<bpmn:parallelGateway id="split"/>{U("a")}{U("b")}<bpmn:parallelGateway id="join"/>{End}{F("1", "s", "split")}{F("2", "split", "a")}{F("3", "split", "b")}{F("4", "a", "join")}{F("5", "b", "join")}{F("6", "join", "e")}"""),
            ["complete:a"], null, ["b"]),
        new("inclusive gateway joins only the flows it took", ["inclusiveGateway"],
            Model($"""{Start}<bpmn:inclusiveGateway id="split"/>{U("a")}{U("b")}<bpmn:inclusiveGateway id="join"/>{End}{F("1", "s", "split")}{F("2", "split", "a", "true")}{F("3", "split", "b", "false")}{F("4", "a", "join")}{F("5", "b", "join")}{F("6", "join", "e")}"""),
            ["complete:a"], "completed", []),
        new("event-based gateway takes the first event", ["eventBasedGateway", "intermediateCatchEvent:timer", "intermediateCatchEvent:message"],
            Model($"""{Start}<bpmn:eventBasedGateway id="g"/><bpmn:intermediateCatchEvent id="t"><bpmn:timerEventDefinition><bpmn:timeDuration>PT1H</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:intermediateCatchEvent><bpmn:intermediateCatchEvent id="msg"><bpmn:extensionElements><tb:correlation key="ref"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:intermediateCatchEvent>{U("late")}{End}{F("1", "s", "g")}{F("2", "g", "t")}{F("3", "g", "msg")}{F("4", "t", "late")}{F("5", "msg", "e")}{F("6", "late", "e")}"""),
            ["fire:g:t"], null, ["late"]),
        new("intermediate throw events pass through", ["intermediateThrowEvent:none", "intermediateThrowEvent:message"],
            Model($"""{Start}<bpmn:intermediateThrowEvent id="mark"/><bpmn:intermediateThrowEvent id="tell"><bpmn:messageEventDefinition messageRef="m"/></bpmn:intermediateThrowEvent>{End}{F("1", "s", "mark")}{F("2", "mark", "tell")}{F("3", "tell", "e")}"""),
            [], "completed", []),
        new("terminate end stops every branch", ["endEvent:terminate"],
            Model($"""{Start}{U("t")}<bpmn:boundaryEvent id="late" attachedToRef="t" cancelActivity="false"><bpmn:timerEventDefinition><bpmn:timeDuration>P5D</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:boundaryEvent><bpmn:endEvent id="stop"><bpmn:terminateEventDefinition/></bpmn:endEvent>{End}{F("1", "s", "t")}{F("2", "t", "e")}{F("3", "late", "stop")}"""),
            ["fire:t:late"], "terminated", []),
        new("error end without a catch ends the instance with the error", ["endEvent:error"],
            Model($"""{Start}<bpmn:endEvent id="fail"><bpmn:errorEventDefinition errorRef="err"/></bpmn:endEvent>{F("1", "s", "fail")}"""), [], "error", []),
        new("error boundary on a sub-process catches its error end", ["subProcess", "boundaryEvent:error"],
            Model($"""{Start}<bpmn:subProcess id="sub"><bpmn:startEvent id="ss"/><bpmn:endEvent id="se"><bpmn:errorEventDefinition errorRef="err"/></bpmn:endEvent>{F("s1", "ss", "se")}</bpmn:subProcess><bpmn:boundaryEvent id="caught" attachedToRef="sub"><bpmn:errorEventDefinition errorRef="err"/></bpmn:boundaryEvent>{U("fix")}{End}{F("1", "s", "sub")}{F("2", "sub", "e")}{F("3", "caught", "fix")}{F("4", "fix", "e")}"""),
            [], null, ["fix"]),
        new("escalation end is caught by a non-interrupting boundary", ["endEvent:escalation", "boundaryEvent:escalation", "intermediateThrowEvent:escalation"],
            Model($"""{Start}<bpmn:subProcess id="sub"><bpmn:startEvent id="ss"/><bpmn:intermediateThrowEvent id="raise"><bpmn:escalationEventDefinition escalationRef="esc"/></bpmn:intermediateThrowEvent>{U("inner")}<bpmn:endEvent id="se"/>{F("s1", "ss", "raise")}{F("s2", "raise", "inner")}{F("s3", "inner", "se")}</bpmn:subProcess><bpmn:boundaryEvent id="heard" attachedToRef="sub" cancelActivity="false"><bpmn:escalationEventDefinition escalationRef="esc"/></bpmn:boundaryEvent>{U("notify")}{End}<bpmn:endEvent id="e2"/>{F("1", "s", "sub")}{F("2", "sub", "e")}{F("3", "heard", "notify")}{F("4", "notify", "e2")}"""),
            [], null, ["inner", "notify", "sub"]),
        new("timer boundary interrupts the task", ["boundaryEvent:timer"],
            Model($"""{Start}{U("t")}<bpmn:boundaryEvent id="late" attachedToRef="t"><bpmn:timerEventDefinition><bpmn:timeDuration>P2D</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:boundaryEvent>{U("chase")}{End}{F("1", "s", "t")}{F("2", "t", "e")}{F("3", "late", "chase")}{F("4", "chase", "e")}"""),
            ["fire:t:late"], null, ["chase"]),
        new("message boundary that does not interrupt runs beside the task", ["boundaryEvent:message"],
            Model($"""{Start}{U("t")}<bpmn:boundaryEvent id="note" attachedToRef="t" cancelActivity="false"><bpmn:extensionElements><tb:correlation key="ref"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:boundaryEvent>{U("read")}{End}<bpmn:endEvent id="e2"/>{F("1", "s", "t")}{F("2", "t", "e")}{F("3", "note", "read")}{F("4", "read", "e2")}"""),
            ["fire:t:note"], null, ["read", "t"]),
        new("event sub-process starts on its message", ["subProcess:event", "startEvent:message"],
            Model($"""{Start}{U("t")}{End}<bpmn:subProcess id="ev" triggeredByEvent="true"><bpmn:startEvent id="es" isInterrupting="false"><bpmn:extensionElements><tb:correlation key="ref"/></bpmn:extensionElements><bpmn:messageEventDefinition messageRef="m"/></bpmn:startEvent>{U("side")}<bpmn:endEvent id="ee"/>{F("s1", "es", "side")}{F("s2", "side", "ee")}</bpmn:subProcess>{F("1", "s", "t")}{F("2", "t", "e")}"""),
            ["fire:es:es"], null, ["side", "t"]),
        new("timer start event starts the instance at that event", ["startEvent:timer"],
            Model($"""<bpmn:startEvent id="tick"><bpmn:timerEventDefinition><bpmn:timeCycle>R/P1D</bpmn:timeCycle></bpmn:timerEventDefinition></bpmn:startEvent>{U("t")}{End}{F("1", "tick", "t")}{F("2", "t", "e")}"""),
            ["complete:t"], "completed", [], StartAt: "tick"),
        new("call activity waits for its child", ["callActivity"],
            Model($"""{Start}<bpmn:callActivity id="c" calledElement="child"/>{End}{F("1", "s", "c")}{F("2", "c", "e")}"""), ["complete:c"], "completed", []),
        new("multi-instance runs one body per item", ["multiInstance"],
            Model($"""{Start}<bpmn:userTask id="each"><bpmn:extensionElements><tb:assignment users="{User}"/><tb:loop collection="items" elementVariable="item"/></bpmn:extensionElements><bpmn:multiInstanceLoopCharacteristics/></bpmn:userTask>{End}{F("1", "s", "each")}{F("2", "each", "e")}"""),
            ["complete:each"], null, ["each"], Variables: """{"items": [1, 2]}"""),
    ];

    public static TheoryData<Case> All()
    {
        var data = new TheoryData<Case>();
        foreach (var c in Cases)
        {
            data.Add(c);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Reference_model_runs_as_specified(Case c)
    {
        var (graph, problems) = BpmnReader.Read("conformance", c.Xml);
        Assert.True(problems.Count == 0, string.Join(", ", problems.Select(p => $"{p.ElementId}:{p.Key}")));
        var state = new InstanceState();
        var effects = new List<Effect>();
        effects.AddRange(new Executor(graph!, state, new Context()).Start(JsonNode.Parse(c.Variables)!.AsObject(), c.StartAt));
        foreach (var step in c.Script)
        {
            var parts = step.Split(':');
            var token = state.Tokens.First(t => t.ElementId == parts[1] && t.Status is "waiting" or "listening");
            var executor = new Executor(graph!, state, new Context());
            effects.AddRange(parts[0] switch
            {
                "complete" => executor.Complete(token.Id, []),
                "fire" => executor.Fire(token.Id, parts[2], null),
                _ => throw new InvalidOperationException(step),
            });
        }
        Assert.Equal(c.Outcome, effects.OfType<Finished>().LastOrDefault()?.Outcome);
        Assert.Equal(c.Waiting, state.Tokens.Where(t => t.Status is "waiting" or "incident").Select(t => t.ElementId).Order().Distinct());
    }

    [Fact]
    public void Every_supported_element_type_has_a_reference_model()
    {
        var covered = Cases.SelectMany(c => c.Covers).Select(c => c.Split(':')[0]).ToHashSet();
        Assert.Equal([], ElementTypes.All.Where(t => !covered.Contains(t)).Order().ToArray());
    }
}
