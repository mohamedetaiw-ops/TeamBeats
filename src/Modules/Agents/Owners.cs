using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Agents;

/// <summary>
/// When an agent owner leaves (spec 020 edge case "agent owner leaves the company"): the active backup owner takes
/// over, otherwise an AI Policy Administrator does; the agent is marked for the new owner, the change is audited
/// <c>agent.owner_changed</c>, and the new owner is told. The agent keeps running. A backup owner who leaves is
/// removed from the agent.
/// </summary>
public sealed class AgentOwnerHandover(IDbSession session, ITenantMemberships memberships, IAuthorizationProvisioning provisioning, IAuditWriter audit,
    INotificationSender notifications, IClock clock)
{
    public const string ReassignedTemplate = "agents.owner_reassigned";

    public async Task OwnerLeftAsync(Guid user, CancellationToken ct)
    {
        await session.ExecAsync("update agents.agent set backup_owner_user_id = null where backup_owner_user_id = @user and owner_user_id <> @user", new { user }, ct);
        var owned = await session.QueryAsync(
            "select id, org_unit_id, backup_owner_user_id from agents.agent where owner_user_id = @user and status <> 'retired' for update",
            new { user }, r => (Id: r.GetGuid(0), Unit: r.GetGuid(1), Backup: r.Get<Guid?>("backup_owner_user_id")), ct);
        if (owned.Count == 0)
        {
            return;
        }
        Guid? administrator = null;
        var administratorRead = false;
        foreach (var agent in owned)
        {
            Guid? next = agent.Backup is { } backup && backup != user && (await memberships.GetAsync(backup, ct))?.Status == MembershipStatus.Active ? backup : null;
            var reason = "backup";
            if (next is null)
            {
                if (!administratorRead)
                {
                    administrator = await AdministratorAsync(user, ct);
                    administratorRead = true;
                }
                next = administrator;
                reason = "policy_manager";
            }
            if (next is not { } owner)
            {
                // Nobody can take it: the agent stays with the person who left, marked, until someone names an owner.
                await session.ExecAsync("update agents.agent set owner_flagged_at = @now where id = @id", new { id = agent.Id, now = clock.UtcNow }, ct);
                await audit.WriteAsync(new AuditEntry(AuditActions.AgentOwnerChanged, "agent", agent.Id.ToString(), TargetOrgUnitId: agent.Unit,
                    Changes: new { from = new { owner = user }, to = new { owner = user }, reason = "no_successor" }), ct);
                continue;
            }
            await session.ExecAsync(
                "update agents.agent set owner_user_id = @owner, backup_owner_user_id = null, owner_flagged_at = @now, updated_at = @now where id = @id",
                new { id = agent.Id, owner, now = clock.UtcNow }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.AgentOwnerChanged, "agent", agent.Id.ToString(), TargetOrgUnitId: agent.Unit,
                Changes: new { from = new { owner = user, backup = agent.Backup }, to = new { owner, backup = (Guid?)null }, reason }), ct);
            await notifications.SendAsync(new NotificationRequest([owner], NotificationCategories.System, ReassignedTemplate, $"/admin/agents/{agent.Id}",
                DedupeKey: $"agents.owner_reassigned:{agent.Id:N}:{owner:N}"), ct);
        }
    }

    /// <summary>The AI Policy Administrator who takes over: the first active tenant-wide holder of <c>ai.policy_manage</c> by ID, so the choice is stable.</summary>
    private async Task<Guid?> AdministratorAsync(Guid leaver, CancellationToken ct)
    {
        foreach (var holder in (await provisioning.TenantHoldersAsync(Permissions.AiPolicyManage, ct)).Where(h => h != leaver).Order())
        {
            if ((await memberships.GetAsync(holder, ct))?.Status == MembershipStatus.Active)
            {
                return holder;
            }
        }
        return null;
    }
}

public sealed class AgentOwnerHandler(AgentOwnerHandover handover) : IMessageHandler<UserDeprovisioned>
{
    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) => handover.OwnerLeftAsync(message.UserId, ct);
}
