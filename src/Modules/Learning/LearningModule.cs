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
        services.AddScoped<LearningReports>();
        services.AddScoped<LmsAvailability>();
        services.AddScoped<LmsSync>();
        services.AddScoped<Packages>();
        services.AddScoped<PackageRuntime>();
        services.AddScoped<Contracts.ILearningObligations>(sp => sp.GetRequiredService<LearningObligations>());
        services.AddMessageHandler<Tenancy.Contracts.OrganizationMembershipChanged, AssignmentRules>("learning.rule_membership");
        services.AddMessageHandler<Tenancy.Contracts.TenantMembershipDisabled, AssignmentRules>("learning.rule_disabled");
        services.AddMessageHandler<Contracts.CoursePublished, AssignmentRules>("learning.retraining");
        return services;
    }

    public static IEndpointRouteBuilder MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        MapPackageContent(app);
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

        // ---- SCORM 1.2 and xAPI packages (US7) ----

        root.MapGet("/manage/courses/{id:guid}/packages", async (Guid id, Packages packages, CancellationToken ct) => Results.Ok(await packages.ListAsync(id, ct)));

        // The zip is the request body. The service enforces 500 MB while streaming; the server-wide cap is lifted to that.
        root.MapPost("/manage/courses/{id:guid}/packages", async (Guid id, HttpRequest http, Packages packages, IDbSession session, CancellationToken ct) =>
        {
            if (http.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = Packages.MaxBytes + 1;
            }
            var view = await packages.ImportAsync(id, http.Body, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/learning/manage/courses/{id}/packages/{view.Id}", view);
        }).DisableAntiforgery();

        root.MapPost("/courses/{id:guid}/lessons/{key}:launch", async (Guid id, string key, LaunchRequest? request, Player player, IDbSession session, CancellationToken ct) =>
        {
            var view = await player.LaunchAsync(id, key, request ?? new LaunchRequest(null), ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });


        // ---- The LMS (US6) ----

        root.MapGet("/manage/lms", async (LmsSync lms, CancellationToken ct) => Results.Ok(await lms.StatusAsync(ct)));

        root.MapPost("/manage/lms:sync", async (LmsSync lms, IDbSession session, CancellationToken ct) =>
        {
            var view = await lms.SyncNowAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapGet("/reports/courses", async (LearningReports reports, CancellationToken ct) => Results.Ok(await reports.CoursesAsync(ct)));

        root.MapGet("/reports/courses/{id:guid}", async (Guid id, LearningReports reports, CancellationToken ct) => Results.Ok(await reports.CourseAsync(id, ct)));

        root.MapGet("/reports/courses/{id:guid}/people", async (Guid id, Guid? unit, LearningReports reports, CancellationToken ct) =>
            Results.Ok(await reports.PeopleAsync(id, unit, ct)));

        root.MapGet("/reports/courses/{id:guid}/evidence", async (Guid id, Guid? unit, LearningReports reports, IDbSession session, CancellationToken ct) =>
        {
            var (name, content) = await reports.EvidenceAsync(id, unit, ct);
            await session.CommitAsync(ct);
            return Results.File(content, "text/csv; charset=utf-8", name);
        });

        root.MapGet("/team", async (LearningReports reports, CancellationToken ct) => Results.Ok(await reports.TeamAsync(ct)));

        root.MapPost("/team/{userId:guid}/assignments", async (Guid userId, TeamAssignRequest request, LearningReports reports, IDbSession session, CancellationToken ct) =>
        {
            var team = await reports.AssignAsync(userId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(team);
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

    /// <summary>
    /// Package content and runtime (US7, FR-016). No session: the tenant comes from the host and the launch token in the
    /// path is the only key, so the sandboxed frame needs no cookie. Every answer carries the sandbox policy, no referrer
    /// (the token stays out of other sites' logs) and an open CORS answer without credentials, because the frame's
    /// opaque origin is not the host's.
    /// </summary>
    private static void MapPackageContent(IEndpointRouteBuilder app)
    {
        var content = app.MapGroup(PackageRuntime.ContentBase + "/{token}").WithTags("Learning").ExcludeFromDescription()
            .AddEndpointFilter(async (ctx, next) =>
            {
                var http = ctx.HttpContext;
                var tenant = http.RequestServices.GetRequiredService<ITenantContext>();
                if (tenant.Host.Kind != HostScopeKind.Tenant || tenant.Host.Tenant is not { Status: TenantLifecycleStatus.Active } t)
                {
                    return Results.NotFound();
                }
                var accessor = http.RequestServices.GetRequiredService<TenantContextAccessor>();
                accessor.SetTenant(t.Id);
                accessor.SetActor(ActorType.System, null);
                var headers = http.Response.Headers;
                headers.ContentSecurityPolicy = PackageRuntime.ContentPolicy;
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Content-Type-Options"] = "nosniff";
                headers.CacheControl = "private, no-store";
                headers.AccessControlAllowOrigin = "*";
                headers.AccessControlAllowHeaders = "Authorization, Content-Type, X-Experience-API-Version";
                headers.AccessControlAllowMethods = "GET, PUT, POST, DELETE, OPTIONS";
                headers["X-Experience-API-Version"] = "1.0.3";
                return await next(ctx);
            });

        content.MapMethods("/{**path}", ["OPTIONS"], () => Results.NoContent());

        content.MapGet("/__tb/scorm.js", async (string token, PackageRuntime runtime, CancellationToken ct) =>
            await runtime.BridgeAsync(token, ct) is { } script ? Results.Text(script, "text/javascript") : Results.NotFound());

        content.MapPost("/__tb/runtime", async (string token, HttpRequest http, PackageRuntime runtime, IDbSession session, CancellationToken ct) =>
        {
            var body = await ReadTextAsync(http, ct);
            if (body is null || !await runtime.ReportScormAsync(token, body, ct))
            {
                return Results.NotFound();
            }
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).DisableAntiforgery();

        content.MapGet("/__tb/xapi/about", () => Results.Json(new { version = new[] { "1.0.3", "1.0.0" } }));

        content.MapGet("/__tb/xapi/statements", () => Results.Json(new { statements = Array.Empty<object>(), more = "" }));

        content.MapMethods("/__tb/xapi/statements", ["POST", "PUT"], async (string token, string? statementId, HttpRequest http, PackageRuntime runtime, IDbSession session,
            CancellationToken ct) =>
        {
            var body = await ReadTextAsync(http, ct);
            if (body is null || await runtime.StatementsAsync(token, body, statementId, ct) is not { } ids)
            {
                return Results.BadRequest();
            }
            await session.CommitAsync(ct);
            return http.Method == "PUT" ? Results.NoContent() : Results.Json(ids);
        }).DisableAntiforgery();

        content.MapGet("/__tb/xapi/activities/state", async (string token, string? stateId, PackageRuntime runtime, CancellationToken ct) =>
        {
            var (found, document) = await runtime.StateAsync(token, stateId, ct);
            return !found ? Results.BadRequest() : document is null ? Results.NotFound() : Results.Text(document, "application/json");
        });

        content.MapMethods("/__tb/xapi/activities/state", ["PUT", "POST", "DELETE"], async (string token, string? stateId, HttpRequest http, PackageRuntime runtime,
            IDbSession session, CancellationToken ct) =>
        {
            var body = http.Method == "DELETE" ? null : await ReadTextAsync(http, ct);
            if (http.Method != "DELETE" && body is null || !await runtime.SaveStateAsync(token, stateId, body, ct))
            {
                return Results.BadRequest();
            }
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).DisableAntiforgery();

        content.MapGet("/{**path}", async (string token, string? path, PackageRuntime runtime, CancellationToken ct) =>
            await runtime.OpenAsync(token, path ?? "", ct) is { } file ? Results.Stream(file.Content, file.Type, enableRangeProcessing: true) : Results.NotFound());
    }

    private static async Task<string?> ReadTextAsync(HttpRequest http, CancellationToken ct)
    {
        using var reader = new StreamReader(http.Body);
        var buffer = new char[PackageRuntime.MaxReportBytes + 1];
        var total = 0;
        int read;
        while (total <= PackageRuntime.MaxReportBytes && (read = await reader.ReadAsync(buffer.AsMemory(total), ct)) > 0)
        {
            total += read;
        }
        return total > PackageRuntime.MaxReportBytes ? null : new string(buffer, 0, total);
    }
}
