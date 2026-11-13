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
        services.AddScoped<Apps>();
        services.AddScoped<ApiSettings>();
        services.AddScoped<Consents>();
        services.AddScoped<IAppPrincipalDirectory, AppPrincipalDirectory>();
        services.AddScoped<PublicApiMaintenance>();
        services.AddScoped<PublicApiLeavers>();
        services.AddMessageHandler<TenantMembershipDisabled, PublicApiLeavers>("publicapi.leaver");
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

        // The platform operator keeps the verified MCP client list after reviewing each client ID metadata document (FR-023).
        var platform = app.MapGroup("/api/v1/platform/api").RequirePlatformOperator().WithTags("Platform: API and MCP");
        platform.MapGet("/verified-mcp-clients", async (VerifiedMcpClients verified, CancellationToken ct) =>
            Results.Ok(new { items = await verified.ListAsync(ct) }));
        platform.MapPut("/verified-mcp-clients", async (VerifiedMcpClientRequest body, VerifiedMcpClients verified, IDbSession session, CancellationToken ct) =>
            await CommitAsync(session, await verified.PutAsync(body, ct), ct));

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
