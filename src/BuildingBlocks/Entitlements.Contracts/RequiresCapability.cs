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

/// <summary>Marks a POST that only reads (a query or a preview), so it keeps working while an expired tenant is read only (spec 005 US4).</summary>
public sealed class ReadOnlySafeMetadata
{
    public static readonly ReadOnlySafeMetadata Instance = new();
}

public static class ReadOnlySafeExtensions
{
    public static TBuilder ReadOnlySafe<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(ReadOnlySafeMetadata.Instance);
}
