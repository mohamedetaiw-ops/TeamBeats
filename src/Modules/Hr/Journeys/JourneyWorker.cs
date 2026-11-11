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
/// issues approved letters the conversion service could not print when they were approved (slice 5). Runs as the system
/// actor. Hosted by the worker, and by the development host when <c>Hr:Journeys:InProcess</c> is true.
/// </summary>
public sealed class JourneyWorker(IServiceScopeFactory scopes, ILogger<JourneyWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Hr:Journeys:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(2);

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
            logger.LogWarning(ex, "Journey steps could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                var entitlements = scope.ServiceProvider.GetRequiredService<IEntitlementService>();
                if (await entitlements.IsEnabledAsync(Capabilities.HrJourneys, ct))
                {
                    var journeys = scope.ServiceProvider.GetRequiredService<JourneyService>();
                    await journeys.OpenDueAsync(null, ct);
                    await journeys.SettleAttestationsAsync(null, ct);
                    await journeys.EscalateAccessAsync(ct);
                    await scope.ServiceProvider.GetRequiredService<Preboarding>().SendDueAsync(ct);
                }
                if (await entitlements.IsEnabledAsync(Capabilities.HrLetters, ct))
                {
                    await scope.ServiceProvider.GetRequiredService<Letters.LetterRequests>().IssueDueAsync(ct);
                }
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Journey steps failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
