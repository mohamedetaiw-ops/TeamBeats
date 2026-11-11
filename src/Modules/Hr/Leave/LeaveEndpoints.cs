using TeamBeats.Localization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Leave;

/// <summary>Leave endpoints (spec 022 R2 slice 1). Every endpoint needs <c>hr.leave</c> in the plan.</summary>
public static class LeaveEndpoints
{
    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapLeaveEndpoints(this IEndpointRouteBuilder app)
    {
        // Both reads commit: with HR as the balance source they keep the balances just read as the last known (spec 019 US7).
        app.MapGet("/api/v1/me/leave", async (LeaveRequests leave, IDbSession session, CancellationToken ct) =>
        {
            var mine = await leave.MineAsync(ct);
            await session.CommitAsync(ct);
            return Results.Ok(mine);
        })
            .RequireTenantUser().RequiresCapability(Capabilities.HrLeave).WithTags("Leave");

        var leave = app.MapGroup("/api/v1/hr/leave").RequireTenantUser().RequiresCapability(Capabilities.HrLeave).WithTags("Leave");

        leave.MapGet("/types", async (bool? all, LeaveConfig config, CancellationToken ct) => Results.Ok(await config.ListTypesAsync(all == true, ct)));

        leave.MapPut("/types/{key}", async (string key, LeaveTypeRequest request, HttpRequest http, LeaveConfig config, IDbSession session, CancellationToken ct) =>
        {
            var view = await config.PutTypeAsync(key, IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        leave.MapGet("/policies", async (LeaveConfig config, CancellationToken ct) => Results.Ok(await config.ListPoliciesAsync(ct))).InAdminArea();

        leave.MapPost("/policies", async (LeavePolicyRequest request, LeaveConfig config, IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            var view = await config.CreatePolicyAsync(request, ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Created($"/api/v1/hr/leave/policies/{view.Id}", view);
        }).InAdminArea();

        leave.MapPut("/policies/{id:guid}", async (Guid id, LeavePolicyRequest request, HttpRequest http, LeaveConfig config, IDbSession session, CancellationToken ct) =>
        {
            var view = await config.UpdatePolicyAsync(id, IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        leave.MapDelete("/policies/{id:guid}", async (Guid id, LeaveConfig config, IDbSession session, CancellationToken ct) =>
        {
            await config.DeletePolicyAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).InAdminArea();

        leave.MapPost("/adjustments", async (AdjustmentRequest request, LeaveLedger ledger, IDbSession session, CancellationToken ct) =>
        {
            var entry = await ledger.AdjustAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/leave/ledger/{request.UserId}", entry);
        }).InAdminArea();

        leave.MapGet("/ledger/{userId:guid}", async (Guid userId, LeaveLedger ledger, CancellationToken ct) => Results.Ok(await ledger.EntriesAsync(userId, ct)));

        leave.MapPost("/preview", async (LeaveRequestInput input, LeaveRequests requests, ILocalizer localizer, RequestLocale locale, IDbSession session, CancellationToken ct) =>
        {
            var preview = await requests.PreviewAsync(input, ct);
            await session.CommitAsync(ct);
            return Results.Ok(preview with { Problems = preview.Problems.Select(p => p with { Message = localizer.Get(p.Key, locale.Locale) }).ToList() });
        });

        leave.MapPost("/requests", async (LeaveRequestInput input, HttpRequest http, LeaveRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var key = http.Headers[IdempotencyStore.HeaderName].ToString();
            var view = await requests.SubmitAsync(input, key.Length > 0 ? key : null, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/leave/requests/{view.Id}", view);
        });

        leave.MapGet("/requests/{id:guid}", async (Guid id, LeaveRequests requests, CancellationToken ct) => Results.Ok(await requests.GetAsync(id, ct)));

        leave.MapGet("/requests/{id:guid}/document", async (Guid id, LeaveRequests requests, HttpResponse response, CancellationToken ct) =>
        {
            var (content, type, name) = await requests.DocumentAsync(id, ct);
            response.Headers.CacheControl = "no-store";
            return Results.File(content, type, name);
        });

        leave.MapPost("/requests/{id:guid}:cancel", async (Guid id, LeaveRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.CancelAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        leave.MapPost("/requests/{id:guid}:shorten", async (Guid id, ShortenRequest request, LeaveRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.ShortenAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        leave.MapPost("/requests/{id:guid}/replacement:answer", async (Guid id, ReplacementAnswer answer, LeaveRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.AnswerReplacementAsync(id, answer, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        leave.MapPut("/requests/{id:guid}/replacement", async (Guid id, ReplacementChange change, LeaveRequests requests, IDbSession session, CancellationToken ct) =>
        {
            var view = await requests.NameReplacementAsync(id, change, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        leave.MapPost("/documents", async (string? name, HttpRequest http, LeaveDocuments documents, IDbSession session, CancellationToken ct) =>
        {
            var buffer = new byte[LeaveDocuments.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct)) > 0)
            {
                total += read;
                if (total > LeaveDocuments.MaxBytes)
                {
                    throw Primitives.AppException.Validation("file", "validation.leave.document_size");
                }
            }
            var view = await documents.UploadAsync(name, http.ContentType, buffer[..total], ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/leave/documents/{view.Id}", view);
        }).DisableAntiforgery();

        leave.MapGet("/team", async (DateOnly from, DateOnly to, Guid? orgUnitId, LeaveRequests requests, CancellationToken ct) =>
            Results.Ok(await requests.TeamAsync(from, to, orgUnitId, ct)));

        return app;
    }
}
