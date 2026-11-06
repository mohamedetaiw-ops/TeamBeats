using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy.DirectoryMapping;

/// <summary>
/// Directory mapping administration (spec 002 US5). Changing the strategy, previewing and approving are tenant admin
/// actions. Anyone who can view part of the hierarchy can read the mapping and its run summaries (scenario 9); the
/// units themselves are listed through the scoped hierarchy API, which shows their owner.
/// </summary>
public static class DirectoryMappingEndpoints
{
    public static IEndpointRouteBuilder MapDirectoryMappingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/org").RequireTenantUser().WithTags("Organization");

        g.MapGet("/directory-mapping", async (IAuthorizationService authz, DirectoryMappingService mapping, CancellationToken ct) =>
        {
            if (!(await authz.CoverageAsync(Permissions.OrgUnitView, ct)).Any)
            {
                throw AppException.Denied();
            }
            var view = await mapping.GetAsync(ct);
            var runs = view is null ? [] : await mapping.RecentRunsAsync(view.ConnectionId, 10, ct);
            return Results.Ok(new { mapping = view, runs });
        });

        g.MapPut("/directory-mapping", async (OrgMappingRequest request, IAuthorizationService authz, IEntitlementService entitlements,
            DirectoryMappingService mapping, IDbSession session, CancellationToken ct) =>
        {
            await DemandAdminAsync(authz, entitlements, ct);
            var saved = await mapping.SaveAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(saved);
        });

        g.MapPost("/directory-mapping:preview", async (IAuthorizationService authz, IEntitlementService entitlements, DirectoryMappingService mapping,
            IDbSession session, CancellationToken ct) =>
        {
            await DemandAdminAsync(authz, entitlements, ct);
            var result = await mapping.PreviewAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        g.MapPost("/directory-mapping:approve", async (IAuthorizationService authz, IEntitlementService entitlements, DirectoryMappingService mapping,
            IDbSession session, CancellationToken ct) =>
        {
            await DemandAdminAsync(authz, entitlements, ct);
            var result = await mapping.ApproveAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        return app;
    }

    private static async Task DemandAdminAsync(IAuthorizationService authz, IEntitlementService entitlements, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.OrgUnitManage, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(Capabilities.IdentityDirectorySync, ct);
    }
}

/// <summary>Applies the approved mapping after each directory sync run.</summary>
public sealed class ApplyMappingAfterDirectorySync(DirectoryMappingService mapping) : IMessageHandler<DirectorySynced>
{
    public async Task HandleAsync(DirectorySynced message, CancellationToken ct) => await mapping.ApplyAsync(ct);
}

/// <summary>Hands directory-owned units back to the product when the connection is deleted.</summary>
public sealed class ReleaseMappingOnConnectionDeleted(DirectoryMappingService mapping) : IMessageHandler<ConnectionDeleted>
{
    public InactiveTenantPolicy InactiveTenantPolicy => InactiveTenantPolicy.Process;

    public async Task HandleAsync(ConnectionDeleted message, CancellationToken ct) => await mapping.ReleaseConnectionAsync(message.ConnectionId, ct);
}
