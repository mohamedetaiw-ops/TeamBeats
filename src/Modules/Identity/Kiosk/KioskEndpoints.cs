using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Kiosk;

/// <summary>Kiosk devices, badges and kiosk sign-in (spec 030 US4).</summary>
public static class KioskEndpoints
{
    public const string RateLimit = "kiosk";

    public static IEndpointRouteBuilder MapKioskEndpoints(this IEndpointRouteBuilder app)
    {
        // The tablet: no session needed, the device token cookie says which kiosk it is.
        var kiosk = app.MapGroup("/api/v1/kiosk").WithTags("Kiosk");
        kiosk.MapGet("/device", async (HttpRequest request, HttpResponse response, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var token = request.Cookies[KioskRules.DeviceCookie];
            var info = await kiosks.InfoAsync(token, ct);
            await session.CommitAsync(ct);
            if (!info.Enrolled && !string.IsNullOrEmpty(token))
            {
                response.Cookies.Delete(KioskRules.DeviceCookie, DeviceCookie(null));
            }
            response.Headers.CacheControl = "no-store";
            return Results.Ok(info);
        });
        kiosk.MapPost("/enrol", async (KioskEnrolRequest body, HttpRequest request, HttpResponse response, KioskService kiosks, IDbSession session,
            CancellationToken ct) =>
        {
            var token = await kiosks.EnrolAsync(body.Code, ct);
            var info = await kiosks.InfoAsync(token, ct);
            await session.CommitAsync(ct);
            response.Cookies.Append(KioskRules.DeviceCookie, token, DeviceCookie(KioskRules.DeviceCookieLifetime));
            return Results.Ok(info);
        }).RequireRateLimiting(RateLimit);
        kiosk.MapPost("/signin", async (KioskSignInRequest body, HttpRequest request, HttpResponse response, KioskService kiosks, SessionService sessions,
            IDbSession session, CancellationToken ct) =>
        {
            var (token, _) = await kiosks.SignInAsync(request.Cookies[KioskRules.DeviceCookie], body, ct);
            await session.CommitAsync(ct);
            sessions.SetCookie(response, token, persistent: false);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimit);

        // Device administrators.
        var admin = app.MapGroup("/api/v1/identity").RequireTenantUser().InAdminArea().WithTags("Tenant: kiosks");
        admin.MapGet("/kiosks", async (KioskService kiosks, CancellationToken ct) => Results.Ok(await kiosks.ListAsync(ct)));
        admin.MapPost("/kiosks", async (KioskDeviceRequest body, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var created = await kiosks.CreateAsync(body, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/identity/kiosks/{created.Device.Id}", created);
        });
        admin.MapPut("/kiosks/{id:guid}", async (Guid id, KioskDeviceRequest body, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var view = await kiosks.UpdateAsync(id, body, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        admin.MapPost("/kiosks/{id:guid}:revoke", async (Guid id, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var view = await kiosks.RevokeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        admin.MapPost("/kiosks/{id:guid}:enrol-code", async (Guid id, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var enrolment = await kiosks.NewEnrolCodeAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(enrolment);
        });
        admin.MapGet("/badges/{userId:guid}", async (Guid userId, KioskService kiosks, CancellationToken ct) => Results.Ok(await kiosks.BadgeAsync(userId, ct)));
        admin.MapPost("/badges/{userId:guid}", async (Guid userId, HttpResponse response, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var issued = await kiosks.IssueBadgeAsync(userId, ct);
            await session.CommitAsync(ct);
            response.Headers.CacheControl = "no-store";
            return Results.Ok(issued);
        });
        admin.MapPost("/badges/{userId:guid}:reset-pin", async (Guid userId, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var view = await kiosks.ResetPinAsync(userId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // The person's own badge, from their own device.
        var me = app.MapGroup("/api/v1/me/kiosk-badge").RequireTenantUser().WithTags("Me");
        me.MapGet("/", async (KioskService kiosks, CancellationToken ct) => Results.Ok(await kiosks.MyBadgeAsync(ct)));
        me.MapPut("/pin", async (KioskPinRequest body, KioskService kiosks, IDbSession session, CancellationToken ct) =>
        {
            var view = await kiosks.SetMyPinAsync(body.Pin, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireStepUp();
        return app;
    }

    private static CookieOptions DeviceCookie(TimeSpan? maxAge) =>
        new() { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = maxAge };
}
