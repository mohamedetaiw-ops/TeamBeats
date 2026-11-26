using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.People;

/// <summary>Employee directory, profiles and org chart (spec 010, module map: People).</summary>
public static class PeopleModule
{
    public static readonly MigrationSource Migrations = new("people", typeof(PeopleModule).Assembly);

    /// <summary>The rate limiter policy for people search (FR-017), registered by the host.</summary>
    public const string SearchRateLimit = "people_search";

    public static IServiceCollection AddPeopleModule(this IServiceCollection services)
    {
        services.AddScoped<TeamBeats.Modules.PublicApi.Contracts.IPublicResourceVisibility, PersonVisibility>();
        services.AddScoped<TeamBeats.Modules.Search.Contracts.ISearchSource, PeopleSearchSource>();
        services.AddScoped<ProfileStore>();
        services.AddScoped<ReportingLine>();
        services.AddScoped<IReportingLine>(sp => sp.GetRequiredService<ReportingLine>());
        services.AddScoped<VisibilityResolver>();
        services.AddScoped<Profiles>();
        services.AddScoped<IProfiles>(sp => sp.GetRequiredService<Profiles>());
        services.AddScoped<PeopleService>();
        services.AddScoped<ProfilePhotos>();
        services.AddScoped<DirectoryProfilePhotos>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, FindPersonTool>();
        services.AddScoped<TeamBeats.Modules.Agents.Contracts.IAgentTool, GetPersonTool>();
        services.AddScoped<IEmployeeNumbers, EmployeeNumbers>();
        services.AddScoped<ICelebrationDates, CelebrationDates>();
        services.AddScoped<DirectoryProfiles>();
        services.AddScoped<HrProfiles>();
        services.AddScoped<Skills>();
        services.AddScoped<IPersonSkills, PersonSkills>();
        services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, PeopleComponents>();
        services.AddMessageHandler<DirectorySynced, ApplyDirectoryProfiles>("people.directory_profiles");
        services.AddMessageHandler<DirectorySynced, ApplyDirectoryPhotos>("people.directory_photos");
        services.AddMessageHandler<TeamBeats.Modules.Integrations.Contracts.HrSyncCompleted, ApplyHrProfiles>("people.hr_profiles");
        services.AddMessageHandler<TeamBeats.Modules.Integrations.Contracts.EmployeeManagerChanged, ApplyHrManager>("people.hr_manager");
        services.AddMessageHandler<UserProfileSynced, RefreshProfileOnSync>("people.profile_synced");
        services.AddMessageHandler<UserProvisioned, RefreshProfileOnSync>("people.user_provisioned");
        services.AddMessageHandler<UserDeprovisioned, RefreshProfileOnSync>("people.user_deprovisioned");
        return services;
    }

    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        // Every read may refresh the account read model, so reads commit too.
        var people = app.MapGroup("/api/v1/people").RequireTenantUser().RequiresCapability(Capabilities.PeopleDirectory).WithTags("People");
        var me = app.MapGroup("/api/v1/me").RequireTenantUser().RequiresCapability(Capabilities.PeopleDirectory).WithTags("People");

        people.MapGet("", async (string? q, Guid? unitId, string? location, string? ids, int? offset, int? limit, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var page = await service.SearchAsync(q, unitId, location, offset, limit, ct, PeopleService.ParseIds(ids));
            await session.CommitAsync(ct);
            return Results.Ok(page);
        }).RequireRateLimiting(SearchRateLimit);

        people.MapGet("/newcomers", async (int? limit, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var list = await service.NewcomersAsync(limit, ct);
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        people.MapGet("/locations", async (PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var list = await service.LocationsAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(list);
        });

        people.MapPut("/locations/{key}", async (string key, LocationRequest request, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.RenameLocationAsync(key, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).InAdminArea();

        people.MapGet("/policy", async (PeopleService service, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var view = await service.PolicyAsync(ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        people.MapPut("/policy", async (PolicyRequest request, HttpRequest http, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdatePolicyAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        people.MapGet("/{id:guid}", async (Guid id, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.GetAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        people.MapGet("/{id:guid}/org-chart", async (Guid id, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var chart = await service.OrgChartAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(chart);
        });

        people.MapPut("/{id:guid}/fields", async (Guid id, AdminFieldsRequest request, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.UpdateAdminFieldsAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).InAdminArea();

        people.MapPut("/{id:guid}/manager", async (Guid id, ManagerRequest request, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.SetManagerAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).InAdminArea();

        // Profile photos (FR-013): the person's own upload and removal, the photo itself, and removal by a manager or HR.
        people.MapGet("/{id:guid}/photo", async (Guid id, int? size, ProfilePhotos photos, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var content = await photos.OpenAsync(id, size, ct);
            await session.CommitAsync(ct);
            // The link carries the photo's version, so a changed photo has a new link; the file is private to the session.
            response.Headers.CacheControl = "private, max-age=86400";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.File(content, "image/png");
        });

        people.MapDelete("/{id:guid}/photo", async (Guid id, ProfilePhotos photos, IDbSession session, CancellationToken ct) =>
        {
            await photos.RemoveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).InAdminArea();

        me.MapPut("/photo", async (HttpRequest http, ProfilePhotos photos, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[ProfilePhotos.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > ProfilePhotos.MaxBytes)
                {
                    throw Primitives.AppException.Validation("file", "validation.people.photo_size");
                }
            }
            var view = await photos.UploadOwnAsync(http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).DisableAntiforgery();

        me.MapDelete("/photo", async (ProfilePhotos photos, IDbSession session, CancellationToken ct) =>
        {
            var view = await photos.RemoveOwnAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapGet("/team", async (bool? indirect, int? offset, int? limit, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var page = await service.TeamAsync(indirect ?? false, offset, limit, ct);
            await session.CommitAsync(ct);
            return Results.Ok(page);
        });

        me.MapGet("/profile", async (PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var profile = await service.OwnAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(profile);
        });

        me.MapPut("/profile", async (OwnProfileRequest request, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var profile = await service.UpdateOwnAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(profile);
        });

        people.MapPut("/{id:guid}/assistants", async (Guid id, AssistantsRequest request, PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var view = await service.SetAssistantsAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapPost("/profile/completeness:dismiss", async (PeopleService service, IDbSession session, CancellationToken ct) =>
        {
            var profile = await service.DismissCompletenessAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(profile);
        });

        // Skills and Find an expert (spec 010 R2 US6).
        people.MapGet("/skills", async (Skills skills, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var view = await skills.ListAsync(ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        people.MapPut("/skills", async (SkillListRequest request, HttpRequest http, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.SetListAsync(request, IfMatch(http), ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        people.MapPost("/skills/{id:guid}:merge", async (Guid id, MergeSkillRequest request, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.MergeAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        people.MapGet("/experts", async (Guid? skillId, string? q, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.ExpertsAsync(skillId, q, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireRateLimiting(SearchRateLimit);

        people.MapGet("/{id:guid}/skills", async (Guid id, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.PersonAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        people.MapPut("/{id:guid}/skills/{skillId:guid}/endorsement", async (Guid id, Guid skillId, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.EndorseAsync(id, skillId, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        people.MapDelete("/{id:guid}/skills/{skillId:guid}/endorsement", async (Guid id, Guid skillId, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.EndorseAsync(id, skillId, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapGet("/skills", async (Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.MineAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapPut("/skills", async (OwnSkillsRequest request, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.SetMineAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapPost("/skills/{skillId:guid}/endorsements/{endorserId:guid}:hide", async (Guid skillId, Guid endorserId, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.HideAsync(skillId, endorserId, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        me.MapPost("/skills/{skillId:guid}/endorsements/{endorserId:guid}:show", async (Guid skillId, Guid endorserId, Skills skills, IDbSession session, CancellationToken ct) =>
        {
            var view = await skills.HideAsync(skillId, endorserId, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        app.MapPeoplePublicEndpoints();
        return app;
    }
}
