using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Sso;

public static class SsoEndpoints
{
    public const string BindingCookie = "tb_sso";
    public const string StartPath = "/api/v1/auth/signin/sso";
    public const string CallbackPath = "/api/v1/auth/signin/sso/callback";

    public static IEndpointRouteBuilder MapSsoEndpoints(this IEndpointRouteBuilder app)
    {
        var signin = app.MapGroup("/api/v1/auth/signin").WithTags("Auth");

        // What the sign-in page offers on this tenant host. Reveals only whether single sign-on is set up.
        signin.MapGet("/options", async (ITenantContext context, TenantContextAccessor accessor, IdentityProviderStore providers,
            IEntitlementService entitlements, CancellationToken ct) =>
        {
            var tenant = RequireTenantHost(context);
            accessor.SetTenant(tenant.Id);
            var config = tenant.Status == TenantLifecycleStatus.Active ? await providers.GetEnabledAsync(ct) : null;
            var sso = config is not null && await entitlements.IsEnabledAsync(
                config.Protocol == SamlService.Protocol ? Capabilities.IdentitySaml : Capabilities.IdentityOidc, ct);
            return Results.Ok(new SignInOptions(sso, sso ? config!.Kind : null, sso ? config!.DisplayName : null));
        });

        signin.MapGet("/sso", async (string? returnTo, bool? stepUp, HttpRequest request, HttpResponse response, ITenantContext context,
            SsoService sso, IDbSession session, CancellationToken ct) =>
        {
            var tenant = RequireTenantHost(context);
            var step = await sso.StartAsync(tenant, CallbackUri(request), returnTo, ct, stepUp == true);
            await session.CommitAsync(ct);
            if (step.BindingCookie is { } binding)
            {
                // The SAML IdP answers with a cross-site POST, which carries only a SameSite=None cookie; it is scoped to
                // the consumer path and bound to a single-use request, so it cannot start or finish a sign-in on its own.
                response.Cookies.Append(step.Saml ? SamlEndpoints.BindingCookie : BindingCookie, binding, new CookieOptions
                {
                    HttpOnly = true, Secure = true, SameSite = step.Saml ? SameSiteMode.None : SameSiteMode.Lax,
                    Path = step.Saml ? SamlEndpoints.AcsPath : StartPath, MaxAge = SsoService.StateLifetime,
                });
            }
            return Results.Redirect(step.Location);
        });

        signin.MapGet("/sso/callback", async (string? code, string? state, string? error, HttpRequest request, HttpResponse response,
            ITenantContext context, SsoService sso, SessionService sessions, IDbSession session, CancellationToken ct) =>
        {
            var tenant = RequireTenantHost(context);
            var step = await sso.CallbackAsync(tenant, CallbackUri(request), code, state, error, request.Cookies[BindingCookie], ct);
            await session.CommitAsync(ct);
            response.Cookies.Delete(BindingCookie, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, Path = StartPath });
            if (step.SessionToken is { } token)
            {
                sessions.SetCookie(response, token);
            }
            return Results.Redirect(step.Location);
        });

        var admin = app.MapGroup("/api/v1/tenant/identity-provider").RequireTenantUser().InAdminArea().WithTags("Tenant: identity");

        admin.MapGet("/", async (IAuthorizationService authz, IdentityProviderStore providers, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IdentityConfigManage, AuthzTarget.Tenant, ct);
            var config = await providers.GetCurrentAsync(ct);
            return config is null ? Results.NoContent() : Results.Ok(config);
        });

        admin.MapPut("/", async (IdentityProviderRequest request, IAuthorizationService authz, IEntitlementService entitlements,
            IdentityProviderStore providers, IFourEyes fourEyes, IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.IdentityConfigManage, AuthzTarget.Tenant, ct);
            await entitlements.DemandAsync(request.Protocol == SamlService.Protocol ? Capabilities.IdentitySaml : Capabilities.IdentityOidc, ct);
            var (saved, held) = await providers.SaveOrHoldAsync(request, fourEyes, ct);
            await session.CommitAsync(ct);
            return held is not null ? Results.Accepted(null, new { pendingChange = held }) : Results.Ok(saved);
        }).RequireStepUp();

        return app;
    }

    private static TenantInfo RequireTenantHost(ITenantContext context) =>
        context.Host.Kind == HostScopeKind.Tenant && context.Host.Tenant is { } tenant ? tenant : throw AppException.NotFound();

    // The callback is on the tenant's own host, so the session cookie is set for that host only.
    private static string CallbackUri(HttpRequest request) => $"https://{request.Host.Value}{CallbackPath}";
}

public sealed record SignInOptions(bool Sso, string? Kind, string? DisplayName);

/// <summary>Applies an approved change of the sign-in configuration (spec 026 FR-016) through the direct path, as the approver.</summary>
public sealed class IdentityProviderApplier(IdentityProviderStore providers, IAuthorizationService authz) : IFourEyesApplier
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    public string Kind => FourEyesKinds.IdentityProvider;

    public string Permission => Permissions.IdentityConfigManage;

    public async Task ApplyAsync(System.Text.Json.JsonElement request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.IdentityConfigManage, AuthzTarget.Tenant, ct);
        await providers.SaveAsync(System.Text.Json.JsonSerializer.Deserialize<IdentityProviderRequest>(request, Json)!, ct);
    }
}
