using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Identity;

public enum SignInOutcome
{
    Succeeded,
    RefusedTenantInactive,
    RefusedNoAccess,
}

public sealed record SignInResult(SignInOutcome Outcome, Guid? UserId, string? SessionToken);

/// <summary>
/// Maps an authenticated external identity (issuer, subject) to a tenant user and membership, then creates a session.
/// Used by development sign-in in R0 and by the Entra ID flow in R1. Identity is never matched by email (FR-003).
/// </summary>
public sealed class SignInService(
    IDbSession session,
    TenantContextAccessor context,
    ITenantMemberships memberships,
    SessionService sessions,
    IAuditWriter audit,
    IOutbox outbox)
{
    public async Task<SignInResult> SignInAsync(TenantInfo tenant, string issuer, string subject, string email, string displayName,
        string method, string createdVia, CancellationToken ct)
    {
        context.SetTenant(tenant.Id);
        var subjectHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "|" + subject)))[..16];

        if (tenant.Status != TenantLifecycleStatus.Active)
        {
            await RefuseAsync("tenant_inactive", issuer, subjectHash, ct);
            return new SignInResult(SignInOutcome.RefusedTenantInactive, null, null);
        }

        var userId = await session.ScalarAsync<Guid?>(
            "select user_id from identity.external_identity where issuer = @i and subject = @s", new { i = issuer, s = subject }, ct);

        if (userId is null)
        {
            // First sign-in of an appointed administrator: provision the user and activate the appointment (spec 001 edge case).
            var normalizedEmail = email.Trim().ToLowerInvariant();
            var pending = await memberships.HasPendingAdminAppointmentAsync(normalizedEmail, ct);
            if (!pending)
            {
                await RefuseAsync("unknown_identity", issuer, subjectHash, ct);
                return new SignInResult(SignInOutcome.RefusedNoAccess, null, null);
            }

            userId = Ids.New();
            await session.ExecAsync(
                """
                insert into identity.user_account (tenant_id, id, display_name, email, status, created_via)
                values (@t, @id, @name, @email, 'active', @via)
                """, new { t = tenant.Id, id = userId, name = displayName, email = normalizedEmail, via = createdVia }, ct);
            await session.ExecAsync(
                "insert into identity.external_identity (tenant_id, id, issuer, subject, user_id) values (@t, @id, @i, @s, @u)",
                new { t = tenant.Id, id = Ids.New(), i = issuer, s = subject, u = userId }, ct);
            await memberships.CreateAsync(userId.Value, MembershipTypes.Employee, ct);
            context.SetActor(ActorType.System, null);
            await memberships.ClaimAdminAppointmentsAsync(normalizedEmail, userId.Value, ct);
            await outbox.EnqueueAsync(new UserProvisioned(userId.Value, createdVia), ct);
        }

        var status = await session.ScalarAsync<string?>("select status from identity.user_account where id = @u", new { u = userId }, ct);
        var membership = await memberships.GetAsync(userId.Value, ct);
        if (status != "active" || membership is null || membership.Status != MembershipStatus.Active)
        {
            await RefuseAsync("no_access", issuer, subjectHash, ct, userId);
            return new SignInResult(SignInOutcome.RefusedNoAccess, null, null);
        }

        context.SetActor(ActorType.User, userId, null);
        var token = await sessions.CreateUserSessionAsync(userId.Value, method, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SignInSucceeded, "user", userId.ToString(),
            Changes: new { method, issuer, subjectHash }), ct);
        await outbox.EnqueueAsync(new UserSignedIn(userId.Value, method), ct);
        return new SignInResult(SignInOutcome.Succeeded, userId, token);
    }

    private async Task RefuseAsync(string reason, string issuer, string subjectHash, CancellationToken ct, Guid? userId = null)
    {
        context.SetActor(ActorType.Anonymous, null);
        await audit.WriteAsync(new AuditEntry(AuditActions.SignInRefused, "user", userId?.ToString(), AuditOutcome.Denied,
            Changes: new { reason, issuer, subjectHash }), ct);
    }
}
