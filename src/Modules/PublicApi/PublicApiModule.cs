using TeamBeats.Authorization.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>Public API, MCP server and app access (spec 035, module map: PublicApi, ADR-0018).</summary>
public static class PublicApiModule
{
    public static readonly MigrationSource Migrations = new("publicapi", typeof(PublicApiModule).Assembly);

    public static IServiceCollection AddPublicApiModule(this IServiceCollection services, PublicApiLimits? limits = null)
    {
        services.AddSingleton<TenantKeys>();
        services.AddSingleton(limits ?? new PublicApiLimits());
        services.AddSingleton<PublicRateLimits>();
        services.AddScoped<CallLog>();
        services.AddScoped<ApiUsage>();
        services.AddScoped<PublicIdempotency>();
        services.AddScoped<TokenFamilies>();
        services.AddScoped<VerifiedMcpClients>();
        services.AddScoped<ClientRegistrations>();
        services.AddScoped<PlatformBlocks>();
        services.AddScoped<Apps>();
        services.AddScoped<ApiSettings>();
        services.AddScoped<Consents>();
        services.AddScoped<IAppPrincipalDirectory, AppPrincipalDirectory>();
        services.AddScoped<PublicApiMaintenance>();
        services.AddScoped<PublicApiLeavers>();
        services.AddMessageHandler<TenantMembershipDisabled, PublicApiLeavers>("publicapi.leaver");
        services.AddMessageHandler<TokenFamilyRevoked, TokenReuseNotices>("publicapi.token_reuse_notices");
        services.AddScoped<EventSubscriptions>();
        services.AddScoped<PublicEventFanout>();
        services.AddMessageHandler<Content.Contracts.ContentPublished, PublicEventHandlers>("publicapi.events.content_published");
        services.AddMessageHandler<Documents.Contracts.DocumentVersionPublished, PublicEventHandlers>("publicapi.events.document_version_published");
        services.AddMessageHandler<Policies.Contracts.PolicyPublished, PublicEventHandlers>("publicapi.events.policy_published");
        services.AddMessageHandler<Services.Contracts.RequestSubmitted, PublicEventHandlers>("publicapi.events.request_submitted");
        services.AddMessageHandler<Services.Contracts.RequestCompleted, PublicEventHandlers>("publicapi.events.request_completed");
        services.AddMessageHandler<People.Contracts.ProfileUpdated, PublicEventHandlers>("publicapi.events.profile_updated");
        services.AddMessageHandler<OrganizationUnitMoved, PublicEventHandlers>("publicapi.events.org_unit_moved");
        services.AddMessageHandler<Integrations.Contracts.ConnectorActionCompleted, SubscriptionHealth>("publicapi.subscription_health");
        services.AddOAuthServer();
        services.AddMcpEndpoint();
        services.AddScoped<Agents.Contracts.IAgentTool, WhoAmITool>();
        return services;
    }

    private static async Task<IResult> CommitAsync<T>(IDbSession session, T value, CancellationToken ct)
    {
        await session.CommitAsync(ct);
        return Results.Ok(value);
    }

    public static IEndpointRouteBuilder MapPublicApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapOAuthEndpoints();
        app.MapMcpEndpoint();

        // API administrators (US1): apps, credentials, admin consent and settings.
        var admin = app.MapGroup("/api/v1/api-access").RequireTenantUser().InAdminArea().WithTags("Tenant: API and MCP access");
        admin.MapGet("/scopes", () => Results.Ok(ScopeCatalogue.All.Select(s => new
        {
            s.Key, s.DescriptionKey, s.UserConsent, holder = s.Holder.ToString().ToLowerInvariant(), s.Capabilities,
        })));
        admin.MapGet("/apps", async (Apps apps, CancellationToken ct) => Results.Ok(await apps.ListAsync(ct)));
        // The platform's verified MCP clients a tenant may allow in its external AI client policy (FR-023). Platform data only.
        admin.MapGet("/verified-mcp-clients", async (VerifiedMcpClients verified, CancellationToken ct) =>
            Results.Ok(new { items = await verified.ActiveAsync(ct) }));
        admin.MapPost("/apps", async (AppRequest body, Apps apps, IDbSession session, CancellationToken ct) =>
        {
            var view = await apps.CreateAsync(body, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/api-access/apps/{view.Id}", view);
        });
        admin.MapGet("/apps/{id:guid}", async (Guid id, Apps apps, CancellationToken ct) => Results.Ok(await apps.DetailAsync(id, ct)));
        admin.MapPut("/apps/{id:guid}", async (Guid id, AppRequest body, Apps apps, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await apps.UpdateAsync(id, body, ct), ct));
        admin.MapPost("/apps/{id:guid}:disable", async (Guid id, Apps apps, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await apps.DisableAsync(id, ct), ct));
        admin.MapPost("/apps/{id:guid}:revoke-tokens", async (Guid id, Apps apps, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await apps.RevokeTokensAsync(id, ct), ct));
        admin.MapPost("/apps/{id:guid}:enable", async (Guid id, Apps apps, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await apps.EnableAsync(id, ct), ct));
        // A new secret is a sensitive action (FR-005): it needs a recent sign-in, and the answer is never cached.
        admin.MapPost("/apps/{id:guid}/credentials", async (Guid id, CredentialRequest? body, HttpResponse response, Apps apps, IDbSession session,
            CancellationToken ct) =>
        {
            var issued = await apps.AddCredentialAsync(id, body ?? new CredentialRequest(null), ct);
            await session.CommitAsync(ct);
            response.Headers.CacheControl = "no-store";
            return Results.Created($"/api/v1/api-access/apps/{id}", issued);
        }).RequireStepUp();
        admin.MapPost("/apps/{id:guid}/credentials/{credentialId:guid}:revoke", async (Guid id, Guid credentialId, Apps apps, IDbSession session,
            CancellationToken ct) => await CommitAsync(session, await apps.RevokeCredentialAsync(id, credentialId, ct), ct));
        admin.MapPut("/apps/{id:guid}/admin-consent", async (Guid id, AdminConsentRequest body, Consents consents, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await consents.GrantAdminAsync(id, body, ct), ct));
        admin.MapGet("/consents", async (Guid? appId, Consents consents, CancellationToken ct) => Results.Ok(await consents.ListAllAsync(appId, ct)));
        admin.MapPost("/consents/{id:guid}:revoke", async (Guid id, Consents consents, IDbSession session, CancellationToken ct) =>
        {
            await consents.RevokeAsAdminAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        admin.MapGet("/usage", async (int? days, ApiUsage usage, CancellationToken ct) => Results.Ok(await usage.GetAsync(days ?? ApiRules.CallLogDays, ct)));
        admin.MapGet("/settings", async (ApiSettings settings, CancellationToken ct) => Results.Ok(await settings.GetAsync(ct)));
        admin.MapPut("/settings", async (ApiSettingsRequest body, ApiSettings settings, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await settings.UpdateAsync(body, ct), ct));

        // Event subscriptions (US4): the catalogue, each app's subscriptions and their delivery log.
        admin.MapGet("/event-catalogue", async (EventSubscriptions subscriptions, CancellationToken ct) => Results.Ok(await subscriptions.CatalogueAsync(ct)));
        admin.MapGet("/subscriptions", async (Guid? appId, EventSubscriptions subscriptions, CancellationToken ct) =>
            Results.Ok(await subscriptions.ListAsync(appId, ct)));
        admin.MapPost("/apps/{id:guid}/subscriptions", async (Guid id, EventSubscriptionRequest body, EventSubscriptions subscriptions, IDbSession session,
            CancellationToken ct) =>
        {
            var created = await subscriptions.CreateAsync(id, body, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/api-access/subscriptions/{created.Id}", created);
        });
        admin.MapPost("/subscriptions/{id:guid}:resume", async (Guid id, EventSubscriptions subscriptions, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await subscriptions.ResumeAsync(id, ct), ct));
        admin.MapDelete("/subscriptions/{id:guid}", async (Guid id, EventSubscriptions subscriptions, IDbSession session, CancellationToken ct) =>
        {
            await subscriptions.DeleteAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
        admin.MapGet("/subscriptions/{id:guid}/deliveries", async (Guid id, EventSubscriptions subscriptions, CancellationToken ct) =>
            Results.Ok(await subscriptions.DeliveriesAsync(id, ct)));

        // The platform operator keeps the verified MCP client list after reviewing each client ID metadata document (FR-023).
        var platform = app.MapGroup("/api/v1/platform/api").RequirePlatformOperator().WithTags("Platform: API and MCP");
        platform.MapGet("/verified-mcp-clients", async (VerifiedMcpClients verified, CancellationToken ct) =>
            Results.Ok(new { items = await verified.ListAsync(ct) }));
        platform.MapPut("/verified-mcp-clients", async (VerifiedMcpClientRequest body, VerifiedMcpClients verified, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await verified.PutAsync(body, ct), ct));
        platform.MapGet("/blocked-clients", async (PlatformBlocks blocks, CancellationToken ct) => Results.Ok(new { items = await blocks.ListAsync(ct) }));
        platform.MapPost("/blocked-clients", async (PlatformBlockRequest body, PlatformBlocks blocks, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await blocks.BlockAsync(body, ct), ct));
        platform.MapDelete("/blocked-clients", async (string clientId, PlatformBlocks blocks, IDbSession session, CancellationToken ct) =>
        {
            await blocks.UnblockAsync(clientId, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Employees (US2): the consent screen and connected apps (FR-013, FR-029).
        var me = app.MapGroup("/api/v1/me").RequireTenantUser().WithTags("Me: connected apps");
        me.MapGet("/app-consent", async (string? client_id, string? redirect_uri, string? scope, Consents consents, CancellationToken ct) =>
            Results.Ok(await consents.PromptAsync(client_id, redirect_uri, scope, ct)));
        me.MapPost("/app-consent", async (ConsentGrantRequest body, Consents consents, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await consents.GrantAsync(body, ct), ct));
        me.MapGet("/connected-apps", async (Consents consents, CancellationToken ct) => Results.Ok(await consents.MineAsync(ct)));
        me.MapDelete("/connected-apps/{id:guid}", async (Guid id, Consents consents, IDbSession session, CancellationToken ct) =>
        {
            await consents.RevokeMineAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // The public REST API (FR-014): bearer tokens only, checked by UsePublicApiTokens; each module maps its own.
        app.MapPublicApi("Public: profile").MapGet("/me", async (HttpContext http, IUserDirectory users, ITenantMemberships memberships, CancellationToken ct) =>
        {
            var id = http.Caller().UserId ?? throw AppException.Denied("authz.denied.api_delegated_only");
            var user = await users.GetAsync(id, ct) ?? throw AppException.NotFound();
            var membership = await memberships.GetAsync(id, ct) ?? throw AppException.NotFound();
            return PublicEndpoints.Resource(http, new PublicProfile(id, user.DisplayName, user.Email, membership.MembershipType, user.PreferredLocale));
        }).RequireScope(ScopeCatalogue.ProfileRead).Produces<PublicProfile>();

        // events:subscribe (application apps): the event catalogue and the app's own subscriptions (FR-018).
        var events = app.MapPublicApi("Public: events");
        events.MapGet("/events", async (EventSubscriptions subscriptions, CancellationToken ct) =>
            Results.Ok(new PublicPage<EventCatalogueEntry>((await subscriptions.AvailableAsync(ct)).Where(e => e.Available).ToList(), null)))
            .RequireScope(ScopeCatalogue.EventsSubscribe).Produces<PublicPage<EventCatalogueEntry>>();
        events.MapGet("/event-subscriptions", async (HttpContext http, EventSubscriptions subscriptions, CancellationToken ct) =>
            Results.Ok(new PublicPage<PublicEventSubscription>(await subscriptions.OwnAsync(http.Caller().AppId, ct), null)))
            .RequireScope(ScopeCatalogue.EventsSubscribe).Produces<PublicPage<PublicEventSubscription>>();
        events.MapDelete("/event-subscriptions/{id:guid}", async (Guid id, HttpContext http, EventSubscriptions subscriptions, IDbSession session,
            CancellationToken ct) =>
        {
            await subscriptions.DeleteOwnAsync(http.Caller().AppId, id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).RequireScope(ScopeCatalogue.EventsSubscribe).Produces(StatusCodes.Status204NoContent);
        return app;
    }
}

/// <summary>The signed-in person of a delegated token (<c>profile:read</c>).</summary>
public sealed record PublicProfile(Guid Id, string DisplayName, string Email, string MembershipType, string? Locale);

/// <summary>Application apps hold roles in Authorization (FR-011); delegated apps act with the person's own access.</summary>
internal sealed class AppPrincipalDirectory(Apps apps) : IAppPrincipalDirectory
{
    public async Task<bool> IsApplicationAppAsync(Guid appId, CancellationToken ct = default) =>
        await apps.FindAsync(appId, ct) is { Kind: AppKinds.Application };
}

/// <summary>A membership that ends takes the person's app tokens with it at once (FR-007).</summary>
public sealed class PublicApiLeavers(TokenFamilies families) : IMessageHandler<TenantMembershipDisabled>
{
    public Task HandleAsync(TenantMembershipDisabled message, CancellationToken ct) =>
        families.RevokeForUserAsync(message.UserId, "membership_ended", ct);
}

/// <summary>
/// A token used twice (US6 scenario 2): the person and the API administrators are told, so the person can sign the app
/// in again and the administrators can check the app. The security alert is raised by the Security module.
/// </summary>
public sealed class TokenReuseNotices(IAuthorizationProvisioning roles, Modules.Notifications.Contracts.INotificationSender notifications) : IMessageHandler<TokenFamilyRevoked>
{
    public const string UserTemplate = "api.token_reuse.user";
    public const string AdminTemplate = "api.token_reuse.admin";

    public async Task HandleAsync(TokenFamilyRevoked message, CancellationToken ct)
    {
        if (message.Reason != TokenFamilyReasons.ReuseDetected)
        {
            return;
        }
        await notifications.SendAsync(new Modules.Notifications.Contracts.NotificationRequest([message.UserId],
            Modules.Notifications.Contracts.NotificationCategories.SecurityNotice, UserTemplate, "/settings/connected-apps",
            DedupeKey: "api_token_reuse:" + message.FamilyId), ct);
        var admins = await roles.TenantHoldersAsync(Permissions.ApiManage, ct);
        if (admins.Count > 0)
        {
            await notifications.SendAsync(new Modules.Notifications.Contracts.NotificationRequest(admins,
                Modules.Notifications.Contracts.NotificationCategories.SecurityNotice, AdminTemplate, $"/admin/api/apps/{message.AppId}",
                DedupeKey: "api_token_reuse_admin:" + message.FamilyId, GroupKey: "api_token_reuse:" + message.AppId), ct);
        }
    }
}
