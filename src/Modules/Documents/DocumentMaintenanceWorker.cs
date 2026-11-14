using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Documents;

/// <summary>
/// Every minute, for every active tenant in its own scope and transaction: purges documents left in the
/// recycle bin past the tenant's period (spec 011 FR-018) reminds holders of check-outs older than fourteen days, puts
/// approved controlled versions in force on their date, opens or flags owner reviews, and acts on documents whose retention
/// label period has ended (spec 011 US6).
/// Runs as the system actor. Hosted by the worker, and by the development host when <c>Documents:Maintenance:InProcess</c> is true.
/// </summary>
public sealed class DocumentMaintenanceWorker(IServiceScopeFactory scopes, IClock clock, ILogger<DocumentMaintenanceWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Documents:Maintenance:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Document maintenance could not list tenants");
            return;
        }
        var now = clock.UtcNow;
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                var versions = scope.ServiceProvider.GetRequiredService<DocumentVersions>();
                await versions.PurgeAsync(now, 100, ct);
                await scope.ServiceProvider.GetRequiredService<Records>().RunAsync(now, 100, ct);
                await scope.ServiceProvider.GetRequiredService<Assets>().ExpireAsync(now, 100, ct);
                await versions.RemindCheckOutsAsync(now, 200, ct);
                await scope.ServiceProvider.GetRequiredService<DocumentReleases>().RunDueAsync(now, 200, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Document maintenance failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
