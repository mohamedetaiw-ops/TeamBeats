using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// Resumes people's agent runs a restart cut off (spec 020 T021, plan item 6). Each stalled run is claimed in a system
/// scope, then continued in a scope of its own as its person, from its last checkpoint, and committed. A run of a person
/// who is no longer active is left for the purge to close as interrupted.
/// </summary>
public sealed class AgentRunResumer(IServiceScopeFactory scopes, ITenantContext context, IClock clock, ILogger<AgentRunResumer> logger)
{
    public const int BatchSize = 20;

    private async Task<T> InScopeAsync<T>(Guid tenant, Guid? user, Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = scopes.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(user is null ? ActorType.System : ActorType.User, user);
        var result = await action(scope.ServiceProvider);
        await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync();
        return result;
    }

    /// <summary>Resumes the tenant's stalled runs; returns how many came to an end or a confirmation card.</summary>
    public async Task<int> ResumeStalledAsync(CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var stalled = await InScopeAsync(tenant, null, sp => sp.GetRequiredService<AgentRuns>().ClaimStalledAsync(clock.UtcNow, BatchSize, ct));
        var resumed = 0;
        foreach (var (runId, userId, attempt) in stalled)
        {
            try
            {
                resumed += await InScopeAsync(tenant, userId, async sp =>
                {
                    var person = await sp.GetRequiredService<IUserDirectory>().GetAsync(userId, ct);
                    if (person is not { Status: "active" })
                    {
                        return 0;
                    }
                    return await sp.GetRequiredService<AgentRuns>().ResumeAsync(runId, attempt, ct) is null ? 0 : 1;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Resuming agent run {RunId} failed", runId);
            }
        }
        return resumed;
    }

    /// <summary>
    /// Carries out approvers' decisions on agent actions and cancels those nobody decided in time (spec 027 US4), each as
    /// the person who asked. A person who is no longer active gets nothing run: the approval is cancelled and the purge
    /// closes the run. Returns how many were carried out.
    /// </summary>
    public async Task<int> ApplyApprovalsAsync(CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        var due = await InScopeAsync(tenant, null, sp => sp.GetRequiredService<AgentApprovals>().ClaimDueAsync(clock.UtcNow, BatchSize, ct));
        var applied = 0;
        foreach (var (approvalId, userId) in due)
        {
            try
            {
                applied += await InScopeAsync(tenant, userId, async sp =>
                {
                    var person = await sp.GetRequiredService<IUserDirectory>().GetAsync(userId, ct);
                    if (person is not { Status: "active" })
                    {
                        await sp.GetRequiredService<AgentApprovals>().CancelAsync(approvalId, ct);
                        return 0;
                    }
                    return await sp.GetRequiredService<AgentRuns>().ApprovalAsync(approvalId, ct) is null ? 0 : 1;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Applying agent approval {ApprovalId} failed", approvalId);
            }
        }
        return applied;
    }
}
