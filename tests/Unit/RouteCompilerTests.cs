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

    [Fact]
    public void A_line_manager_step_with_or_without_a_fallback_permission_survives_BPMN()
    {
        var route = new RouteSpec("Leave", [
            new RouteStep("manager", "Manager approval", new CandidateRule(LineManager: true)),
            new RouteStep("hr", "HR approval", new CandidateRule(Permission: "workflow.approve", LineManager: true)),
        ]);
        Assert.False(RouteCompiler.Validate(route).Any);
        var compiled = RouteCompiler.Compile(route);
        var xml = RouteCompiler.ToBpmn("service.leave", compiled);
        Assert.Contains("lineManager", xml);
        var back = RouteCompiler.FromBpmn(xml);
        Assert.Equal(compiled.ToJson(), back.ToJson());
        Assert.All(back.Steps, s => Assert.True(s.LineManager));
    }

    [Fact]
    public void A_step_for_the_instances_subject_with_a_fallback_permission_survives_BPMN()
    {
        var route = new RouteSpec("Journey step", [new RouteStep("welcome", "Read the welcome pack", new CandidateRule(Permission: "hr.manage", Subject: true))]);
        Assert.False(RouteCompiler.Validate(route).Any);
        var compiled = RouteCompiler.Compile(route);
        var xml = RouteCompiler.ToBpmn("hr.journey.onboarding.1.welcome", compiled);
        Assert.Contains("subject", xml);
        var back = RouteCompiler.FromBpmn(xml);
        Assert.Equal(compiled.ToJson(), back.ToJson());
        Assert.True(Assert.Single(back.Steps).Subject);
    }

    [Theory]
    [InlineData("subject_with_users")]
    [InlineData("subject_with_line_manager")]
    [InlineData("line_manager_with_users")]
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
            "line_manager_with_users" => new RouteSpec("R", [ok with { Candidates = new CandidateRule(Users: [Legal], LineManager: true) }]),
            "subject_with_users" => new RouteSpec("R", [ok with { Candidates = new CandidateRule(Users: [Legal], Subject: true) }]),
            "subject_with_line_manager" => new RouteSpec("R", [ok with { Candidates = new CandidateRule(LineManager: true, Subject: true) }]),
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

    private static readonly Guid Desk = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private static RouteSpec WithAction() => new("New laptop",
    [
        new RouteStep("approval", "Manager approval", new CandidateRule(Permission: "workflow.approve", LineManager: true)),
        new RouteStep("action_1", "Open the IT ticket", new CandidateRule(), null,
            new ConnectorStep(Desk, "create_incident", new Dictionary<string, string> { ["summary"] = "form.device", ["requester"] = "requester.email" },
                new Dictionary<string, string> { ["invalid_category"] = BusinessErrorPaths.Reject })),
    ]);

    [Fact]
    public void A_connector_step_becomes_a_service_task_with_its_mapping_and_reads_back_unchanged()
    {
        var compiled = RouteCompiler.Compile(WithAction());
        var xml = RouteCompiler.ToBpmn("service.x", compiled);
        XNamespace bpmn = RouteCompiler.BpmnNs;
        var process = XDocument.Parse(xml).Root!.Element(bpmn + "process")!;
        var task = Assert.Single(process.Elements(bpmn + "serviceTask"));
        Assert.Equal("action_1", (string)task.Attribute("id")!);
        Assert.Contains("create_incident", xml);
        Assert.Contains("requester.email", xml);
        var ids = process.Elements().Select(e => (string?)e.Attribute("id")).ToHashSet();
        Assert.All(process.Elements(bpmn + "sequenceFlow"), f => Assert.True(ids.Contains((string)f.Attribute("sourceRef")!) && ids.Contains((string)f.Attribute("targetRef")!)));
        Assert.Equal(compiled.ToJson(), RouteCompiler.FromBpmn(xml).ToJson());
    }

    [Fact]
    public void A_connector_step_with_a_bad_mapping_or_path_is_refused()
    {
        var bad = new RouteSpec("x",
        [
            new RouteStep("action_1", "Call", new CandidateRule(Users: [Legal]), null,
                new ConnectorStep(Guid.Empty, "Bad Name", new Dictionary<string, string> { ["summary"] = "not a variable" },
                    new Dictionary<string, string> { ["invalid_category"] = "explode" })),
        ]);
        var keys = RouteCompiler.Validate(bad).Entries.Select(e => e.Key).ToHashSet();
        Assert.Contains("validation.workflow.action_connection", keys);
        Assert.Contains("validation.workflow.action_name", keys);
        Assert.Contains("validation.workflow.action_inputs", keys);
        Assert.Contains("validation.workflow.action_errors", keys);
        Assert.Contains("validation.workflow.action_candidates", keys);
    }
}
