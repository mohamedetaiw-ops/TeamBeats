using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.TenantContext;
using static OpenIddict.Abstractions.OpenIddictConstants;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The per-tenant OAuth 2.1 authorization server (spec 035 FR-001 to FR-006, ADR-0018) on OpenIddict. One server serves
/// every tenant: each request's issuer is <c>https://&lt;tenant host&gt;/oauth</c>, tokens are signed and encrypted with
/// that tenant's keys, and apps, consents and token families are read under the tenant from the host. OpenIddict runs
/// without its own stores (degraded mode); the handlers below and the passthrough endpoints check clients, redirect URIs,
/// secrets, consents and token families against the module's tables. PKCE with S256 is required for every code flow;
/// the implicit and password grants are not offered.
/// </summary>
public static class OAuthServer
{
    public const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    // Private claims carried in tokens. tb_gen is kept only inside the encrypted refresh token.
    public const string TenantClaim = "tb_tenant";
    public const string AppClaim = "tb_app";
    public const string FamilyClaim = "tb_family";
    public const string KindClaim = "tb_kind";
    public const string GenerationClaim = "tb_gen";
    public const string Delegated = "delegated";
    public const string Application = "application";

    /// <summary>The credential a confidential client authenticated with, kept for the passthrough token endpoint.</summary>
    internal const string CredentialProperty = ".tb_credential";

    public static IServiceCollection AddOAuthServer(this IServiceCollection services)
    {
        services.AddAuthentication();
        services.AddOpenIddict().AddServer(o =>
        {
            o.EnableDegradedMode();
            o.DisableTokenStorage();
            o.DisableAuthorizationStorage();
            // Endpoints are relative to the tenant's issuer, which the first handler sets from the host.
            o.SetAuthorizationEndpointUris("authorize").SetTokenEndpointUris("token")
                .SetConfigurationEndpointUris(".well-known/oauth-authorization-server").SetJsonWebKeySetEndpointUris("jwks");
            o.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().AllowClientCredentialsFlow().RequireProofKeyForCodeExchange();
            o.RegisterScopes([.. ScopeCatalogue.Keys]);
            // Resource indicators name the tenant's own API or MCP server; the validation handlers check them per host.
            o.DisableResourceValidation();
            o.DisableAccessTokenEncryption();
            o.SetAccessTokenLifetime(TimeSpan.FromMinutes(ApiRules.DefaultAccessTokenMinutes));
            o.SetRefreshTokenLifetime(TimeSpan.FromDays(ApiRules.DefaultRefreshTokenDays));
            o.SetAuthorizationCodeLifetime(ApiRules.CodeLifetime);
            // Every token is signed and encrypted with the tenant's keys (TenantKeys); these process keys only satisfy
            // OpenIddict's start-up check and are replaced on every token by the handlers below.
            o.AddSigningKey(new ECDsaSecurityKey(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256)));
            o.AddEncryptionKey(new SymmetricSecurityKey(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            o.Configure(options =>
            {
                options.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain);
                options.Scopes.Remove(Scopes.OpenId);
                options.ResponseModes.Remove(ResponseModes.Fragment);
            });
            o.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();

            o.AddEventHandler<ProcessRequestContext>(b => b.UseInlineHandler(TenantIssuer)
                .SetOrder(OpenIddictServerHandlers.InferEndpointType.Descriptor.Order - 1));
            o.AddEventHandler<ProcessRequestContext>(b => b.UseInlineHandler(TenantContextFromHost)
                .SetOrder(OpenIddictServerHandlers.InferEndpointType.Descriptor.Order + 1));
            o.AddEventHandler<ValidateAuthorizationRequestContext>(b => b.UseScopedHandler<AuthorizationRequestCheck>());
            // The client is authenticated before its code or refresh token is read.
            o.AddEventHandler<ValidateTokenRequestContext>(b => b.UseScopedHandler<TokenRequestCheck>()
                .SetOrder(OpenIddictServerHandlers.Exchange.ValidateAuthentication.Descriptor.Order - 500));
            o.AddEventHandler<GenerateTokenContext>(b => b.UseInlineHandler(TenantCredentials)
                .SetOrder(OpenIddictServerHandlers.Protection.AttachSecurityCredentials.Descriptor.Order + 1));
            o.AddEventHandler<ValidateTokenContext>(b => b.UseInlineHandler(TenantValidationKeys)
                .SetOrder(OpenIddictServerHandlers.Protection.ResolveTokenValidationParameters.Descriptor.Order + 1));
            // Client assertions are checked by TokenRequestCheck against the app's registered keys, not by OpenIddict.
            o.AddEventHandler<ProcessAuthenticationContext>(b => b.UseInlineHandler(OwnClientAssertions)
                .SetOrder(OpenIddictServerHandlers.EvaluateValidatedTokens.Descriptor.Order + 1));
            o.AddEventHandler<HandleJsonWebKeySetRequestContext>(b => b.UseInlineHandler(TenantJwks).SetOrder(int.MaxValue));
            o.AddEventHandler<HandleConfigurationRequestContext>(b => b.UseInlineHandler(RegistrationEndpoint).SetOrder(int.MaxValue));
            o.AddEventHandler<ProcessSignInContext>(b => b.UseInlineHandler(RefreshTokensForDelegatedApps)
                .SetOrder(OpenIddictServerHandlers.EvaluateGeneratedTokens.Descriptor.Order + 1));
        });
        // Local hosts run on plain HTTP; deployed ones sit behind TLS and forward the scheme.
        services.AddOptions<OpenIddictServerAspNetCoreOptions>().Configure<IHostEnvironment>((o, env) =>
            o.DisableTransportSecurityRequirement = env.IsDevelopment() || env.IsEnvironment("Test"));
        return services;
    }

    private static HttpContext Http(BaseContext context) =>
        context.Transaction.GetHttpRequest()?.HttpContext ?? throw new InvalidOperationException("The OAuth server runs only inside an HTTP request.");

    /// <summary>The issuer of a tenant host is its origin plus /oauth (FR-001); other hosts have no authorization server.</summary>
    private static ValueTask TenantIssuer(ProcessRequestContext context)
    {
        var tenant = Http(context).RequestServices.GetRequiredService<ITenantContext>().Host;
        if (tenant is not { Kind: HostScopeKind.Tenant } || context.BaseUri is null)
        {
            context.SkipRequest();
            return default;
        }
        context.BaseUri = new Uri(context.BaseUri, "oauth");
        return default;
    }

    /// <summary>The metadata names the registration endpoint only where the tenant turned dynamic registration on (FR-023).</summary>
    private static async ValueTask RegistrationEndpoint(HandleConfigurationRequestContext context)
    {
        var http = Http(context);
        if ((await http.RequestServices.GetRequiredService<ApiSettings>().CurrentAsync(http.RequestAborted)).DynamicRegistration)
        {
            context.Metadata["registration_endpoint"] = Origin(http.Request) + "/oauth/register";
        }
    }

    /// <summary>An OAuth request reads apps and families under the tenant of the host, signed in or not.</summary>
    private static ValueTask TenantContextFromHost(ProcessRequestContext context)
    {
        if (context.EndpointType == OpenIddictServerEndpointType.Unknown)
        {
            return default;
        }
        var http = Http(context);
        var tenant = http.RequestServices.GetRequiredService<TenantContextAccessor>();
        if (!http.RequestServices.GetRequiredService<TenantKeys>().Available || tenant.Host.Tenant is not { } host)
        {
            context.Reject(Errors.ServerError, "The authorization server is not available.");
            return default;
        }
        if (tenant.TenantId is { } signedIn && signedIn != host.Id)
        {
            tenant.SetActor(ActorType.Anonymous, null);
        }
        tenant.SetTenant(host.Id);
        return default;
    }

    private static Guid HostTenant(BaseContext context) =>
        Http(context).RequestServices.GetRequiredService<ITenantContext>().TenantId ?? throw new InvalidOperationException("No tenant for the OAuth request.");

    private static ValueTask TenantCredentials(GenerateTokenContext context)
    {
        var keys = Http(context).RequestServices.GetRequiredService<TenantKeys>();
        var tenant = HostTenant(context);
        context.SigningCredentials = new SigningCredentials(keys.SigningKey(tenant), SecurityAlgorithms.EcdsaSha256);
        if (context.EncryptionCredentials is not null)
        {
            context.EncryptionCredentials = new EncryptingCredentials(keys.EncryptionKey(tenant), SecurityAlgorithms.Aes256KW,
                SecurityAlgorithms.Aes256CbcHmacSha512);
        }
        return default;
    }

    private static ValueTask TenantValidationKeys(ValidateTokenContext context)
    {
        var keys = Http(context).RequestServices.GetRequiredService<TenantKeys>();
        var tenant = HostTenant(context);
        context.TokenValidationParameters.IssuerSigningKeys = keys.ValidationKeys(tenant);
        context.TokenValidationParameters.TokenDecryptionKeys = keys.DecryptionKeys(tenant);
        return default;
    }

    private static ValueTask OwnClientAssertions(ProcessAuthenticationContext context)
    {
        context.ExtractClientAssertion = false;
        context.RequireClientAssertion = false;
        context.ValidateClientAssertion = false;
        return default;
    }

    private static ValueTask TenantJwks(HandleJsonWebKeySetRequestContext context)
    {
        var keys = Http(context).RequestServices.GetRequiredService<TenantKeys>();
        context.Keys.Clear();
        foreach (var key in keys.ValidationKeys(HostTenant(context)))
        {
            context.Keys.Add(TenantKeys.PublicJwk(key));
        }
        return default;
    }

    /// <summary>
    /// Delegated apps always get a refresh token, rotated on every use (FR-006), without asking for offline_access.
    /// Application apps get only access tokens and ask again with their credential.
    /// </summary>
    private static ValueTask RefreshTokensForDelegatedApps(ProcessSignInContext context)
    {
        if (context.EndpointType == OpenIddictServerEndpointType.Token && context.Principal?.FindFirst(KindClaim)?.Value == Delegated)
        {
            context.GenerateRefreshToken = true;
            context.IncludeRefreshToken = true;
        }
        else if (context.Principal?.FindFirst(KindClaim)?.Value == Application)
        {
            context.GenerateRefreshToken = false;
            context.IncludeRefreshToken = false;
        }
        return default;
    }

    /// <summary>The origin of the request, as the issuer and resource indicators use it.</summary>
    public static string Origin(HttpRequest request) => $"{request.Scheme}://{request.Host}";

    /// <summary>The resource an app's tokens are bound to: the MCP server for MCP clients, the REST API otherwise.</summary>
    public static string ResourceFor(AppRecord app, HttpRequest request) =>
        app.Kind == AppKinds.McpClient ? ApiRules.McpResource(Origin(request)) : ApiRules.ApiResource(Origin(request));

    /// <summary>Whether the API is open on this tenant: the package has it and the tenant switch is on (FR-028).</summary>
    internal static async Task<bool> OpenAsync(IServiceProvider services, CancellationToken ct) =>
        await services.GetRequiredService<IEntitlementService>().IsEnabledAsync(Capabilities.ApiPublic, ct)
        && (await services.GetRequiredService<ApiSettings>().CurrentAsync(ct)).ApiEnabled;
}

/// <summary>Checks an authorization request against the app registration (FR-004): client, exact redirect URI, scopes and resource.</summary>
internal sealed class AuthorizationRequestCheck(Apps apps, IServiceProvider services) : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        var http = context.Transaction.GetHttpRequest()!.HttpContext;
        if (!await OAuthServer.OpenAsync(services, http.RequestAborted))
        {
            context.Reject(Errors.AccessDenied, "The API is not available for this organization.");
            return;
        }
        // A client on the platform's verified list is known before the tenant has registered it (FR-023).
        var app = string.IsNullOrEmpty(context.ClientId) ? null : await apps.ResolveAsync(context.ClientId, http.RequestAborted);
        if (app is not { Active: true } || !AppKinds.IsDelegated(app.Kind))
        {
            context.Reject(Errors.InvalidClient, "The client is not known.");
            return;
        }
        if (context.RedirectUri is not { } redirect || !ApiRules.RedirectRegistered(app.RedirectUris, redirect, app.Kind))
        {
            context.Reject(Errors.InvalidRequest, "The redirect_uri is not registered for this client.");
            return;
        }
        context.SetRedirectUri(redirect);
        if (context.Request.GetScopes().Any(s => !app.Scopes.Contains(s, StringComparer.Ordinal)))
        {
            context.Reject(Errors.InvalidScope, "The client is not registered for the requested scope.");
            return;
        }
        var resource = OAuthServer.ResourceFor(app, http.Request);
        if (context.Request.Resources is { Length: > 0 } resources && (resources.Length != 1 || resources[0] != resource))
        {
            context.Reject(Errors.InvalidTarget, "The resource is not served for this client.");
        }
    }
}

/// <summary>
/// Authenticates the client at the token endpoint (FR-004, FR-005): confidential apps with a live secret or a client
/// assertion signed with a registered public key (private key JWT, never both), public ones with neither; client
/// credentials only for application apps, codes and refresh tokens only for delegated ones.
/// </summary>
internal sealed class TokenRequestCheck(Apps apps, IServiceProvider services) : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        var http = context.Transaction.GetHttpRequest()!.HttpContext;
        var ct = http.RequestAborted;
        if (!await OAuthServer.OpenAsync(services, ct))
        {
            context.Reject(Errors.AccessDenied, "The API is not available for this organization.");
            return;
        }
        var app = string.IsNullOrEmpty(context.ClientId) ? null : await apps.FindByClientIdAsync(context.ClientId, ct);
        if (app is not { Active: true })
        {
            context.Reject(Errors.InvalidClient, "The client is not known.");
            return;
        }
        // An app limited to address ranges gets tokens only from them (FR-027).
        if (!ApiRules.AddressAllowed(app.IpRanges, http.Connection.RemoteIpAddress))
        {
            context.Reject(Errors.UnauthorizedClient, "The client may not be used from this network.");
            return;
        }
        var assertion = context.Request.ClientAssertion;
        var hasAssertion = !string.IsNullOrEmpty(assertion) || !string.IsNullOrEmpty(context.Request.ClientAssertionType);
        if (AppKinds.IsConfidential(app.Kind))
        {
            Guid? credential;
            if (hasAssertion)
            {
                credential = !string.IsNullOrEmpty(context.ClientSecret) || context.Request.ClientAssertionType != ClientKeys.AssertionType ? null
                    : await apps.VerifyAssertionAsync(app, assertion, OAuthServer.Origin(http.Request) + "/oauth", ct);
            }
            else
            {
                credential = await apps.VerifySecretAsync(app.Id, context.ClientSecret, ct);
            }
            if (credential is not { } live)
            {
                context.Reject(Errors.InvalidClient, "The client credentials are not valid.");
                return;
            }
            context.Transaction.Properties[OAuthServer.CredentialProperty] = live;
        }
        else if (!string.IsNullOrEmpty(context.ClientSecret) || hasAssertion)
        {
            context.Reject(Errors.InvalidClient, "A public client has no credential.");
            return;
        }

        var request = context.Request;
        var allowed = request.IsClientCredentialsGrantType() ? app.Kind == AppKinds.Application
            : (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType()) && AppKinds.IsDelegated(app.Kind);
        if (!allowed)
        {
            context.Reject(Errors.UnauthorizedClient, "The grant type is not allowed for this client.");
            return;
        }
        if (request.GetScopes().Any(s => !app.Scopes.Contains(s, StringComparer.Ordinal)))
        {
            context.Reject(Errors.InvalidScope, "The client is not registered for the requested scope.");
            return;
        }
        var resource = OAuthServer.ResourceFor(app, http.Request);
        if (request.Resources is { Length: > 0 } resources && (resources.Length != 1 || resources[0] != resource))
        {
            context.Reject(Errors.InvalidTarget, "The resource is not served for this client.");
        }
    }
}
