using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Microsoft365;
using TeamBeats.Modules.Integrations.Weather;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations;

/// <summary>Connections to external systems (spec 006 User Story 5, module map: Integrations).</summary>
public static class IntegrationsModule
{
    public static readonly MigrationSource Migrations = new("integrations", typeof(IntegrationsModule).Assembly);

    public const string ConsentBindingCookie = "tb_consent";
    public const string ConsentCallbackPath = "/api/v1/integrations/consent/callback";
    public const string OutlookBindingCookie = "tb_outlook";
    public const string OutlookCallbackPath = "/api/v1/integrations/outlook/callback";
    private const string OutlookCookiePath = "/api/v1/integrations/outlook";

    public static IServiceCollection AddIntegrationsModule(this IServiceCollection services)
    {
        services.AddHttpClient(HttpMicrosoftGraph.ClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<IMicrosoftGraph, HttpMicrosoftGraph>();
        services.AddScoped<ConnectionService>();
        services.AddScoped<IConnectionFeatures, ConnectionFeatures>();
        services.AddScoped<IDirectorySource, MicrosoftDirectorySource>();
        services.AddSingleton<TokenProtector>();
        services.AddScoped<DelegatedTokenStore>();
        services.AddScoped<DelegatedAccessService>();
        services.AddMemoryCache();
        services.AddScoped<GraphReadCache>();
        services.AddScoped<IMailbox, GraphMailbox>();
        services.AddScoped<ICalendar, GraphCalendar>();
        services.AddScoped<IAvailability, GraphAvailability>();
        services.AddSingleton<TeamBeats.Modules.Experience.Contracts.IComponentRegistration, OutlookComponents>();
        services.AddHttpClient(OpenMeteoWeather.ClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IWeather, OpenMeteoWeather>();
        services.AddMessageHandler<SessionEnded, DropTokensOnSessionEnded>("integrations.tokens_session_ended");
        services.AddMessageHandler<SessionRevoked, DropTokensOnSessionRevoked>("integrations.tokens_session_revoked");
        services.AddMessageHandler<UserDeprovisioned, DropTokensOnUserDeprovisioned>("integrations.tokens_user_deprovisioned");
        return services;
    }

    public static IEndpointRouteBuilder MapIntegrationsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1/integrations").RequireTenantUser().InAdminArea().WithTags("Integrations: connections");

        api.MapGet("/catalogue/microsoft365", async (IAuthorizationService authz, ConnectionService connections, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await connections.CatalogueAsync(ct));
        });

        api.MapGet("/connections", async (IAuthorizationService authz, ConnectionService connections, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await connections.ListAsync(ct));
        });

        api.MapPost("/connections", async (CreateConnectionRequest request, IAuthorizationService authz, ConnectionService connections,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            var created = await connections.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/integrations/connections/{created.Id}", created);
        });

        api.MapGet("/connections/{id:guid}", async (Guid id, IAuthorizationService authz, ConnectionService connections, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await connections.GetAsync(id, ct) ?? throw AppException.NotFound());
        });

        api.MapPost("/connections/{id:guid}:test", async (Guid id, IAuthorizationService authz, ConnectionService connections,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            var result = await connections.TestAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        api.MapPost("/connections/{id:guid}:disable", (Guid id, IAuthorizationService authz, ConnectionService connections, IDbSession session, CancellationToken ct) =>
            SetEnabled(id, false, authz, connections, session, ct));
        api.MapPost("/connections/{id:guid}:enable", (Guid id, IAuthorizationService authz, ConnectionService connections, IDbSession session, CancellationToken ct) =>
            SetEnabled(id, true, authz, connections, session, ct));

        api.MapPost("/connections/{id:guid}/features/{feature}:enable", (Guid id, string feature, IAuthorizationService authz,
            ConnectionService connections, IDbSession session, CancellationToken ct) => SetFeature(id, feature, true, authz, connections, session, ct));
        api.MapPost("/connections/{id:guid}/features/{feature}:disable", (Guid id, string feature, IAuthorizationService authz,
            ConnectionService connections, IDbSession session, CancellationToken ct) => SetFeature(id, feature, false, authz, connections, session, ct));

        api.MapPut("/connections/{id:guid}/features/{feature}/options", async (Guid id, string feature, FeatureOptionsRequest request,
            IAuthorizationService authz, ConnectionService connections, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            var view = await connections.SetOptionsAsync(id, feature, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });

        api.MapGet("/connections/{id:guid}/dependents", async (Guid id, IAuthorizationService authz, ConnectionService connections, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await connections.PreviewDeleteAsync(id, ct));
        });

        api.MapDelete("/connections/{id:guid}", async (Guid id, bool? confirm, IAuthorizationService authz, ConnectionService connections,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            await connections.DeleteAsync(id, confirm == true, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Starts Microsoft admin consent for one feature (spec 006 US5 scenario 3 and 5).
        api.MapGet("/connections/{id:guid}/features/{feature}/consent", async (Guid id, string feature, HttpRequest request, HttpResponse response,
            ITenantContext context, IAuthorizationService authz, ConnectionService connections, IOptions<HostResolutionOptions> hosts,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            var tenantKey = context.Host.Tenant?.Key ?? throw AppException.NotFound();
            var (url, binding) = await connections.StartConsentAsync(id, feature, tenantKey, ConsentRedirectUri(request, hosts.Value), ct);
            await session.CommitAsync(ct);
            response.Cookies.Append(ConsentBindingCookie, binding, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/api/v1/integrations", MaxAge = ConnectionService.ConsentLifetime,
            });
            return Results.Redirect(url);
        });

        // Microsoft returns here. In a shared installation one redirect URI on the platform host serves every tenant,
        // so the platform host only routes the browser back to the tenant host named in the state, where the
        // signed-in administrator, the single-use state and the browser binding are all checked.
        app.MapGet(ConsentCallbackPath, async (HttpRequest request, HttpResponse response, ITenantContext context,
            IOptions<HostResolutionOptions> hosts, IServiceProvider services, CancellationToken ct) =>
        {
            var state = request.Query["state"].ToString();
            if (context.Host.Kind == HostScopeKind.Platform)
            {
                var key = state.Split('.', 2)[0];
                if (!TenantKeyShape(key))
                {
                    throw AppException.NotFound();
                }
                return Results.Redirect($"https://{key}.{hosts.Value.BaseDomain}{ConsentCallbackPath}{request.QueryString}");
            }
            if (context.Host.Kind != HostScopeKind.Tenant)
            {
                throw AppException.NotFound();
            }
            if (context.ActorType != ActorType.User)
            {
                throw new AppException(ProblemCodes.Unauthenticated, 401);
            }
            var authz = services.GetRequiredService<IAuthorizationService>();
            await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
            var connections = services.GetRequiredService<ConnectionService>();
            var (connectionId, granted) = await connections.CompleteConsentAsync(state, request.Cookies[ConsentBindingCookie],
                request.Query["admin_consent"], request.Query["tenant"], request.Query["error"], ct);
            await services.GetRequiredService<IDbSession>().CommitAsync(ct);
            response.Cookies.Delete(ConsentBindingCookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = "/api/v1/integrations" });
            var target = "/admin/connections" + (connectionId is { } c ? $"?connection={c}&consent={(granted ? "granted" : "failed")}" : "?consent=failed");
            return Results.Redirect(target);
        }).WithTags("Integrations: connections");

        MapOutlookEndpoints(app);
        WorkspaceEndpoints.Map(app);
        WeatherEndpoints.Map(app);
        return app;
    }

    /// <summary>
    /// The employee's own Outlook connection (spec 018 FR-013, spec 003 FR-016). Access to a mailbox is decided by
    /// Microsoft 365 for the signed-in user, so no TeamBeats permission is checked, only the entitlement.
    /// </summary>
    private static void MapOutlookEndpoints(IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/v1/me/outlook").RequireTenantUser().WithTags("Integrations: Outlook");

        me.MapGet("", async (DelegatedAccessService outlook, CancellationToken ct) => Results.Ok(await outlook.StatusAsync(ct)));

        me.MapGet("/connect", async (bool? silent, string? returnTo, HttpRequest request, HttpResponse response, ITenantContext context,
            DelegatedAccessService outlook, IOptions<HostResolutionOptions> hosts, IDbSession session, CancellationToken ct) =>
        {
            var tenantKey = context.Host.Tenant?.Key ?? throw AppException.NotFound();
            var back = LocalPath(returnTo);
            var start = await outlook.StartConnectAsync(tenantKey, RedirectUri(request, hosts.Value, OutlookCallbackPath), silent == true, back, ct);
            await session.CommitAsync(ct);
            if (start.Url is null)
            {
                return Results.Redirect(WithOutcome(back, start.Outcome!));
            }
            response.Cookies.Append(OutlookBindingCookie, start.Binding!, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = OutlookCookiePath, MaxAge = DelegatedAccessService.ConnectLifetime,
            });
            return Results.Redirect(start.Url);
        });

        me.MapDelete("", async (DelegatedAccessService outlook, IDbSession session, CancellationToken ct) =>
        {
            await outlook.DisconnectAsync(ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Microsoft returns here; routed from the platform host to the tenant host as for admin consent. The same
        // user and session that started the connect must finish it, with the single-use state and browser binding.
        app.MapGet(OutlookCallbackPath, async (HttpRequest request, HttpResponse response, ITenantContext context,
            IOptions<HostResolutionOptions> hosts, IServiceProvider services, CancellationToken ct) =>
        {
            var state = request.Query["state"].ToString();
            if (context.Host.Kind == HostScopeKind.Platform)
            {
                var key = state.Split('.', 2)[0];
                if (!TenantKeyShape(key))
                {
                    throw AppException.NotFound();
                }
                return Results.Redirect($"https://{key}.{hosts.Value.BaseDomain}{OutlookCallbackPath}{request.QueryString}");
            }
            if (context.Host.Kind != HostScopeKind.Tenant)
            {
                throw AppException.NotFound();
            }
            if (context.ActorType != ActorType.User)
            {
                throw new AppException(ProblemCodes.Unauthenticated, 401);
            }
            var outlook = services.GetRequiredService<DelegatedAccessService>();
            var (returnTo, outcome) = await outlook.CompleteConnectAsync(state, request.Cookies[OutlookBindingCookie], request.Query["code"],
                request.Query["error"], request.Query["error_description"], RedirectUri(request, hosts.Value, OutlookCallbackPath), ct);
            await services.GetRequiredService<IDbSession>().CommitAsync(ct);
            response.Cookies.Delete(OutlookBindingCookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = OutlookCookiePath });
            return Results.Redirect(WithOutcome(returnTo, outcome));
        }).WithTags("Integrations: Outlook");
    }

    /// <summary>Only a path on this host, so the connect cannot be used as an open redirect.</summary>
    private static string LocalPath(string? returnTo) =>
        returnTo is { Length: > 0 and <= 200 } p && p[0] == '/' && !p.StartsWith("//", StringComparison.Ordinal) && !p.Contains('\\')
            && Uri.IsWellFormedUriString(p, UriKind.Relative) ? p : "/";

    private static string WithOutcome(string path, string outcome) => path + (path.Contains('?') ? '&' : '?') + "outlook=" + outcome;

    private static async Task<IResult> SetEnabled(Guid id, bool enabled, IAuthorizationService authz, ConnectionService connections, IDbSession session, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
        await connections.SetEnabledAsync(id, enabled, ct);
        await session.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetFeature(Guid id, string feature, bool enabled, IAuthorizationService authz, ConnectionService connections,
        IDbSession session, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.IntegrationManage, AuthzTarget.Tenant, ct);
        await connections.SetFeatureAsync(id, feature, enabled, ct);
        await session.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Shared installs use one platform-host redirect URI for the TeamBeats application; dedicated installs use their own host.</summary>
    private static string ConsentRedirectUri(HttpRequest request, HostResolutionOptions hosts) => RedirectUri(request, hosts, ConsentCallbackPath);

    private static string RedirectUri(HttpRequest request, HostResolutionOptions hosts, string path) =>
        hosts.Mode == InstallationMode.Shared
            ? $"https://{hosts.PlatformSubdomain}.{hosts.BaseDomain}{path}"
            : $"https://{request.Host.Value}{path}";

    private static bool TenantKeyShape(string key) =>
        key.Length is > 0 and <= 63 && key.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
}
