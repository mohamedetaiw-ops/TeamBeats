using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using TeamBeats.Audit.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The passthrough endpoints of the authorization server: the authorization endpoint signs the employee in through the
/// tenant's own sign-in (spec 003, FR-003) and asks for consent, the token endpoint checks the token family on every code
/// and refresh, and revocation (RFC 7009) ends a family.
/// </summary>
public static class OAuthEndpoints
{
    /// <summary>The web page where an employee consents to an app (FR-013).</summary>
    public const string ConsentPage = "/app-consent";

    /// <summary>The RFC 8414 metadata location of the issuer https://host/oauth.</summary>
    public const string MetadataPath = "/.well-known/oauth-authorization-server/oauth";

    /// <summary>Serves the RFC 8414 location from the issuer-relative metadata endpoint, then runs the authorization server.</summary>
    public static IApplicationBuilder UseOAuthServer(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (http.Request.Path.Equals(MetadataPath, StringComparison.Ordinal))
            {
                http.Request.Path = "/oauth/.well-known/oauth-authorization-server";
            }
            if (HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Equals("/oauth/token", StringComparison.Ordinal)
                && await TokenRequestsOverLimitAsync(http))
            {
                return;
            }
            await next(http);
        }).UseAuthentication();

    /// <summary>
    /// Token requests per app (section 6): 60 a minute. The client is named by <c>client_id</c> in the form or in Basic
    /// authentication; an over-limit request is answered 429 with Retry-After before any credential is checked.
    /// </summary>
    private static async Task<bool> TokenRequestsOverLimitAsync(HttpContext http)
    {
        if (http.RequestServices.GetRequiredService<ITenantContext>().Host.Tenant is not { } tenant)
        {
            return false;
        }
        string? clientId = null;
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                clientId = Uri.UnescapeDataString(pair.Split(':', 2)[0]);
            }
            catch (FormatException)
            {
                clientId = null;
            }
        }
        if (clientId is null && http.Request.HasFormContentType)
        {
            clientId = (await http.Request.ReadFormAsync(http.RequestAborted))["client_id"].ToString();
        }
        if (string.IsNullOrEmpty(clientId) || clientId.Length > VerifiedMcpClients.MaxClientIdLength)
        {
            return false;
        }
        var share = http.RequestServices.GetRequiredService<PublicApiLimits>().Share(ApiRules.TokenRequestsPerMinute);
        var result = http.RequestServices.GetRequiredService<PublicRateLimits>().Take(PublicRateLimits.Token(tenant.Id, clientId), ApiRules.TokenRequestsPerMinute, share);
        if (result.Allowed)
        {
            return false;
        }
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.Headers.RetryAfter = result.ResetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers.CacheControl = "no-store";
        await http.Response.WriteAsJsonAsync(new Dictionary<string, string>
        {
            ["error"] = "temporarily_unavailable", ["error_description"] = "Too many token requests. Try again later.",
        }, http.RequestAborted);
        return true;
    }

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var oauth = app.MapGroup("/oauth").WithTags("OAuth").ExcludeFromDescription();
        oauth.MapGet("/authorize", AuthorizeAsync);
        oauth.MapPost("/token", TokenAsync);
        oauth.MapPost("/revoke", RevokeAsync);
        oauth.MapPost("/register", RegisterAsync);
        return app;
    }

    private static async Task<IResult> AuthorizeAsync(HttpContext http, ITenantContext context, Apps apps, Consents consents, TokenFamilies families,
        IDbSession session, CancellationToken ct)
    {
        if (http.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }
        var app = (await apps.ResolveAsync(request.ClientId!, ct))!;
        if (context.ActorType != ActorType.User || context.ActorId is not { } user)
        {
            // The tenant's own sign-in, then back to this same request (FR-003): the authorization server holds no passwords.
            return Results.Redirect("/signin?returnTo=" + Uri.EscapeDataString(http.Request.Path + http.Request.QueryString));
        }
        if (http.Request.Query.ContainsKey("tb_deny"))
        {
            return Refuse(Errors.AccessDenied, "The person did not authorize the app.");
        }
        if (!app.AllowGuests && await consents.IsGuestAsync(user, ct))
        {
            return Refuse(Errors.AccessDenied, "Guests cannot authorize this app.");
        }
        // An MCP client must be allowed by the tenant's external AI client policy (spec 035 US3 scenario 2); the refusal is audited.
        if (app.Kind == AppKinds.McpClient
            && await http.RequestServices.GetRequiredService<IExternalToolGateway>().RefusalAsync(app.ClientId, ct) is { } refusal)
        {
            await http.RequestServices.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(AuditActions.AgentExternalCallRefused, "api_app",
                app.Id.ToString(), AuditOutcome.Denied, Changes: new { stage = "authorize", reason = refusal }), ct);
            await session.CommitAsync(ct);
            return Refuse(Errors.AccessDenied, "The organization's external AI client policy does not allow this client.");
        }
        // The first employee to connect an allowed verified client registers it for the tenant (US3 scenario 1).
        var registered = !app.Provisioned;
        if (registered)
        {
            if (await apps.ProvisionVerifiedAsync(app, user, ct) is not { } provisioned)
            {
                return Refuse(Errors.AccessDenied, "The organization has reached its app limit.");
            }
            app = provisioned;
        }
        var scopes = Consents.Requested(app, request.Scope);
        if ((await consents.MissingAsync(app.Id, user, scopes, ct)).Count > 0)
        {
            if (registered)
            {
                await session.CommitAsync(ct);
            }
            return Results.Redirect(ConsentPage + http.Request.QueryString);
        }

        var resource = OAuthServer.ResourceFor(app, http.Request);
        var family = await families.CreateAsync(app.Id, user, scopes, resource, ApiRules.CodeLifetime + TimeSpan.FromMinutes(1), ct);
        await consents.TouchAsync(app.Id, user, ct);
        await session.CommitAsync(ct);
        var principal = Principal(user.ToString(), context.RequireTenant(), app.Id, family, OAuthServer.Delegated, scopes, resource);
        principal.SetAuthorizationCodeLifetime(ApiRules.CodeLifetime);
        return Results.SignIn(principal, null, OAuthServer.Scheme);
    }

    private static async Task<IResult> TokenAsync(HttpContext http, ITenantContext context, Apps apps, Consents consents, TokenFamilies families,
        ApiSettings settings, ITenantMemberships memberships, IDbSession session, CancellationToken ct)
    {
        if (http.GetOpenIddictServerRequest() is not { } request)
        {
            return Results.NotFound();
        }
        var tenant = context.RequireTenant();
        var app = (await apps.FindByClientIdAsync(request.ClientId!, ct))!;
        var current = await settings.CurrentAsync(ct);
        var accessLifetime = TimeSpan.FromMinutes(current.AccessTokenMinutes);
        var refreshLifetime = TimeSpan.FromDays(current.RefreshTokenDays);
        await apps.TouchAsync(app.Id, ct);

        if (request.IsClientCredentialsGrantType())
        {
            // An application app acts as itself (US1); its credential is its token family, so revoking the credential
            // refuses its tokens. Application tokens are only for the REST API (FR-019).
            var credential = (Guid)http.Features.Get<OpenIddictServerAspNetCoreFeature>()!.Transaction!.Properties[OAuthServer.CredentialProperty]!;
            var scopes = request.GetScopes() is { Length: > 0 } asked ? asked.ToList() : app.Scopes.ToList();
            await session.CommitAsync(ct);
            var principal = Principal(app.Id.ToString(), tenant, app.Id, credential, OAuthServer.Application, scopes, ApiRules.ApiResource(OAuthServer.Origin(http.Request)));
            principal.SetAccessTokenLifetime(accessLifetime);
            return Results.SignIn(principal, null, OAuthServer.Scheme);
        }

        var result = await http.AuthenticateAsync(OAuthServer.Scheme);
        var presented = result.Principal;
        if (presented?.FindFirst(OAuthServer.TenantClaim)?.Value != tenant.ToString() || presented.FindFirst(OAuthServer.AppClaim)?.Value != app.Id.ToString()
            || !Guid.TryParse(presented.FindFirst(OAuthServer.FamilyClaim)?.Value, out var familyId) || !Guid.TryParse(presented.GetClaim(Claims.Subject), out var user))
        {
            return Refuse(Errors.InvalidGrant, "The grant is not valid.");
        }
        var granted = presented.GetScopes().ToList();
        var resource = presented.GetResources().FirstOrDefault() ?? OAuthServer.ResourceFor(app, http.Request);

        // A membership that ended or a consent that no longer covers the scopes refuses at once (FR-007).
        if (await memberships.GetAsync(user, ct) is not { Status: MembershipStatus.Active })
        {
            await families.RevokeAsync(familyId, "membership_ended", ct);
            await session.CommitAsync(ct);
            return Refuse(Errors.InvalidGrant, "The person can no longer use this app.");
        }
        if (!await consents.CoversAsync(app.Id, user, granted, ct))
        {
            await families.RevokeAsync(familyId, "consent_revoked", ct);
            await session.CommitAsync(ct);
            return Refuse(Errors.InvalidGrant, "The consent for this app has ended.");
        }
        // A refresh may ask for fewer scopes, never more.
        var scopesNow = request.GetScopes() is { Length: > 0 } narrower ? narrower.ToList() : granted;
        if (scopesNow.Any(s => !granted.Contains(s, StringComparer.Ordinal)))
        {
            return Refuse(Errors.InvalidScope, "A refresh cannot add scopes.");
        }

        var generation = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(24));
        var outcome = request.IsAuthorizationCodeGrantType()
            ? await families.RedeemCodeAsync(familyId, generation, refreshLifetime, ct)
            : await families.RotateAsync(familyId, presented.FindFirst(OAuthServer.GenerationClaim)?.Value ?? "", generation, refreshLifetime, ct);
        await consents.TouchAsync(app.Id, user, ct);
        await session.CommitAsync(ct);
        if (outcome != Redemption.Accepted)
        {
            return Refuse(Errors.InvalidGrant, outcome == Redemption.Reused ? "The grant was already used; the sign-in is revoked." : "The grant is no longer valid.");
        }
        var next = Principal(user.ToString(), tenant, app.Id, familyId, OAuthServer.Delegated, scopesNow, resource);
        next.SetClaim(OAuthServer.GenerationClaim, generation);
        next.SetAccessTokenLifetime(accessLifetime);
        next.SetRefreshTokenLifetime(refreshLifetime);
        return Results.SignIn(next, null, OAuthServer.Scheme);
    }

    /// <summary>
    /// Token revocation (RFC 7009): the client presents one of its refresh or access tokens and its whole family ends.
    /// The answer is always 200 for an authenticated client, so it tells nothing about the token.
    /// </summary>
    private static async Task<IResult> RevokeAsync(HttpContext http, ITenantContext context, Apps apps, TokenFamilies families, TenantKeys keys,
        IDbSession session, CancellationToken ct)
    {
        var tenant = context.Host.Tenant;
        if (tenant is null || !keys.Available || !http.Request.HasFormContentType)
        {
            return Results.NotFound();
        }
        (context as TenantContextAccessor)?.SetTenant(tenant.Id);
        var form = await http.Request.ReadFormAsync(ct);
        var (clientId, secret) = ClientCredentials(http.Request, form);
        var app = string.IsNullOrEmpty(clientId) ? null : await apps.FindByClientIdAsync(clientId, ct);
        var authenticated = app is { Active: true }
            && (AppKinds.IsConfidential(app.Kind) ? await apps.VerifySecretAsync(app.Id, secret, ct) is not null : string.IsNullOrEmpty(secret));
        if (!authenticated)
        {
            return Results.Json(new { error = Errors.InvalidClient }, statusCode: 401);
        }
        var token = form["token"].ToString();
        var issuer = OAuthServer.Origin(http.Request) + "/oauth";
        if (await BearerTokens.ReadAsync(token, issuer, keys, tenant.Id) is { } claims
            && claims.TryGetValue(OAuthServer.AppClaim, out var appClaim) && appClaim?.ToString() == app!.Id.ToString()
            && claims.TryGetValue(OAuthServer.KindClaim, out var kind) && kind?.ToString() == OAuthServer.Delegated
            && claims.TryGetValue(OAuthServer.FamilyClaim, out var family) && Guid.TryParse(family?.ToString(), out var familyId))
        {
            await families.RevokeAsync(familyId, "user", ct);
            await session.CommitAsync(ct);
        }
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok();
    }

    private static (string? ClientId, string? Secret) ClientCredentials(HttpRequest request, IFormCollection form)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon > 0)
                {
                    return (Uri.UnescapeDataString(decoded[..colon]), Uri.UnescapeDataString(decoded[(colon + 1)..]));
                }
            }
            catch (FormatException)
            {
                return (null, null);
            }
        }
        return (form["client_id"].ToString(), form["client_secret"].ToString());
    }

    private static ClaimsPrincipal Principal(string subject, Guid tenant, Guid app, Guid family, string kind, IReadOnlyList<string> scopes, string resource)
    {
        var identity = new ClaimsIdentity(OAuthServer.Scheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, subject);
        identity.SetClaim(OAuthServer.TenantClaim, tenant.ToString());
        identity.SetClaim(OAuthServer.AppClaim, app.ToString());
        identity.SetClaim(OAuthServer.FamilyClaim, family.ToString());
        identity.SetClaim(OAuthServer.KindClaim, kind);
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(scopes);
        principal.SetResources(resource);
        // The access token carries the tenant, app, principal, scopes and family (FR-006); the refresh generation stays
        // inside the encrypted refresh token.
        principal.SetDestinations(claim => claim.Type == OAuthServer.GenerationClaim ? [] : [Destinations.AccessToken]);
        return principal;
    }

    /// <summary>
    /// RFC 7591 registration for MCP clients where the tenant turned it on (FR-023); elsewhere the endpoint does not exist.
    /// The client is held for an API administrator's approval, and registrations are limited per tenant.
    /// </summary>
    private static async Task<IResult> RegisterAsync(HttpContext http, TenantContextAccessor tenant, ApiSettings settings, ClientRegistrations registrations,
        PublicRateLimits limits, IDbSession session, CancellationToken ct)
    {
        if (tenant.Host.Tenant is not { } host)
        {
            return Results.NotFound();
        }
        tenant.SetTenant(host.Id);
        if (!await OAuthServer.OpenAsync(http.RequestServices, ct) || !(await settings.CurrentAsync(ct)).DynamicRegistration)
        {
            return Results.NotFound();
        }
        http.Response.Headers.CacheControl = "no-store";
        var limit = limits.Take("register:" + host.Id, ClientRegistrations.PerMinute);
        if (!limit.Allowed)
        {
            http.Response.Headers.RetryAfter = limit.ResetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(new ClientRegistrationError("temporarily_unavailable", "Too many registrations. Try again later."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }
        ClientRegistrationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ClientRegistrationRequest>(ct);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            request = null;
        }
        if (request is null)
        {
            return Results.Json(new ClientRegistrationError("invalid_client_metadata", "The body must be a JSON client metadata document."), statusCode: 400);
        }
        var (registered, refused) = await registrations.RegisterAsync(request, ct);
        if (registered is null)
        {
            return Results.Json(refused, statusCode: 400);
        }
        await session.CommitAsync(ct);
        return Results.Json(registered, statusCode: StatusCodes.Status201Created);
    }

    private static IResult Refuse(string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OAuthServer.Scheme]);
}
