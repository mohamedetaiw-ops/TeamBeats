using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Evaluation.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

/// <summary>
/// The AI Evaluation Layer (spec 034, ADR-0015, module map: Evaluation). R1 slice 1 is the control plane: platform and
/// mandatory suites, an agent's own suite with versions and thresholds, golden datasets with import, personas backed by
/// synthetic test identities, and the tenant's thresholds. Slice 2 adds runs on the workers, the evaluators, the judge
/// and scoring. Slice 3 adds the release gate for agent versions, with overrides and the owner's follow-up task.
/// </summary>
public static class EvaluationModule
{
    public static readonly MigrationSource Migrations = new("evaluation", typeof(EvaluationModule).Assembly);

    public static IServiceCollection AddEvaluationModule(this IServiceCollection services)
    {
        services.AddScoped<EvalAccess>();
        services.AddScoped<EvalSettingsService>();
        services.AddScoped<SuiteService>();
        services.AddScoped<DatasetService>();
        services.AddScoped<UnitEvalSettings>();
        services.AddScoped<SearchJudgedSets>();
        services.AddScoped<PersonaService>();
        services.AddScoped<RunService>();
        services.AddScoped<RunStore>();
        services.AddScoped<CaseExecutor>();
        services.AddScoped<EvaluationJudge>();
        services.AddScoped<GateService>();
        services.RemoveAll<Agents.Contracts.IAgentReleaseGate>();
        services.AddScoped<Agents.Contracts.IAgentReleaseGate>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, GateFollowUpTasks>();
        services.AddScoped<RouteGates>();
        services.AddScoped<RetrievalGates>();
        services.AddScoped<ResultsService>();
        services.AddScoped<CalibrationService>();
        services.AddScoped<ProductionPolicyService>();
        services.AddScoped<ProductionResults>();
        services.AddScoped<ProcessEvaluationResults>();
        services.AddMessageHandler<Agents.Contracts.AgentTaskRunRecorded, ProcessStepEvaluator>("evaluation.process_step");
        services.AddMessageHandler<Workflow.Contracts.ProcessInstanceCompleted, ProcessInstanceEvaluator>("evaluation.process_instance_completed");
        services.AddMessageHandler<Workflow.Contracts.ProcessInstanceWithdrawn, ProcessInstanceEvaluator>("evaluation.process_instance_withdrawn");
        services.AddScoped<ProductionRetention>();
        services.AddScoped<DriftDetector>();
        services.AddScoped<DriftAlerts>();
        services.AddScoped<ReviewService>();
        services.AddScoped<ComparisonService>();
        services.AddScoped<OutcomeService>();
        services.AddScoped<ComparisonArmAllocator>();
        services.RemoveAll<Agents.Contracts.IAgentArmAllocator>();
        services.AddScoped<Agents.Contracts.IAgentArmAllocator>(sp => sp.GetRequiredService<ComparisonArmAllocator>());
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, ReviewTasks>();
        services.AddMessageHandler<Agents.Contracts.AgentRunFeedbackGiven, ReviewQueue>("evaluation.review_feedback");
        services.AddScoped<ProductionSampler>();
        services.RemoveAll<Agents.Contracts.IProductionSampler>();
        services.AddScoped<Agents.Contracts.IProductionSampler>(sp => sp.GetRequiredService<ProductionSampler>());
        services.AddMessageHandler<ProductionSampleCaptured, ProductionScorer>("evaluation.production");
        services.AddMessageHandler<CalibrationRequested, CalibrationRunner>("evaluation.calibration");
        services.AddMessageHandler<EvaluationRunRequested, EvaluationRunner>("evaluation.run");
        services.AddMessageHandler<AiGateway.Contracts.AiModelRouteChangeProposed, RouteGateStarter>("evaluation.route_gate");
        services.AddMessageHandler<Search.Contracts.RetrievalConfigChangeProposed, RetrievalGateStarter>("evaluation.retrieval_gate");
        return services;
    }

    public static IEndpointRouteBuilder MapEvaluationEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/evaluation").RequireTenantUser().WithTags("Evaluation");

        root.MapGet("/metrics", () => Results.Ok(MetricCatalog.All));
        root.MapGet("/platform/suites/{key}", (string key) =>
            PlatformSuites.Find(key) is { } suite ? Results.Ok(suite) : throw AppException.NotFound());

        root.MapGet("/settings", async (EvalSettingsService settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        root.MapPut("/settings", async (SettingsRequest request, EvalSettingsService settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.SaveAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Organization level (R2 T023, US10): a unit's stricter thresholds and business datasets for it and below.
        root.MapGet("/units/{unitId:guid}/settings", async (Guid unitId, UnitEvalSettings units, CancellationToken ct) => Results.Ok(await units.GetAsync(unitId, ct)));
        root.MapPut("/units/{unitId:guid}/settings", async (Guid unitId, UnitSettingsRequest request, UnitEvalSettings units, IDbSession session, CancellationToken ct) =>
        {
            var view = await units.SaveAsync(unitId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/agents/{agentId:guid}/suites", async (Guid agentId, SuiteService suites, CancellationToken ct) => Results.Ok(await suites.GetAsync(agentId, ct)));
        root.MapGet("/agents/{agentId:guid}/suite/versions/{number:int}", async (Guid agentId, int number, SuiteService suites, CancellationToken ct) =>
            Results.Ok(await suites.VersionAsync(agentId, number, ct)));
        root.MapPut("/agents/{agentId:guid}/suite", async (Guid agentId, SuiteRequest request, SuiteService suites, IDbSession session, CancellationToken ct) =>
        {
            var view = await suites.SaveAsync(agentId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/datasets", async (Guid agentId, DatasetService datasets, CancellationToken ct) => Results.Ok(await datasets.ListAsync(agentId, ct)));
        root.MapPost("/datasets", async (DatasetCreateRequest request, DatasetService datasets, IDbSession session, CancellationToken ct) =>
        {
            var view = await datasets.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/datasets/{view.Id}", view);
        });
        root.MapGet("/datasets/{id:guid}", async (Guid id, int? version, DatasetService datasets, CancellationToken ct) => Results.Ok(await datasets.GetAsync(id, version, ct)));
        root.MapPut("/datasets/{id:guid}/cases", async (Guid id, DatasetCasesRequest request, DatasetService datasets, IDbSession session, CancellationToken ct) =>
        {
            var view = await datasets.SaveCasesAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        root.MapPost("/datasets/{id:guid}/import", async (Guid id, ImportRequest request, DatasetService datasets, IDbSession session, ILocalizer localizer,
            RequestLocale locale, CancellationToken ct) =>
        {
            var preview = await datasets.ImportAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(preview with { Errors = preview.Errors.Select(e => e with { Message = localizer.Get(e.Key, locale.Locale) }).ToList() });
        });
        root.MapPost("/datasets/{id:guid}/imports/{importId:guid}/confirm", async (Guid id, Guid importId, DatasetService datasets, IDbSession session, CancellationToken ct) =>
        {
            var view = await datasets.ConfirmImportAsync(id, importId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Judged query sets for search ranking (spec 012 US8), managed by search administrators.
        root.MapGet("/search-sets", async (SearchJudgedSets sets, CancellationToken ct) => Results.Ok(await sets.ListAsync(ct)));
        root.MapPost("/search-sets", async (JudgedSetCreateRequest request, SearchJudgedSets sets, IDbSession session, CancellationToken ct) =>
        {
            var view = await sets.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/search-sets/{view.Id}", view);
        });
        root.MapGet("/search-sets/{id:guid}", async (Guid id, SearchJudgedSets sets, CancellationToken ct) => Results.Ok(await sets.GetAsync(id, ct)));
        root.MapPut("/search-sets/{id:guid}/queries", async (Guid id, JudgedSetQueriesRequest request, SearchJudgedSets sets, IDbSession session, CancellationToken ct) =>
        {
            var view = await sets.SaveQueriesAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/personas", async (PersonaService personas, CancellationToken ct) => Results.Ok(await personas.ListAsync(ct)));
        root.MapPost("/personas", async (PersonaRequest request, PersonaService personas, IDbSession session, CancellationToken ct) =>
        {
            var view = await personas.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/personas/{view.Id}", view);
        });

        root.MapPost("/runs", async (RunStartRequest request, RunService runs, IDbSession session, CancellationToken ct) =>
        {
            var view = await runs.StartAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/runs/{view.Run.Id}", view);
        });
        root.MapGet("/runs", async (Guid agentId, RunService runs, CancellationToken ct) => Results.Ok(await runs.ListAsync(agentId, ct)));
        root.MapGet("/assistant/runs", async (RunService runs, CancellationToken ct) => Results.Ok(await runs.AssistantRunsAsync(ct)));
        root.MapGet("/runs/{id:guid}", async (Guid id, RunService runs, CancellationToken ct) => Results.Ok(await runs.GetAsync(id, ct)));
        root.MapGet("/runs/{id:guid}/cases", async (Guid id, RunService runs, CancellationToken ct) => Results.Ok(await runs.CasesAsync(id, ct)));
        root.MapPost("/runs/{id:guid}/cancel", async (Guid id, RunService runs, IDbSession session, CancellationToken ct) =>
        {
            var view = await runs.CancelAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/agents", async (ResultsService results, CancellationToken ct) => Results.Ok(await results.AgentsAsync(ct)));
        root.MapGet("/agents/{agentId:guid}/dashboard", async (Guid agentId, ResultsService results, CancellationToken ct) =>
            Results.Ok(await results.DashboardAsync(agentId, ct)));
        root.MapGet("/runs/compare", async (Guid a, Guid b, ResultsService results, CancellationToken ct) => Results.Ok(await results.CompareAsync(a, b, ct)));
        root.MapGet("/runs/{id:guid}/cases/{n:int}", async (Guid id, int n, ResultsService results, CancellationToken ct) => Results.Ok(await results.CaseAsync(id, n, ct)));

        root.MapGet("/calibration", async (CalibrationService calibration, CancellationToken ct) => Results.Ok(await calibration.RubricsAsync(ct)));
        root.MapGet("/calibration/{metric}/labels", async (string metric, CalibrationService calibration, CancellationToken ct) =>
            Results.Ok(await calibration.LabelsAsync(metric, ct)));
        root.MapPost("/calibration/{metric}/labels", async (string metric, CalibrationLabelRequest request, CalibrationService calibration, IDbSession session, CancellationToken ct) =>
        {
            var view = await calibration.AddLabelAsync(metric, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/calibration/labels/{view.Id}", view);
        });
        root.MapDelete("/calibration/labels/{id:guid}", async (Guid id, CalibrationService calibration, IDbSession session, CancellationToken ct) =>
        {
            await calibration.DeleteLabelAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        root.MapPost("/calibration/{metric}/runs", async (string metric, CalibrationService calibration, IDbSession session, CancellationToken ct) =>
        {
            var view = await calibration.StartAsync(metric, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/calibration/runs/{view.Id}", view);
        });
        root.MapGet("/calibration/runs/{id:guid}", async (Guid id, CalibrationService calibration, CancellationToken ct) => Results.Ok(await calibration.GetAsync(id, ct)));

        // Continuous production evaluation (R2 T018, US7): the tenant's sampling policy, each person's own opt-out and an agent's production results.
        root.MapGet("/production/policy", async (ProductionPolicyService policies, CancellationToken ct) => Results.Ok(await policies.GetAsync(ct)));
        root.MapPut("/production/policy", async (ProductionPolicyRequest request, ProductionPolicyService policies, IDbSession session, CancellationToken ct) =>
        {
            var view = await policies.SaveAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        root.MapGet("/production/opt-out", async (ProductionPolicyService policies, CancellationToken ct) => Results.Ok(await policies.OptOutAsync(ct)));
        root.MapPut("/production/opt-out", async (ProductionOptOutRequest request, ProductionPolicyService policies, IDbSession session, CancellationToken ct) =>
        {
            var view = await policies.SetOptOutAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        root.MapGet("/agents/{agentId:guid}/production", async (Guid agentId, ProductionResults results, CancellationToken ct) =>
            Results.Ok(await results.ForAgentAsync(agentId, ct)));
        // L4 process runs end to end (R3): the steps of a process's agent tasks in the viewer's scope, per process version.
        root.MapGet("/processes/{key}", async (string key, ProcessEvaluationResults results, CancellationToken ct) => Results.Ok(await results.GetAsync(key, ct)));
        root.MapGet("/agents/{agentId:guid}/drift", async (Guid agentId, DriftAlerts alerts, CancellationToken ct) => Results.Ok(await alerts.ForAgentAsync(agentId, ct)));

        // Online comparisons (R2 T021, US9): proposed by a designer, approved by the AI Policy Administrator, stopped by either.
        root.MapGet("/agents/{agentId:guid}/comparisons", async (Guid agentId, ComparisonService comparisons, CancellationToken ct) =>
            Results.Ok(await comparisons.ListAsync(agentId, ct)));
        root.MapGet("/comparisons/{id:guid}", async (Guid id, ComparisonService comparisons, CancellationToken ct) => Results.Ok(await comparisons.GetAsync(id, ct)));
        root.MapPost("/comparisons", async (ComparisonRequest request, ComparisonService comparisons, IDbSession session, CancellationToken ct) =>
        {
            var view = await comparisons.ProposeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/evaluation/comparisons/{view.Id}", view);
        });
        root.MapPost("/comparisons/{id:guid}/decision", async (Guid id, ComparisonDecisionRequest request, ComparisonService comparisons, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await comparisons.DecideAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        root.MapPost("/comparisons/{id:guid}/stop", async (Guid id, ComparisonService comparisons, IDbSession session, CancellationToken ct) =>
        {
            var view = await comparisons.StopAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Business outcomes (R2 T022, US9 scenario 1): per agent against the tenant's baselines, with the minimum group size.
        root.MapGet("/outcomes", async (int? days, OutcomeService outcomes, CancellationToken ct) => Results.Ok(await outcomes.GetAsync(days, ct)));
        root.MapPut("/outcomes/baselines/{agentId:guid}", async (Guid agentId, OutcomeBaselineRequest request, OutcomeService outcomes, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await outcomes.SetBaselineAsync(agentId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Human review queue (R2 T020, US8): flagged production samples for evaluation reviewers in scope.
        root.MapGet("/review", async (string? status, ReviewService reviews, CancellationToken ct) => Results.Ok(await reviews.ListAsync(status, ct)));
        root.MapGet("/review/{id:guid}", async (Guid id, ReviewService reviews, CancellationToken ct) => Results.Ok(await reviews.GetAsync(id, ct)));
        root.MapPost("/review/{id:guid}/decision", async (Guid id, ReviewDecisionRequest request, ReviewService reviews, IDbSession session, CancellationToken ct) =>
        {
            var view = await reviews.DecideAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        root.MapPost("/review/{id:guid}/promote", async (Guid id, ReviewPromoteRequest request, ReviewService reviews, IDbSession session, CancellationToken ct) =>
        {
            var view = await reviews.PromoteAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/gates", async (Guid agentId, int version, GateService gates, CancellationToken ct) => Results.Ok(await gates.GetAsync(agentId, version, ct)));
        root.MapPost("/gates/{runId:guid}/override", async (Guid runId, GateOverrideRequest request, GateService gates, IDbSession session, CancellationToken ct) =>
        {
            var view = await gates.OverrideAsync(runId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        return app;
    }
}
