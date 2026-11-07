using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>
/// Enforces the tenant's network rules on every request to a tenant host, before any module code and before a session
/// can be created (spec 026 US1, FR-001). The address is the connection's, after the forwarded headers middleware, which
/// only runs when the platform is configured behind proxies it trusts (FR-002); a client-set header is never read here.
/// Refusals are audited with the source address, at most once a minute per address and area so a blocked client cannot
/// flood the audit log.
/// </summary>
public static class NetworkGuard
{
    public static readonly TimeSpan AuditEvery = TimeSpan.FromMinutes(1);

    public static IApplicationBuilder UseNetworkRules(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var services = http.RequestServices;
            var context = services.GetRequiredService<TenantContextAccessor>();
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { } tenant)
            {
                await next(http);
                return;
            }

            var policy = await services.GetRequiredService<PolicyStore>().ForTenantAsync(tenant.Id, http.RequestAborted);
            var address = http.Connection.RemoteIpAddress;
            var adminArea = http.GetEndpoint()?.Metadata.GetMetadata<AdminAreaMetadata>() is not null;
            if (NetworkRules.Refusal(policy, address, adminArea) is not { } area)
            {
                await next(http);
                return;
            }

            var source = address is null ? null : (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            var cache = services.GetRequiredService<IMemoryCache>();
            var key = $"security.refused:{tenant.Id}:{source}:{area}";
            if (!cache.TryGetValue(key, out _))
            {
                cache.Set(key, true, AuditEvery);
                var previous = context.TenantId;
                context.SetTenant(tenant.Id);
                await services.GetRequiredService<IAuditWriter>().WriteAsync(new AuditEntry(AuditActions.SecurityRequestBlockedNetwork, "network", source,
                    AuditOutcome.Denied, Changes: new { address = source, area = area == NetworkArea.Admin ? "admin" : "tenant", path = http.Request.Path.Value }),
                    http.RequestAborted);
                // Sign-ins and requests from blocked networks reach security officers as one alert per address and hour.
                if (await services.GetRequiredService<TeamBeats.Entitlements.Contracts.IEntitlementService>()
                        .IsEnabledAsync(TeamBeats.Entitlements.Contracts.Capabilities.SecurityAlerts, http.RequestAborted))
                {
                    var now = services.GetRequiredService<IClock>().UtcNow;
                    await services.GetRequiredService<AlertService>().RaiseAsync(AlertKinds.NetworkBlocked, null,
                        new { address = source, area = area == NetworkArea.Admin ? "admin" : "tenant" },
                        $"{AlertKinds.NetworkBlocked}:{source}:{area}:{now:yyyyMMddHH}", http.RequestAborted);
                }
                await services.GetRequiredService<IDbSession>().CommitAsync(http.RequestAborted);
                context.SetTenant(previous);
            }
            throw new AppException(ProblemCodes.NetworkNotAllowed, 403, null, null,
                new Dictionary<string, object?> { ["area"] = area == NetworkArea.Admin ? "admin" : "tenant" });
        });
}
