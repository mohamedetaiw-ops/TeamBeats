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
        services.AddScoped<TeamBeats.Modules.Search.Contracts.ISearchSource, PeopleSearchSource>();
        services.AddScoped<ProfileStore>();
        services.AddScoped<ReportingLine>();
        services.AddScoped<IReportingLine>(sp => sp.GetRequiredService<ReportingLine>());
        services.AddScoped<VisibilityResolver>();
        services.AddScoped<Profiles>();
        services.AddScoped<IProfiles>(sp => sp.GetRequiredService<Profiles>());
        services.AddScoped<PeopleService>();
        services.AddScoped<IEmployeeNumbers, EmployeeNumbers>();
        services.AddScoped<ICelebrationDates, CelebrationDates>();
        services.AddScoped<DirectoryProfiles>();
        services.AddScoped<HrProfiles>();
        services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, PeopleComponents>();
        services.AddMessageHandler<DirectorySynced, ApplyDirectoryProfiles>("people.directory_profiles");
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

        app.MapPeoplePublicEndpoints();
        return app;
    }
}
