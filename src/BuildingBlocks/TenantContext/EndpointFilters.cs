using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Primitives;

namespace TeamBeats.TenantContext;

/// <summary>
/// Marks an endpoint as part of the administration area, so the tenant's admin area network rules apply to it (spec 026
/// US1 scenario 2). It changes nothing else: authorization still happens per action.
/// </summary>
public sealed class AdminAreaMetadata
{
    public static readonly AdminAreaMetadata Instance = new();
}

/// <summary>
/// Marks a sensitive action (spec 026 section 6): the caller must have authenticated within the tenant's step-up window,
/// otherwise the API answers 401 <c>security.step_up_required</c> and the web app sends them for a fresh sign-in.
/// </summary>
public sealed class StepUpMetadata
{
    public static readonly StepUpMetadata Instance = new();
}

/// <summary>When the current session's holder last authenticated, and how; set by the session middleware.</summary>
public sealed record SessionAssurance(DateTimeOffset AuthenticatedAt, string Method);

public static class EndpointScopeExtensions
{
    /// <summary>Makes the endpoints sensitive actions that need a recent sign-in (spec 026 FR-005).</summary>
    public static TBuilder RequireStepUp<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(StepUpMetadata.Instance);

    /// <summary>Puts the endpoints in the administration area (spec 026): the admin area network rules apply to them.</summary>
    public static TBuilder InAdminArea<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AdminAreaMetadata.Instance);

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
