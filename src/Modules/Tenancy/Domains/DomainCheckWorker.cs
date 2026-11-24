using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy.Domains;

/// <summary>
/// Every hour, for every active tenant (spec 033 R2 T021): checks again the TXT record of verified domains due a check and
/// stops serving a domain whose record is gone, then reads each verified domain's certificate and tells the tenant's
/// administrators when it ends within the warning period or cannot be read. The tenant's own subdomain is never touched,
/// so people keep working there. Hosted by the worker, and by the development host when <c>Tenancy:Domains:InProcess</c>
/// is true.
/// </summary>
public sealed class DomainCheckWorker(IServiceScopeFactory scopes, ILogger<DomainCheckWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Tenancy:Domains:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(scopes, logger, stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    public static async Task RunOnceAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Domain check could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await RunForTenantAsync(scopes, tenant.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Domain check failed for tenant {TenantId}", tenant.Id);
            }
        }
    }

    public static async Task RunForTenantAsync(IServiceScopeFactory scopes, Guid tenantId, CancellationToken ct)
    {
        IReadOnlyList<string> hosts;
        {
            await using var scope = Scoped(scopes, tenantId, out var services);
            var domains = services.GetRequiredService<CustomDomains>();
            var stopped = await domains.RecheckAsync(ct);
            foreach (var host in stopped)
            {
                await TellAdminsAsync(services, "tenancy.domain.stopped", host, ct);
            }
            hosts = await domains.VerifiedHostsAsync(ct);
            await services.GetRequiredService<IDbSession>().CommitAsync(ct);
        }
        // Certificates are read outside any transaction: a slow host never holds a database connection.
        foreach (var host in hosts)
        {
            DateTimeOffset? expires;
            {
                await using var probeScope = scopes.CreateAsyncScope();
                expires = await probeScope.ServiceProvider.GetRequiredService<ICertificateProbe>().ExpiresAsync(host, ct);
            }
            await using var scope = Scoped(scopes, tenantId, out var services);
            if (await services.GetRequiredService<CustomDomains>().RecordCertificateAsync(host, expires, ct))
            {
                await TellAdminsAsync(services, "tenancy.domain.certificate", host, ct);
            }
            await services.GetRequiredService<IDbSession>().CommitAsync(ct);
        }
    }

    private static AsyncServiceScope Scoped(IServiceScopeFactory scopes, Guid tenantId, out IServiceProvider services)
    {
        var scope = scopes.CreateAsyncScope();
        services = scope.ServiceProvider;
        var context = services.GetRequiredService<TenantContextAccessor>();
        context.SetTenant(tenantId);
        context.SetActor(ActorType.System, null);
        return scope;
    }

    private static async Task TellAdminsAsync(IServiceProvider services, string template, string host, CancellationToken ct)
    {
        var admins = await services.GetRequiredService<IAuthorizationProvisioning>().TenantHoldersAsync(Permissions.TenantSettingsManage, ct);
        if (admins.Count == 0)
        {
            return;
        }
        await services.GetRequiredService<INotificationSender>().SendAsync(new NotificationRequest(admins, NotificationCategories.System, template,
            "/admin/tenant/domains", new Dictionary<string, string> { ["host"] = host }, DedupeKey: $"{template}:{host}"), ct);
    }
}
