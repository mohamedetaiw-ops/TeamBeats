using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Learning;

/// <summary>Learning and mandatory training (spec 028, module map: Learning).</summary>
public static class LearningModule
{
    public static readonly MigrationSource Migrations = new("learning", typeof(LearningModule).Assembly);

    public static IServiceCollection AddLearningModule(this IServiceCollection services)
    {
        services.AddSingleton<Experience.Contracts.IComponentRegistration, LearningComponents>();
        services.AddScoped<Search.Contracts.ISearchSource, LearningSearchSource>();
        services.AddScoped<Courses>();
        services.AddScoped<Player>();
        services.AddScoped<Paths>();
        services.AddScoped<Certificates>();
        services.AddScoped<CourseLearners>();
        services.AddScoped<AssignmentRules>();
        services.AddScoped<LearningObligations>();
        services.AddScoped<Contracts.ILearningObligations>(sp => sp.GetRequiredService<LearningObligations>());
        services.AddMessageHandler<Tenancy.Contracts.OrganizationMembershipChanged, AssignmentRules>("learning.rule_membership");
        services.AddMessageHandler<Tenancy.Contracts.TenantMembershipDisabled, AssignmentRules>("learning.rule_disabled");
        services.AddMessageHandler<Contracts.CoursePublished, AssignmentRules>("learning.retraining");
        return services;
    }

    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/learning").RequireTenantUser().WithTags("Learning");

        root.MapGet("/rights", async (Courses courses, CancellationToken ct) => Results.Ok(await courses.RightsAsync(ct)));

        // ---- Building courses ----

        root.MapGet("/manage/courses", async (Courses courses, CancellationToken ct) => Results.Ok(await courses.ListAsync(ct)));

        root.MapPost("/manage/courses", async (CourseRequest request, Courses courses, IDbSession session, CancellationToken ct) =>
        {
            var view = await courses.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/learning/manage/courses/{view.Id}", view);
        });

        root.MapGet("/manage/courses/{id:guid}", async (Guid id, Courses courses, CancellationToken ct) => Results.Ok(await courses.GetAsync(id, ct)));

        root.MapPut("/manage/courses/{id:guid}", async (Guid id, CourseRequest request, Courses courses, IDbSession session, CancellationToken ct) =>
        {
            var view = await courses.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/manage/courses/{id:guid}:publish", async (Guid id, PublishRequest request, Courses courses, IDbSession session, CancellationToken ct) =>
        {
            var view = await courses.PublishAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/manage/courses/{id:guid}:retire", async (Guid id, Courses courses, IDbSession session, CancellationToken ct) =>
        {
            var view = await courses.RetireAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/manage/courses/{id:guid}/learners", async (Guid id, CourseLearners learners, CancellationToken ct) => Results.Ok(await learners.ListAsync(id, ct)));

        root.MapPost("/manage/courses/{id:guid}/learners/{assignmentId:guid}/attempts", async (Guid id, Guid assignmentId, GrantAttemptsRequest request,
            CourseLearners learners, IDbSession session, CancellationToken ct) =>
        {
            var view = await learners.GrantAsync(id, assignmentId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // ---- Building paths ----

        root.MapGet("/manage/courses/{id:guid}/rules", async (Guid id, AssignmentRules rules, CancellationToken ct) => Results.Ok(await rules.ListAsync(id, ct)));

        root.MapPost("/manage/courses/{id:guid}/rules", async (Guid id, RuleRequest request, AssignmentRules rules, IDbSession session, CancellationToken ct) =>
        {
            var view = await rules.CreateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/learning/manage/courses/{id}/rules/{view.Id}", view);
        });

        root.MapPost("/manage/courses/{id:guid}/rules/{ruleId:guid}:end", async (Guid id, Guid ruleId, AssignmentRules rules, IDbSession session, CancellationToken ct) =>
        {
            var view = await rules.EndAsync(id, ruleId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/manage/paths", async (Paths paths, CancellationToken ct) => Results.Ok(await paths.ManagedListAsync(ct)));

        root.MapPost("/manage/paths", async (PathRequest request, Paths paths, IDbSession session, CancellationToken ct) =>
        {
            var view = await paths.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/learning/paths/{view.Id}", view);
        });

        root.MapPut("/manage/paths/{id:guid}", async (Guid id, PathRequest request, Paths paths, IDbSession session, CancellationToken ct) =>
        {
            var view = await paths.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/manage/paths/{id:guid}:publish", async (Guid id, Paths paths, IDbSession session, CancellationToken ct) =>
        {
            var view = await paths.PublishAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // ---- Learning ----

        root.MapGet("/me", async (Player player, CancellationToken ct) => Results.Ok(await player.MineAsync(ct)));

        root.MapGet("/catalogue", async (string? q, Player player, CancellationToken ct) => Results.Ok(await player.CatalogueAsync(q, ct)));

        root.MapGet("/courses/{id:guid}", async (Guid id, Player player, CancellationToken ct) => Results.Ok(await player.GetAsync(id, ct)));

        root.MapPost("/courses/{id:guid}:enrol", async (Guid id, Player player, IDbSession session, CancellationToken ct) =>
        {
            var view = await player.EnrolAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPut("/courses/{id:guid}/position", async (Guid id, PositionRequest request, Player player, IDbSession session, CancellationToken ct) =>
        {
            var view = await player.PositionAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/courses/{id:guid}/lessons/{key}:complete", async (Guid id, string key, Player player, IDbSession session, CancellationToken ct) =>
        {
            var view = await player.CompleteLessonAsync(id, key, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/courses/{id:guid}/lessons/{key}/attempts", async (Guid id, string key, QuizAnswers answers, Player player, IDbSession session, CancellationToken ct) =>
        {
            var result = await player.AttemptAsync(id, key, answers, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        root.MapGet("/paths", async (Paths paths, CancellationToken ct) => Results.Ok(await paths.CatalogueAsync(ct)));

        root.MapGet("/paths/{id:guid}", async (Guid id, Paths paths, CancellationToken ct) => Results.Ok(await paths.GetAsync(id, ct)));

        root.MapPost("/paths/{id:guid}/courses/{courseId:guid}:start", async (Guid id, Guid courseId, Paths paths, IDbSession session, CancellationToken ct) =>
        {
            var view = await paths.StartCourseAsync(id, courseId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // ---- Certificates ----

        root.MapGet("/certificates/verify", async (string? code, Certificates certificates, CancellationToken ct) => Results.Ok(await certificates.VerifyAsync(code, ct)));

        root.MapGet("/certificates/{id:guid}", async (Guid id, Certificates certificates, CancellationToken ct) => Results.Ok(await certificates.GetAsync(id, ct)));

        root.MapGet("/certificates/{id:guid}/pdf", async (Guid id, Certificates certificates, HttpResponse response, CancellationToken ct) =>
        {
            var (content, name) = await certificates.PdfAsync(id, ct);
            response.Headers.CacheControl = "no-store";
            return Results.File(content, "application/pdf", name);
        });

        return app;
    }
}
