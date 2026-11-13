using System.Text.Json.Nodes;
using TeamBeats.Modules.Workflow.Feel;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 024 T010: the FEEL subset (FR-005) parses, evaluates in a sandbox, and keeps to its limits.</summary>
public sealed class FeelTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, object?> Vars = FeelEngine.Variables(JsonNode.Parse("""
        { "amount": 12500, "category": "hardware", "requester": { "name": "Layla", "grade": 7 },
          "items": [ { "sku": "a", "qty": 2 }, { "sku": "b", "qty": 5 } ], "approved": true, "start": "2026-11-01" }
        """)!.AsObject());

    private static object? Eval(string expression) => FeelEngine.Evaluate(expression, Vars, Now);

    [Theory]
    [InlineData("amount > 10000", true)]
    [InlineData("amount >= 12500 and category = \"hardware\"", true)]
    [InlineData("amount < 100 or approved", true)]
    [InlineData("not(approved)", false)]
    [InlineData("requester.grade between 5 and 8", true)]
    [InlineData("category in (\"software\", \"hardware\")", true)]
    [InlineData("amount in [10000..20000)", true)]
    [InlineData("amount in (12500..20000]", false)]
    [InlineData("starts with(requester.name, \"La\")", true)]
    [InlineData("contains(upper case(category), \"WARE\")", true)]
    [InlineData("list contains([1, 2, 3], 2)", true)]
    [InlineData("date(start) < today()", true)]
    [InlineData("date(start) + duration(\"P14D\") = date(\"2026-11-15\")", true)]
    [InlineData("missing = null", true)]
    [InlineData("is null(missing)", true)]
    public void Conditions_evaluate(string expression, bool expected) => Assert.Equal(expected, FeelEngine.Condition(expression, Vars, Now));

    [Fact]
    public void Arithmetic_paths_filters_and_functions()
    {
        Assert.Equal(12600m, Eval("amount + 100"));
        Assert.Equal(1.5m, Eval("3 / 2"));
        Assert.Null(Eval("1 / 0"));
        Assert.Equal(8m, Eval("2 ** 3"));
        Assert.Equal(7m, Eval("sum(items.qty)"));
        Assert.Equal(new List<object?> { "b" }, Eval("items[qty > 3].sku"));
        Assert.Equal("b", Eval("items[-1].sku"));
        Assert.Equal(2m, Eval("count(items)"));
        Assert.Equal("ayl", Eval("substring(requester.name, 2, 3)"));
        Assert.Equal("high", Eval("if amount > 10000 then \"high\" else \"low\""));
        Assert.Equal(5m, Eval("max(2, 5, 3)"));
        Assert.Equal(1m, Eval("min([4, 1, 9])"));
        Assert.Equal(13m, Eval("round(12.5)"));
        Assert.Equal(new DateOnly(2026, 11, 13), Eval("today()"));
        Assert.Equal(new FeelDuration(0, TimeSpan.FromDays(12)), Eval("today() - date(start)"));
        Assert.Equal(new FeelDuration(14, TimeSpan.Zero), Eval("duration(\"P1Y2M\")"));
        var ctx = Assert.IsType<Dictionary<string, object?>>(Eval("{ level: \"head\", limit: amount * 2 }"));
        Assert.Equal(25000m, ctx["limit"]);
    }

    [Fact]
    public void Comparisons_with_null_or_mixed_types_give_null_and_conditions_treat_it_as_false()
    {
        Assert.Null(Eval("missing > 3"));
        Assert.Null(Eval("category > 3"));
        Assert.False(FeelEngine.Condition("missing > 3", Vars, Now));
        Assert.Null(Eval("missing > 3 and true"));
        Assert.Equal(false, Eval("missing > 3 and false"));
        Assert.Equal(true, Eval("missing > 3 or true"));
    }

    [Theory]
    [InlineData("-", 5, true)]
    [InlineData("< 10", 5, true)]
    [InlineData(">= 10", 5, false)]
    [InlineData("[1..5]", 5, true)]
    [InlineData("[1..5)", 5, false)]
    [InlineData("1, 3, 5", 5, true)]
    [InlineData("not(1, 3, 5)", 5, false)]
    [InlineData("not(< 3)", 5, true)]
    [InlineData("> amount", 20000, true)]
    public void Unary_tests_of_decision_cells(string tests, int input, bool expected) =>
        Assert.Equal(expected, FeelEngine.Test(tests, (decimal)input, Vars, Now));

    [Fact]
    public void String_unary_tests()
    {
        Assert.True(FeelEngine.Test("\"hardware\", \"software\"", "hardware", Vars, Now));
        Assert.False(FeelEngine.Test("\"travel\"", "hardware", Vars, Now));
    }

    [Theory]
    [InlineData("amount >", "unexpected:")]
    [InlineData("\"open", "string_unclosed")]
    [InlineData("amount # 2", "unexpected_character")]
    [InlineData("(1 + 2", "expected:)")]
    [InlineData("if amount then 1", "expected:else")]
    public void Syntax_errors_are_reported(string expression, string code)
    {
        var problem = FeelEngine.CheckExpression(expression);
        Assert.NotNull(problem);
        Assert.StartsWith(code, problem);
    }

    [Fact]
    public void Only_listed_functions_exist_and_nothing_reaches_the_host()
    {
        Assert.Null(FeelEngine.CheckExpression("upper case(\"a\")"));
        // An unknown name is a variable, and unknown variables are null: nothing resolves to a type, file or process.
        Assert.Null(Eval("System.IO.File"));
        Assert.Null(Eval("environment.PATH"));
        Assert.NotNull(FeelEngine.CheckExpression("exec(\"rm\")"));
        Assert.Throws<FeelSyntaxException>(() => FeelEngine.Evaluate("exec(\"rm\")", Vars, Now));
    }

    [Fact]
    public void Limits_stop_runaway_work_and_huge_results()
    {
        var big = string.Join(" + ", Enumerable.Repeat("1", 900));
        Assert.Equal(900m, Eval(big));
        var deep = new string('(', 200) + "1" + new string(')', 200);
        Assert.NotNull(FeelEngine.CheckExpression(deep));
        Assert.NotNull(FeelEngine.CheckExpression(new string('x', FeelLexer_MaxLength + 1)));
        var list = "[" + string.Join(",", Enumerable.Range(0, 900).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture))) + "]";
        var vars = new Dictionary<string, object?> { ["l"] = FeelEngine.Evaluate(list, Vars, Now) };
        var ex = Assert.Throws<FeelEvaluationException>(() => FeelEngine.Evaluate("l[item > 0][item > 0][item > 0][item > 0][item > 0][item > 0][item > 0][item > 0]", vars, Now));
        Assert.Equal("limit", ex.Code);
    }

    private const int FeelLexer_MaxLength = 4000;

    [Fact]
    public void Values_round_trip_to_JSON()
    {
        var value = Eval("{ due: date(\"2026-12-01\"), wait: duration(\"P3D\"), n: 2.5, tags: [\"a\"] }");
        var json = FeelEngine.ToJson(value)!.ToJsonString();
        Assert.Equal("""{"due":"2026-12-01","wait":"P3D","n":2.5,"tags":["a"]}""", json);
    }
}
