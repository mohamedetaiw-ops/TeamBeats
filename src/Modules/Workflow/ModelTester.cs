using TeamBeats.Modules.Workflow.Contracts;

namespace TeamBeats.Modules.Workflow;

/// <summary>
/// Test runs of compiled models for the modules that own them (spec 016 R2 test mode): the same checks as publishing,
/// then the sandbox walk of spec 024 US1 scenario 4. A model with problems answers with them and no steps.
/// </summary>
public sealed class ModelTester(ProcessEngine engine, ProcessSandbox sandbox) : IModelTester
{
    public async Task<ModelTestResult> TestAsync(string key, string bpmnXml, ModelTestRequest request, string locale, CancellationToken ct = default)
    {
        var (graph, problems) = await engine.CheckModelAsync(key, bpmnXml, ct);
        if (graph is null || problems.Count > 0)
        {
            return new ModelTestResult("invalid", [], problems);
        }
        var run = await sandbox.RunAsync(graph, new ProcessTestRequest(request.Variables, request.RequesterId, request.Outcomes, null, null), locale, ct);
        return new ModelTestResult(run.Outcome,
            run.Steps.Select(s => new ModelTestStep(s.ElementId, s.Event, s.Assignees?.Select(p => new ModelTestPerson(p.Id, p.Name)).ToList(), s.Permission, s.LineManager)).ToList(),
            run.Problems.Select(p => new ModelProblemInfo(p.ElementId, p.Key)).ToList());
    }
}
