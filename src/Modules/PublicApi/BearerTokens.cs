using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TeamBeats.Audit.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>The app and principal behind a public API call, with the scopes its token holds.</summary>
public sealed record PublicCaller(Guid AppId, string Kind, Guid? UserId, IReadOnlySet<string> Scopes, Guid FamilyId)
{
    public bool IsDelegated => Kind == OAuthServer.Delegated;

    /// <summary>Scopes only narrow (FR-009): the operation still asks the authorization service afterwards.</summary>
    public void Demand(string scope)
    {
        if (!Scopes.Contains(scope))
        {
            throw new AppException(BearerTokens.InsufficientScope, 403, null, null, new Dictionary<string, object?> { ["scope"] = scope });
        }
    }
}

/// <summary>
/// Access token checks for the public API (spec 035 FR-002, FR-006, FR-007). The tenant comes from the host; the token
/// must be signed with that tenant's key, issued by that tenant's issuer and bound to this resource. Then the app, the
/// family or credential and the person's membership are checked against the database on every call, so revocation and
/// a disabled membership take effect at once. A cookie session never authenticates a public API call.
/// </summary>
public static class BearerTokens
{
    public const string TokenInvalid = ProblemCodes.ApiTokenInvalid;
    public const string InsufficientScope = ProblemCodes.ApiInsufficientScope;
    public const string PublicPrefix = "/api/public";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    public static IApplicationBuilder UsePublicApiTokens(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (!http.Request.Path.StartsWithSegments(PublicPrefix))
            {
                await next(http);
                return;
            }
            var context = http.RequestServices.GetRequiredService<TenantContextAccessor>();
            context.SetActor(ActorType.Anonymous, null);
            context.SetTenant(null);
            if (context.Host.Tenant is not { } tenant)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            context.SetTenant(tenant.Id);
            var refusal = await AuthenticateAsync(http, context, tenant.Id);
            if (refusal is not null)
            {
                await WriteRefusalAsync(http, refusal);
                return;
            }
            await next(http);
        });

    private static async Task<string?> AuthenticateAsync(HttpContext http, TenantContextAccessor context, Guid tenant)
    {
        var services = http.RequestServices;
        var ct = http.RequestAborted;
        if (!await OAuthServer.OpenAsync(services, ct))
        {
            return "api_closed";
        }
        var header = http.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length < 8)
        {
            return "missing";
        }
        var token = header[7..].Trim();
        var keys = services.GetRequiredService<TenantKeys>();
        var origin = OAuthServer.Origin(http.Request);
        var issuer = origin + "/oauth";
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = ApiRules.ApiResource(origin),
            IssuerSigningKeys = keys.ValidationKeys(tenant),
            ValidTypes = ["at+jwt"],
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        if (!result.IsValid)
        {
            // A token another tenant's server issued is a cross-tenant attempt and is audited; other failures are telemetry.
            var foreign = Handler.CanReadToken(token) && Handler.ReadJsonWebToken(token).Issuer is { } iss && iss != issuer && iss.EndsWith("/oauth", StringComparison.Ordinal);
            if (foreign)
            {
                await AuditRefusalAsync(services, null, "cross_tenant", ct);
            }
            return foreign ? "cross_tenant" : "invalid";
        }
        var claims = result.Claims;
        if (Text(claims, OAuthServer.TenantClaim) != tenant.ToString() || !Guid.TryParse(Text(claims, OAuthServer.AppClaim), out var appId)
            || !Guid.TryParse(Text(claims, OAuthServer.FamilyClaim), out var family) || Text(claims, OAuthServer.KindClaim) is not { } kind)
        {
            return "invalid";
        }
        var apps = services.GetRequiredService<Apps>();
        var app = await apps.FindAsync(appId, ct);
        var issuedAt = claims.TryGetValue("iat", out var iat) && iat is IConvertible number ? DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(number, System.Globalization.CultureInfo.InvariantCulture)) : DateTimeOffset.MinValue;
        if (app is not { Active: true } || (app.TokensRevokedAt is { } revokedAt && issuedAt <= revokedAt))
        {
            await AuditRefusalAsync(services, appId, "app_disabled", ct);
            return "revoked";
        }
        var scopes = (Text(claims, "scope") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        Guid? user = null;
        if (kind == OAuthServer.Delegated)
        {
            var state = await services.GetRequiredService<TokenFamilies>().StateAsync(family, ct);
            if (state is not { Revoked: false } || state.AppId != appId || !Guid.TryParse(Text(claims, "sub"), out var subject) || subject != state.UserId)
            {
                await AuditRefusalAsync(services, appId, "revoked", ct);
                return "revoked";
            }
            if (await services.GetRequiredService<ITenantMemberships>().GetAsync(subject, ct) is not { Status: MembershipStatus.Active })
            {
                return "revoked";
            }
            user = subject;
            context.SetActor(ActorType.User, subject);
        }
        else if (kind != OAuthServer.Application || !await apps.CredentialLiveAsync(appId, family, ct))
        {
            await AuditRefusalAsync(services, appId, "revoked", ct);
            return "revoked";
        }
        http.Features.Set(new PublicCaller(appId, kind, user, scopes, family));
        return null;
    }

    /// <summary>Reads a token of this tenant's issuer, an access token or an encrypted refresh token, without checking it is still live.</summary>
    internal static async Task<IDictionary<string, object>?> ReadAsync(string token, string issuer, TenantKeys keys, Guid tenant)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }
        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidateAudience = false,
            IssuerSigningKeys = keys.ValidationKeys(tenant),
            TokenDecryptionKeys = keys.DecryptionKeys(tenant),
            ClockSkew = TimeSpan.FromSeconds(30),
        });
        return result.IsValid && Text(result.Claims, OAuthServer.TenantClaim) == tenant.ToString() ? result.Claims : null;
    }

    private static string? Text(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            _ => value?.ToString(),
        } : null;

    private static async Task AuditRefusalAsync(IServiceProvider services, Guid? appId, string reason, CancellationToken ct)
    {
        var host = services.GetRequiredService<ITenantContext>().Host.Tenant?.Key;
        await services.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(AuditActions.ApiTokenRefused, "api_app", appId?.ToString(), AuditOutcome.Denied,
            Changes: new { reason, hostTenant = host }), ct);
        await services.GetRequiredService<IDbSession>().CommitAsync(ct);
    }

    /// <summary>401 with a Bearer challenge (RFC 6750), or 403 when the API is switched off for the tenant.</summary>
    private static async Task WriteRefusalAsync(HttpContext http, string reason)
    {
        var closed = reason == "api_closed";
        var code = closed ? ProblemCodes.EntitlementNotInPackage : TokenInvalid;
        http.Response.StatusCode = closed ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized;
        if (!closed)
        {
            http.Response.Headers.WWWAuthenticate = reason == "missing" ? "Bearer" : "Bearer error=\"invalid_token\"";
        }
        http.Response.Headers.CacheControl = "no-store";
        var localizer = http.RequestServices.GetRequiredService<ILocalizer>();
        var locale = http.RequestServices.GetService<RequestLocale>()?.Locale ?? Locales.Fallback;
        http.Response.Headers.ContentLanguage = locale;
        await http.Response.WriteAsJsonAsync(new Dictionary<string, object?>
        {
            ["type"] = ProblemCodes.TypeUri(code),
            ["title"] = localizer.Get("problem." + code, locale),
            ["status"] = http.Response.StatusCode,
            ["code"] = code,
        }, (JsonSerializerOptions?)null, "application/problem+json");
    }
}
