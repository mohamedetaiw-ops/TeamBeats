using System.Text.Json;
using TeamBeats.Localization;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

/// <summary>A test run of the draft route: who it runs as (a sample employee; the owner when empty), form values and the outcome per step.</summary>
public sealed record RouteTestRequest(Guid? RequesterId, Dictionary<string, JsonElement>? Data, IReadOnlyDictionary<string, string>? Outcomes);

/// <summary>A step the test run reached: the designer's step, what happened, and who a person step would go to.</summary>
public sealed record RouteTestStep(string StepId, IReadOnlyDictionary<string, string>? Names, string Event, IReadOnlyList<ModelTestPerson>? Assignees, string? Permission,
    bool LineManager);

public sealed record RouteTestProblem(string Field, string StepId, string Key, string Message);

/// <summary>How the run ended (the end's result, waiting, or invalid with its problems on the steps).</summary>
public sealed record RouteTestResult(string Outcome, IReadOnlyList<RouteTestStep> Steps, IReadOnlyList<RouteTestProblem> Problems);

/// <summary>
/// Test mode of the route designer (spec 016 US8 scenario 6): the latest draft's step graph is compiled and walked in the
/// spec 024 sandbox as a sample employee, with the values a real request would carry. Nothing is created or sent; the
/// result shows each step's resolved assignee and the branches taken. Owners only (service.manage at the owning unit).
/// </summary>
public sealed class RouteTests(ServiceAdmin admin, IModelTester tester, IUserDirectory users, ITenantContext context, ILocalizer localizer, RequestLocale locale)
{
    public async Task<RouteTestResult> RunAsync(Guid serviceId, RouteTestRequest request, CancellationToken ct)
    {
        var latest = await admin.LatestManagedAsync(serviceId, ct);
        if (latest.Route.Graph is not { } graph)
        {
            throw AppException.Validation("route.graph", "validation.services.route_graph");
        }
        var errors = new ValidationErrors();
        RouteGraphCheck.Validate(graph, latest.Form, errors);
        errors.ThrowIfAny();

        var requester = request.RequesterId ?? context.RequireUser();
        var person = await users.GetAsync(requester, ct) ?? throw AppException.Validation("requesterId", "validation.process.test_requester");
        var variables = new Dictionary<string, object?>
        {
            ["form"] = request.Data ?? [],
            ["request"] = new { number = "TEST" },
            ["requester"] = new { name = person.DisplayName, email = person.Email },
        };
        var run = await tester.TestAsync(ServiceAdmin.ProcessKey(serviceId), RouteGraphCompiler.Compile(serviceId.ToString("N"), graph),
            new ModelTestRequest(JsonSerializer.SerializeToElement(variables), requester, Outcomes(graph, request.Outcomes)), locale.Locale, ct);
        // Helper elements ({step}__part) belong to their step; the start is nobody's.
        var steps = run.Steps.Where(s => graph.Step(RouteGraph.StepIdOf(s.ElementId)) is not null)
            .Select(s =>
            {
                var step = graph.Step(RouteGraph.StepIdOf(s.ElementId))!;
                return new RouteTestStep(step.Id, step.Names, s.ElementId.EndsWith("__auto_approved", StringComparison.Ordinal) ? "auto_approved" : s.Event, s.Assignees,
                    s.Permission, s.LineManager);
            }).ToList();
        var problems = run.Problems.Select(p =>
        {
            var stepId = RouteGraph.StepIdOf(p.ElementId);
            var index = graph.Steps.ToList().FindIndex(s => s.Id == stepId);
            return new RouteTestProblem(index >= 0 ? $"route.graph.steps[{index}]" : "route.graph", stepId, p.Key, localizer.Get(p.Key, locale.Locale));
        }).ToList();
        return new RouteTestResult(run.Outcome, steps, problems);
    }

    /// <summary>The outcome the owner picked per step, on the step's user task (approve when not named).</summary>
    private static IReadOnlyDictionary<string, string>? Outcomes(RouteGraph graph, IReadOnlyDictionary<string, string>? outcomes) =>
        outcomes?.Where(o => graph.Step(o.Key) is { Type: StepTypes.Approval or StepTypes.Task }).ToDictionary(o => o.Key, o => o.Value);
}
