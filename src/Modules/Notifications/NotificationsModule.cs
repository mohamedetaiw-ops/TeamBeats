using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Notifications;

public sealed record UnreadCount(int Unread);

public sealed record MarkReadResult(string Link);

public sealed record RelayTestResult(string Outcome, string? Error);

/// <summary>Notifications and email (spec 015, module map: Notifications).</summary>
public static class NotificationsModule
{
    public static readonly MigrationSource Migrations = new("notifications", typeof(NotificationsModule).Assembly);

    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.Section));
        services.AddScoped<NotificationSettingsService>();
        services.AddScoped<PreferenceService>();
        services.AddScoped<NotificationTemplates>();
        services.AddScoped<NotificationCentre>();
        services.AddScoped<INotificationSender, NotificationSender>();
        services.AddScoped<EmailDelivery>();
        services.AddScoped<CapturedMailbox>();
        if (EmailOptions.IsCapture(configuration))
        {
            services.AddScoped<IEmailSender, CaptureEmailSender>();
        }
        else
        {
            services.AddScoped<IEmailSender, SmtpEmailSender>();
        }
        return services;
    }

    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me").RequireTenantUser().WithTags("Notifications: my notifications");

        me.MapGet("/notifications", async (string? filter, string? category, string? cursor, int? limit, ITenantContext context,
            NotificationCentre centre, CancellationToken ct) =>
        {
            var parsed = filter switch
            {
                null or "all" => NotificationFilter.All,
                "unread" => NotificationFilter.Unread,
                "mandatory" => NotificationFilter.Mandatory,
                _ => throw AppException.Validation("filter", "validation.notification.filter_invalid"),
            };
            return Results.Ok(await centre.ListAsync(context.RequireUser(), parsed, category, cursor, limit ?? 20, ct));
        });

        me.MapGet("/notifications/count", async (ITenantContext context, NotificationCentre centre, HttpResponse response, CancellationToken ct) =>
        {
            response.Headers.CacheControl = "private, no-store";
            return Results.Ok(new UnreadCount(await centre.UnreadAsync(context.RequireUser(), ct)));
        });

        me.MapPost("/notifications/{id:guid}/read", async (Guid id, ITenantContext context, NotificationCentre centre, IDbSession session, CancellationToken ct) =>
        {
            var link = await centre.MarkReadAsync(context.RequireUser(), id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new MarkReadResult(link));
        });

        me.MapPost("/notifications/read-all", async (ITenantContext context, NotificationCentre centre, IDbSession session, CancellationToken ct) =>
        {
            await centre.MarkAllReadAsync(context.RequireUser(), ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        me.MapGet("/notification-preferences", async (ITenantContext context, PreferenceService preferences, CancellationToken ct) =>
            Results.Ok(await preferences.GetAsync(context.RequireUser(), ct)));

        me.MapPut("/notification-preferences", async (UpdatePreferencesRequest request, ITenantContext context, PreferenceService preferences,
            IDbSession session, CancellationToken ct) =>
        {
            var view = await preferences.UpdateAsync(context.RequireUser(), request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        var admin = app.MapGroup("/api/v1/notifications").RequireTenantUser().InAdminArea().WithTags("Notifications: administration");

        admin.MapGet("/settings", async (IAuthorizationService authz, NotificationSettingsService settings, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await settings.GetViewAsync(ct));
        });

        admin.MapPut("/settings", async (UpdateNotificationSettingsRequest request, IAuthorizationService authz, NotificationSettingsService settings,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            var view = await settings.UpdateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        // Sends a test email to the administrator's own address through the effective relay (US4 scenario 2).
        admin.MapPost("/settings/test", async (IAuthorizationService authz, ITenantContext context, IUserDirectory users, RequestLocale locale,
            EmailDelivery delivery, IAuditWriter audit, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            var me = await users.GetAsync(context.RequireUser(), ct);
            if (me is null || string.IsNullOrWhiteSpace(me.Email))
            {
                throw AppException.Conflict("conflict.notifications.no_email_address");
            }
            var result = await delivery.SendTestAsync(me.Email, locale.Locale, ct);
            var outcome = result.Outcome switch { EmailOutcome.Sent => "sent", EmailOutcome.HardBounce => "refused", _ => "failed" };
            await audit.WriteAsync(new AuditEntry(AuditActions.NotificationRelayTested, "notification_settings", context.RequireTenant().ToString(),
                result.Outcome == EmailOutcome.Sent ? AuditOutcome.Success : AuditOutcome.Failure, Changes: new { outcome, error = result.Error }), ct);
            await session.CommitAsync(ct);
            return Results.Ok(new RelayTestResult(outcome, result.Error));
        });

        admin.MapGet("/templates/{templateKey}/{locale}", async (string templateKey, string locale, IAuthorizationService authz,
            NotificationTemplates templates, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await templates.GetAsync(templateKey, locale, ct));
        });

        admin.MapPut("/templates/{templateKey}/{locale}", async (string templateKey, string locale, TemplateOverrideRequest request,
            IAuthorizationService authz, NotificationTemplates templates, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            var view = await templates.SetOverrideAsync(templateKey, locale, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        admin.MapDelete("/templates/{templateKey}/{locale}", async (string templateKey, string locale, IAuthorizationService authz,
            NotificationTemplates templates, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            var view = await templates.ResetAsync(templateKey, locale, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        return app;
    }

    /// <summary>
    /// Development, test and staging only (mapped by the development host): the captured mailbox, and a sample
    /// notification to the signed-in user so the centre and email can be tried without a sending module.
    /// </summary>
    public static IEndpointRouteBuilder MapNotificationDevEndpoints(this IEndpointRouteBuilder app)
    {
        var dev = app.MapGroup("/api/v1/dev/notifications").RequireTenantUser().WithTags("Development: notifications").ExcludeFromDescription();

        dev.MapGet("/mailbox", async (CapturedMailbox mailbox, IAuthorizationService authz, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.NotificationsManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await mailbox.ListAsync(50, ct));
        });

        dev.MapPost("/sample", async (SampleNotificationRequest? request, ITenantContext context, INotificationSender sender, IDbSession session, CancellationToken ct) =>
        {
            var result = await sender.SendAsync(new NotificationRequest(
                [context.RequireUser()],
                request?.Category ?? NotificationCategories.System,
                "system.sample",
                "/",
                new Dictionary<string, string> { ["label"] = request?.Label ?? "TeamBeats" },
                Urgent: request?.Urgent ?? false,
                GroupKey: request?.GroupKey), ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        return app;
    }
}

public sealed record SampleNotificationRequest(string? Category, string? Label, bool? Urgent, string? GroupKey);
