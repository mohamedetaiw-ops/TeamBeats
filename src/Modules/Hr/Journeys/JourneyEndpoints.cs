using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

/// <summary>Journey endpoints (spec 022 R2 slice 3). Every endpoint needs <c>hr.journeys</c> in the plan.</summary>
public static class JourneyEndpoints
{
    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapJourneyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/me/journeys", async (JourneyService journeys, CancellationToken ct) => Results.Ok(await journeys.MineAsync(ct)))
            .RequireTenantUser().RequiresCapability(Capabilities.HrJourneys).WithTags("Journeys");

        var hr = app.MapGroup("/api/v1/hr").RequireTenantUser().RequiresCapability(Capabilities.HrJourneys).WithTags("Journeys");

        hr.MapGet("/journey-templates", async (JourneyTemplates templates, CancellationToken ct) => Results.Ok(await templates.ListAsync(ct))).InAdminArea();

        hr.MapPost("/journey-templates", async (JourneyTemplateRequest request, JourneyTemplates templates, IDbSession session, HttpResponse response,
            CancellationToken ct) =>
        {
            var view = await templates.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Created($"/api/v1/hr/journey-templates/{view.Id}", view);
        }).InAdminArea();

        hr.MapGet("/journey-templates/{id:guid}", async (Guid id, JourneyTemplates templates, HttpResponse response, CancellationToken ct) =>
        {
            var view = await templates.GetAsync(id, ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapPut("/journey-templates/{id:guid}", async (Guid id, JourneyTemplateRequest request, HttpRequest http, JourneyTemplates templates, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await templates.UpdateAsync(id, IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapGet("/journey-settings", async (Preboarding preboarding, HttpResponse response, CancellationToken ct) =>
        {
            var view = await preboarding.SettingsAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        hr.MapPut("/journey-settings", async (JourneySettingsRequest request, HttpRequest http, Preboarding preboarding, IDbSession session, CancellationToken ct) =>
        {
            var view = await preboarding.UpdateSettingsAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).InAdminArea();

        // The pre-boarding page (FR-016). No session: the joiner has no account yet. The tenant comes from the host, as
        // for inbound webhooks; the link's token is the only thing that opens it.
        app.MapGet("/api/v1/preboarding/{token}", async (string token, ITenantContext context, TenantContextAccessor accessor, Preboarding preboarding,
            IDbSession session, HttpResponse response, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { Status: TenantLifecycleStatus.Active } tenant)
            {
                throw AppException.NotFound();
            }
            accessor.SetTenant(tenant.Id);
            accessor.SetActor(ActorType.Anonymous, null);
            var view = await preboarding.PageAsync(token, ct);
            await session.CommitAsync(ct);
            response.Headers.CacheControl = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            return Results.Ok(view);
        }).WithTags("Journeys");

        hr.MapGet("/journeys", async (string? status, bool? overdue, Guid? userId, JourneyService journeys, CancellationToken ct) =>
            Results.Ok(await journeys.ListAsync(status, overdue == true, userId, ct)));

        hr.MapPost("/journeys", async (JourneyStartRequest request, JourneyService journeys, IDbSession session, CancellationToken ct) =>
        {
            var view = await journeys.StartAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/hr/journeys/{view.Journey.Id}", view);
        });

        hr.MapGet("/journeys/{id:guid}", async (Guid id, JourneyService journeys, CancellationToken ct) => Results.Ok(await journeys.GetAsync(id, ct)));

        hr.MapPut("/journeys/{id:guid}/anchor", async (Guid id, JourneyAnchorRequest request, JourneyService journeys, IDbSession session, CancellationToken ct) =>
        {
            var view = await journeys.MoveAnchorAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapPut("/journeys/{id:guid}/buddy", async (Guid id, JourneyBuddyRequest request, JourneyService journeys, IDbSession session, CancellationToken ct) =>
        {
            var view = await journeys.SetBuddyAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapPost("/journeys/{id:guid}/cancel", async (Guid id, JourneyCancelRequest request, JourneyService journeys, IDbSession session, CancellationToken ct) =>
        {
            var view = await journeys.CancelAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapPost("/journeys/{id:guid}/steps/{key}/access-items/{itemId:guid}", async (Guid id, string key, Guid itemId, AccessConfirmRequest request,
            JourneyService journeys, IDbSession session, CancellationToken ct) =>
        {
            var view = await journeys.ConfirmAccessAsync(id, key, itemId, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapPost("/journeys/{id:guid}/steps/{key}/exit-interview", async (Guid id, string key, ExitInterviewRequest request, JourneyService journeys,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await journeys.RecordExitInterviewAsync(id, key, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        hr.MapGet("/exit-reasons", async (DateOnly? from, DateOnly? to, JourneyService journeys, CancellationToken ct) =>
            Results.Ok(await journeys.ExitReasonsAsync(from, to, ct)));

        return app;
    }
}
