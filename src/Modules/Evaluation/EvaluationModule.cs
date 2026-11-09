using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
        services.AddScoped<PersonaService>();
        services.AddScoped<RunService>();
        services.AddScoped<RunStore>();
        services.AddScoped<CaseExecutor>();
        services.AddScoped<EvaluationJudge>();
        services.AddScoped<GateService>();
        services.RemoveAll<Agents.Contracts.IAgentReleaseGate>();
        services.AddScoped<Agents.Contracts.IAgentReleaseGate>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<Workflow.Contracts.IPersonalTaskSource, GateFollowUpTasks>();
        services.AddMessageHandler<EvaluationRunRequested, EvaluationRunner>("evaluation.run");
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
        root.MapPost("/datasets/{id:guid}/import", async (Guid id, ImportRequest request, DatasetService datasets, IDbSession session, CancellationToken ct) =>
        {
            var preview = await datasets.ImportAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(preview);
        });
        root.MapPost("/datasets/{id:guid}/imports/{importId:guid}/confirm", async (Guid id, Guid importId, DatasetService datasets, IDbSession session, CancellationToken ct) =>
        {
            var view = await datasets.ConfirmImportAsync(id, importId, ct);
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
        root.MapGet("/runs/{id:guid}", async (Guid id, RunService runs, CancellationToken ct) => Results.Ok(await runs.GetAsync(id, ct)));
        root.MapGet("/runs/{id:guid}/cases", async (Guid id, RunService runs, CancellationToken ct) => Results.Ok(await runs.CasesAsync(id, ct)));
        root.MapPost("/runs/{id:guid}/cancel", async (Guid id, RunService runs, IDbSession session, CancellationToken ct) =>
        {
            var view = await runs.CancelAsync(id, ct);
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
