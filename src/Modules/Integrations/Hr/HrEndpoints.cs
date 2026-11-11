using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

public sealed record HrLinkRequest(Guid UserId);

/// <summary>
/// The HR integration page (spec 019 section 10, permission <c>hr.integration_manage</c> at tenant scope): last run and
/// counts, the unmatched, stale and conflicting records, held runs to confirm or discard, links made by hand, the
/// matching rule and change threshold, and a sync now. The HR connection itself is made in the Connections area.
/// </summary>
public static class HrEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/hr/integration").RequireTenantUser().InAdminArea().WithTags("HR integration");

        api.MapGet("", async (IAuthorizationService authz, HrSync hr, HttpResponse response, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var overview = await hr.OverviewAsync(ct);
            response.Headers.ETag = HrSync.ETag(overview.Settings.Version);
            return Results.Ok(overview);
        });

        api.MapPut("/settings", async (HrSettingsRequest request, HttpRequest http, IAuthorizationService authz, HrSync hr, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var view = await hr.ChangeSettingsAsync(request, http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = HrSync.ETag(view.Version);
            return Results.Ok(view);
        });

        api.MapGet("/runs", async (IAuthorizationService authz, HrSync hr, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await hr.RunsAsync(ct));
        });

        api.MapPost("/runs/{id:guid}:confirm", async (Guid id, IAuthorizationService authz, HrSync hr, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var run = await hr.ConfirmAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(run);
        });

        api.MapPost("/runs/{id:guid}:discard", async (Guid id, IAuthorizationService authz, HrSync hr, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var run = await hr.DiscardAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(run);
        });

        api.MapGet("/records", async (string? state, string? search, string? cursor, IAuthorizationService authz, HrSync hr, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await hr.RecordsAsync(state, search, cursor, 50, ct));
        });

        api.MapPost("/records/{recordId}:link", async (string recordId, HrLinkRequest request, IAuthorizationService authz, HrSync hr, IDbSession session,
            CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var row = await hr.LinkAsync(recordId, request.UserId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(row);
        });

        api.MapPost("/records/{recordId}:unlink", async (string recordId, IAuthorizationService authz, HrSync hr, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var row = await hr.UnlinkAsync(recordId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(row);
        });

        // Starting a run: a sync of the HR connection now, outside the nightly schedule.
        api.MapPost("/runs", async (IAuthorizationService authz, HrSchedule schedule, IDbSession session, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var result = await schedule.SyncNowAsync(ct);
            await session.CommitAsync(ct);
            return Results.Accepted(value: result);
        });
    }

    private static Task Demand(IAuthorizationService authz, CancellationToken ct) => authz.DemandAsync(Permissions.HrIntegrationManage, AuthzTarget.Tenant, ct);
}
