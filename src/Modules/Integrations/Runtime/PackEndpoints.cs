using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Runtime;

/// <summary>A product event an outbound webhook may send, and the fields a receiver may get.</summary>
public sealed record WebhookEventView(string Type, IReadOnlyList<string> Fields);

/// <summary>
/// Connector packs and their connections (spec 017 US1, US4; permission <c>integration.manage</c>). Creating a connection,
/// changing its settings or credentials, and defining custom REST actions need step-up (FR-003).
/// </summary>
public static class PackEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/integrations").RequireTenantUser().InAdminArea().WithTags("Integrations: connector packs");

        api.MapGet("/packs", async (IAuthorizationService authz, PackConnections packs, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await packs.CatalogueAsync(ct));
        });

        // The product events an outbound webhook may send, with the fields of each (spec 017 US5).
        api.MapGet("/webhook-events", async (IAuthorizationService authz, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(Webhooks.OutboundEvents.All.Select(e => new WebhookEventView(e.Key, e.Value)).OrderBy(e => e.Type, StringComparer.Ordinal).ToList());
        });

        api.MapGet("/pack-connections", async (IAuthorizationService authz, PackConnections packs, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await packs.ListAsync(ct));
        });

        api.MapPost("/pack-connections", async (CreatePackConnectionRequest request, IAuthorizationService authz, PackConnections packs,
            IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var created = await packs.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/integrations/pack-connections/{created.Id}", created);
        }).RequireStepUp();

        api.MapGet("/pack-connections/{id:guid}", async (Guid id, IAuthorizationService authz, PackConnections packs, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await packs.GetAsync(id, ct) ?? throw AppException.NotFound());
        });

        api.MapPatch("/pack-connections/{id:guid}", async (Guid id, UpdatePackConnectionRequest request, IAuthorizationService authz,
            PackConnections packs, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var view = await packs.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireStepUp();

        api.MapPost("/pack-connections/{id:guid}:test", async (Guid id, IAuthorizationService authz, PackConnections packs,
            IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var result = await packs.TestAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        api.MapPost("/pack-connections/{id:guid}:enable", (Guid id, IAuthorizationService authz, PackConnections packs, IDbSession session,
            CancellationToken ct) => SetEnabled(id, true, authz, packs, session, ct));
        api.MapPost("/pack-connections/{id:guid}:disable", (Guid id, IAuthorizationService authz, PackConnections packs, IDbSession session,
            CancellationToken ct) => SetEnabled(id, false, authz, packs, session, ct));

        api.MapGet("/pack-connections/{id:guid}/inbound", async (Guid id, IAuthorizationService authz, Webhooks.InboundWebhooks inbound, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await inbound.CountsAsync(id, ct) ?? throw AppException.NotFound());
        });

        api.MapGet("/pack-connections/{id:guid}/dependents", async (Guid id, IAuthorizationService authz, PackConnections packs, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await packs.PreviewDeleteAsync(id, ct));
        });

        api.MapDelete("/pack-connections/{id:guid}", async (Guid id, bool? confirm, IAuthorizationService authz, PackConnections packs,
            IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            await packs.DeleteAsync(id, confirm == true, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        api.MapGet("/pack-connections/{id:guid}/actions", async (Guid id, IAuthorizationService authz, PackConnections packs, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await packs.ActionsAsync(id, ct));
        });

        api.MapPut("/pack-connections/{id:guid}/actions/{name}", async (Guid id, string name, RestActionRequest request, IAuthorizationService authz,
            PackConnections packs, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var view = await packs.PutActionAsync(id, name, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireStepUp();

        api.MapDelete("/pack-connections/{id:guid}/actions/{name}", async (Guid id, string name, IAuthorizationService authz, PackConnections packs,
            IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            await packs.DeleteActionAsync(id, name, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).RequireStepUp();

        api.MapPost("/pack-connections/{id:guid}/actions:test", async (Guid id, RestActionTestRequest request, IAuthorizationService authz,
            PackConnections packs, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await packs.TestActionAsync(id, request, ct));
        }).RequireStepUp();
    }

    /// <summary>Connector actions as agent tools (spec 017 T009, spec 027 FR-006). Registering one needs step-up, like defining an action.</summary>
    public static void MapTools(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/integrations/pack-connections/{id:guid}/tools").RequireTenantUser().InAdminArea()
            .WithTags("Integrations: connector packs");

        api.MapGet("", async (Guid id, IAuthorizationService authz, ConnectorTools tools, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await tools.ListAsync(id, ct));
        });

        api.MapPost("", async (Guid id, ConnectorToolRequest request, IAuthorizationService authz, ConnectorTools tools, IDbSession session,
            CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var view = await tools.RegisterAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/integrations/pack-connections/{id}/tools/{view.Id}", view);
        }).RequireStepUp();

        api.MapDelete("/{toolId:guid}", async (Guid id, Guid toolId, IAuthorizationService authz, ConnectorTools tools, IDbSession session,
            CancellationToken ct) =>
        {
            await Demand(authz, ct);
            await tools.DeleteAsync(id, toolId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    private static Task Demand(IAuthorizationService authz, CancellationToken ct) => authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);

    private static async Task<IResult> SetEnabled(Guid id, bool enabled, IAuthorizationService authz, PackConnections packs, IDbSession session,
        CancellationToken ct)
    {
        await Demand(authz, ct);
        await packs.SetEnabledAsync(id, enabled, ct);
        var view = await packs.GetAsync(id, ct);
        await session.CommitAsync(ct);
        return Results.Ok(view);
    }
}
