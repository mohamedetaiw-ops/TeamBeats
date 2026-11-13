using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

public static class AccessReviewKinds
{
    public const string Guest = "guest";
    public const string AdminRole = "admin_role";
}

public static class AccessReviewDecisions
{
    public const string Confirmed = "confirmed";
    public const string Removed = "removed";
    /// <summary>Not decided within 14 days, so removed.</summary>
    public const string Expired = "expired";
    /// <summary>Not decided, but kept because removing it would leave the tenant without an administrator.</summary>
    public const string Kept = "kept";
}

/// <summary>One thing a reviewer confirms or removes: a guest in a space, or an administrator's role assignment.</summary>
public sealed record AccessReviewItemView(
    Guid Id, string Kind, Guid SubjectId, string SubjectName, Guid? SpaceId, string? SpaceName, string? RoleKey, string? ScopeType, Guid? ScopeId,
    string? Decision, DateTimeOffset DueAt);

public sealed record MyAccessReviewsView(IReadOnlyList<AccessReviewItemView> Items);

public sealed record AccessReviewSummary(
    Guid Id, DateTimeOffset OpenedAt, DateTimeOffset DueAt, DateTimeOffset? ClosedAt, int Open, int Confirmed, int Removed, int Expired, int Kept);

public sealed record AccessReviewsPage(IReadOnlyList<AccessReviewSummary> Reviews, DateTimeOffset? NextAt, bool CanStart);

/// <summary>
/// Access reviews (spec 026 FR-012, US3 scenario 5). Every 90 days a review opens: each guest's sponsor confirms or
/// removes the guest in each space, and each administrator's manager confirms or removes each administrator role
/// assignment. Nobody reviews their own access; when the sponsor or manager is gone or is the person reviewed, the
/// security administrators review instead. Items not confirmed within 14 days are removed, except the last tenant
/// administrator assignment, which is kept and recorded. Security administrators can open a review at any time.
/// </summary>
public sealed class AccessReviews(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning roles,
    ISpaceGuestReview guests,
    IReportingLine reporting,
    ITenantMemberships memberships,
    IUserDirectory users,
    INotificationSender notifications,
    IAuditWriter audit,
    IClock clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(90);
    public static readonly TimeSpan DecideWithin = TimeSpan.FromDays(14);
    public const string RequestedTemplate = "security.access_review.requested";
    public const string Link = "/access-reviews";
    public const int MaxListed = 20;

    private async Task<bool> CanAsync(string permission, CancellationToken ct) => (await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed;

    private async Task<DateTimeOffset?> NextAtAsync(CancellationToken ct) =>
        (await session.QueryAsync("select next_at from security.access_review_schedule", null, r => r.GetFieldValue<DateTimeOffset>(0), ct)) is [var next] ? next : null;

    // Reviews for administrators

    public async Task<AccessReviewsPage> ListAsync(CancellationToken ct)
    {
        if (!await CanAsync(Permissions.SecurityManage, ct) && !await CanAsync(Permissions.SecurityView, ct))
        {
            throw AppException.Denied();
        }
        var reviews = await session.QueryAsync(
            """
            select r.id, r.opened_at, r.due_at, r.closed_at,
              count(i.id) filter (where i.decision is null), count(i.id) filter (where i.decision = 'confirmed'),
              count(i.id) filter (where i.decision = 'removed'), count(i.id) filter (where i.decision = 'expired'),
              count(i.id) filter (where i.decision = 'kept')
            from security.access_review r left join security.access_review_item i on i.review_id = r.id
            group by r.id, r.opened_at, r.due_at, r.closed_at order by r.opened_at desc limit @max
            """, new { max = MaxListed },
            r => new AccessReviewSummary(r.GetGuid(0), r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2), r.Get<DateTimeOffset?>("closed_at"),
                (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6), (int)r.GetInt64(7), (int)r.GetInt64(8)), ct);
        var next = await NextAtAsync(ct);
        return new AccessReviewsPage(reviews, next, await CanAsync(Permissions.SecurityManage, ct) && reviews.All(r => r.ClosedAt is not null));
    }

    /// <summary>Opens a review now (US3 scenario 5); the next one follows 90 days later.</summary>
    public async Task<AccessReviewSummary> StartAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        return await OpenAsync(context.RequireUser(), clock.UtcNow, ct) ?? throw AppException.Conflict("conflict.security.review_open");
    }

    /// <summary>Opens a review unless one is open; returns null when one is.</summary>
    private async Task<AccessReviewSummary?> OpenAsync(Guid? by, DateTimeOffset now, CancellationToken ct)
    {
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':security.access_review', 0))", null, ct);
        if (await session.ScalarAsync<bool>("select exists (select 1 from security.access_review where closed_at is null)", null, ct))
        {
            return null;
        }
        var id = Ids.New();
        var due = now.Add(DecideWithin);
        await session.ExecAsync(
            "insert into security.access_review (tenant_id, id, opened_at, due_at, opened_by) values (platform.current_tenant_id(), @id, @now, @due, @by)",
            new { id, now, due, by }, ct);

        var fallback = await roles.TenantHoldersAsync(Permissions.SecurityManage, ct);
        var people = await memberships.GetManyAsync(fallback, ct);
        async Task<Guid?> ReviewerAsync(Guid? preferred, Guid subject)
        {
            if (preferred is { } p && p != subject && await memberships.GetAsync(p, ct) is { Status: MembershipStatus.Active, MembershipType: not MembershipTypes.Guest })
            {
                return p;
            }
            return fallback.Where(f => f != subject && people.TryGetValue(f, out var m) && m.Status == MembershipStatus.Active).Order().Cast<Guid?>().FirstOrDefault();
        }

        var items = 0;
        foreach (var guest in await guests.ActiveGuestsAsync(ct))
        {
            if (await ReviewerAsync(guest.SponsorId, guest.UserId) is not { } reviewer)
            {
                continue;
            }
            await session.ExecAsync(
                """
                insert into security.access_review_item (tenant_id, id, review_id, kind, reviewer_id, subject_id, space_id, space_name)
                values (platform.current_tenant_id(), @item, @id, 'guest', @reviewer, @subject, @space, @name)
                """, new { item = Ids.New(), id, reviewer, subject = guest.UserId, space = guest.SpaceId, name = guest.SpaceName }, ct);
            items++;
        }
        foreach (var assignment in await roles.AdminAssignmentsAsync(ct))
        {
            if (await memberships.GetAsync(assignment.UserId, ct) is not { Status: MembershipStatus.Active }
                || await ReviewerAsync(await reporting.ManagerOfAsync(assignment.UserId, ct), assignment.UserId) is not { } reviewer)
            {
                continue;
            }
            await session.ExecAsync(
                """
                insert into security.access_review_item (tenant_id, id, review_id, kind, reviewer_id, subject_id, assignment_id, role_key, scope_type, scope_id)
                values (platform.current_tenant_id(), @item, @id, 'admin_role', @reviewer, @subject, @assignment, @role, @scopeType, @scopeId)
                """, new
                {
                    item = Ids.New(), id, reviewer, subject = assignment.UserId, assignment = assignment.AssignmentId, role = assignment.RoleKey,
                    scopeType = Sql.ToSnake(assignment.ScopeType.ToString()), scopeId = assignment.ScopeId,
                }, ct);
            items++;
        }
        await session.ExecAsync(
            """
            insert into security.access_review_schedule (tenant_id, next_at) values (platform.current_tenant_id(), @next)
            on conflict (tenant_id) do update set next_at = excluded.next_at
            """, new { next = now.Add(Interval) }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.SecurityAccessReviewStarted, "access_review", id.ToString(), Changes: new { items, dueAt = due }), ct);

        var reviewers = await session.QueryAsync("select distinct reviewer_id from security.access_review_item where review_id = @id", new { id }, r => r.GetGuid(0), ct);
        if (reviewers.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(reviewers, NotificationCategories.SecurityNotice, RequestedTemplate, Link,
                new Dictionary<string, string> { ["due"] = due.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) },
                DedupeKey: $"access_review:{id:N}"), ct);
        }
        return new AccessReviewSummary(id, now, due, null, items, 0, 0, 0, 0);
    }

    // Reviewers

    /// <summary>The caller's undecided items in the open review.</summary>
    public async Task<MyAccessReviewsView> MineAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await session.QueryAsync(
            """
            select i.id, i.kind, i.subject_id, i.space_id, i.space_name, i.role_key, i.scope_type, i.scope_id, i.decision, r.due_at
            from security.access_review_item i join security.access_review r on r.id = i.review_id
            where i.reviewer_id = @user and r.closed_at is null
            order by i.kind, i.space_name nulls last, i.role_key nulls last, i.id
            """, new { user },
            r => (Id: r.GetGuid(0), Kind: r.GetString(1), Subject: r.GetGuid(2), Space: r.Get<Guid?>("space_id"), SpaceName: r.Get<string>("space_name"),
                Role: r.Get<string>("role_key"), ScopeType: r.Get<string>("scope_type"), ScopeId: r.Get<Guid?>("scope_id"), Decision: r.Get<string>("decision"),
                Due: r.GetFieldValue<DateTimeOffset>(9)), ct);
        var names = await users.GetManyAsync(rows.Select(r => r.Subject).Distinct().ToList(), ct);
        return new MyAccessReviewsView(rows.Select(r => new AccessReviewItemView(r.Id, r.Kind, r.Subject, names.TryGetValue(r.Subject, out var u) ? u.DisplayName : "",
            r.Space, r.SpaceName, r.Role, r.ScopeType, r.ScopeId, r.Decision, r.Due)).ToList());
    }

    /// <summary>Confirms or removes one of the caller's items. Removal happens at once.</summary>
    public async Task<MyAccessReviewsView> DecideAsync(Guid itemId, bool keep, CancellationToken ct)
    {
        var user = context.RequireUser();
        var item = await session.QuerySingleOrDefaultAsync(
            """
            select i.kind, i.subject_id, i.space_id, i.assignment_id, i.role_key, i.decision
            from security.access_review_item i join security.access_review r on r.id = i.review_id
            where i.id = @itemId and i.reviewer_id = @user and r.closed_at is null for update of i
            """, new { itemId, user },
            r => (Kind: r.GetString(0), Subject: r.GetGuid(1), Space: r.Get<Guid?>("space_id"), Assignment: r.Get<Guid?>("assignment_id"),
                Role: r.Get<string>("role_key"), Decision: r.Get<string>("decision")), ct);
        if (item.Kind is null)
        {
            throw AppException.NotFound();
        }
        if (item.Decision is not null)
        {
            throw AppException.Conflict("conflict.security.review_decided");
        }
        var decision = keep ? AccessReviewDecisions.Confirmed : AccessReviewDecisions.Removed;
        if (!keep && !await RemoveAsync(item.Kind, item.Subject, item.Space, item.Assignment, "access_review_removed", ct))
        {
            throw AppException.Conflict("conflict.security.review_last_admin");
        }
        await RecordAsync(itemId, item.Kind, item.Subject, item.Space, item.Role, decision, user, ct);
        return await MineAsync(ct);
    }

    /// <summary>Removes the access an item names; false only when it is the last tenant administrator assignment.</summary>
    private async Task<bool> RemoveAsync(string kind, Guid subject, Guid? space, Guid? assignment, string reason, CancellationToken ct)
    {
        if (kind == AccessReviewKinds.Guest)
        {
            // A guest who already left the space has nothing left to remove.
            await guests.RemoveGuestAsync(space!.Value, subject, reason, ct);
            return true;
        }
        return await roles.RevokeSystemAssignmentAsync(assignment!.Value, reason, ct);
    }

    private async Task RecordAsync(Guid itemId, string kind, Guid subject, Guid? space, string? role, string decision, Guid? by, CancellationToken ct)
    {
        await session.ExecAsync("update security.access_review_item set decision = @decision, decided_by = @by, decided_at = @now where id = @itemId",
            new { decision, by, now = clock.UtcNow, itemId }, ct);
        await audit.WriteAsync(kind == AccessReviewKinds.Guest
            ? new AuditEntry(AuditActions.GuestReviewed, "user", subject.ToString(), Changes: new { space, decision })
            : new AuditEntry(AuditActions.SecurityAccessReviewDecided, "user", subject.ToString(), Changes: new { role, decision }), ct);
    }

    // Schedule

    /// <summary>
    /// Closes reviews past their date, removing what nobody confirmed, then opens the next review when it is due. The
    /// first review of a tenant opens 90 days after this first runs for it.
    /// </summary>
    public async Task<int> RunAsync(DateTimeOffset now, CancellationToken ct)
    {
        var closed = 0;
        var due = await session.QueryAsync("select id from security.access_review where closed_at is null and due_at <= @now", new { now }, r => r.GetGuid(0), ct);
        foreach (var review in due)
        {
            var open = await session.QueryAsync(
                "select id, kind, subject_id, space_id, assignment_id, role_key from security.access_review_item where review_id = @review and decision is null",
                new { review },
                r => (Id: r.GetGuid(0), Kind: r.GetString(1), Subject: r.GetGuid(2), Space: r.Get<Guid?>("space_id"), Assignment: r.Get<Guid?>("assignment_id"),
                    Role: r.Get<string>("role_key")), ct);
            foreach (var item in open)
            {
                var removed = await RemoveAsync(item.Kind, item.Subject, item.Space, item.Assignment, "access_review_expired", ct);
                await RecordAsync(item.Id, item.Kind, item.Subject, item.Space, item.Role, removed ? AccessReviewDecisions.Expired : AccessReviewDecisions.Kept, null, ct);
            }
            await session.ExecAsync("update security.access_review set closed_at = @now where id = @review", new { now, review }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SecurityAccessReviewCompleted, "access_review", review.ToString(),
                Changes: new { removedUnconfirmed = open.Count }), ct);
            closed++;
        }

        var next = await NextAtAsync(ct);
        if (next is null)
        {
            await session.ExecAsync("insert into security.access_review_schedule (tenant_id, next_at) values (platform.current_tenant_id(), @next) on conflict do nothing",
                new { next = now.Add(Interval) }, ct);
        }
        else if (next <= now)
        {
            await OpenAsync(null, now, ct);
        }
        return closed;
    }
}

/// <summary>Runs the access review schedule for every active tenant once an hour.</summary>
public sealed class AccessReviewWorker(IServiceScopeFactory scopes, ILogger<AccessReviewWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Security:AccessReviews:InProcess";
    public static readonly TimeSpan Tick = TimeSpan.FromHours(1);

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
            logger.LogWarning(ex, "Access reviews could not list tenants");
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
                await scope.ServiceProvider.GetRequiredService<AccessReviews>().RunAsync(scope.ServiceProvider.GetRequiredService<IClock>().UtcNow, ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Access reviews failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
