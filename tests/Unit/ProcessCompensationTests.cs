using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Graph;
using Record = TeamBeats.Modules.Workflow.Graph.Record;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 024 R3 (FR-011, US4 scenario 3): compensation. A completed activity with a compensation boundary can be undone by
/// its handler; a compensation throw or end event runs the handlers of the activities completed in its scope, newest
/// first, one at a time, each once. Skipped activities did no work and are not compensated.
/// </summary>
public sealed class ProcessCompensationTests
{
    private const string User = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static string Model(string body) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          <bpmn:process id="p" name="Purchase">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string F(string id, string from, string to) => $"""<bpmn:sequenceFlow id="f{id}" sourceRef="{from}" targetRef="{to}"/>""";

    private static string U(string id, bool handler = false) =>
        $"""<bpmn:userTask id="{id}"{(handler ? " isForCompensation=\"true\"" : "")}><bpmn:extensionElements><tb:assignment users="{User}"/></bpmn:extensionElements></bpmn:userTask>""";

    /// <summary>An activity, its compensation boundary and the handler the boundary names.</summary>
    private static string Compensable(string activity, string handler) =>
        $"""{U(activity)}<bpmn:boundaryEvent id="{activity}_c" attachedToRef="{activity}"><bpmn:compensateEventDefinition/></bpmn:boundaryEvent>{U(handler, true)}<bpmn:association id="{activity}_a" sourceRef="{activity}_c" targetRef="{handler}"/>""";

    private const string Start = """<bpmn:startEvent id="s"/>""";

    private sealed class Context : IExecutionContext
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

        public (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string key, IReadOnlyDictionary<string, object?> variables) => throw new DecisionException("missing");
    }

    private sealed class Run
    {
        private readonly ProcessGraph graph;

        public Run(string xml)
        {
            var (g, problems) = BpmnReader.Read("purchase", xml);
            Assert.True(problems.Count == 0, string.Join(", ", problems.Select(p => $"{p.ElementId}:{p.Key}")));
            graph = g!;
            All.AddRange(new Executor(graph, State, new Context()).Start([]));
        }

        public InstanceState State { get; } = new();
        public List<Effect> All { get; } = [];
        public string? Outcome => All.OfType<Finished>().LastOrDefault()?.Outcome;
        public string[] Waiting => [.. State.Tokens.Where(t => t.Status is "waiting" or "incident").Select(t => t.ElementId).Order()];
        public string[] Compensated => [.. All.OfType<Record>().Where(r => r.Event == "compensated").Select(r => r.ElementId)];

        public Run Complete(string element)
        {
            var token = State.Tokens.Single(t => t.ElementId == element && t.Status == "waiting");
            All.AddRange(new Executor(graph, State, new Context()).Complete(token.Id, []));
            return this;
        }

        public Run Skip(string element)
        {
            var token = State.Tokens.Single(t => t.ElementId == element && t.Status == "waiting");
            All.AddRange(new Executor(graph, State, new Context()).Skip(token.Id));
            return this;
        }
    }

    private static string Purchase(string end) => Model($"""
        {Start}{Compensable("reserve", "release")}{Compensable("order", "cancel")}{U("approve")}
        {end}
        {F("1", "s", "reserve")}{F("2", "reserve", "order")}{F("3", "order", "approve")}{F("4", "approve", "e")}
        """);

    [Fact]
    public void A_compensation_end_event_undoes_the_completed_steps_newest_first()
    {
        var run = new Run(Purchase("""<bpmn:endEvent id="e"><bpmn:compensateEventDefinition/></bpmn:endEvent>"""))
            .Complete("reserve").Complete("order").Complete("approve");
        // The purchase order was created last, so it is cancelled first; the budget is released after that.
        Assert.Equal(["cancel"], run.Waiting);
        Assert.Null(run.Outcome);
        run.Complete("cancel");
        Assert.Equal(["release"], run.Waiting);
        run.Complete("release");
        Assert.Equal(["order", "reserve"], run.Compensated);
        Assert.Equal("completed", run.Outcome);
        Assert.Empty(run.State.Compensable);
        var record = run.All.OfType<Record>().Single(r => r.Event == "compensation");
        Assert.Equal(["order", "reserve"], record.Detail!["activities"]!.AsArray().Select(a => a!.GetValue<string>()));
    }

    [Fact]
    public void A_compensation_throw_event_waits_for_the_handlers_then_goes_on()
    {
        var run = new Run(Model($"""
            {Start}{Compensable("reserve", "release")}
            <bpmn:intermediateThrowEvent id="undo"><bpmn:compensateEventDefinition/></bpmn:intermediateThrowEvent>
            {U("after")}<bpmn:endEvent id="e"/>
            {F("1", "s", "reserve")}{F("2", "reserve", "undo")}{F("3", "undo", "after")}{F("4", "after", "e")}
            """)).Complete("reserve");
        Assert.Equal(["release"], run.Waiting);
        run.Complete("release");
        Assert.Equal(["after"], run.Waiting);
        run.Complete("after");
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void An_activity_reference_compensates_only_that_activity_and_each_activity_once()
    {
        var run = new Run(Model($"""
            {Start}{Compensable("reserve", "release")}{Compensable("order", "cancel")}
            <bpmn:intermediateThrowEvent id="undo_order"><bpmn:compensateEventDefinition activityRef="order"/></bpmn:intermediateThrowEvent>
            <bpmn:endEvent id="e"><bpmn:compensateEventDefinition/></bpmn:endEvent>
            {F("1", "s", "reserve")}{F("2", "reserve", "order")}{F("3", "order", "undo_order")}{F("4", "undo_order", "e")}
            """)).Complete("reserve").Complete("order");
        Assert.Equal(["cancel"], run.Waiting);
        run.Complete("cancel");
        // The end event compensates what is left: the order was already undone, so only the reservation.
        Assert.Equal(["release"], run.Waiting);
        run.Complete("release");
        Assert.Equal(["order", "reserve"], run.Compensated);
        Assert.Equal("completed", run.Outcome);
    }

    [Fact]
    public void A_skipped_activity_is_not_compensated_and_nothing_to_undo_goes_straight_on()
    {
        var run = new Run(Purchase("""<bpmn:endEvent id="e"><bpmn:compensateEventDefinition/></bpmn:endEvent>"""))
            .Skip("reserve").Complete("order").Complete("approve");
        Assert.Equal(["cancel"], run.Waiting);
        run.Complete("cancel");
        Assert.Equal(["order"], run.Compensated);
        Assert.Equal("completed", run.Outcome);

        var none = new Run(Purchase("""<bpmn:endEvent id="e"><bpmn:compensateEventDefinition/></bpmn:endEvent>"""))
            .Skip("reserve").Skip("order").Complete("approve");
        Assert.Empty(none.Compensated);
        Assert.Equal("completed", none.Outcome);
    }

    [Fact]
    public void A_plain_end_event_does_not_compensate()
    {
        var run = new Run(Purchase("""<bpmn:endEvent id="e"/>""")).Complete("reserve").Complete("order").Complete("approve");
        Assert.Equal("completed", run.Outcome);
        Assert.Empty(run.Compensated);
    }

    [Fact]
    public void The_graph_reads_handlers_and_marks_the_model_as_advanced()
    {
        var (graph, problems) = BpmnReader.Read("purchase", Purchase("""<bpmn:endEvent id="e"><bpmn:compensateEventDefinition/></bpmn:endEvent>"""));
        Assert.Empty(problems);
        Assert.Equal("release", graph!.CompensationOf("reserve")!.Handler);
        Assert.True(graph.Element("release").ForCompensation);
        Assert.True(graph.UsesAdvanced);
        Assert.True(ProcessGraph.FromJson(graph.ToJson()).UsesAdvanced);

        var (plain, _) = BpmnReader.Read("purchase", Model($"""{Start}{U("t")}<bpmn:endEvent id="e"/>{F("1", "s", "t")}{F("2", "t", "e")}"""));
        Assert.False(plain!.UsesAdvanced);
    }

    public static TheoryData<string, string, string> Invalid() => new()
    {
        // The handler has a flow of its own.
        { $"""{Start}{Compensable("a", "h")}<bpmn:endEvent id="e"/>{F("1", "s", "a")}{F("2", "a", "e")}{F("3", "h", "e")}""", "h", "compensation_handler" },
        // A handler no boundary names.
        { $"""{Start}{U("a")}{U("h", true)}<bpmn:endEvent id="e"/>{F("1", "s", "a")}{F("2", "a", "e")}""", "h", "compensation_handler" },
        // A compensation boundary with a flow instead of a handler.
        { $"""{Start}{U("a")}<bpmn:boundaryEvent id="c" attachedToRef="a"><bpmn:compensateEventDefinition/></bpmn:boundaryEvent>{U("x")}<bpmn:endEvent id="e"/>{F("1", "s", "a")}{F("2", "a", "e")}{F("3", "c", "x")}{F("4", "x", "e")}""", "c", "compensation_boundary" },
        // A boundary whose handler is not marked for compensation.
        { $"""{Start}{U("a")}<bpmn:boundaryEvent id="c" attachedToRef="a"><bpmn:compensateEventDefinition/></bpmn:boundaryEvent><bpmn:association id="as" sourceRef="c" targetRef="e"/><bpmn:endEvent id="e"/>{F("1", "s", "a")}{F("2", "a", "e")}""", "c", "compensation_boundary" },
        // Two compensation boundaries on one activity.
        { $"""{Start}{Compensable("a", "h")}<bpmn:boundaryEvent id="c2" attachedToRef="a"><bpmn:compensateEventDefinition/></bpmn:boundaryEvent>{U("h2", true)}<bpmn:association id="as2" sourceRef="c2" targetRef="h2"/><bpmn:endEvent id="e"/>{F("1", "s", "a")}{F("2", "a", "e")}""", "a_c", "compensation_boundary" },
        // An activity reference to an activity with nothing to undo.
        { $"""{Start}{U("a")}<bpmn:endEvent id="e"><bpmn:compensateEventDefinition activityRef="a"/></bpmn:endEvent>{F("1", "s", "a")}{F("2", "a", "e")}""", "e", "compensation_activity" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_compensation_is_marked_on_its_element(string body, string element, string problem)
    {
        var (_, problems) = BpmnReader.Read("purchase", Model(body));
        Assert.Contains(problems, p => p.ElementId == element && p.Key == "validation.process." + problem);
    }
}
