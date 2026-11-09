using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Administration;
using TeamBeats.Modules.Analytics;
using TeamBeats.Modules.Audit;
using TeamBeats.Modules.Authorization;
using TeamBeats.Modules.Entitlements;
using TeamBeats.Modules.Experience;
using TeamBeats.Modules.Hr;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Content;
using TeamBeats.Modules.Documents;
using TeamBeats.Modules.AiGateway;
using TeamBeats.Modules.AiGateway.OpenRouter;
using TeamBeats.Modules.Policies;
using TeamBeats.Modules.Search;
using TeamBeats.Modules.Agents;
using TeamBeats.Modules.Evaluation;
using TeamBeats.Modules.Services;
using TeamBeats.Modules.Workflow;
using TeamBeats.Modules.Identity;
using TeamBeats.Modules.Integrations;
using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.Telemetry;
using TeamBeats.TenantContext;

namespace TeamBeats.Api;

/// <summary>Module and building-block registration shared by the API hosts and the worker host.</summary>
public static class TeamBeatsModules
{
    /// <summary>Migration order: platform helpers first, then each module's own schema.</summary>
    public static readonly IReadOnlyList<MigrationSource> Migrations =
    [
        MigrationRunner.Platform,
        MessagingServiceCollectionExtensions.Migrations,
        TenancyModule.Migrations,
        IdentityModule.Migrations,
        AuthorizationModule.Migrations,
        EntitlementsModule.Migrations,
        AuditModule.Migrations,
        IntegrationsModule.Migrations,
        ExperienceModule.Migrations,
        NotificationsModule.Migrations,
        HrModule.Migrations,
        PeopleModule.Migrations,
        SecurityModule.Migrations,
        WorkflowModule.Migrations,
        ServicesModule.Migrations,
        ContentModule.Migrations,
        DocumentsModule.Migrations,
        PoliciesModule.Migrations,
        AiGatewayModule.Migrations,
        SearchModule.Migrations,
        AgentsModule.Migrations,
        EvaluationModule.Migrations,
        AnalyticsModule.Migrations,
    ];

    public static IServiceCollection AddTeamBeatsCore(this IServiceCollection services, IConfiguration configuration)
    {
        var appConnection = configuration.GetConnectionString("App")
            ?? throw new InvalidOperationException("ConnectionStrings:App is required.");
        services.AddTeamBeatsPersistence(appConnection);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretStore, ConfigurationSecretStore>();
        services.AddSingleton<ILocalizer, JsonCatalogueLocalizer>();
        services.AddScoped<RequestLocale>();
        services.Configure<HostResolutionOptions>(configuration.GetSection(HostResolutionOptions.Section));

        var storageRoot = configuration["Storage:Root"] ?? Path.Combine(AppContext.BaseDirectory, ".storage");
        services.AddSingleton<IObjectStorage>(new FileSystemObjectStorage(storageRoot));
        services.AddScoped<TenantObjectStorage>();
        // Uploads are scanned by ClamAV when it is configured; without it nothing is ever clean (spec 016 plan).
        if (configuration[ClamAvScanner.HostKey] is { Length: > 0 } clamHost)
        {
            services.AddSingleton<IMalwareScanner>(new ClamAvScanner(clamHost, configuration.GetValue(ClamAvScanner.PortKey, 3310), TimeSpan.FromSeconds(30)));
        }
        else
        {
            services.AddSingleton<IMalwareScanner, UnavailableScanner>();
        }
        // Office previews are converted by Gotenberg when it is configured; without it they are not available (spec 011 plan).
        if (configuration[GotenbergConverter.UrlKey] is { Length: > 0 } gotenberg)
        {
            services.AddHttpClient(GotenbergConverter.ClientName, c => c.Timeout = GotenbergConverter.Timeout);
            var baseUrl = new Uri(gotenberg.EndsWith('/') ? gotenberg : gotenberg + "/");
            services.AddSingleton<IDocumentConverter>(sp => new GotenbergConverter(sp.GetRequiredService<IHttpClientFactory>(), baseUrl));
        }
        else
        {
            services.AddSingleton<IDocumentConverter, UnavailableConverter>();
        }

        services.AddTeamBeatsMessaging();
        services.AddMessageTransport(configuration);

        services.AddTenancyModule();
        services.AddIdentityModule(configuration);
        services.AddAuthorizationModule();
        services.AddEntitlementsModule();
        services.AddAuditModule();
        services.AddAdministrationModule();
        services.AddIntegrationsModule();
        services.AddExperienceModule();
        services.AddNotificationsModule(configuration);
        services.AddHrModule();
        services.AddPeopleModule();
        services.AddSecurityModule();
        services.AddWorkflowModule();
        services.AddServicesModule();
        services.AddContentModule();
        services.AddDocumentsModule();
        services.AddPoliciesModule();
        services.AddAiGatewayModule();
        services.AddOpenRouterAdapter();
        services.AddSearchModule();
        services.AddAgentsModule();
        services.AddEvaluationModule();
        services.AddAnalyticsModule();
        return services;
    }
}

/// <summary>HTTP composition shared by the production API and the development host.</summary>
public static class ApiComposition
{
    public static WebApplicationBuilder AddTeamBeatsApi(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddTeamBeatsCore(builder.Configuration);
        services.AddTeamBeatsTelemetry(builder.Configuration, "teambeats-api");
        services.AddOpenApi("v1");
        services.Configure<JsonOptions>(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase);
        services.AddHealthChecks();
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Audit search is an exfiltration path (spec 006 section 14): limit per user.
            o.AddPolicy("audit", http => RateLimitPartition.GetFixedWindowLimiter(
                http.RequestServices.GetRequiredService<ITenantContext>().ActorId?.ToString() ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
            // People search is a scraping path (spec 010 FR-017): limit per user.
            o.AddPolicy(PeopleModule.SearchRateLimit, http => RateLimitPartition.GetFixedWindowLimiter(
                http.RequestServices.GetRequiredService<ITenantContext>().ActorId?.ToString() ?? http.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1) }));
        });
        return builder;
    }

    public static WebApplication UseTeamBeatsApi(this WebApplication app, Action<IEndpointRouteBuilder>? extraEndpoints = null)
    {
        if (app.Configuration.GetValue<bool>("Http:TrustForwardedHeaders"))
        {
            app.UseForwardedHeaders();
        }

        app.UseCorrelationId();
        app.UseExceptionHandler(e => e.Run(ProblemWriter.WriteAsync));
        app.Use(async (http, next) =>
        {
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers.XFrameOptions = "DENY";
            await next(http);
        });
        app.UseTeamBeatsSessions();
        app.UseNetworkRules();
        app.UseStepUp();
        app.UseRateLimiter();

        app.MapHealthChecks("/health/live");
        app.MapOpenApi("/openapi/{documentName}.json");

        app.MapTenancyEndpoints();
        app.MapIdentityEndpoints();
        app.MapAuthorizationEndpoints();
        app.MapEntitlementsEndpoints();
        app.MapAuditEndpoints();
        app.MapAdministrationEndpoints();
        app.MapIntegrationsEndpoints();
        app.MapExperienceEndpoints();
        app.MapNotificationsEndpoints();
        app.MapHrEndpoints();
        app.MapServicesEndpoints();
        app.MapContentEndpoints();
        app.MapDocumentsEndpoints();
        app.MapPoliciesEndpoints();
        app.MapAiGatewayEndpoints();
        app.MapSearchEndpoints();
        app.MapAgentsEndpoints();
        app.MapEvaluationEndpoints();
        app.MapAnalyticsEndpoints();
        app.MapPeopleEndpoints();
        app.MapSecurityEndpoints();
        extraEndpoints?.Invoke(app);
        return app;
    }
}

/// <summary>
/// Problem Details (RFC 9457) with stable <c>type</c> URIs per code, localized titles from resource catalogues,
/// and validation errors as resource keys plus localized messages. Unexpected errors never leak details.
/// </summary>
public static class ProblemWriter
{
    public static async Task WriteAsync(HttpContext http)
    {
        var error = http.Features.Get<IExceptionHandlerFeature>()?.Error;
        var problem = error switch
        {
            AppException app => app,
            BadHttpRequestException bad => new AppException(ProblemCodes.ValidationFailed, bad.StatusCode == 413 ? 413 : 400, "validation.malformed_request"),
            JsonException => new AppException(ProblemCodes.ValidationFailed, 400, "validation.malformed_request"),
            _ => null,
        };

        if (problem is null)
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TeamBeats.Api").LogError(error, "Unhandled error");
            problem = new AppException("internal", 500);
        }

        var localizer = http.RequestServices.GetRequiredService<ILocalizer>();
        var locale = http.RequestServices.GetService<RequestLocale>()?.Locale ?? Locales.Fallback;
        var body = new Dictionary<string, object?>
        {
            ["type"] = ProblemCodes.TypeUri(problem.Code),
            ["title"] = localizer.Get("problem." + problem.Code, locale),
            ["status"] = problem.Status,
            ["code"] = problem.Code,
            ["correlationId"] = Correlation.Id,
        };
        if (problem.DetailKey is not null)
        {
            body["detailKey"] = problem.DetailKey;
            body["detail"] = localizer.Get(problem.DetailKey, locale);
        }
        if (problem.Errors is not null)
        {
            body["errors"] = problem.Errors.ToDictionary(e => e.Key,
                e => e.Value.Select(k => new { key = k, message = localizer.Get(k, locale) }).ToArray());
        }
        if (problem.Extensions is not null)
        {
            foreach (var (k, v) in problem.Extensions)
            {
                body[k] = v;
            }
        }

        http.Response.StatusCode = problem.Status;
        http.Response.Headers.ContentLanguage = locale;
        await http.Response.WriteAsJsonAsync(body, (JsonSerializerOptions?)null, "application/problem+json");
    }
}
