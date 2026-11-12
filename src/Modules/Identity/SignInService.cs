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
/// What the identity provider configuration allows for one sign-in (spec 003 US2): just-in-time provisioning for
/// allowed email domains, and group-to-role mapping. <see cref="Groups"/> is null when the token did not carry the
/// full group list (Entra ID group overage), in which case mapped roles are left unchanged.
/// </summary>
public sealed record ProviderSignInPolicy(
    Guid IdpConfigId,
    bool JitEnabled,
    IReadOnlyList<string> JitDomains,
    IReadOnlyList<string>? Groups,
    IReadOnlyList<Sso.GroupRoleMapping> GroupRoleMap);

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
    IOutbox outbox,
    Sso.GroupRoleSync groupRoles,
    IEnumerable<IGuestInvitationSource> guestSources)
{
    public async Task<SignInResult> SignInAsync(TenantInfo tenant, string issuer, string subject, string email, string displayName,
        string method, string createdVia, CancellationToken ct, ProviderSignInPolicy? policy = null)
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
            var pending = normalizedEmail.Length > 0 && await memberships.HasPendingAdminAppointmentAsync(normalizedEmail, ct);
            var jit = !pending && policy is { JitEnabled: true } && AllowedDomain(normalizedEmail, policy.JitDomains);
            // Someone invited to a space by email arrives as a guest (spec 026 FR-009); a member rule always wins.
            var guest = false;
            if (!pending && !jit && normalizedEmail.Length > 0)
            {
                foreach (var source in guestSources)
                {
                    if (await source.HasPendingAsync(normalizedEmail, ct))
                    {
                        guest = true;
                        break;
                    }
                }
            }
            if (!pending && !jit && !guest)
            {
                await RefuseAsync(policy is { JitEnabled: true } ? "jit_domain_not_allowed" : "unknown_identity", issuer, subjectHash, ct);
                return new SignInResult(SignInOutcome.RefusedNoAccess, null, null);
            }

            // Identity is never linked by email (FR-003): an existing account with this email and another identity is refused.
            var emailTaken = await session.ScalarAsync<bool>(
                "select exists (select 1 from identity.user_account where lower(email) = @e)", new { e = normalizedEmail }, ct);
            if (emailTaken)
            {
                await RefuseAsync("email_in_use", issuer, subjectHash, ct);
                return new SignInResult(SignInOutcome.RefusedNoAccess, null, null);
            }

            if (jit)
            {
                createdVia = "jit";
            }

            userId = Ids.New();
            await session.ExecAsync(
                """
                insert into identity.user_account (tenant_id, id, display_name, email, status, created_via)
                values (@t, @id, @name, @email, 'active', @via)
                """, new { t = tenant.Id, id = userId, name = displayName, email = normalizedEmail, via = createdVia }, ct);
            await session.ExecAsync(
                "insert into identity.external_identity (tenant_id, id, idp_config_id, issuer, subject, user_id) values (@t, @id, @idp, @i, @s, @u)",
                new { t = tenant.Id, id = Ids.New(), idp = policy?.IdpConfigId, i = issuer, s = subject, u = userId }, ct);
            await memberships.CreateAsync(userId.Value, guest ? MembershipTypes.Guest : MembershipTypes.Employee, ct);
            context.SetActor(ActorType.System, null);
            if (guest)
            {
                foreach (var source in guestSources)
                {
                    await source.ClaimAsync(normalizedEmail, userId.Value, ct);
                }
            }
            else
            {
                await memberships.ClaimAdminAppointmentsAsync(normalizedEmail, userId.Value, ct);
            }
            await outbox.EnqueueAsync(new UserProvisioned(userId.Value, createdVia), ct);
        }

        var status = await session.ScalarAsync<string?>("select status from identity.user_account where id = @u and not test_identity", new { u = userId }, ct);
        var membership = await memberships.GetAsync(userId.Value, ct);
        if (status != "active" || membership is null || membership.Status != MembershipStatus.Active)
        {
            await RefuseAsync("no_access", issuer, subjectHash, ct, userId);
            return new SignInResult(SignInOutcome.RefusedNoAccess, null, null);
        }

        if (policy is { Groups: { } groups })
        {
            context.SetActor(ActorType.System, null);
            await groupRoles.ApplyAsync(userId.Value, groups, policy.GroupRoleMap, ct);
        }

        context.SetActor(ActorType.User, userId, null);
        var token = await sessions.CreateUserSessionAsync(userId.Value, method, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SignInSucceeded, "user", userId.ToString(),
            Changes: new { method, issuer, subjectHash }), ct);
        await outbox.EnqueueAsync(new UserSignedIn(userId.Value, method), ct);
        return new SignInResult(SignInOutcome.Succeeded, userId, token);
    }

    private static bool AllowedDomain(string email, IReadOnlyList<string> domains)
    {
        var at = email.LastIndexOf('@');
        return at > 0 && at < email.Length - 1 && domains.Contains(email[(at + 1)..], StringComparer.Ordinal);
    }

    private async Task RefuseAsync(string reason, string issuer, string subjectHash, CancellationToken ct, Guid? userId = null)
    {
        context.SetActor(ActorType.Anonymous, null);
        await audit.WriteAsync(new AuditEntry(AuditActions.SignInRefused, "user", userId?.ToString(), AuditOutcome.Denied,
            Changes: new { reason, issuer, subjectHash }), ct);
    }
}
