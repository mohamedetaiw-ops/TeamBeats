using TeamBeats.Modules.Workflow.Graph;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 024 R3 (US7): BPMN files from Camunda Modeler and other tools are converted, or their unsupported parts listed.</summary>
public sealed class BpmnImporterTests
{
    private const string User = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Group = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    /// <summary>A purchase approval as Camunda Modeler 5 saves it for Camunda 8, with a pool and diagram.</summary>
    private static readonly string Camunda8 = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:bpmndi="http://www.omg.org/spec/BPMN/20100524/DI"
            xmlns:dc="http://www.omg.org/spec/DD/20100524/DC" xmlns:zeebe="http://camunda.org/schema/zeebe/1.0" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
            xmlns:modeler="http://camunda.org/schema/modeler/1.0" id="Definitions_1" targetNamespace="http://bpmn.io/schema/bpmn" exporter="Camunda Modeler"
            exporterVersion="5.20.0" modeler:executionPlatform="Camunda Cloud" modeler:executionPlatformVersion="8.4.0">
          <bpmn:collaboration id="Collab"><bpmn:participant id="Pool" processRef="purchase"/></bpmn:collaboration>
          <bpmn:process id="purchase" name="Purchase" isExecutable="true">
            <bpmn:startEvent id="start"/>
            <bpmn:userTask id="review" name="Review">
              <bpmn:extensionElements>
                <zeebe:userTask/>
                <zeebe:assignmentDefinition assignee="=requester" candidateGroups="{Group}"/>
                <zeebe:taskSchedule dueDate="=&quot;P3D&quot;"/>
              </bpmn:extensionElements>
            </bpmn:userTask>
            <bpmn:exclusiveGateway id="big" default="small"/>
            <bpmn:serviceTask id="post" name="Post order">
              <bpmn:extensionElements>
                <zeebe:taskDefinition type="erp-post" retries="5"/>
                <zeebe:ioMapping><zeebe:input source="=amount" target="total"/><zeebe:output source="=orderId" target="erpId"/></zeebe:ioMapping>
              </bpmn:extensionElements>
            </bpmn:serviceTask>
            <bpmn:receiveTask id="confirm" messageRef="Msg"/>
            <bpmn:callActivity id="ship"><bpmn:extensionElements><zeebe:calledElement processId="shipping" propagateAllChildVariables="false"/></bpmn:extensionElements></bpmn:callActivity>
            <bpmn:endEvent id="end"/>
            <bpmn:sequenceFlow id="f1" sourceRef="start" targetRef="review"/>
            <bpmn:sequenceFlow id="f2" sourceRef="review" targetRef="big"/>
            <bpmn:sequenceFlow id="large" sourceRef="big" targetRef="post"><bpmn:conditionExpression xsi:type="bpmn:tFormalExpression">=amount &gt; 1000</bpmn:conditionExpression></bpmn:sequenceFlow>
            <bpmn:sequenceFlow id="small" sourceRef="big" targetRef="end"/>
            <bpmn:sequenceFlow id="f4" sourceRef="post" targetRef="confirm"/>
            <bpmn:sequenceFlow id="f5" sourceRef="confirm" targetRef="ship"/>
            <bpmn:sequenceFlow id="f6" sourceRef="ship" targetRef="end"/>
          </bpmn:process>
          <bpmn:message id="Msg" name="Supplier confirmed"><bpmn:extensionElements><zeebe:subscription correlationKey="=orderId"/></bpmn:extensionElements></bpmn:message>
          <bpmndi:BPMNDiagram id="D"><bpmndi:BPMNPlane id="P" bpmnElement="Collab"><bpmndi:BPMNShape id="start_di" bpmnElement="start"><dc:Bounds x="100" y="100" width="36" height="36"/></bpmndi:BPMNShape></bpmndi:BPMNPlane></bpmndi:BPMNDiagram>
        </bpmn:definitions>
        """.Trim();

    [Fact]
    public void A_Camunda_8_file_converts_its_assignment_due_date_retries_mappings_messages_and_calls()
    {
        var result = BpmnImporter.Import("purchase", Camunda8);
        Assert.True(result.Unsupported.Count == 0, string.Join(", ", result.Unsupported.Select(u => $"{u.ElementId}:{u.Key}:{u.Detail}")));
        var (graph, problems) = BpmnReader.Read("purchase", result.Xml!);
        // The service step still needs a TeamBeats connector: that stays a problem of the draft, not an import refusal.
        Assert.Equal([new ModelProblem("post", "validation.process.service_binding")], problems);
        Assert.Null(graph);

        Assert.Contains("tb:assignment", result.Xml!);
        Assert.Contains($"groups=\"{Group}\"", result.Xml!);
        Assert.Contains("assignee=\"requester\"", result.Xml!);
        Assert.Contains("<tb:due workingDays=\"3\"", result.Xml!);
        Assert.Contains("<tb:connector retries=\"5\"", result.Xml!);
        Assert.Contains("<tb:input name=\"total\" source=\"amount\"", result.Xml!);
        Assert.Contains("<tb:output variable=\"erpId\" source=\"orderId\"", result.Xml!);
        Assert.Contains("<tb:correlation key=\"orderId\"", result.Xml!);
        Assert.Contains("calledElement=\"shipping\"", result.Xml!);
        Assert.Contains("amount &gt; 1000</bpmn:conditionExpression>", result.Xml!);
        Assert.DoesNotContain("zeebe", result.Xml!);
        Assert.Contains(result.Converted, c => c.ElementId == "post" && c.Key == "process.import.worker_dropped" && c.Detail == "erp-post");

        // Once a connector is bound, the converted model reads into the graph with the values it carried over.
        var bound = result.Xml!.Replace("<tb:connector retries=\"5\"", $"<tb:connector connection=\"{Guid.NewGuid()}\" action=\"post\" retries=\"5\"");
        var (ready, none) = BpmnReader.Read("purchase", bound);
        Assert.Empty(none);
        Assert.Equal("requester", ready!.Element("review").Assignment!.AssigneeExpression);
        Assert.Equal(3, ready.Element("review").DueWorkingDays);
        Assert.Equal(5, ready.Element("post").Call!.Retries);
        Assert.Equal("orderId", ready.Element("confirm").CorrelationKey);
        Assert.Equal("Supplier confirmed", ready.Element("confirm").MessageName);
    }

    [Fact]
    public void A_Camunda_7_file_converts_its_attributes_and_lists_names_forms_and_listeners()
    {
        var xml = $"""
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:camunda="http://camunda.org/schema/1.0/bpmn" id="d">
              <bpmn:process id="leave" isExecutable="true" camunda:historyTimeToLive="180">
                <bpmn:startEvent id="s" camunda:formKey="embedded:app:start.html"/>
                <bpmn:userTask id="approve" camunda:assignee="{"${initiator}"}" camunda:candidateUsers="{User},jane" camunda:candidateGroups="managers" camunda:dueDate="2026-12-01T00:00:00Z" camunda:asyncBefore="true">
                  <bpmn:extensionElements><camunda:taskListener event="create" class="org.example.Notify"/></bpmn:extensionElements>
                  <bpmn:multiInstanceLoopCharacteristics camunda:collection="{"${approvers}"}" camunda:elementVariable="approver"/>
                </bpmn:userTask>
                <bpmn:endEvent id="e"/>
                <bpmn:sequenceFlow id="f1" sourceRef="s" targetRef="approve"><bpmn:conditionExpression>{"${days &gt;= 1 &amp;&amp; paid}"}</bpmn:conditionExpression></bpmn:sequenceFlow>
                <bpmn:sequenceFlow id="f2" sourceRef="approve" targetRef="e"/>
              </bpmn:process>
            </bpmn:definitions>
            """;
        var result = BpmnImporter.Import("leave", xml);
        var unsupported = result.Unsupported.Select(u => (u.ElementId, u.Key, u.Detail)).ToList();
        Assert.Contains(("s", "process.import.vendor_attribute", "formKey"), unsupported);
        Assert.Contains(("approve", "process.import.user_name", "jane"), unsupported);
        Assert.Contains(("approve", "process.import.group_name", "managers"), unsupported);
        Assert.Contains(("approve", "process.import.due_date", "2026-12-01T00:00:00Z"), unsupported);
        Assert.Contains(("approve", "process.import.vendor_extension", "taskListener"), unsupported);
        Assert.DoesNotContain(unsupported, u => u.Detail is "asyncBefore" or "historyTimeToLive");
        Assert.Contains(result.Converted, c => c.ElementId == "approve" && c.Key == "process.import.assignee_expression");
        Assert.Contains(result.Converted, c => c.ElementId == "approve" && c.Key == "process.import.loop");
        Assert.Contains("assignee=\"initiator\"", result.Xml!);
        Assert.Contains($"users=\"{User}\"", result.Xml!);
        Assert.Contains("<tb:loop collection=\"approvers\" elementVariable=\"approver\"", result.Xml!);
        Assert.Contains("days &gt;= 1  and  paid", result.Xml!);
    }

    [Theory]
    [InlineData("""<bpmn:collaboration id="c"><bpmn:participant id="a" processRef="p"/><bpmn:participant id="b"/></bpmn:collaboration>""", "", "process.import.collaboration")]
    [InlineData("""<bpmn:process id="q"><bpmn:startEvent id="s2"/></bpmn:process>""", "", "validation.process.one_process")]
    public void Several_pools_or_processes_are_unsupported(string extra, string element, string key)
    {
        var xml = $"""
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="d">{extra}
              <bpmn:process id="p"><bpmn:startEvent id="s"/><bpmn:endEvent id="e"/><bpmn:sequenceFlow id="f" sourceRef="s" targetRef="e"/></bpmn:process>
            </bpmn:definitions>
            """;
        Assert.Contains(BpmnImporter.Import("p", xml).Unsupported, u => u.Key == key && (element.Length == 0 || u.ElementId == element));
    }

    [Fact]
    public void Elements_outside_the_subset_and_unsafe_xml_are_unsupported()
    {
        var xml = """
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" id="d">
              <bpmn:process id="p"><bpmn:startEvent id="s"/><bpmn:complexGateway id="g"/><bpmn:endEvent id="e"/>
                <bpmn:sequenceFlow id="f1" sourceRef="s" targetRef="g"/><bpmn:sequenceFlow id="f2" sourceRef="g" targetRef="e"/></bpmn:process>
            </bpmn:definitions>
            """;
        Assert.Contains(BpmnImporter.Import("p", xml).Unsupported, u => u.ElementId == "g" && u.Key == "validation.process.unsupported_element");
        Assert.Equal("validation.process.xml",
            Assert.Single(BpmnImporter.Import("p", """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e "x">]><bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL"/>""").Unsupported).Key);
    }

    [Fact]
    public void A_TeamBeats_export_imports_unchanged_in_meaning()
    {
        var xml = $"""
            <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:tb="https://teambeats.example/schema/bpmn/1.0" id="d">
              <bpmn:process id="p"><bpmn:startEvent id="s"/><bpmn:userTask id="t"><bpmn:extensionElements><tb:assignment users="{User}"/></bpmn:extensionElements></bpmn:userTask><bpmn:endEvent id="e"/>
                <bpmn:sequenceFlow id="f1" sourceRef="s" targetRef="t"/><bpmn:sequenceFlow id="f2" sourceRef="t" targetRef="e"/></bpmn:process>
            </bpmn:definitions>
            """;
        var result = BpmnImporter.Import("p", xml);
        Assert.Empty(result.Unsupported);
        Assert.Empty(result.Converted);
        var (graph, problems) = BpmnReader.Read("p", result.Xml!);
        Assert.Empty(problems);
        Assert.Equal(Guid.Parse(User), graph!.Element("t").Assignment!.Users![0]);
    }
}
