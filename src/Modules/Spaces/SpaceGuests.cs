using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Spaces;

public sealed record GuestInviteRequest(string? Email, string? Role, int? Days);

public sealed record GuestRenewRequest(int? Days);

public sealed record GuestFilesRequest(bool? Confidential);

/// <summary>A space the caller is a guest of, for the guest home (spec 026 section 10): who invited them and until when.</summary>
public sealed record GuestSpaceView(Guid SpaceId, string Name, string Role, string SponsorName, DateTimeOffset? ExpiresAt);

public sealed record GuestView(Guid UserId, string DisplayName, string Role, DateTimeOffset? ExpiresAt);

/// <summary>An invitation waiting for the guest's first sign-in. EmailState is sent, retry, bounced or failed.</summary>
public sealed record GuestInvitationView(Guid Id, string Email, string Role, DateTimeOffset ExpiresAt, string EmailState, DateTimeOffset InvitedAt);

/// <summary>
/// The guests of a space for its owners. Allowed is false while the plan or the tenant's rule keeps guests out;
/// MaxDays is the longest access an owner can give. Confidential says whether guests open Confidential files here,
/// which needs ConfidentialAllowed, the tenant's rule.
/// </summary>
public sealed record SpaceGuestsView(
    bool Allowed,
    bool CanInvite,
    int MaxDays,
    int Limit,
    bool Confidential,
    bool ConfidentialAllowed,
    IReadOnlyList<GuestView> Guests,
    IReadOnlyList<GuestInvitationView> Invitations);

/// <summary>
/// Guests of a space (spec 029 US4, FR-007, FR-008; spec 026 FR-009). Owners invite partners by email while the plan has
/// <c>spaces.guests</c> and the tenant's rule allows guests. Someone already a guest of the tenant is added at once;
/// anyone else gets an invitation email and becomes a guest at their first sign-in through the tenant's provider. Each
/// guest's access ends on its own date in each space: owners are told seven days before, and when it ends the guest
/// leaves the space; a guest left in no space is disabled and signed out. People of the tenant's own email domains are
/// members, never guests.
/// </summary>
public sealed class SpaceGuests(
    IDbSession session,
    ITenantContext context,
    IAuthorizationProvisioning grants,
    IEntitlementService entitlements,
    ITenantMemberships memberships,
    IUserDirectory users,
    ISignInMethods signInMethods,
    ISessionRevocation sessions,
    SpacesService spaces,
    SpaceSettings settings,
    ISpaceLibraries libraries,
    INotificationSender notifications,
    IExternalEmail email,
    ISecurityPolicy securityPolicy,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock) : IGuestInvitationSource
{
    public const int MaxGuests = 50;
    public const int NoticeDays = 7;
    public const int MaxEmailAttempts = 5;
    public const string InvitedTemplate = "spaces.guest.invited";
    public const string ExpiringTemplate = "spaces.guest.expiring";
    public const string ExpiredTemplate = "spaces.guest.expired";

    private async Task<bool> AllowedAsync(CancellationToken ct) =>
        await entitlements.IsEnabledAsync(Capabilities.SpacesGuests, ct) && (await settings.CurrentAsync(ct)).GuestsAllowed;

    private Task<int> CountAsync(Guid spaceId, CancellationToken ct) =>
        session.ScalarAsync<int>(
            """
            select ((select count(*) from spaces.space_member where space_id = @spaceId and guest)
              + (select count(*) from spaces.guest_invitation where space_id = @spaceId and status = 'pending'))::int
            """, new { spaceId }, ct);

    private static string Day(DateTimeOffset at) => at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<SpaceGuestsView> ListAsync(Guid id, CancellationToken ct)
    {
        var standing = await spaces.OwnedAsync(id, false, ct);
        var rules = await settings.CurrentAsync(ct);
        var allowed = await AllowedAsync(ct);
        var rows = await session.QueryAsync(
            "select user_id, role, expires_at from spaces.space_member where space_id = @id and guest order by expires_at nulls last, added_at",
            new { id }, r => (User: r.GetGuid(0), Role: r.GetString(1), Expires: r.Get<DateTimeOffset?>("expires_at")), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.User).ToList(), ct);
        var invitations = await session.QueryAsync(
            "select id, email, role, expires_at, email_state, invited_at from spaces.guest_invitation where space_id = @id and status = 'pending' order by invited_at",
            new { id }, r => new GuestInvitationView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.GetString(4),
                r.GetFieldValue<DateTimeOffset>(5)), ct);
        var confidential = await session.ScalarAsync<bool>("select guest_confidential from spaces.space where id = @id", new { id }, ct);
        return new SpaceGuestsView(allowed, allowed && standing.Space.Active && rows.Count + invitations.Count < MaxGuests, rules.GuestDays, MaxGuests,
            confidential && rules.GuestConfidential, rules.GuestConfidential,
            rows.Select(r => new GuestView(r.User, names.TryGetValue(r.User, out var u) ? u.DisplayName : "", r.Role, r.Expires)).ToList(), invitations);
    }

    /// <summary>
    /// The spaces the caller is a guest of, soonest end first, with the person who invited them (the sponsor). Empty for
    /// members of the tenant, who use the space directory.
    /// </summary>
    public async Task<IReadOnlyList<GuestSpaceView>> MineAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            """
            select s.id, s.name, m.role, m.added_by, m.expires_at from spaces.space_member m join spaces.space s on s.id = m.space_id
            where m.user_id = @user and m.guest and s.status = 'active' and (m.expires_at is null or m.expires_at > @now)
            order by m.expires_at nulls last, s.name
            """, new { user, now = clock.UtcNow },
            r => (Id: r.GetGuid(0), Name: r.GetString(1), Role: r.GetString(2), By: r.Get<Guid?>("added_by"), Expires: r.Get<DateTimeOffset?>("expires_at")), ct);
        var names = await users.GetManyAsync(rows.Where(r => r.By is not null).Select(r => r.By!.Value).ToList(), ct);
        return rows.Select(r => new GuestSpaceView(r.Id, r.Name, r.Role, r.By is { } by && names.TryGetValue(by, out var u) ? u.DisplayName : "", r.Expires)).ToList();
    }

    /// <summary>Invites a partner by email (US4 scenario 1). Read-only by default (spec 026 US3 scenario 3); member lets them add and comment.</summary>
    public async Task<SpaceGuestsView> InviteAsync(Guid id, GuestInviteRequest request, CancellationToken ct)
    {
        var standing = await spaces.OwnedAsync(id, false, ct);
        await entitlements.DemandAsync(Capabilities.SpacesGuests, ct);
        var rules = await settings.CurrentAsync(ct);
        if (!rules.GuestsAllowed)
        {
            throw AppException.Conflict("conflict.spaces.guests_off");
        }
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        var errors = new ValidationErrors();
        var address = (request.Email ?? "").Trim().ToLowerInvariant();
        var at = address.IndexOf('@');
        if (address.Length is <= 3 or > 254 || at <= 0 || at == address.Length - 1 || address.Contains(' ') || address.IndexOf('@', at + 1) >= 0)
        {
            errors.Add("email", "validation.email");
        }
        else if ((await signInMethods.CurrentAsync(ct)).MemberDomains is { } domains && domains.Contains(address[(at + 1)..], StringComparer.OrdinalIgnoreCase))
        {
            errors.Add("email", "validation.spaces.guest_member_domain");
        }
        else if ((await securityPolicy.GetAsync(ct)).GuestDomainRefusal(address[(at + 1)..]) is { } refusal)
        {
            // Spec 026 section 14: the tenant's guest domain rules (blocked domains, and allowed domains when set).
            errors.Add("email", refusal);
        }
        var role = request.Role ?? SpaceRoles.Viewer;
        if (role is not (SpaceRoles.Member or SpaceRoles.Viewer))
        {
            errors.Add("role", "validation.spaces.guest_role");
        }
        var days = request.Days ?? rules.GuestDays;
        if (days < 1 || days > rules.GuestDays)
        {
            errors.Add("days", "validation.spaces.guest_access_days");
        }
        errors.ThrowIfAny();
        await session.ExecAsync("select id from spaces.space where id = @id for update", new { id }, ct);
        var count = await CountAsync(id, ct);
        if (count >= MaxGuests)
        {
            throw AppException.Validation("email", "validation.spaces.guest_count");
        }
        if (count == 0)
        {
            await DemandGuestSpaceAsync(ct);
        }
        var now = clock.UtcNow;
        var expires = now.AddDays(days);
        var by = context.RequireUser();

        if (await users.FindByEmailAsync(address, ct) is { } existing)
        {
            // One identity, a membership per space (spec 026 edge case). A member of the tenant joins as a member instead.
            var membership = await memberships.GetAsync(existing.Id, ct);
            if (membership is null || membership.MembershipType != MembershipTypes.Guest)
            {
                throw AppException.Validation("email", "validation.spaces.guest_is_member");
            }
            if (await session.ScalarAsync<bool>("select exists (select 1 from spaces.space_member where space_id = @id and user_id = @u)", new { id, u = existing.Id }, ct))
            {
                throw AppException.Conflict("conflict.spaces.guest_exists");
            }
            if (membership.Status != MembershipStatus.Active)
            {
                await memberships.SetStatusAsync(existing.Id, MembershipStatus.Active, ct);
            }
            await AddGuestAsync(id, existing.Id, role, by, expires, now, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesGuestInvited, SpacesService.ResourceType, id.ToString(),
                Changes: new { guest = existing.Id, role, expiresAt = expires, existing = true }), ct);
            await notifications.SendAsync(new NotificationRequest([existing.Id], NotificationCategories.SpaceActivity, SpacesService.AddedTemplate,
                SpacesService.Link(id), new Dictionary<string, string> { ["space"] = standing.Space.Name }, DedupeKey: $"spaces:{id:N}:added"), ct);
            return await ListAsync(id, ct);
        }

        if (await session.ScalarAsync<bool>("select exists (select 1 from spaces.guest_invitation where space_id = @id and email = @address and status = 'pending')",
            new { id, address }, ct))
        {
            throw AppException.Conflict("conflict.spaces.guest_invited");
        }
        var invitation = Ids.New();
        var sent = await SendAsync(address, standing.Space, expires, ct);
        await session.ExecAsync(
            """
            insert into spaces.guest_invitation (tenant_id, id, space_id, email, role, status, expires_at, email_state, email_attempts, invited_by, invited_at)
            values (platform.current_tenant_id(), @invitation, @id, @address, @role, 'pending', @expires, @state, 1, @by, @now)
            """, new { invitation, id, address, role, expires, state = State(sent, 1), by, now }, ct);
        // The audit names the invitation, never the address.
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesGuestInvited, SpacesService.ResourceType, id.ToString(),
            Changes: new { invitation, role, expiresAt = expires, email = sent }), ct);
        return await ListAsync(id, ct);
    }

    /// <summary>
    /// Spec 026 section 9: <c>security.max_guest_spaces</c> limits how many spaces have guests or open invitations. A
    /// space's first invitation counts it; the check is serialized per tenant.
    /// </summary>
    private async Task DemandGuestSpaceAsync(CancellationToken ct)
    {
        if (await entitlements.GetLimitAsync(Capabilities.SecurityMaxGuestSpaces, ct) is not { } max)
        {
            return;
        }
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':spaces.guest_spaces', 0))", null, ct);
        var used = await session.ScalarAsync<long>(
            """
            select count(*) from spaces.space s where s.status = 'active' and (
              exists (select 1 from spaces.space_member m where m.space_id = s.id and m.guest)
              or exists (select 1 from spaces.guest_invitation i where i.space_id = s.id and i.status = 'pending'))
            """, null, ct);
        if (used >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.spaces.guest_space_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.SecurityMaxGuestSpaces, ["limit"] = max });
        }
    }

    private static string State(string outcome, int attempts) => outcome switch
    {
        ExternalEmailOutcomes.Sent => "sent",
        ExternalEmailOutcomes.Bounced => "bounced",
        _ => attempts >= MaxEmailAttempts ? "failed" : "retry",
    };

    private Task<string> SendAsync(string address, SpaceRow space, DateTimeOffset expires, CancellationToken ct) =>
        email.SendAsync(new ExternalEmailRequest(address, InvitedTemplate, SpacesService.Link(space.Id),
            new Dictionary<string, string> { ["space"] = space.Name, ["until"] = Day(expires) }), ct);

    private async Task AddGuestAsync(Guid spaceId, Guid userId, string role, Guid by, DateTimeOffset expires, DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync(
            """
            insert into spaces.space_member (tenant_id, space_id, user_id, role, guest, expires_at, added_by, added_at)
            values (platform.current_tenant_id(), @spaceId, @userId, @role, true, @expires, @by, @now)
            """, new { spaceId, userId, role, expires, by, now }, ct);
        await grants.SetResourceRoleAsync(userId, SpacesService.ResourceType, spaceId, ResourceRoles.Space(role), by, ct);
        await outbox.EnqueueAsync(new SpaceMembershipChanged(spaceId, userId, role), ct);
    }

    public async Task<SpaceGuestsView> CancelInvitationAsync(Guid id, Guid invitationId, CancellationToken ct)
    {
        await spaces.OwnedAsync(id, false, ct);
        var ended = await session.ExecAsync(
            "update spaces.guest_invitation set status = 'revoked', ended_at = @now where id = @invitationId and space_id = @id and status = 'pending'",
            new { id, invitationId, now = clock.UtcNow }, ct);
        if (ended == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesMemberRemoved, SpacesService.ResourceType, id.ToString(),
            Changes: new { invitation = invitationId, cancelled = true }), ct);
        return await ListAsync(id, ct);
    }

    /// <summary>Gives a guest more time; renewal needs an owner to act (spec 026 US3 scenario 4).</summary>
    public async Task<SpaceGuestsView> RenewAsync(Guid id, Guid userId, GuestRenewRequest request, CancellationToken ct)
    {
        var standing = await spaces.OwnedAsync(id, false, ct);
        await entitlements.DemandAsync(Capabilities.SpacesGuests, ct);
        var rules = await settings.CurrentAsync(ct);
        if (!rules.GuestsAllowed)
        {
            throw AppException.Conflict("conflict.spaces.guests_off");
        }
        if (!standing.Space.Active)
        {
            throw AppException.Conflict("conflict.spaces.archived");
        }
        var days = request.Days ?? rules.GuestDays;
        if (days < 1 || days > rules.GuestDays)
        {
            throw AppException.Validation("days", "validation.spaces.guest_access_days");
        }
        var expires = clock.UtcNow.AddDays(days);
        var updated = await session.ExecAsync(
            "update spaces.space_member set expires_at = @expires, expiry_notice_at = null where space_id = @id and user_id = @userId and guest",
            new { id, userId, expires }, ct);
        if (updated == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesGuestInvited, SpacesService.ResourceType, id.ToString(),
            Changes: new { guest = userId, expiresAt = expires, renewed = true }), ct);
        return await ListAsync(id, ct);
    }

    /// <summary>Lets guests open Confidential files in the space's library, when the tenant's rule allows it (FR-008).</summary>
    public async Task<SpaceGuestsView> SetFilesAsync(Guid id, GuestFilesRequest request, CancellationToken ct)
    {
        var standing = await spaces.OwnedAsync(id, false, ct);
        var rules = await settings.CurrentAsync(ct);
        var allow = request.Confidential ?? false;
        if (allow && !rules.GuestConfidential)
        {
            throw AppException.Validation("confidential", "validation.spaces.guest_confidential_rule");
        }
        await session.ExecAsync("update spaces.space set guest_confidential = @allow, updated_at = @now where id = @id", new { id, allow, now = clock.UtcNow }, ct);
        if (standing.Space.LibraryId is { } library)
        {
            await libraries.SetGuestConfidentialAsync(library, allow && rules.GuestConfidential, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.SpacesChanged, SpacesService.ResourceType, id.ToString(), Changes: new { guestConfidential = allow }), ct);
        return await ListAsync(id, ct);
    }

    /// <summary>Follows a change of the tenant's rule into every space library whose owner allowed Confidential files.</summary>
    internal async Task SyncFilesAsync(bool tenantAllows, CancellationToken ct)
    {
        var libraryIds = await session.QueryAsync("select library_id from spaces.space where guest_confidential and library_id is not null", null, r => r.GetGuid(0), ct);
        foreach (var library in libraryIds)
        {
            await libraries.SetGuestConfidentialAsync(library, tenantAllows, ct);
        }
    }

    // ---- First sign-in (IGuestInvitationSource) ----

    public async Task<bool> HasPendingAsync(string address, CancellationToken ct = default) =>
        await AllowedAsync(ct) && await session.ScalarAsync<bool>(
            "select exists (select 1 from spaces.guest_invitation where email = @address and status = 'pending' and expires_at > @now)",
            new { address = address.Trim().ToLowerInvariant(), now = clock.UtcNow }, ct);

    public async Task<int> ClaimAsync(string address, Guid userId, CancellationToken ct = default)
    {
        if (!await AllowedAsync(ct))
        {
            return 0;
        }
        var now = clock.UtcNow;
        var pending = await session.QueryAsync(
            "select id, space_id, role, expires_at, invited_by from spaces.guest_invitation where email = @address and status = 'pending' and expires_at > @now for update",
            new { address = address.Trim().ToLowerInvariant(), now },
            r => (Id: r.GetGuid(0), Space: r.GetGuid(1), Role: r.GetString(2), Expires: r.GetFieldValue<DateTimeOffset>(3), By: r.GetGuid(4)), ct);
        foreach (var invitation in pending)
        {
            await AddGuestAsync(invitation.Space, userId, invitation.Role, invitation.By, invitation.Expires, now, ct);
            await session.ExecAsync("update spaces.guest_invitation set status = 'accepted', user_id = @userId, ended_at = @now where id = @id",
                new { id = invitation.Id, userId, now }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesMemberAdded, SpacesService.ResourceType, invitation.Space.ToString(),
                Changes: new { member = userId, role = invitation.Role, guest = true, invitation = invitation.Id }), ct);
        }
        return pending.Count;
    }

    // ---- Background ----

    /// <summary>Tells owners about guests whose access ends within seven days, once per end date (US4 scenario 4).</summary>
    public async Task<int> RemindExpiringAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select m.space_id, m.user_id, m.expires_at, s.name from spaces.space_member m join spaces.space s on s.id = m.space_id
            where m.guest and m.expiry_notice_at is null and m.expires_at > @now and m.expires_at <= @until and s.status = 'active'
            order by m.expires_at limit @limit
            """, new { now, until = now.AddDays(NoticeDays), limit },
            r => (Space: r.GetGuid(0), User: r.GetGuid(1), Expires: r.GetFieldValue<DateTimeOffset>(2), Name: r.GetString(3)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.User).Distinct().ToList(), ct);
        foreach (var row in rows)
        {
            await TellOwnersAsync(row.Space, row.Name, ExpiringTemplate, names.TryGetValue(row.User, out var u) ? u.DisplayName : "", row.Expires,
                $"spaces:{row.Space:N}:guest:{row.User:N}:expiring:{Day(row.Expires)}", ct);
            await session.ExecAsync("update spaces.space_member set expiry_notice_at = @now where space_id = @space and user_id = @user",
                new { now, space = row.Space, user = row.User }, ct);
        }
        return rows.Count;
    }

    /// <summary>
    /// Ends access that has run out: the guest leaves the space and its owners are told; a guest left in no space is
    /// disabled and signed out (spec 026 US3 scenario 4). Invitations nobody took up in time lapse.
    /// </summary>
    public async Task<int> ExpireAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        await session.ExecAsync("update spaces.guest_invitation set status = 'expired', ended_at = @now where status = 'pending' and expires_at <= @now", new { now }, ct);
        var rows = await session.QueryAsync(
            """
            select m.space_id, m.user_id, m.role, s.name from spaces.space_member m join spaces.space s on s.id = m.space_id
            where m.guest and m.expires_at <= @now order by m.expires_at limit @limit
            """, new { now, limit }, r => (Space: r.GetGuid(0), User: r.GetGuid(1), Role: r.GetString(2), Name: r.GetString(3)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.User).Distinct().ToList(), ct);
        foreach (var row in rows)
        {
            await session.ExecAsync("delete from spaces.space_member where space_id = @space and user_id = @user", new { space = row.Space, user = row.User }, ct);
            await grants.SetResourceRoleAsync(row.User, SpacesService.ResourceType, row.Space, null, null, ct);
            await outbox.EnqueueAsync(new SpaceMembershipChanged(row.Space, row.User, null), ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SpacesGuestExpired, SpacesService.ResourceType, row.Space.ToString(),
                Changes: new { guest = row.User, role = row.Role }), ct);
            await TellOwnersAsync(row.Space, row.Name, ExpiredTemplate, names.TryGetValue(row.User, out var u) ? u.DisplayName : "", null,
                $"spaces:{row.Space:N}:guest:{row.User:N}:expired", ct);
        }
        foreach (var user in rows.Select(r => r.User).Distinct())
        {
            if (await session.ScalarAsync<bool>("select exists (select 1 from spaces.space_member where user_id = @user)", new { user }, ct)
                || await memberships.GetAsync(user, ct) is not { MembershipType: MembershipTypes.Guest, Status: MembershipStatus.Active })
            {
                continue;
            }
            await memberships.SetStatusAsync(user, MembershipStatus.Disabled, ct);
            await sessions.RevokeUserSessionsAsync(user, "guest_expired", ct);
            await outbox.EnqueueAsync(new TenantMembershipDisabled(user), ct);
        }
        return rows.Count;
    }

    /// <summary>Tries again the invitation emails the relay could not take, up to five times.</summary>
    public async Task<int> RetryEmailsAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select i.id, i.email, i.expires_at, i.email_attempts, i.space_id
            from spaces.guest_invitation i join spaces.space s on s.id = i.space_id
            where i.status = 'pending' and i.email_state = 'retry' and i.expires_at > @now and s.status = 'active' order by i.invited_at limit @limit
            """, new { now, limit },
            r => (Id: r.GetGuid(0), Email: r.GetString(1), Expires: r.GetFieldValue<DateTimeOffset>(2), Attempts: r.GetInt32(3), Space: r.GetGuid(4)), ct);
        foreach (var row in rows)
        {
            var attempts = row.Attempts + 1;
            var outcome = await SendAsync(row.Email, (await spaces.RowAsync(row.Space, ct))!, row.Expires, ct);
            await session.ExecAsync("update spaces.guest_invitation set email_state = @state, email_attempts = @attempts where id = @id",
                new { id = row.Id, state = State(outcome, attempts), attempts }, ct);
        }
        return rows.Count;
    }

    private async Task TellOwnersAsync(Guid spaceId, string space, string template, string guest, DateTimeOffset? until, string dedupe, CancellationToken ct)
    {
        var owners = await session.QueryAsync("select user_id from spaces.space_member where space_id = @spaceId and role = 'owner'", new { spaceId }, r => r.GetGuid(0), ct);
        if (owners.Count == 0)
        {
            return;
        }
        var parameters = new Dictionary<string, string> { ["space"] = space, ["guest"] = guest };
        if (until is { } u)
        {
            parameters["until"] = Day(u);
        }
        await notifications.SendAsync(new NotificationRequest(owners, NotificationCategories.SpaceActivity, template, $"/spaces/{spaceId}/members",
            parameters, DedupeKey: dedupe), ct);
    }
}
