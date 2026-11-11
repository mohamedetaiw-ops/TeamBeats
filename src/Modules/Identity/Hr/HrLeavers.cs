using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Identity.Hr;

/// <summary>
/// Leavers lose access on time (spec 019 US4, FR-008): when HR's last working day has ended, the TeamBeats account is
/// disabled and every session revoked, whether or not the directory has disabled it yet. The tenant's last
/// administrator is kept, as directory sync does. A rehire reopens the account only when the tenant allows it and only
/// when HR closed it.
/// </summary>
public sealed class HrLeavers(
    IDbSession session,
    ITenantMemberships memberships,
    IAuthorizationProvisioning authz,
    ISessionRevocation sessions,
    IUserDirectory users,
    IOutbox outbox,
    IAuditWriter audit,
    IClock clock,
    ILogger<HrLeavers> logger)
{
    public async Task LeftAsync(EmployeeLeft message, CancellationToken ct)
    {
        if (await memberships.GetAsync(message.UserId, ct) is not { Status: MembershipStatus.Active })
        {
            return;
        }
        if (await authz.IsLastTenantAdministratorAsync(message.UserId, ct))
        {
            logger.LogWarning("Kept the tenant's last administrator enabled although HR says they left");
            return;
        }
        await memberships.SetStatusAsync(message.UserId, MembershipStatus.Disabled, ct);
        await session.ExecAsync(
            """
            insert into identity.hr_leaver (tenant_id, user_id, record_id, disabled_at) values (platform.current_tenant_id(), @u, @r, @now)
            on conflict (tenant_id, user_id) do update set record_id = excluded.record_id, disabled_at = excluded.disabled_at
            """, new { u = message.UserId, r = message.RecordId, now = clock.UtcNow }, ct);
        var revoked = await sessions.RevokeUserSessionsAsync(message.UserId, "hr_left", ct);
        await outbox.EnqueueAsync(new TenantMembershipDisabled(message.UserId), ct);
        await outbox.EnqueueAsync(new UserDeprovisioned(message.UserId, "hr"), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DisabledByHr, "user", message.UserId.ToString(),
            Changes: new { record = message.RecordId, lastWorkingDay = message.LastWorkingDay.ToString("yyyy-MM-dd"), change = message.ChangeId, sessions = revoked }), ct);
    }

    public async Task RehiredAsync(EmployeeRehired message, CancellationToken ct)
    {
        if (!message.Reactivate || message.UserId is not { } user)
        {
            return;
        }
        var closedByHr = await session.ScalarAsync<bool>("select exists (select 1 from identity.hr_leaver where user_id = @u)", new { u = user }, ct);
        if (!closedByHr || await memberships.GetAsync(user, ct) is not { Status: MembershipStatus.Disabled } || await users.IsDisabledByDirectoryAsync(user, ct))
        {
            return;
        }
        await memberships.SetStatusAsync(user, MembershipStatus.Active, ct);
        await session.ExecAsync("delete from identity.hr_leaver where user_id = @u", new { u = user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ReenabledByHr, "user", user.ToString(),
            Changes: new { record = message.RecordId, startDate = message.StartDate.ToString("yyyy-MM-dd"), change = message.ChangeId }), ct);
    }
}

public sealed class DisableOnEmployeeLeft(HrLeavers leavers) : IMessageHandler<EmployeeLeft>
{
    public Task HandleAsync(EmployeeLeft message, CancellationToken ct) => leavers.LeftAsync(message, ct);
}

public sealed class ReactivateOnEmployeeRehired(HrLeavers leavers) : IMessageHandler<EmployeeRehired>
{
    public Task HandleAsync(EmployeeRehired message, CancellationToken ct) => leavers.RehiredAsync(message, ct);
}
