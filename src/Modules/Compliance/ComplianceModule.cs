using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Compliance;

/// <summary>Compliance projects and the control library of spec 026 US7 (module map: Compliance).</summary>
public static class ComplianceModule
{
    public static readonly MigrationSource Migrations = new("compliance", typeof(ComplianceModule).Assembly);

    public static IServiceCollection AddComplianceModule(this IServiceCollection services)
    {
        services.AddScoped<ComplianceLibrary>();
        services.AddScoped<ComplianceProjects>();
        return services;
    }

    public static IEndpointRouteBuilder MapComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        // Not in the admin area: control owners are ordinary employees and auditors are guests of a shared space.
        var group = app.MapGroup("/api/v1/compliance").RequireTenantUser().WithTags("Compliance");

        group.MapGet("/rights", async (ComplianceProjects projects, CancellationToken ct) => Results.Ok(await projects.RightsAsync(ct)));

        group.MapGet("/library", async (ComplianceLibrary library, CancellationToken ct) => Results.Ok(await library.ListAsync(ct)));

        group.MapGet("/library/{key}", async (string key, ComplianceLibrary library, CancellationToken ct) => Results.Ok(await library.GetAsync(key, ct)));

        group.MapPost("/library", async (FrameworkRequest request, ComplianceLibrary library, IDbSession session, CancellationToken ct) =>
        {
            var framework = await library.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/compliance/library/{framework.Key}", framework);
        });

        group.MapPut("/library/{id:guid}", async (Guid id, FrameworkRequest request, ComplianceLibrary library, IDbSession session, CancellationToken ct) =>
        {
            var framework = await library.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(framework);
        });

        group.MapGet("/projects", async (Guid? spaceId, ComplianceProjects projects, CancellationToken ct) => Results.Ok(await projects.ListAsync(spaceId, ct)));

        group.MapPost("/projects", async (ProjectRequest request, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/compliance/projects/{view.Project.Id}", view);
        });

        group.MapGet("/projects/{id:guid}", async (Guid id, ComplianceProjects projects, CancellationToken ct) => Results.Ok(await projects.GetAsync(id, ct)));

        group.MapPut("/projects/{id:guid}", async (Guid id, ProjectChange change, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.UpdateAsync(id, change, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPut("/projects/{id:guid}/share", async (Guid id, ShareRequest request, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.ShareAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapGet("/projects/{id:guid}/readiness", async (Guid id, ComplianceProjects projects, CancellationToken ct) => Results.Ok(await projects.ReadinessAsync(id, ct)));

        group.MapGet("/controls/{id:guid}", async (Guid id, ComplianceProjects projects, CancellationToken ct) => Results.Ok(await projects.GetControlAsync(id, ct)));

        group.MapPut("/controls/{id:guid}", async (Guid id, ControlChange change, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.UpdateControlAsync(id, change, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPost("/controls/{id:guid}/tasks", async (Guid id, TaskRequest request, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.AddTaskAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapPut("/tasks/{id:guid}", async (Guid id, TaskRequest request, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.UpdateTaskAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapDelete("/tasks/{id:guid}", async (Guid id, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.DeleteTaskAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapGet("/exports", async (ComplianceProjects projects, CancellationToken ct) => Results.Ok(await projects.ExportsAsync(ct)));

        group.MapPost("/tasks/{id:guid}/evidence", async (Guid id, EvidenceRequest request, ComplianceProjects projects, IDbSession session, CancellationToken ct) =>
        {
            var view = await projects.AttachExportAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        group.MapGet("/evidence/{id:guid}/file", async (Guid id, ComplianceProjects projects, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, name, sha256) = await projects.EvidenceFileAsync(id, ct);
            response.Headers["X-Content-SHA256"] = sha256;
            return Results.File(content, type, name);
        });

        return app;
    }
}
