using TeamBeats.Modules.Audit;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Workers;

/// <summary>Runs an action once per tenant, each in its own scope, transaction and tenant context.</summary>
public sealed class PerTenantRunner(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger)
{
    public async Task RunAsync(Func<IServiceProvider, CancellationToken, Task> action, CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        await using (var listScope = scopes.CreateAsyncScope())
        {
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }

        foreach (var tenant in tenants)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await action(scope.ServiceProvider, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Tenant job failed for {TenantId}", tenant.Id);
            }
        }
    }
}

/// <summary>Seals committed audit rows into each tenant's hash chain (ADR-0008).</summary>
public sealed class AuditSealerService(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PerTenantRunner(scopes, logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            await runner.RunAsync((sp, ct) => sp.GetRequiredService<AuditMaintenance>().SealAsync(1000, ct), stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}

/// <summary>Daily retention purge (AUD-006) and removal of expired export files (7 days).</summary>
public sealed class AuditRetentionService(IServiceScopeFactory scopes, ILogger<PerTenantRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runner = new PerTenantRunner(scopes, logger);
        while (!stoppingToken.IsCancellationRequested)
        {
            await runner.RunAsync(async (sp, ct) =>
            {
                await sp.GetRequiredService<AuditMaintenance>().PurgeAsync(ct);
                await sp.GetRequiredService<AuditMaintenance>().DeleteExpiredExportsAsync(ct);
            }, stoppingToken);
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}
