using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Authorization;

public static class AuthorizationModule
{
    public static readonly MigrationSource Migrations = new("authz", typeof(AuthorizationModule).Assembly);

    public static IServiceCollection AddAuthorizationModule(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<EffectiveGrantsLoader>();
        services.AddScoped<AuthorizationService>();
        services.AddScoped<IAuthorizationService>(sp => sp.GetRequiredService<AuthorizationService>());
        services.AddScoped<AuthorizationAdmin>();
        services.AddScoped<AssignmentStore>();
        services.AddScoped<AuthorizationProvisioning>();
        services.AddScoped<IAuthorizationProvisioning>(sp => sp.GetRequiredService<AuthorizationProvisioning>());
        return services;
    }

    public static IEndpointRouteBuilder MapAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/authz").RequireTenantUser().InAdminArea().WithTags("Authorization");

        g.MapGet("/roles", async (AuthorizationAdmin admin, CancellationToken ct) => Results.Ok(await admin.ListRolesAsync(ct)));

        g.MapPost("/roles", async (DefineRoleRequest request, AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            var role = await admin.DefineRoleAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/authz/roles/{role.Id}", role);
        });

        g.MapGet("/assignments", async (Guid? userId, AuthorizationAdmin admin, CancellationToken ct) =>
            Results.Ok(await admin.ListAssignmentsAsync(userId, ct)));

        g.MapPost("/assignments", async (AssignRoleRequest request, AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            var assignment = await admin.AssignAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/authz/assignments/{assignment.Id}", assignment);
        }).RequireStepUp();

        g.MapDelete("/assignments/{id:guid}", async (Guid id, AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            await admin.RevokeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).RequireStepUp();

        g.MapGet("/resources/{type}/{id:guid}/acl", async (string type, Guid id, AuthorizationAdmin admin, CancellationToken ct) =>
            Results.Ok(await admin.GetAclAsync(type, id, ct)));

        g.MapPut("/resources/{type}/{id:guid}/acl", async (string type, Guid id, IReadOnlyList<AclEntryRequest> entries,
            AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            await admin.ReplaceAclAsync(type, id, entries, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        g.MapPut("/rules", async (InheritanceRuleRequest request, AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            await admin.SetInheritanceRuleAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/groups", async (CreateGroupRequest request, AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            var id = await admin.CreateGroupAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/authz/groups/{id}", new { id });
        });

        g.MapPost("/groups/{id:guid}/members", async (Guid id, GroupMemberRequest request, AuthorizationAdmin admin, IDbSession session, CancellationToken ct) =>
        {
            await admin.AddGroupMemberAsync(id, request.UserId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/explain", async (Guid userId, string permission, Guid? orgUnitId, string? resourceType, Guid? resourceId,
            AuthorizationAdmin admin, CancellationToken ct) =>
            Results.Ok(await admin.ExplainAsync(userId, permission, orgUnitId, resourceType, resourceId, ct)));

        app.MapGet("/api/v1/me/access", async (AuthorizationAdmin admin, CancellationToken ct) => Results.Ok(await admin.MyAccessAsync(ct)))
            .RequireTenantUser().WithTags("Me");

        return app;
    }
}
