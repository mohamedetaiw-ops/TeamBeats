using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Messaging;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// L4 agent tasks of approved processes (spec 027 US1 scenario 5, FR-015, FR-024). Each request runs once, in its own
/// scope as the person who started the instance, and answers the process with <see cref="AgentTaskFinished"/> in the same
/// transaction as the run's record. A run stopped by a limit fails the step, which raises a process incident, and opens a
/// security incident for the agent. The kill switch and suspension stop it like any run.
/// </summary>
public sealed class AgentProcessTaskHandler(IServiceScopeFactory scopes, ITenantContext context, IClock clock) : IMessageHandler<AgentTaskRequested>
{
    public async Task HandleAsync(AgentTaskRequested task, CancellationToken ct)
    {
        var tenant = context.RequireTenant();
        await using var scope = scopes.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var accessor = sp.GetRequiredService<TenantContextAccessor>();
        accessor.SetTenant(tenant);
        accessor.SetActor(ActorType.User, task.OnBehalfOf);
        var session = sp.GetRequiredService<IDbSession>();
        // One run per step run: a redelivered request finds its row (written with the run) and does nothing.
        if (await session.ScalarAsync<bool?>("select true from agents.process_task where service_task_id = @id", new { id = task.ServiceTaskId }, ct) == true)
        {
            return;
        }
        var person = await sp.GetRequiredService<IUserDirectory>().GetAsync(task.OnBehalfOf, ct);
        RunView? run = null;
        string? refusal;
        if (person is not { Status: "active" })
        {
            refusal = "person_inactive";
        }
        else
        {
            try
            {
                (run, refusal) = await sp.GetRequiredService<AgentRuns>().ProcessTaskAsync(task.AgentId, task.Tools, task.MaxMinutes, task.Inputs, ct);
            }
            catch (AppException ex)
            {
                refusal = ex.DetailKey is { Length: > 0 } key ? key[(key.LastIndexOf('.') + 1)..] : "refused";
            }
        }
        var completed = run is { Status: RunStatuses.Completed, Outcome: RunOutcomes.Answered };
        var reason = completed ? null
            : refusal ?? (run!.Outcome == RunOutcomes.LimitReached ? "limit_" + (run.Reason ?? "reached") : run.Outcome ?? "failed");
        reason = reason is { Length: > 100 } ? reason[..100] : reason;
        var unit = await session.ScalarAsync<Guid?>("select org_unit_id from agents.agent where id = @id", new { id = task.AgentId }, ct);
        if (run is { Outcome: RunOutcomes.LimitReached } && unit is { } agentUnit)
        {
            await sp.GetRequiredService<AgentSecurityCenter>().OpenAutomaticAsync("l4_limit", task.AgentId, agentUnit, run.Id, ct);
        }
        await session.ExecAsync(
            """
            insert into agents.process_task (tenant_id, service_task_id, instance_id, definition_key, definition_version, element_id, agent_id, user_id, run_id,
              outcome, reason, created_at)
            values (platform.current_tenant_id(), @ServiceTaskId, @InstanceId, @DefinitionKey, @DefinitionVersion, @ElementId, @AgentId, @OnBehalfOf, @run,
              @outcome, @reason, @now)
            """, new
            {
                task.ServiceTaskId, task.InstanceId, task.DefinitionKey, task.DefinitionVersion, task.ElementId, task.AgentId, task.OnBehalfOf, run = run?.Id,
                outcome = completed ? AgentTaskOutcomes.Completed : AgentTaskOutcomes.Failed, reason, now = clock.UtcNow,
            }, ct);
        await sp.GetRequiredService<IOutbox>().EnqueueAsync(new AgentTaskFinished(task.ServiceTaskId, task.InstanceId,
            completed ? AgentTaskOutcomes.Completed : AgentTaskOutcomes.Failed, completed ? run!.Answer : null, reason, run?.Id), ct);
        if (unit is { } owner)
        {
            // The step's facts for its end to end evaluation (spec 034 R3): counts only, no content.
            var facts = run is null ? (Calls: 0, Refused: 0, Scope: 0, Approval: 0, Ms: 0) : await session.QuerySingleOrDefaultAsync(
                """
                select count(*) filter (where kind = 'tool' and decision <> 'refused')::int,
                  count(*) filter (where kind = 'tool' and decision = 'refused')::int,
                  count(*) filter (where reason = 'tool.not_in_process')::int,
                  count(*) filter (where reason = 'tool.approval_not_available')::int,
                  coalesce(sum(duration_ms), 0)::int
                from agents.run_step where run_id = @run
                """, new { run = run.Id }, r => (Calls: r.GetInt32(0), Refused: r.GetInt32(1), Scope: r.GetInt32(2), Approval: r.GetInt32(3), Ms: r.GetInt32(4)), ct);
            var version = run?.AgentVersion;
            await sp.GetRequiredService<IOutbox>().EnqueueAsync(new AgentTaskRunRecorded(task.ServiceTaskId, task.InstanceId, task.DefinitionKey, task.DefinitionVersion,
                task.ElementId, task.AgentId, version, owner, run?.Id, completed ? AgentTaskOutcomes.Completed : AgentTaskOutcomes.Failed, reason,
                facts.Calls, facts.Refused, facts.Scope, facts.Approval, facts.Ms, task.MaxMinutes), ct);
        }
        await session.CommitAsync(ct);
    }
}
