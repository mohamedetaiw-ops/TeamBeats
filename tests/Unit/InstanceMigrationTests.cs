using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Graph;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 024 R3 (FR-015, US6): which running instances can move to another version, and how their state moves.</summary>
public sealed class InstanceMigrationTests
{
    private const string User = "0f8fad5b-d9cb-469f-a165-70867728950e";

    private static string Model(string body) => $"""
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
          <bpmn:process id="p" name="Purchase">{body}</bpmn:process>
        </bpmn:definitions>
        """;

    private static string F(string id, string from, string to) => $"""<bpmn:sequenceFlow id="{id}" sourceRef="{from}" targetRef="{to}"/>""";

    private static string U(string id) => $"""<bpmn:userTask id="{id}"><bpmn:extensionElements><tb:assignment users="{User}"/></bpmn:extensionElements></bpmn:userTask>""";

    private static string Sub(string id, string inner) =>
        $"""<bpmn:subProcess id="{id}"><bpmn:startEvent id="{id}_s"/>{inner}<bpmn:endEvent id="{id}_e"/></bpmn:subProcess>""";

    private static ProcessGraph Read(string body)
    {
        var (graph, problems) = BpmnReader.Read("purchase", Model(body));
        Assert.True(problems.Count == 0, string.Join(", ", problems.Select(p => $"{p.ElementId}:{p.Key}")));
        return graph!;
    }

    private sealed class Context : IExecutionContext
    {
        public DateTimeOffset Now { get; } = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);

        public (JsonNode? Result, IReadOnlyList<int> Rules) Decide(string key, IReadOnlyDictionary<string, object?> variables) => throw new DecisionException("missing");
    }

    private static InstanceState Started(ProcessGraph graph)
    {
        var state = new InstanceState();
        new Executor(graph, state, new Context()).Start([]);
        return state;
    }

    private static readonly ProcessGraph V1 = Read($"""<bpmn:startEvent id="s"/>{U("review")}{U("approve")}<bpmn:endEvent id="e"/>{F("f1", "s", "review")}{F("f2", "review", "approve")}{F("f3", "approve", "e")}""");

    private static readonly ProcessGraph V2 = Read($"""<bpmn:startEvent id="s"/>{U("review")}{U("sign_off")}<bpmn:endEvent id="e"/>{F("f1", "s", "review")}{F("f2", "review", "sign_off")}{F("f3", "sign_off", "e")}""");

    [Fact]
    public void The_proposal_maps_same_id_and_kind_and_leaves_the_rest()
    {
        var proposal = InstanceMigration.Propose(V1, V2);
        Assert.Equal("review", proposal["review"]);
        Assert.Null(proposal["approve"]);
    }

    [Fact]
    public void A_token_at_a_renamed_step_moves_with_an_explicit_mapping_only()
    {
        var state = Started(V1);
        new Executor(V1, state, new Context()).Complete(state.Tokens.Single().Id, []);
        Assert.Equal("approve", state.Tokens.Single().ElementId);

        var refused = InstanceMigration.Check(V1, V2, new Dictionary<string, string>(), state, []);
        Assert.Equal((false, InstanceMigration.Unmapped, "approve"), (refused.Migratable, refused.Reason, refused.ElementId));

        var mapping = new Dictionary<string, string> { ["approve"] = "sign_off" };
        Assert.True(InstanceMigration.Check(V1, V2, mapping, state, []).Migratable);
        InstanceMigration.Apply(V2, mapping, state);
        Assert.Equal("sign_off", state.Tokens.Single().ElementId);
        Assert.Equal(1, state.Visits["sign_off"]);
        Assert.False(state.Visits.ContainsKey("approve"));
    }

    [Fact]
    public void A_mapping_to_another_kind_or_a_wait_that_is_gone_is_refused()
    {
        var timer = Read($"""<bpmn:startEvent id="s"/>{U("review")}<bpmn:manualTask id="file"/><bpmn:boundaryEvent id="late" attachedToRef="review"><bpmn:timerEventDefinition><bpmn:timeDuration>P2D</bpmn:timeDuration></bpmn:timerEventDefinition></bpmn:boundaryEvent>{U("chase")}<bpmn:endEvent id="e"/>{F("f1", "s", "review")}{F("f2", "review", "file")}{F("f3", "file", "e")}{F("f4", "late", "chase")}{F("f5", "chase", "e")}""");
        var plain = Read($"""<bpmn:startEvent id="s"/>{U("review")}<bpmn:endEvent id="e"/>{F("f1", "s", "review")}{F("f2", "review", "e")}""");
        var state = Started(timer);
        var token = state.Tokens.Single();

        var waits = new[] { new OpenWait(token.Id, "late") };
        Assert.Equal(InstanceMigration.WaitChanged, InstanceMigration.Check(timer, plain, new Dictionary<string, string>(), state, waits).Reason);
        Assert.True(InstanceMigration.Check(timer, timer, new Dictionary<string, string>(), state, waits).Migratable);

        var kinds = Read($"""<bpmn:startEvent id="s"/><bpmn:manualTask id="review"/><bpmn:endEvent id="e"/>{F("f1", "s", "review")}{F("f2", "review", "e")}""");
        Assert.Equal(InstanceMigration.KindChanged, InstanceMigration.Check(timer, kinds, new Dictionary<string, string>(), state, []).Reason);
    }

    [Fact]
    public void An_instance_inside_a_sub_process_moves_only_when_the_sub_process_is_unchanged()
    {
        var before = Read($"""<bpmn:startEvent id="s"/>{Sub("sub", U("inner") + F("i1", "sub_s", "inner") + F("i2", "inner", "sub_e"))}<bpmn:endEvent id="e"/>{F("f1", "s", "sub")}{F("f2", "sub", "e")}""");
        var same = Read($"""<bpmn:startEvent id="s"/>{U("extra")}{Sub("sub", U("inner") + F("i1", "sub_s", "inner") + F("i2", "inner", "sub_e"))}<bpmn:endEvent id="e"/>{F("f1", "s", "extra")}{F("f0", "extra", "sub")}{F("f2", "sub", "e")}""");
        var changed = Read($"""<bpmn:startEvent id="s"/>{Sub("sub", U("inner") + U("more") + F("i1", "sub_s", "inner") + F("i2", "inner", "more") + F("i3", "more", "sub_e"))}<bpmn:endEvent id="e"/>{F("f1", "s", "sub")}{F("f2", "sub", "e")}""");
        var state = Started(before);
        Assert.Contains(state.Tokens, t => t.ElementId == "inner");

        Assert.True(InstanceMigration.Check(before, same, new Dictionary<string, string>(), state, []).Migratable);
        var refused = InstanceMigration.Check(before, changed, new Dictionary<string, string>(), state, []);
        Assert.Equal((InstanceMigration.StructureChanged, "sub"), (refused.Reason, refused.ElementId));
    }

    [Fact]
    public void A_multi_instance_with_another_loop_is_refused()
    {
        string Loop(string collection) => $"""<bpmn:startEvent id="s"/><bpmn:userTask id="each"><bpmn:extensionElements><tb:assignment users="{User}"/><tb:loop collection="{collection}" elementVariable="item"/></bpmn:extensionElements><bpmn:multiInstanceLoopCharacteristics/></bpmn:userTask><bpmn:endEvent id="e"/>{F("f1", "s", "each")}{F("f2", "each", "e")}""";
        var before = Read(Loop("items"));
        var state = new InstanceState();
        new Executor(before, state, new Context()).Start(new JsonObject { ["items"] = new JsonArray(1, 2), ["others"] = new JsonArray(1) });
        Assert.True(InstanceMigration.Check(before, Read(Loop("items")), new Dictionary<string, string>(), state, []).Migratable);
        Assert.Equal(InstanceMigration.StructureChanged, InstanceMigration.Check(before, Read(Loop("others")), new Dictionary<string, string>(), state, []).Reason);
    }
}
