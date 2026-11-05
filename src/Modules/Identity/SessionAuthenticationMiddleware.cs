using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity;

/// <summary>
/// Resolves host scope, validates the session cookie and establishes tenant context for the request.
/// Tenant identity comes from the server-side session looked up under the host's tenant context, so a session
/// from tenant A presented on tenant B's host finds nothing (ADR-0007). Membership and user status are checked
/// on every request, so disabling a membership takes effect immediately (spec 003 US3).
/// </summary>
public static class SessionAuthenticationMiddleware
{
    public static IApplicationBuilder UseTeamBeatsSessions(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var services = http.RequestServices;
            var context = services.GetRequiredService<TenantContextAccessor>();
            var hosts = services.GetRequiredService<IOptions<HostResolutionOptions>>().Value;
            var sessionOptions = services.GetRequiredService<IOptions<SessionOptions>>().Value;
            context.SourceIp = http.Connection.RemoteIpAddress?.ToString();

            var (match, key) = HostResolver.Resolve(http.Request.Host.Host, hosts);
            var token = http.Request.Cookies[sessionOptions.CookieName];
            var db = services.GetRequiredService<IDbSession>();

            if (match == HostMatch.Platform)
            {
                context.SetHost(HostScope.Platform);
                if (!string.IsNullOrEmpty(token))
                {
                    var op = await db.QuerySingleOrDefaultAsync(
                        """
                        select s.id, s.operator_id from identity.platform_session s
                        join identity.platform_operator o on o.id = s.operator_id
                        where s.token_hash = @h and s.revoked_at is null and s.expires_at > now() and o.status = 'active'
                        """, new { h = SessionTokens.Hash(token) }, r => (Session: r.GetGuid(0), Operator: r.GetGuid(1)), http.RequestAborted);
                    if (op != default)
                    {
                        context.SetActor(ActorType.PlatformOperator, op.Operator, op.Session);
                    }
                }
            }
            else if (match == HostMatch.TenantKey)
            {
                var registry = services.GetRequiredService<ITenantRegistry>();
                var tenant = await registry.FindByKeyAsync(key!, http.RequestAborted);
                if (tenant is not null)
                {
                    context.SetHost(HostScope.ForTenant(tenant));
                    if (tenant.Status != TenantLifecycleStatus.Active && !IsSignInPath(http.Request.Path))
                    {
                        // Suspended or closed: every request is refused; data is retained (spec 001 US3).
                        throw new AppException(
                            tenant.Status == TenantLifecycleStatus.Suspended ? ProblemCodes.TenantSuspended : ProblemCodes.TenantClosed, 403);
                    }

                    if (!string.IsNullOrEmpty(token))
                    {
                        context.SetTenant(tenant.Id);
                        var s = await db.QuerySingleOrDefaultAsync(
                            """
                            select s.id, s.user_id from identity.user_session s
                            join identity.user_account u on u.id = s.user_id
                            where s.token_hash = @h and s.revoked_at is null and s.expires_at > now() and u.status = 'active'
                            """, new { h = SessionTokens.Hash(token) }, r => (Session: r.GetGuid(0), User: r.GetGuid(1)), http.RequestAborted);
                        var membership = s == default ? null
                            : await services.GetRequiredService<ITenantMemberships>().GetAsync(s.User, http.RequestAborted);
                        if (membership is { Status: MembershipStatus.Active })
                        {
                            context.SetActor(ActorType.User, s.User, s.Session);
                        }
                        else
                        {
                            context.SetTenant(null);
                        }
                    }
                }
            }

            await ResolveLocaleAsync(http, context, db);
            await next(http);
        });

    /// <summary>Sign-in endpoints run for inactive tenants so the refusal is answered and audited by the sign-in flow.</summary>
    private static bool IsSignInPath(PathString path) =>
        path.StartsWithSegments("/dev/signin") || path.StartsWithSegments("/api/v1/auth/signin");

    private static async Task ResolveLocaleAsync(HttpContext http, ITenantContext context, IDbSession db)
    {
        var locale = http.RequestServices.GetRequiredService<RequestLocale>();
        if (context.ActorType == ActorType.User)
        {
            var pref = await db.ScalarAsync<string?>("select preferred_locale from identity.user_account where id = @u",
                new { u = context.ActorId }, http.RequestAborted);
            var settings = await http.RequestServices.GetRequiredService<ITenantSettingsReader>().GetLocaleSettingsAsync(http.RequestAborted);
            locale.Locale = LocaleResolver.Resolve(pref, settings.EnabledLocales, settings.DefaultLocale);
            return;
        }

        // Anonymous callers: honour a supported Accept-Language, otherwise the fallback.
        var accept = http.Request.Headers.AcceptLanguage.ToString();
        var first = accept.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Split(';')[0].Split('-')[0].ToLowerInvariant())
            .FirstOrDefault(Locales.IsSupported);
        locale.Locale = first ?? Locales.Fallback;
    }
}
