using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

/// <summary>What the viewer may do across spaces: create one, and manage the tenant's space rules and templates.</summary>
public sealed record SpacesRights(bool CanCreate, bool CanManage, bool Guests);

/// <summary>Project and team spaces (spec 029, module map: Spaces).</summary>
public static class SpacesModule
{
    public static readonly MigrationSource Migrations = new("spaces", typeof(SpacesModule).Assembly);

    public static IServiceCollection AddSpacesModule(this IServiceCollection services)
    {
        services.AddScoped<SpacesService>();
        services.AddScoped<SpaceSettings>();
        services.AddScoped<IResourceDescriptorProvider, SpaceDescriptorProvider>();
        services.AddScoped<Modules.Search.Contracts.ISearchSource, SpacesSearchSource>();
        services.AddMessageHandler<Tenancy.Contracts.TenantMembershipDisabled, SpaceLeavers>("spaces.ownerless");
        return services;
    }

    public static IEndpointRouteBuilder MapSpacesEndpoints(this IEndpointRouteBuilder app)
    {
        var root = app.MapGroup("/api/v1/spaces").RequireTenantUser().WithTags("Spaces");

        root.MapGet("/rights", async (SpacesService spaces, IAuthorizationService authz, IEntitlementService entitlements, CancellationToken ct) =>
        {
            var enabled = await entitlements.IsEnabledAsync(Capabilities.SpacesProjects, ct);
            return Results.Ok(new SpacesRights(
                enabled && await spaces.MayCreateAsync(ct),
                enabled && (await authz.CheckAsync(Permissions.SpacesManage, AuthzTarget.Tenant, ct)).Allowed,
                enabled && await entitlements.IsEnabledAsync(Capabilities.SpacesGuests, ct)));
        });

        root.MapGet("/templates", async (SpaceSettings settings, CancellationToken ct) => Results.Ok(await settings.OptionsAsync(ct)));

        root.MapGet("", async (string? scope, string? q, SpacesService spaces, CancellationToken ct) =>
            Results.Ok(await spaces.ListAsync(scope == "mine", scope == "all", q, ct)));

        root.MapPost("", async (SpaceRequest request, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/spaces/{view.Id}", view);
        });

        root.MapGet("/{id:guid}", async (Guid id, SpacesService spaces, CancellationToken ct) => Results.Ok(await spaces.GetAsync(id, ct)));

        root.MapPut("/{id:guid}", async (Guid id, SpaceRequest request, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:archive", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.ArchiveAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:restore", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.ArchiveAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:join", async (Guid id, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var view = await spaces.JoinAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/{id:guid}:leave", async (Guid id, SpacesService spaces, ITenantContext context, IDbSession session, CancellationToken ct) =>
        {
            await spaces.RemoveMemberAsync(id, context.RequireUser(), ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        root.MapGet("/{id:guid}/members", async (Guid id, SpacesService spaces, CancellationToken ct) => Results.Ok(await spaces.MembersAsync(id, ct)));

        root.MapPost("/{id:guid}/members", async (Guid id, MemberRequest request, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            var members = await spaces.SetMemberAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(members);
        });

        root.MapDelete("/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, SpacesService spaces, IDbSession session, CancellationToken ct) =>
        {
            await spaces.RemoveMemberAsync(id, userId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // ---- Tenant rules and templates (spaces administrators) ----

        root.MapGet("/manage/settings", async (SpaceSettings settings, CancellationToken ct) => Results.Ok(await settings.GetRulesAsync(ct)));

        root.MapPut("/manage/settings", async (SpaceRulesRequest request, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var rules = await settings.SetRulesAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(rules);
        });

        root.MapGet("/manage/templates", async (SpaceSettings settings, CancellationToken ct) => Results.Ok(await settings.TemplatesAsync(ct)));

        root.MapPost("/manage/templates", async (TemplateRequest request, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.CreateTemplateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/spaces/manage/templates/{view.Id}", view);
        });

        root.MapPut("/manage/templates/{id:guid}", async (Guid id, TemplateRequest request, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.UpdateTemplateAsync(id, request, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        root.MapPost("/manage/templates/{id:guid}:retire", async (Guid id, SpaceSettings settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.UpdateTemplateAsync(id, new TemplateRequest(null, null, null, null), true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        return app;
    }
}
