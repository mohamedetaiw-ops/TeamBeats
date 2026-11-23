using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy.Domains;

/// <summary>What a page on this host shows before and after sign-in: the custom domain's texts, or nothing on the tenant's own subdomain.</summary>
public sealed record SitePresentation(string? AppTitle, string? Description, string? SignInMessage, bool CustomDomain);

/// <summary>The presentation of a verified domain for other modules (the web app manifest and the shell, spec 033 R2 T021).</summary>
public sealed class CustomDomainPresentation(CustomDomains domains, ITenantSettingsReader settings) : ICustomDomainPresentation
{
    public async Task<DomainPresentation?> ForHostAsync(string host, string locale, CancellationToken ct = default) =>
        await domains.PresentationAsync(host, locale, (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale, ct);
}

public static class DomainEndpoints
{
    public static void MapDomainEndpoints(this IEndpointRouteBuilder app)
    {
        var x = app.MapGroup("/api/v1/tenant/domains").RequireTenantUser().InAdminArea().WithTags("Tenant: custom domains");
        x.MapGet("", async (CustomDomains domains, CancellationToken ct) => Results.Ok(await domains.ListAsync(ct)));
        x.MapPost("", async (AddDomainRequest request, CustomDomains domains, IDbSession session, CancellationToken ct) =>
        {
            var view = await domains.AddAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/tenant/domains/{view.Id}", view);
        });
        x.MapPost("/{id:guid}/verify", async (Guid id, CustomDomains domains, IDbSession session, CancellationToken ct) =>
        {
            var view = await domains.VerifyAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        x.MapPut("/{id:guid}/presentation", async (Guid id, DomainPresentationRequest request, CustomDomains domains, IDbSession session, CancellationToken ct) =>
        {
            var view = await domains.SetPresentationAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(view);
        });
        x.MapDelete("/{id:guid}", async (Guid id, CustomDomains domains, IDbSession session, CancellationToken ct) =>
        {
            await domains.RemoveAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        // Before sign-in too: the page title, description and sign-in message of this host. Nothing on a tenant subdomain.
        app.MapGet("/api/v1/site/presentation", async (ITenantContext context, TenantContextAccessor accessor, CustomDomains domains,
            ITenantSettingsReader settings, RequestLocale locale, CancellationToken ct) =>
        {
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { } tenant)
            {
                throw AppException.NotFound();
            }
            if (context.Host.CustomDomain is not { } host)
            {
                return Results.Ok(new SitePresentation(null, null, null, false));
            }
            accessor.SetTenant(tenant.Id);
            var shown = await domains.PresentationAsync(host, locale.Locale, (await settings.GetLocaleSettingsAsync(ct)).DefaultLocale, ct);
            return Results.Ok(new SitePresentation(shown?.AppTitle, shown?.Description, shown?.SignInMessage, true));
        }).WithTags("Site");

        // The installation's edge asks this before it issues a certificate for a host on demand: yes only for a verified host.
        app.MapGet("/api/v1/domains/tls-allowed", async (string? domain, ICustomDomainResolver resolver, CancellationToken ct) =>
            domain is not null && CustomDomains.Normalize(domain) is { } host && await resolver.TenantForHostAsync(host, ct) is not null
                ? Results.Ok()
                : Results.NotFound()).WithTags("Site");
    }
}
