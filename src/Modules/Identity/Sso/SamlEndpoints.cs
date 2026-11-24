using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Sso;

/// <summary>
/// The SAML 2.0 service provider on each tenant host (spec 003 US4, US5): its metadata for the IdP, and the assertion
/// consumer the IdP posts back to. Sign-in starts at the single sign-on start address, whatever the protocol.
/// </summary>
public static class SamlEndpoints
{
    public const string BindingCookie = "tb_saml";
    public const string MetadataPath = "/api/v1/auth/signin/saml/metadata";
    public const string AcsPath = "/api/v1/auth/signin/saml/acs";

    public static IEndpointRouteBuilder MapSamlEndpoints(this IEndpointRouteBuilder app)
    {
        var saml = app.MapGroup("/api/v1/auth/signin/saml").WithTags("Auth");

        // Public, like any SP metadata: it names only this host's entity ID and consumer address.
        saml.MapGet("/metadata", (HttpRequest request, ITenantContext context) =>
        {
            RequireTenantHost(context);
            return Results.Text(SamlService.Metadata(request.Host.Value!), "application/samlmetadata+xml");
        });

        saml.MapPost("/acs", async (HttpRequest request, HttpResponse response, ITenantContext context, SamlService service,
            SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            var tenant = RequireTenantHost(context);
            if (!request.HasFormContentType)
            {
                throw AppException.Validation("SAMLResponse", "validation.saml.form_required");
            }
            var form = await request.ReadFormAsync(ct);
            var step = await service.AcsAsync(tenant, request.Host.Value!, form["SAMLResponse"].FirstOrDefault(), form["RelayState"].FirstOrDefault(),
                request.Cookies[BindingCookie], ct);
            await session.CommitAsync(ct);
            response.Cookies.Delete(BindingCookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.None, Path = AcsPath });
            if (step.SessionToken is { } token)
            {
                sessions.SetCookie(response, token);
            }
            // 303: the browser follows a POST with a GET of the return path.
            response.Headers.Location = step.Location;
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        }).DisableAntiforgery();

        return app;
    }

    private static TenantInfo RequireTenantHost(ITenantContext context) =>
        context.Host.Kind == HostScopeKind.Tenant && context.Host.Tenant is { } tenant ? tenant : throw AppException.NotFound();
}
