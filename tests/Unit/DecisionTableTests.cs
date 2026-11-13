using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Decisions;
using TeamBeats.Modules.Workflow.Feel;
using TeamBeats.Modules.Workflow.Graph;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 024 T012 (FR-007): DMN decision tables read, validate (overlaps under UNIQUE), and evaluate under every hit policy.</summary>
public sealed class DecisionTableTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static string Dmn(string policy, string inputs, string outputs, string rules, string aggregation = "") => $"""
        <definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="defs" name="Defs">
          <decision id="approver" name="Approver"><tb:name xml:lang="ar">المعتمد</tb:name>
            <decisionTable id="table" hitPolicy="{policy}"{(aggregation.Length > 0 ? $" aggregation=\"{aggregation}\"" : "")}>
              {inputs}{outputs}{rules}
            </decisionTable>
          </decision>
        </definitions>
        """;

    private static string Input(string id, string expression) => $"""<input id="{id}" label="{id}"><inputExpression typeRef="string"><text>{System.Security.SecurityElement.Escape(expression)}</text></inputExpression></input>""";

    private static string Output(string name, string? values = null) =>
        $"""<output id="o_{name}" name="{name}">{(values is null ? "" : $"<outputValues><text>{System.Security.SecurityElement.Escape(values)}</text></outputValues>")}</output>""";

    private static string Rule(string id, string[] inputs, params string[] outputs) =>
        $"""<rule id="{id}">{string.Concat(inputs.Select(i => $"<inputEntry><text>{System.Security.SecurityElement.Escape(i)}</text></inputEntry>"))}{string.Concat(outputs.Select(o => $"<outputEntry><text>{System.Security.SecurityElement.Escape(o)}</text></outputEntry>"))}</rule>""";

    private static DecisionTable Read(string xml)
    {
        var (table, problems) = DecisionTable.Read("approver", xml);
        Assert.True(problems.Count == 0, string.Join(", ", problems.Select(p => $"{p.ElementId}:{p.Key}")));
        return table!;
    }

    private static (JsonNode? Result, IReadOnlyList<int> Rules) Eval(DecisionTable table, string variables) =>
        table.Evaluate(FeelEngine.Variables(JsonNode.Parse(variables)!.AsObject()), Now);

    private static string Amounts(string policy, string aggregation = "", string? values = null) => Dmn(policy,
        Input("amount", "amount") + Input("unit", "unit"), Output("approver", values),
        Rule("r1", ["< 1000", "-"], "\"manager\"") + Rule("r2", ["[1000..10000]", "\"finance\", \"legal\""], "\"head\"")
        + Rule("r3", ["> 10000", "-"], "\"ceo\"") + Rule("r4", ["-", "\"legal\""], "\"counsel\""), aggregation);

    [Fact]
    public void A_table_reads_with_names_columns_and_rules()
    {
        var table = Read(Amounts("FIRST"));
        Assert.Equal(("Approver", "المعتمد"), (table.Names["en"], table.Names["ar"]));
        Assert.Equal(["amount", "unit"], table.Inputs.Select(i => i.Expression));
        Assert.Equal(4, table.Rules.Count);
        Assert.Equal(table.ToJson(), DecisionTable.FromJson(table.ToJson()).ToJson());
    }

    [Fact]
    public void Unique_refuses_overlapping_rules_at_validation_and_two_matches_at_run_time()
    {
        var (_, problems) = DecisionTable.Read("approver", Amounts("UNIQUE"));
        Assert.Contains(new ModelProblem("r4", "validation.decision.overlap"), problems);

        var clean = Read(Dmn("UNIQUE", Input("amount", "amount"), Output("approver"),
            Rule("r1", ["< 1000"], "\"manager\"") + Rule("r2", ["[1000..10000]"], "\"head\"") + Rule("r3", ["> 10000"], "\"ceo\"")));
        var edge = Eval(clean, """{"amount": 10000}""");
        Assert.Equal("\"head\"", edge.Result!.ToJsonString());
        Assert.Equal([2], edge.Rules);
        Assert.Null(Eval(clean, """{"amount": null}""").Result);

        // Cells it cannot compare are not reported; a run that matches two rules fails.
        var loose = Read(Dmn("UNIQUE", Input("amount", "amount"), Output("approver"),
            Rule("r1", ["> limit"], "\"head\"") + Rule("r2", ["> 10"], "\"manager\"")));
        Assert.Equal("unique_violated", Assert.Throws<DecisionException>(() => Eval(loose, """{"amount": 500, "limit": 100}""")).Code);
    }

    [Fact]
    public void First_any_and_priority_pick_one_result()
    {
        Assert.Equal("\"head\"", Eval(Read(Amounts("FIRST")), """{"amount": 2000, "unit": "legal"}""").Result!.ToJsonString());

        var any = Read(Amounts("ANY"));
        Assert.Equal("any_conflict", Assert.Throws<DecisionException>(() => Eval(any, """{"amount": 2000, "unit": "legal"}""")).Code);
        Assert.Equal("\"manager\"", Eval(any, """{"amount": 10, "unit": "it"}""").Result!.ToJsonString());

        var priority = Read(Amounts("PRIORITY", values: "\"ceo\",\"counsel\",\"head\",\"manager\""));
        var (result, rules) = Eval(priority, """{"amount": 2000, "unit": "legal"}""");
        Assert.Equal("\"counsel\"", result!.ToJsonString());
        Assert.Equal([4], rules);
    }

    [Fact]
    public void Collect_rule_order_and_output_order_return_lists_and_collect_aggregates()
    {
        var vars = """{"amount": 2000, "unit": "legal"}""";
        Assert.Equal("""["head","counsel"]""", Eval(Read(Amounts("RULE ORDER")), vars).Result!.ToJsonString());
        Assert.Equal("""["counsel","head"]""", Eval(Read(Amounts("OUTPUT ORDER", values: "\"ceo\",\"counsel\",\"head\",\"manager\"")), vars).Result!.ToJsonString());
        Assert.Equal("""["head","counsel"]""", Eval(Read(Amounts("COLLECT")), vars).Result!.ToJsonString());
        Assert.Equal("2", Eval(Read(Amounts("COLLECT", "COUNT")), vars).Result!.ToJsonString());

        var fees = Dmn("COLLECT", Input("days", "days"), Output("fee"), Rule("a", ["> 0"], "50") + Rule("b", ["> 5"], "20") + Rule("c", ["> 10"], "5"), "SUM");
        Assert.Equal("70", Eval(Read(fees), """{"days": 7}""").Result!.ToJsonString());
        Assert.Equal("20", Eval(Read(fees.Replace("SUM", "MIN")), """{"days": 7}""").Result!.ToJsonString());
        Assert.Equal("50", Eval(Read(fees.Replace("SUM", "MAX")), """{"days": 7}""").Result!.ToJsonString());
        Assert.Null(Eval(Read(fees), """{"days": 0}""").Result);
    }

    [Fact]
    public void Several_outputs_return_an_object_per_rule()
    {
        var table = Read(Dmn("FIRST", Input("amount", "amount"), Output("approver") + Output("days"),
            Rule("r1", ["< 1000"], "\"manager\"", "2") + Rule("r2", ["-"], "\"head\"", "5")));
        Assert.Equal("""{"approver":"head","days":5}""", Eval(table, """{"amount": 5000}""").Result!.ToJsonString());
    }

    [Fact]
    public void Validation_names_each_problem_on_its_element()
    {
        Assert.Equal([new ModelProblem("", "validation.decision.xml")], DecisionTable.Read("k", "<not-dmn/>").Problems);
        Assert.Equal([new ModelProblem("", "validation.decision.xml")],
            DecisionTable.Read("k", """<?xml version="1.0"?><!DOCTYPE d [<!ENTITY x SYSTEM "file:///etc/passwd">]><definitions xmlns="https://www.omg.org/spec/DMN/20191111/MODEL/">&x;</definitions>""").Problems);
        var problems = DecisionTable.Read("k", Dmn("WHATEVER", Input("amount", "amount >"), Output("approver"),
            Rule("r1", ["< 1000", "-"], "\"manager\"") + Rule("r2", ["> (", ], "\"x\"")).Replace("COLLECT", "")).Problems;
        Assert.Contains(new ModelProblem("table", "validation.decision.hit_policy"), problems);
        Assert.Contains(new ModelProblem("amount", "validation.decision.expression"), problems);
        Assert.Contains(new ModelProblem("r1", "validation.decision.rule_columns"), problems);
        Assert.Contains(new ModelProblem("r2", "validation.decision.expression"), problems);
        Assert.Contains(new ModelProblem("table", "validation.decision.priority_values"),
            DecisionTable.Read("k", Dmn("PRIORITY", Input("a", "a"), Output("x"), Rule("r1", ["-"], "1"))).Problems);
        Assert.Contains(new ModelProblem("table", "validation.decision.hit_policy"),
            DecisionTable.Read("k", Dmn("FIRST", Input("a", "a"), Output("x"), Rule("r1", ["-"], "1"), "SUM")).Problems);
    }

    [Fact]
    public void A_table_of_200_rules_decides_within_20_ms_at_the_95th_percentile()
    {
        var rules = new StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            rules.Append(Rule($"r{i}", [$"[{i * 100}..{i * 100 + 99}]", "\"gulf\", \"africa\"", "-"], $"\"band{i}\""));
        }
        var table = Read(Dmn("UNIQUE", Input("amount", "amount") + Input("region", "region") + Input("grade", "grade"), Output("band"), rules.ToString()));
        var variables = FeelEngine.Variables(JsonNode.Parse("""{"amount": 19950, "region": "africa", "grade": 7}""")!.AsObject());
        Assert.Equal("\"band199\"", table.Evaluate(variables, Now).Result!.ToJsonString());
        var times = new List<double>();
        for (var run = 0; run < 100; run++)
        {
            var watch = Stopwatch.StartNew();
            table.Evaluate(variables, Now);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Assert.True(times[94] < 20, $"p95 {times[94]:F2} ms");
    }
}
