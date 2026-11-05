using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace TeamBeats.Entitlements.Contracts;

public static class RequiresCapabilityExtensions
{
    /// <summary>Declarative backend gate (spec 005 plan): the endpoint answers 403 entitlement.not_in_package when disabled.</summary>
    public static TBuilder RequiresCapability<TBuilder>(this TBuilder builder, string capabilityKey) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var entitlements = ctx.HttpContext.RequestServices.GetRequiredService<IEntitlementService>();
            await entitlements.DemandAsync(capabilityKey, ctx.HttpContext.RequestAborted);
            return await next(ctx);
        });
}
