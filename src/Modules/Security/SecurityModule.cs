using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>Security controls of spec 026 (module map: Security). R1: the tenant security policy and its enforcement.</summary>
public static class SecurityModule
{
    public static readonly MigrationSource Migrations = new("security", typeof(SecurityModule).Assembly);

    public static IServiceCollection AddSecurityModule(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<PolicyStore>();
        services.AddScoped<ISecurityPolicy>(sp => sp.GetRequiredService<PolicyStore>());
        services.AddScoped<PolicyService>();
        services.AddScoped<AlertService>();
        services.AddScoped<PostureService>();
        services.AddScoped<ISecurityAlertSummary, AlertSummary>();
        services.AddMessageHandler<SecurityAlertRaised, AlertNotifier>("security.alert_notifier");
        services.AddMessageHandler<TeamBeats.Modules.Documents.Contracts.DocumentDownloaded, DownloadVolumeRule>("security.download_volume");
        services.AddMessageHandler<TeamBeats.Authorization.Contracts.RoleAssignmentChanged, OutOfHoursGrantRule>("security.privilege_grant_out_of_hours");
        services.AddMessageHandler<TeamBeats.Modules.PublicApi.Contracts.TokenFamilyRevoked, ApiTokenReuseRule>("security.api_token_reuse");
        services.AddMessageHandler<TeamBeats.Modules.PublicApi.Contracts.ApiCallsDenied, ApiDeniedBurstRule>("security.api_denied_burst");
        services.AddMessageHandler<TeamBeats.Modules.PublicApi.Contracts.ApiVolumeSpike, ApiVolumeSpikeRule>("security.api_volume_spike");
        services.AddMessageHandler<TeamBeats.Modules.Documents.Contracts.GuestOpenedClassified, GuestConfidentialRule>("security.guest_confidential_access");
        services.AddScoped<AlertSettingsService>();
        services.AddScoped<ISecurityAlertThresholds>(sp => sp.GetRequiredService<AlertSettingsService>());
        services.AddScoped<SiemService>();
        services.AddScoped<SiemStreamer>();
        services.AddScoped<AccessReviews>();
        services.AddScoped<IAuthenticationAssurance, AuthenticationAssurance>();
        services.AddScoped<TeamBeats.Authorization.Contracts.IRequestAttributeProvider, NetworkZoneAttributes>();
        return services;
    }

    private static string? IfMatch(HttpRequest http) => http.Headers.IfMatch.ToString() is { Length: > 0 } tag ? tag : null;

    public static IEndpointRouteBuilder MapSecurityEndpoints(this IEndpointRouteBuilder app)
    {
        var security = app.MapGroup("/api/v1/security").RequireTenantUser().InAdminArea().WithTags("Security");

        security.MapGet("/policy", async (PolicyService policies, HttpResponse response, CancellationToken ct) =>
        {
            var view = await policies.GetAsync(ct);
            response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        });

        security.MapPut("/policy", async (SecurityPolicyRequest request, HttpRequest http, PolicyService policies, IDbSession session, CancellationToken ct) =>
        {
            var view = await policies.UpdateAsync(IfMatch(http), request, ct);
            await session.CommitAsync(ct);
            http.HttpContext.Response.Headers.ETag = view.ETag;
            return Results.Ok(view);
        }).RequireStepUp();

        security.MapGet("/policy/preview", async (string? ipAllow, string? ipBlock, PolicyService policies, CancellationToken ct) =>
            Results.Ok(await policies.PreviewAsync(ipAllow, ipBlock, ct)));

        security.MapGet("/posture", async (PostureService posture, CancellationToken ct) => Results.Ok(await posture.GetAsync(ct)));

        security.MapGet("/alerts", async (string? status, AlertService alerts, CancellationToken ct) => Results.Ok(await alerts.ListAsync(status, ct)));

        security.MapPost("/alerts/{id:guid}/handle", async (Guid id, HandleAlertRequest request, AlertService alerts, IDbSession session, CancellationToken ct) =>
        {
            var view = await alerts.HandleAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Alert thresholds (FR-013): read with the alerts, changed with security.alerts_advanced and step-up.
        security.MapGet("/alert-settings", async (AlertSettingsService settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        security.MapPut("/alert-settings", async (AlertSettingsRequest request, AlertSettingsService settings, IDbSession session, CancellationToken ct) =>
        {
            var view = await settings.UpdateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireStepUp();

        // SIEM streaming (US4): destinations, a test event, enable and disable. Changes need step-up (section 6).
        // Access reviews (FR-012): security administrators see the reviews and can open one; reviewers decide their items.
        security.MapGet("/access-reviews", async (AccessReviews reviews, CancellationToken ct) => Results.Ok(await reviews.ListAsync(ct)));
        security.MapPost("/access-reviews", async (AccessReviews reviews, IDbSession session, CancellationToken ct) =>
        {
            var view = await reviews.StartAsync(ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/security/access-reviews/{view.Id}", view);
        }).RequireStepUp();
        var reviewer = app.MapGroup("/api/v1/access-reviews").RequireTenantUser().WithTags("Access reviews");
        reviewer.MapGet("", async (AccessReviews reviews, CancellationToken ct) => Results.Ok(await reviews.MineAsync(ct)));
        reviewer.MapPost("/items/{id:guid}:confirm", async (Guid id, AccessReviews reviews, IDbSession session, CancellationToken ct) =>
        {
            var view = await reviews.DecideAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        reviewer.MapPost("/items/{id:guid}:remove", async (Guid id, AccessReviews reviews, IDbSession session, CancellationToken ct) =>
        {
            var view = await reviews.DecideAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        security.MapGet("/siem", async (SiemService siem, CancellationToken ct) => Results.Ok(await siem.ListAsync(ct)));
        security.MapPost("/siem", async (SiemDestinationBody body, SiemService siem, IDbSession session, CancellationToken ct) =>
        {
            var view = await siem.CreateAsync(body, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/security/siem/{view.Id}", view);
        }).RequireStepUp();
        security.MapPut("/siem/{id:guid}", async (Guid id, SiemDestinationBody body, SiemService siem, IDbSession session, CancellationToken ct) =>
        {
            var view = await siem.UpdateAsync(id, body, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireStepUp();
        security.MapDelete("/siem/{id:guid}", async (Guid id, SiemService siem, IDbSession session, CancellationToken ct) =>
        {
            await siem.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).RequireStepUp();
        security.MapPost("/siem/{id:guid}:enable", async (Guid id, SiemService siem, IDbSession session, CancellationToken ct) =>
        {
            var view = await siem.SetEnabledAsync(id, true, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        security.MapPost("/siem/{id:guid}:disable", async (Guid id, SiemService siem, IDbSession session, CancellationToken ct) =>
        {
            var view = await siem.SetEnabledAsync(id, false, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        }).RequireStepUp();
        security.MapPost("/siem/{id:guid}:test", async (Guid id, SiemService siem, IDbSession session, CancellationToken ct) =>
        {
            var result = await siem.TestAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        return app;
    }
}
