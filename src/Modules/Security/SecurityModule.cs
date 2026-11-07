using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
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
        });

        security.MapGet("/policy/preview", async (string? ipAllow, string? ipBlock, PolicyService policies, CancellationToken ct) =>
            Results.Ok(await policies.PreviewAsync(ipAllow, ipBlock, ct)));

        return app;
    }
}
