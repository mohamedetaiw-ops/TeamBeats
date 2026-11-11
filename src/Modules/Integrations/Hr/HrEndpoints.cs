using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

public sealed record HrLinkRequest(Guid UserId);

/// <summary>
/// The HR integration page (spec 019 section 10, permission <c>hr.integration_manage</c> at tenant scope): last run and
/// counts, the unmatched, stale and conflicting records, held runs to confirm or discard, links made by hand, the
/// matching rule and change threshold, the lifecycle changes waiting or applied, the HR org codes and their units, and a sync now. The HR connection itself is made in the Connections area.
/// </summary>
public static class HrEndpoints
{
    /// <summary>Payslips are never kept by a browser or a shared device (FR-011).</summary>
    private static void NoStore(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, private";
        response.Headers.Pragma = "no-cache";
        response.Headers["X-Robots-Tag"] = "noindex";
    }

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/hr/integration").RequireTenantUser().InAdminArea().WithTags("HR integration");

        // My payslips (US6): the signed-in person's own only, after step-up, never cached.
        var payslips = app.MapGroup("/api/v1/me/payslips").RequireTenantUser().RequiresCapability(Capabilities.HrPayslips).WithTags("Payslips");
        // Whether to offer My payslips in the account menu; says nothing about the payslips themselves, so no step-up.
        app.MapGet("/api/v1/me/payslips-available", async (HrPayslips service, CancellationToken ct) =>
            Results.Ok(new { available = await service.AvailableAsync(ct) })).RequireTenantUser().WithTags("Payslips");
        payslips.MapGet("", async (HrPayslips service, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var list = await service.ListAsync(ct);
            await session.CommitAsync(ct);
            NoStore(response);
            return Results.Ok(list);
        }).RequireStepUp();
        payslips.MapGet("/{period}/pdf", async (string period, HrPayslips service, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var pdf = await service.PdfAsync(period, ct) ?? throw AppException.Conflict("conflict.hr.payslip_unavailable");
            await session.CommitAsync(ct);
            NoStore(response);
            return Results.File(pdf, "application/pdf", $"payslip-{period}.pdf");
        }).RequireStepUp();

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

        api.MapGet("/changes", async (string? status, IAuthorizationService authz, HrLifecycle lifecycle, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await lifecycle.ChangesAsync(status, 100, ct));
        });

        api.MapGet("/org-codes", async (IAuthorizationService authz, HrLifecycle lifecycle, CancellationToken ct) =>
        {
            await Demand(authz, ct);
            return Results.Ok(await lifecycle.OrgCodesAsync(ct));
        });

        api.MapPut("/org-codes/{code}", async (string code, HrOrgCodeRequest request, IAuthorizationService authz, HrLifecycle lifecycle, IDbSession session,
            CancellationToken ct) =>
        {
            await Demand(authz, ct);
            var view = await lifecycle.MapOrgCodeAsync(code, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
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
