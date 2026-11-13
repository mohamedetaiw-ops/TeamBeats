using TeamBeats.Authorization.Contracts;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Entitlements.Contracts;
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

    /// <summary>
    /// The effective decision of a public operation (FR-009): the token must hold the operation's scope and the central
    /// authorization service must allow it for the principal. A scope never allows what the decision does not.
    /// </summary>
    public bool May(string scope, Decision decision) => Scopes.Contains(scope) && decision.Allowed;

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

    /// <summary>The largest request body of a public call (section 6); document uploads set their own limit.</summary>
    public const long MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// The request pipeline of the public API (FR-017, FR-027, T009), after the tenant's network rules: the token, the
    /// tenant switch and package, the app's own address ranges, the limits per tenant and per app or app and person,
    /// the monthly quota, and then the call itself, logged with its outcome and latency.
    /// </summary>
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
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            {
                size.MaxRequestBodySize = MaxBodyBytes;
            }
            var refusal = await AuthenticateAsync(http, context, tenant.Id);
            if (refusal is not null)
            {
                await WriteRefusalAsync(http, refusal);
                return;
            }
            await RunCallAsync(http, next, tenant.Id);
        });

    private static async Task RunCallAsync(HttpContext http, RequestDelegate next, Guid tenant)
    {
        var services = http.RequestServices;
        var ct = http.RequestAborted;
        var caller = http.Features.Get<PublicCaller>()!;
        var app = http.Features.Get<AppRecord>()!;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var operation = http.Request.Method + " " + ((http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched");
        var monthlyLimit = await services.GetRequiredService<IEntitlementService>().GetLimitAsync(Capabilities.ApiMonthlyCalls, ct);

        async Task LogAsync(int status, string? errorClass)
        {
            var latency = (int)Math.Min(int.MaxValue, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            // A scope of its own: the call's own transaction may have failed, and the log must still be written.
            await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            var own = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
            own.SetTenant(tenant);
            own.SetActor(ActorType.System, null);
            await scope.ServiceProvider.GetRequiredService<CallLog>().RecordAsync(
                new CallRecord(caller.AppId, caller.UserId, operation, status, errorClass, latency, Correlation.Id), monthlyLimit, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(CancellationToken.None);
        }

        // The app's own address ranges (FR-027), on top of the tenant's network rules.
        if (!ApiRules.AddressAllowed(app.IpRanges, http.Connection.RemoteIpAddress))
        {
            // Audited at most once a minute per app and address, so a blocked client cannot flood the audit log.
            var cache = services.GetRequiredService<IMemoryCache>();
            var key = $"publicapi.network:{tenant:N}:{app.Id:N}:{http.Connection.RemoteIpAddress}";
            if (!cache.TryGetValue(key, out _))
            {
                cache.Set(key, true, TimeSpan.FromMinutes(1));
                await AuditRefusalAsync(services, app.Id, "app_network", ct);
            }
            await LogAsync(StatusCodes.Status403Forbidden, "denied");
            await WriteProblemAsync(http, StatusCodes.Status403Forbidden, ProblemCodes.NetworkNotAllowed, new Dictionary<string, object?> { ["area"] = "app" });
            return;
        }

        // Limits per tenant and per app, or per app and person for a delegated app (FR-017); the tighter one answers.
        var limiter = services.GetRequiredService<PublicRateLimits>();
        var settings = await services.GetRequiredService<ApiSettings>().CurrentAsync(ct);
        var installation = services.GetRequiredService<PublicApiLimits>();
        var forTenant = installation.ForTenant(settings.TenantCallsPerMinute);
        var tenantLimit = limiter.Take(PublicRateLimits.Tenant(tenant), forTenant, installation.Share(forTenant));
        var ownLimit = limiter.Take(caller.UserId is { } person ? PublicRateLimits.AppUser(tenant, app.Id, person) : PublicRateLimits.App(tenant, app.Id),
            app.Limit, installation.Share(app.Limit));
        var shown = !tenantLimit.Allowed || (ownLimit.Allowed && tenantLimit.Remaining < ownLimit.Remaining) ? tenantLimit : ownLimit;
        // Set as the answer starts, so an error answer written by the exception handler carries them too.
        http.Response.OnStarting(() =>
        {
            http.Response.Headers["RateLimit-Limit"] = shown.Limit.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["RateLimit-Remaining"] = shown.Remaining.ToString(CultureInfo.InvariantCulture);
            http.Response.Headers["RateLimit-Reset"] = shown.ResetSeconds.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        });
        if (!tenantLimit.Allowed || !ownLimit.Allowed)
        {
            await LogAsync(StatusCodes.Status429TooManyRequests, CallLog.RateLimited);
            http.Response.Headers.RetryAfter = shown.ResetSeconds.ToString(CultureInfo.InvariantCulture);
            await WriteProblemAsync(http, StatusCodes.Status429TooManyRequests, RateLimited,
                new Dictionary<string, object?> { ["limit"] = tenantLimit.Allowed ? "app" : "tenant" });
            return;
        }

        // The monthly quota of the package (US5 scenario 2): the web app keeps working, only the API answers 429.
        if (monthlyLimit is { } quota && await services.GetRequiredService<CallLog>().MonthCallsAsync(ct) >= quota)
        {
            await LogAsync(StatusCodes.Status429TooManyRequests, CallLog.Quota);
            var now = services.GetRequiredService<IClock>().UtcNow;
            var reset = new DateTimeOffset(CallLog.MonthOf(now).AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            http.Response.Headers.RetryAfter = ((long)Math.Ceiling((reset - now).TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            await WriteProblemAsync(http, StatusCodes.Status429TooManyRequests, ProblemCodes.EntitlementQuotaExhausted,
                new Dictionary<string, object?> { ["capability"] = Capabilities.ApiMonthlyCalls, ["limit"] = quota });
            return;
        }

        try
        {
            await next(http);
        }
        catch (Exception ex)
        {
            var status = ex switch
            {
                AppException a => a.Status,
                BadHttpRequestException b => b.StatusCode,
                OperationCanceledException when ct.IsCancellationRequested => 499,
                _ => StatusCodes.Status500InternalServerError,
            };
            await LogAsync(status, status == 499 ? "client" : CallLog.ClassOf(status));
            throw;
        }
        await LogAsync(http.Response.StatusCode, CallLog.ClassOf(http.Response.StatusCode));
    }

    public const string RateLimited = ProblemCodes.ApiRateLimited;

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
        else
        {
            // The app acts as itself: the authorization service sees only the roles given to the app (FR-011).
            context.SetActor(ActorType.App, appId);
        }
        http.Features.Set(new PublicCaller(appId, kind, user, scopes, family));
        http.Features.Set(app);
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
        await WriteProblemAsync(http, http.Response.StatusCode, code, null);
    }

    /// <summary>A problem details answer written here, before the endpoint, so the headers set for it are kept.</summary>
    internal static async Task WriteProblemAsync(HttpContext http, int status, string code, IReadOnlyDictionary<string, object?>? extensions)
    {
        http.Response.StatusCode = status;
        http.Response.Headers.CacheControl = "no-store";
        var localizer = http.RequestServices.GetRequiredService<ILocalizer>();
        var locale = http.RequestServices.GetService<RequestLocale>()?.Locale ?? Locales.Fallback;
        http.Response.Headers.ContentLanguage = locale;
        var body = new Dictionary<string, object?>
        {
            ["type"] = ProblemCodes.TypeUri(code),
            ["title"] = localizer.Get("problem." + code, locale),
            ["status"] = status,
            ["code"] = code,
        };
        foreach (var (key, value) in extensions ?? new Dictionary<string, object?>())
        {
            body[key] = value;
        }
        await http.Response.WriteAsJsonAsync(body, (JsonSerializerOptions?)null, "application/problem+json");
    }
}
