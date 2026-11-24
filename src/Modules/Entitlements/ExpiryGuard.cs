using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Entitlements;

/// <summary>
/// Read-only mode after an expired contract's grace (spec 005 US4 scenario 3, decided 2026-11-04): every write on the
/// tenant host is refused with <c>entitlement.expired</c>, so people can still read and export their data. Signing in
/// and out, the platform host and POSTs marked read-only safe (queries, previews) keep working.
/// </summary>
public static class ExpiryGuard
{
    private static readonly string[] ExemptPrefixes = ["/api/v1/auth", "/dev", "/oauth/token", "/health"];

    public static bool IsWrite(string method) =>
        !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

    public static bool IsExempt(PathString path) => ExemptPrefixes.Any(p => path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));

    public static IApplicationBuilder UseSubscriptionExpiry(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var context = http.RequestServices.GetRequiredService<TenantContextAccessor>();
            if (!IsWrite(http.Request.Method) || IsExempt(http.Request.Path) || context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { } tenant
                || http.GetEndpoint()?.Metadata.GetMetadata<ReadOnlySafeMetadata>() is not null)
            {
                await next(http);
                return;
            }
            if (context.TenantId is null)
            {
                context.SetTenant(tenant.Id);
            }
            var state = await http.RequestServices.GetRequiredService<IEntitlementService>().StateAsync(http.RequestAborted);
            if (state.State == SubscriptionState.ReadOnly)
            {
                throw new AppException(ProblemCodes.EntitlementExpired, 403);
            }
            await next(http);
        });
}
