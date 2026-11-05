using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Primitives;

namespace TeamBeats.TenantContext;

public static class EndpointScopeExtensions
{
    /// <summary>Requires an authenticated tenant user on a tenant host. Authorization still happens per action.</summary>
    public static TBuilder RequireTenantUser<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var tenant = ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.ActorType != ActorType.User || tenant.TenantId is null || tenant.Host.Kind != HostScopeKind.Tenant)
            {
                throw AppException.Unauthenticated();
            }
            return await next(ctx);
        });

    /// <summary>Requires an authenticated platform operator on the platform host.</summary>
    public static TBuilder RequirePlatformOperator<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var tenant = ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
            if (tenant.Host.Kind != HostScopeKind.Platform)
            {
                // Platform endpoints do not exist on tenant hosts.
                throw AppException.NotFound();
            }
            if (tenant.ActorType != ActorType.PlatformOperator)
            {
                throw AppException.Unauthenticated();
            }
            return await next(ctx);
        });
}
