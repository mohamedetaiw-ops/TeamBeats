using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>Run history and replay (spec 017 US3, <c>integration.operate</c>) and the outbound host allowlist (FR-012, <c>integration.manage</c>).</summary>
public static class RuntimeEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/integrations").RequireTenantUser().InAdminArea().WithTags("Integrations: runs");

        api.MapGet("/connections/{id:guid}/runs", async (Guid id, string? status, string? action, string? cursor, int? limit,
            IAuthorizationService authz, RunHistory history, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationOperate, AuthzTarget.Tenant, ct);
            return Results.Ok(await history.ListAsync(id, status, action, cursor, limit ?? 50, ct));
        });

        api.MapPost("/connections/{id:guid}/runs:replay", async (Guid id, ReplayRequest request, IAuthorizationService authz, RunHistory history,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationOperate, AuthzTarget.Tenant, ct);
            var result = await history.ReplayAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        api.MapGet("/allowlist", async (IAuthorizationService authz, HostAllowlist allowlist, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await allowlist.ListAsync(ct));
        });

        api.MapPost("/allowlist", async (AllowlistRequest request, IAuthorizationService authz, HostAllowlist allowlist, IDbSession session,
            CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            var entry = await allowlist.AddAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(entry);
        }).RequireStepUp();

        api.MapDelete("/allowlist/{host}", async (string host, IAuthorizationService authz, HostAllowlist allowlist, IDbSession session,
            CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            await allowlist.RemoveAsync(host, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).RequireStepUp();
    }
}
