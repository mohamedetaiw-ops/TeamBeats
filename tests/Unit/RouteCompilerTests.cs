using System.Xml.Linq;
using TeamBeats.Modules.Workflow;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 024 T002: routes compile to BPMN 2.0 with the teambeats namespace and back to the same executable graph.</summary>
public sealed class RouteCompilerTests
{
    private static readonly Guid Legal = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static RouteSpec Policy() => new("Policy approval",
    [
        new RouteStep("legal", "Legal review", new CandidateRule(Users: [Legal]), DueWorkingDays: 3),
        new RouteStep("ciso", "CISO approval", new CandidateRule(Permission: "workflow.approve")),
        new RouteStep("ceo", "CEO approval", new CandidateRule(Users: [Legal], Permission: "workflow.approve"), DueWorkingDays: 5),
    ]);

    [Fact]
    public void A_sequential_route_becomes_BPMN_with_a_gateway_per_step_and_reads_back_unchanged()
    {
        var compiled = RouteCompiler.Compile(Policy());
        var xml = RouteCompiler.ToBpmn("policy.p-17", compiled);
        var doc = XDocument.Parse(xml);
        XNamespace bpmn = RouteCompiler.BpmnNs;
        var process = doc.Root!.Element(bpmn + "process")!;

        Assert.Equal(3, process.Elements(bpmn + "userTask").Count());
        Assert.Equal(3, process.Elements(bpmn + "exclusiveGateway").Count());
        Assert.Equal(["end_approved", "end_rejected", "end_returned"], process.Elements(bpmn + "endEvent").Select(e => (string)e.Attribute("id")!).Order());
        Assert.Contains(RouteCompiler.TeamBeatsNs, xml);
        // Every flow leaves from and goes to an element of the process (no dangling references).
        var ids = process.Elements().Select(e => (string?)e.Attribute("id")).ToHashSet();
        Assert.All(process.Elements(bpmn + "sequenceFlow"), f => Assert.True(ids.Contains((string)f.Attribute("sourceRef")!) && ids.Contains((string)f.Attribute("targetRef")!)));

        var back = RouteCompiler.FromBpmn(xml);
        Assert.Equal(compiled.ToJson(), back.ToJson());
    }

    [Fact]
    public void Each_gateway_routes_reject_and_return_to_their_ends_and_approve_onwards()
    {
        var xml = RouteCompiler.ToBpmn("k", RouteCompiler.Compile(Policy()));
        XNamespace bpmn = RouteCompiler.BpmnNs;
        var flows = XDocument.Parse(xml).Descendants(bpmn + "sequenceFlow")
            .Select(f => ((string)f.Attribute("sourceRef")!, (string)f.Attribute("targetRef")!, f.Element(bpmn + "conditionExpression")?.Value)).ToList();
        Assert.Contains(("gw_legal", "ciso", "=outcome = \"approve\""), flows);
        Assert.Contains(("gw_ceo", "end_approved", "=outcome = \"approve\""), flows);
        Assert.Contains(("gw_ciso", "end_rejected", "=outcome = \"reject\""), flows);
        Assert.Contains(("gw_ciso", "end_returned", "=outcome = \"return\""), flows);
    }

    [Fact]
    public void Elements_outside_the_R1_subset_are_refused_when_reading_BPMN()
    {
        var xml = RouteCompiler.ToBpmn("k", RouteCompiler.Compile(Policy()))
            .Replace("<bpmn:endEvent id=\"end_returned\" />", "<bpmn:endEvent id=\"end_returned\" /><bpmn:scriptTask id=\"x\" />");
        Assert.Throws<FormatException>(() => RouteCompiler.FromBpmn(xml));
    }

    [Theory]
    [InlineData("no_steps")]
    [InlineData("no_candidates")]
    [InlineData("duplicate_id")]
    [InlineData("reserved_id")]
    [InlineData("bad_permission")]
    [InlineData("due_out_of_range")]
    [InlineData("too_many_steps")]
    public void Invalid_routes_are_refused_before_publishing(string problem)
    {
        var ok = new RouteStep("a", "A", new CandidateRule(Users: [Legal]));
        var route = problem switch
        {
            "no_steps" => new RouteSpec("R", []),
            "no_candidates" => new RouteSpec("R", [ok with { Candidates = new CandidateRule() }]),
            "duplicate_id" => new RouteSpec("R", [ok, ok]),
            "reserved_id" => new RouteSpec("R", [ok with { Id = "gw_a" }]),
            "bad_permission" => new RouteSpec("R", [ok with { Candidates = new CandidateRule(Permission: "Approver") }]),
            "due_out_of_range" => new RouteSpec("R", [ok with { DueWorkingDays = 0 }]),
            _ => new RouteSpec("R", Enumerable.Range(0, RouteCompiler.MaxSteps + 1).Select(i => ok with { Id = "s" + i }).ToList()),
        };
        Assert.True(RouteCompiler.Validate(route).Any);
        Assert.Throws<AppException>(() => RouteCompiler.Compile(route));
    }

    [Theory]
    [InlineData("service.leave-request", true)]
    [InlineData("policy:p-17", true)]
    [InlineData("Service", false)]
    [InlineData("", false)]
    public void Process_keys_are_lower_case_and_short(string key, bool valid) => Assert.Equal(valid, RouteCompiler.IsValidKey(key));
}
