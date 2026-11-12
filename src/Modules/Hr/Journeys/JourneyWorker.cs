using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

/// <summary>
/// Every two minutes, for every active tenant with <c>hr.journeys</c> in its own scope and transaction: starts the
/// engine process of each journey step whose scheduled date has come, closes attestation steps with nothing left to attest
/// to and escalates access removal lines not confirmed after one working day (spec 022 R2 plan, slice 3); with <c>hr.letters</c>,
/// issues approved letters the conversion service could not print when they were approved (slice 5). A tenant whose
/// batch of due steps was full gets more batches in the same tick, each in its own transaction, within
/// <see cref="DrainBudget"/>, so a busy day's steps still open within minutes (section 15). Runs as the system actor. Hosted by the worker, and by the development host when <c>Hr:Journeys:InProcess</c> is true.
/// </summary>
public sealed class JourneyWorker(IServiceScopeFactory scopes, ILogger<JourneyWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Hr:Journeys:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);

    /// <summary>Time a tick may spend on further batches of due steps; with the tick it keeps a step within 5 minutes of its time.</summary>
    public static readonly TimeSpan DrainBudget = TimeSpan.FromSeconds(90);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(Tick, stoppingToken);
        }
    }

    /// <summary>One tick: every active tenant once, then further batches for the tenants whose batch of due steps was full.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct, IReadOnlyCollection<Guid>? only = null)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Journey steps could not list tenants");
            return 0;
        }
        var opened = 0;
        var busy = new List<Guid>();
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active && (only is null || only.Contains(t.Id))))
        {
            var count = await InTenantAsync(tenant.Id, TenantPassAsync, ct);
            opened += count;
            if (count >= JourneyService.DueBatch)
            {
                busy.Add(tenant.Id);
            }
        }
        // Every tenant gets its first batch before any tenant gets a second one.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (busy.Count > 0 && watch.Elapsed < DrainBudget)
        {
            var still = new List<Guid>();
            foreach (var id in busy)
            {
                var count = await InTenantAsync(id, (sp, c) => sp.GetRequiredService<JourneyService>().OpenDueAsync(null, c), ct);
                opened += count;
                if (count >= JourneyService.DueBatch)
                {
                    still.Add(id);
                }
            }
            busy = still;
        }
        return opened;
    }

    private static async Task<int> TenantPassAsync(IServiceProvider sp, CancellationToken ct)
    {
        var entitlements = sp.GetRequiredService<IEntitlementService>();
        var opened = 0;
        if (await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct))
        {
            var journeys = sp.GetRequiredService<JourneyService>();
            opened = await journeys.OpenDueAsync(null, ct);
            await journeys.SettleAttestationsAsync(null, ct);
            await journeys.SettleTrainingAsync(null, ct);
            await journeys.EscalateAccessAsync(ct);
            await sp.GetRequiredService<Preboarding>().SendDueAsync(ct);
        }
        if (await entitlements.IsEnabledAsync(Capabilities.HrLetters, ct))
        {
            await sp.GetRequiredService<Letters.LetterRequests>().IssueDueAsync(ct);
        }
        return opened;
    }

    /// <summary>Runs <paramref name="action"/> for one tenant in its own scope and transaction as the system actor; 0 when it fails.</summary>
    private async Task<int> InTenantAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task<int>> action, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
            context.SetTenant(tenantId);
            context.SetActor(ActorType.System, null);
            var result = await action(scope.ServiceProvider, ct);
            await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Journey steps failed for tenant {TenantId}", tenantId);
            return 0;
        }
    }
}
