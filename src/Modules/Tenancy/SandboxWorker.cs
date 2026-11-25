using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

/// <summary>
/// Every hour (spec 035 R3, T024): suspends developer sandboxes past their date and closes them 14 days later, each in
/// its own scope and transaction, as the system. Hosted by the worker, and by the development host when
/// <c>Tenancy:Sandboxes:InProcess</c> is true.
/// </summary>
public sealed class SandboxWorker(IServiceScopeFactory scopes, ILogger<SandboxWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Tenancy:Sandboxes:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(scopes, logger, stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    public static async Task<int> RunOnceAsync(IServiceScopeFactory scopes, ILogger logger, CancellationToken ct)
    {
        IReadOnlyList<Guid> due;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            listScope.ServiceProvider.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
            due = await listScope.ServiceProvider.GetRequiredService<PlatformTenants>().DueSandboxesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sandbox expiry could not list tenants");
            return 0;
        }
        var ended = 0;
        foreach (var id in due)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContextAccessor>().SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<PlatformTenants>().EndSandboxAsync(id, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
                ended++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sandbox expiry failed for tenant {TenantId}", id);
            }
        }
        return ended;
    }
}
