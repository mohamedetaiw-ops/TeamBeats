using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity.Directory;

/// <summary>
/// The provisioning extension point (spec 003 FR-008): the user and group operations a provisioning source applies,
/// shaped after SCIM 2.0. Directory sync runs every change through it; a SCIM endpoint would call the same operations.
/// Fields the source owns become read-only in the product (FR-014). Users are never linked by email (FR-003).
/// </summary>
public sealed class Provisioning(
    IDbSession session,
    TenantContextAccessor context,
    ITenantMemberships memberships,
    IAuthorizationProvisioning authz,
    ISessionRevocation sessions,
    IAuditWriter audit,
    IOutbox outbox) : IProvisioningTarget
{
    public Task<Guid?> FindUserAsync(string issuer, string externalId, CancellationToken ct) =>
        session.ScalarAsync<Guid?>("select user_id from identity.external_identity where issuer = @i and subject = @s", new { i = issuer, s = externalId }, ct);

    public async Task<(ProvisionOutcome Outcome, Guid? UserId)> CreateOrLinkUserAsync(ProvisioningSource source, ProvisionedUser user, CancellationToken ct)
    {
        var existing = await FindUserAsync(source.Issuer, user.ExternalId, ct);
        if (existing is { } linked)
        {
            await session.ExecAsync("update identity.user_account set owner_fields = @f where id = @u", new { f = DirectorySyncService.OwnerFields, u = linked }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.UserSynced, "user", linked.ToString(), Changes: new { change = "linked", via = source.Via, connection = source.ConnectionId }), ct);
            return (ProvisionOutcome.Linked, linked);
        }
        if (user.Email is not { } email || await EmailTakenAsync(email, null, ct))
        {
            return (ProvisionOutcome.Skipped, null);
        }
        var tenantId = context.RequireTenant();
        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into identity.user_account (tenant_id, id, display_name, email, status, created_via, owner_fields)
            values (@t, @id, @name, @email, 'active', @via, @f)
            """, new { t = tenantId, id, name = user.DisplayName, email, via = source.Via, f = DirectorySyncService.OwnerFields }, ct);
        await session.ExecAsync(
            "insert into identity.external_identity (tenant_id, id, idp_config_id, issuer, subject, user_id) values (@t, @id, @idp, @i, @s, @u)",
            new { t = tenantId, id = Ids.New(), idp = source.IdpConfigId, i = source.Issuer, s = user.ExternalId, u = id }, ct);
        await memberships.CreateAsync(id, MembershipTypes.Employee, ct);
        await outbox.EnqueueAsync(new UserProvisioned(id, source.Via), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.UserSynced, "user", id.ToString(), Changes: new { change = "created", via = source.Via, connection = source.ConnectionId }), ct);
        return (ProvisionOutcome.Created, id);
    }

    public async Task<ProvisionUpdate> UpdateUserAsync(ProvisioningSource source, Guid userId, ProvisionedUser user, CancellationToken ct)
    {
        var current = await session.QuerySingleOrDefaultAsync("select display_name, email from identity.user_account where id = @u", new { u = userId },
            r => (Name: r.GetString(0), Email: r.GetString(1)), ct);
        if (current.Email is null)
        {
            // Not a user of this tenant.
            throw AppException.NotFound();
        }
        var email = user.Email ?? current.Email;
        var kept = email != current.Email && await EmailTakenAsync(email, userId, ct);
        if (kept)
        {
            email = current.Email;
        }
        var changed = new List<string>();
        if (user.DisplayName != current.Name) changed.Add("display_name");
        if (email != current.Email) changed.Add("email");
        if (changed.Count > 0)
        {
            await session.ExecAsync("update identity.user_account set display_name = @n, email = @e, owner_fields = @f where id = @u",
                new { n = user.DisplayName, e = email, f = DirectorySyncService.OwnerFields, u = userId }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.UserSynced, "user", userId.ToString(), Changes: new { change = "updated", fields = changed, connection = source.ConnectionId }), ct);
            await outbox.EnqueueAsync(new UserProfileSynced(userId, changed), ct);
        }
        return new ProvisionUpdate(changed, kept);
    }

    public async Task<bool> DeactivateUserAsync(ProvisioningSource source, Guid userId, CancellationToken ct)
    {
        await EnsureUserAsync(userId, ct);
        if (await authz.IsLastTenantAdministratorAsync(userId, ct))
        {
            // The tenant must keep an administrator.
            return false;
        }
        await memberships.SetStatusAsync(userId, MembershipStatus.Disabled, ct);
        await sessions.RevokeUserSessionsAsync(userId, source.Via + "_disabled", ct);
        await outbox.EnqueueAsync(new TenantMembershipDisabled(userId), ct);
        await outbox.EnqueueAsync(new UserDeprovisioned(userId, source.Via), ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.DisabledByDirectory, "user", userId.ToString(), Changes: new { via = source.Via, connection = source.ConnectionId }), ct);
        return true;
    }

    public async Task ReactivateUserAsync(ProvisioningSource source, Guid userId, CancellationToken ct)
    {
        await EnsureUserAsync(userId, ct);
        await memberships.SetStatusAsync(userId, MembershipStatus.Active, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ReenabledByDirectory, "user", userId.ToString(), Changes: new { via = source.Via, connection = source.ConnectionId }), ct);
    }

    public Task<Guid> SetGroupMembersAsync(string groupKey, string displayName, IReadOnlyList<Guid> members, CancellationToken ct) =>
        authz.SyncDirectoryGroupAsync(groupKey, displayName, members, ct);

    /// <summary>Only users of the tenant in context; another tenant's user is not found.</summary>
    private async Task EnsureUserAsync(Guid userId, CancellationToken ct)
    {
        if (!await session.ScalarAsync<bool>("select exists (select 1 from identity.user_account where id = @u)", new { u = userId }, ct))
        {
            throw AppException.NotFound();
        }
    }

    private Task<bool> EmailTakenAsync(string email, Guid? except, CancellationToken ct) =>
        session.ScalarAsync<bool>("select exists (select 1 from identity.user_account where lower(email) = @e and (@x::uuid is null or id <> @x))",
            new { e = email, x = except }, ct);
}
