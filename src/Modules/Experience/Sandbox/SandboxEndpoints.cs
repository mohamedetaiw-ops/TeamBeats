using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Sandbox;

/// <summary>Routes for tenant-built components (spec 032 R3 FR-031): the tenant's own, the framed document, and the platform review.</summary>
public static class SandboxEndpoints
{
    public static void MapSandboxEndpoints(this IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/experience").RequireTenantUser().WithTags("Experience: tenant components");

        x.MapGet("/tenant-components", async (TenantComponents components, CancellationToken ct) => Results.Ok(await components.ListAsync(ct)));

        x.MapPost("/tenant-components", async (TenantComponentRequest request, TenantComponents components, IDbSession session, CancellationToken ct) =>
        {
            var view = await components.SubmitAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        x.MapPut("/tenant-components/{id}/enablement", async (string id, TenantComponentEnablement request, TenantComponents components, IDbSession session,
            CancellationToken ct) =>
        {
            var view = await components.EnableAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // What a page needs to frame the component; not found unless it is enabled with an approved version.
        x.MapGet("/sandbox/{id}", async (string id, TenantComponents components, CancellationToken ct) =>
            await components.RuntimeAsync(id, ct) is { } runtime ? Results.Ok(runtime) : throw AppException.NotFound());

        // The framed document, under its own sandbox policy, never cached and never sniffed.
        x.MapGet("/sandbox/{id}/{version:int}", async (string id, int version, TenantComponents components, HttpResponse response, CancellationToken ct) =>
        {
            var (document, hosts) = await components.DocumentAsync(id, version, ct);
            response.Headers.ContentSecurityPolicy = SandboxPolicy.For(hosts);
            response.Headers.CacheControl = "no-store";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Results.Content(document, "text/html; charset=utf-8");
        }).ExcludeFromDescription();

        var platform = app.MapGroup("/api/v1/platform/component-reviews").RequirePlatformOperator().WithTags("Platform: tenant components");

        // Versions waiting for review across tenants: each tenant is read in its own context, and only its component tables.
        platform.MapGet("/", async (ITenantLister lister, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var queue = new List<ComponentReviewItem>();
            foreach (var tenant in (await lister.ListAsync(ct)).Where(t => t.Status != TenantLifecycleStatus.Closed))
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                queue.AddRange(await scope.ServiceProvider.GetRequiredService<TenantComponents>().PendingAsync(tenant, ct));
            }
            return Results.Ok(queue.OrderBy(q => q.SubmittedAt));
        });

        platform.MapGet("/{tenantId:guid}/{id}/{version:int}/document", async (Guid tenantId, string id, int version, ITenantRegistry tenants,
            TenantContextAccessor context, TenantComponents components, CancellationToken ct) =>
        {
            context.SetTenant((await tenants.FindByIdAsync(tenantId, ct) ?? throw AppException.NotFound()).Id);
            return Results.Text(await components.ReviewDocumentAsync(id, version, ct), "text/plain; charset=utf-8");
        });

        platform.MapPost("/{tenantId:guid}/{id}/{version:int}", async (Guid tenantId, string id, int version, ComponentReviewRequest request,
            ITenantRegistry tenants, TenantContextAccessor context, TenantComponents components, IDbSession session, CancellationToken ct) =>
        {
            context.SetTenant((await tenants.FindByIdAsync(tenantId, ct) ?? throw AppException.NotFound()).Id);
            await components.ReviewAsync(tenantId, id, version, request, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
    }
}
