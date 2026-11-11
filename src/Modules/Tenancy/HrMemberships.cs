using Microsoft.Extensions.Logging;
using TeamBeats.Messaging;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Tenancy;

/// <summary>
/// Movers keep the org right (spec 019 US3 scenario 1, spec 002): on the effective date the person's main membership
/// moves to the unit HR's org code is mapped to. The previous main membership ends when TeamBeats or HR made it; one
/// that directory mapping owns stays (as a secondary) for directory mapping to manage. History is kept.
/// </summary>
public sealed class HrMemberships(IDbSession session, OrgMembershipService memberships, ILogger<HrMemberships> logger)
{
    public async Task TransferAsync(EmployeeTransferred message, CancellationToken ct)
    {
        var activeMember = await session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.tenant_membership where user_id = @u and status = 'active')", new { u = message.UserId }, ct);
        var unitActive = await session.ScalarAsync<bool>(
            "select exists (select 1 from tenancy.org_unit where id = @unit and status = 'active')", new { unit = message.ToUnitId }, ct);
        if (!activeMember || !unitActive)
        {
            logger.LogWarning("HR move {Change} not applied to the organization: the person or the unit is not active", message.ChangeId);
            return;
        }
        var current = await session.QueryAsync(
            "select id, org_unit_id, is_primary, owner from tenancy.org_membership where user_id = @u and valid_to is null",
            new { u = message.UserId }, r => (Id: r.GetGuid(0), Unit: r.GetGuid(1), Primary: r.GetBoolean(2), Owner: r.GetString(3)), ct);
        if (current.Any(m => m.Primary && m.Unit == message.ToUnitId))
        {
            return;
        }
        foreach (var m in current.Where(m => m.Unit == message.ToUnitId || (m.Primary && m.Owner != OrgOwners.Directory)))
        {
            await memberships.EndFromDirectoryAsync(m.Id, ct);
        }
        await memberships.AddCoreAsync(message.ToUnitId, message.UserId, "primary", null, null, ct, OrgOwners.Hr);
    }
}

public sealed class MoveMembershipOnTransfer(HrMemberships memberships) : IMessageHandler<EmployeeTransferred>
{
    public Task HandleAsync(EmployeeTransferred message, CancellationToken ct) => memberships.TransferAsync(message, ct);
}
