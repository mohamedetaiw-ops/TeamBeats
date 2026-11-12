using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Kiosk;

/// <summary>
/// The kiosk feature block (spec 030 US4 scenario 3, FR-017), enforced by the API: a session on a shared device reaches
/// only the routes <see cref="SharedDeviceRules"/> lists for the features its kiosk allows. Everything else, payslips,
/// mail and documents among it, answers 403 even when the address is typed. A package without kiosks ends the session.
/// </summary>
public static class SharedDeviceGuard
{
    public static IApplicationBuilder UseSharedDeviceRules(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var context = http.RequestServices.GetRequiredService<ITenantContext>();
            if (context.SharedDevice is not { } device || http.GetEndpoint() is not RouteEndpoint endpoint)
            {
                await next(http);
                return;
            }
            if (!await http.RequestServices.GetRequiredService<IEntitlementService>().IsEnabledAsync(Capabilities.MobileKiosk, http.RequestAborted))
            {
                throw AppException.NotInPackage(Capabilities.MobileKiosk);
            }
            if (!SharedDeviceRules.AllowsRoute(http.Request.Method, endpoint.RoutePattern.RawText, device.Features))
            {
                throw AppException.Denied("authz.denied.shared_device");
            }
            await next(http);
        });
}
