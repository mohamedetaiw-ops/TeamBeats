using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Content;

/// <summary>What the owner does with a review task: keep it as it is, update it, or take it down (US3 scenario 2).</summary>
public sealed record OwnerReviewRequest(string? Action, DateOnly? NextReviewOn);

/// <summary>Where the item stands after the owner's review. The owner may not be an editor, so this is all they get back.</summary>
public sealed record OwnerReviewResult(Guid ItemId, string Action, string Status, DateTimeOffset? ReviewDueAt, int? DraftVersion, bool RetirePending);

/// <summary>Counts per site for the content health dashboard (US3 scenario 5). Views come with spec 021, so <see cref="NotViewed"/> is null until then.</summary>
/// <summary>
/// Health of one site (US3 scenario 5, R2 additions): items overdue for review, without an active owner, expired; whether
/// the site itself has no active owner (nobody holds <c>content.admin</c> at its unit, section 4 edge case); and for an
/// archived site, how many of its items are still live and readable by link.
/// </summary>
public sealed record SiteHealth(Guid SiteId, IReadOnlyDictionary<string, string> Names, int OverdueForReview, int WithoutActiveOwner, int Expired, int? NotViewed,
    bool Ownerless = false, bool Archived = false, int LiveLinks = 0);

public sealed record HealthItem(Guid Id, Guid SiteId, string Type, string Status, string Title, Guid? OwnerId, DateTimeOffset? ReviewDueAt, DateTimeOffset? ExpiredAt);

public static class ReviewActions
{
    public const string Confirm = "confirm";
    public const string Edit = "edit";
    public const string Retire = "retire";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Confirm, Edit, Retire };
}

public static class HealthKinds
{
    public const string Overdue = "overdue";
    public const string Ownerless = "ownerless";
    public const string Expired = "expired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Overdue, Ownerless, Expired };
}

/// <summary>
/// Content governance of spec 009 US3 (FR-008): the owner's review task opens 30 days before the review date as a
/// user task on the process engine, with confirm, edit and retire; overdue items tell the owner's manager and the
/// communications administrators (who own sites in R1); retiring needs a publisher, so an owner without
/// <c>content.publish</c> sends it to the site's publishers; an owner who leaves hands their items to the backup owner;
/// the health dashboard counts what needs attention.
/// </summary>
public sealed class ContentGovernance(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning provisioning,
    IEntitlementService entitlements,
    ITenantMemberships memberships,
    IReportingLine reportingLine,
    IOrganizationHierarchy hierarchy,
    IProcessEngine engine,
    Items items,
    Sites sites,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string ReviewPrefix = "content.review.";
    public const string RetirePrefix = "content.retire.";
    public static readonly TimeSpan ReviewWindow = TimeSpan.FromDays(30);
    public const int DefaultReviewMonths = 12;

    public const string ReviewWaiting = "content.owner_review.waiting";
    public const string ReviewOverdue = "content.owner_review.overdue";
    public const string OwnerMissing = "content.owner_missing";

    public static bool Owns(string definitionKey) =>
        definitionKey.StartsWith(ReviewPrefix, StringComparison.Ordinal) || definitionKey.StartsWith(RetirePrefix, StringComparison.Ordinal);

    private static bool IsReview(string definitionKey) => definitionKey.StartsWith(ReviewPrefix, StringComparison.Ordinal);

    // ---- Scheduler ----

    /// <summary>Opens the review task of live items whose window started, and tells people about items now overdue.</summary>
    internal async Task<int> RunAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        var opening = await session.QueryAsync(
            """
            select id from content.content_item
            where status = 'published' and review_due_at is not null and review_due_at <= @start and owner_user_id is not null
              and owner_review_instance_id is null and retire_instance_id is null
            order by review_due_at, id limit @limit
            """, new { start = now + ReviewWindow, limit }, r => r.GetGuid(0), ct);
        foreach (var id in opening)
        {
            if (await items.RowAsync(id, forUpdate: true, ct) is { Status: ContentStatuses.Published, OwnerId: { } owner, ReviewDueAt: { } due, OwnerReviewInstanceId: null } item)
            {
                await OpenReviewAsync(item, owner, due, ct);
                moved++;
            }
        }
        var overdue = await session.QueryAsync(
            """
            select id from content.content_item
            where status = 'published' and review_due_at is not null and review_due_at <= @now and review_overdue_notified_at is null
            order by review_due_at, id limit @limit
            """, new { now, limit }, r => r.GetGuid(0), ct);
        foreach (var id in overdue)
        {
            if (await items.RowAsync(id, forUpdate: true, ct) is { Status: ContentStatuses.Published } item)
            {
                var recipients = new HashSet<Guid>(await provisioning.TenantHoldersAsync(Permissions.ContentAdmin, ct));
                if (item.OwnerId is { } owner && await reportingLine.ManagerOfAsync(owner, ct) is { } manager)
                {
                    recipients.Add(manager);
                }
                if (recipients.Count > 0)
                {
                    await notifications.SendAsync(new NotificationRequest(recipients.ToArray(), NotificationCategories.RequestUpdate, ReviewOverdue,
                        ContentTaskSource.Link(id), DedupeKey: $"content:{id:N}:overdue:{item.ReviewDueAt:yyyyMMdd}"), ct);
                }
                await session.ExecAsync("update content.content_item set review_overdue_notified_at = @now where id = @id", new { id, now }, ct);
                moved++;
            }
        }
        return moved;
    }

    private async Task OpenReviewAsync(ItemRow item, Guid owner, DateTimeOffset due, CancellationToken ct)
    {
        var key = $"{ReviewPrefix}{owner:N}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            await engine.PublishRouteAsync(key, RouteSources.ContentRoute,
                new RouteSpec("content review", [new RouteStep("review", "review", new CandidateRule(Users: [owner]))], ExcludeStarter: false), ct);
        }
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        var instance = await engine.StartAsync(key, $"{item.Id:N}{Ids.New():N}", site.OrgUnitId, null, null, owner, ct: ct);
        await session.ExecAsync("update content.content_item set owner_review_instance_id = @instance where id = @id", new { id = item.Id, instance = instance.Id }, ct);
        await outbox.EnqueueAsync(new ContentReviewDue(item.Id, site.Id, owner, due), ct);
    }

    // ---- Owner review ----

    /// <summary>The owner acts on their review task from the item (the inbox maps approve, return and reject to confirm, edit and retire).</summary>
    public async Task<OwnerReviewResult> ReviewAsync(Guid id, OwnerReviewRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        var user = context.RequireUser();
        var action = request.Action ?? string.Empty;
        if (!ReviewActions.All.Contains(action))
        {
            throw AppException.Validation("action", "validation.content.review_action");
        }
        var item = await items.RowAsync(id, forUpdate: true, ct) ?? throw AppException.NotFound();
        if (item.OwnerReviewInstanceId is not { } instance)
        {
            throw item.OwnerId == user ? AppException.Conflict("conflict.content.no_review") : AppException.NotFound();
        }
        var task = (await engine.InstanceTasksAsync(instance, ct)).FirstOrDefault(t => t.Status == "open") ?? throw AppException.Conflict("conflict.content.no_review");
        if (await engine.MyTaskAsync(task.Id, ct) is null)
        {
            throw item.OwnerId == user ? AppException.Denied() : AppException.NotFound();
        }
        var site = (await sites.RowAsync(item.SiteId, false, ct))!;
        var next = NextReview(request.NextReviewOn, site);
        await engine.CompleteTaskAsync(task.Id, action switch
        {
            ReviewActions.Confirm => TaskOutcomes.Approve,
            ReviewActions.Edit => TaskOutcomes.Return,
            _ => TaskOutcomes.Reject,
        }, null, ct);
        await ApplyAsync(item, site, action, next, user, ct);
        var after = (await items.RowAsync(id, false, ct))!;
        return new OwnerReviewResult(id, action, after.Status, after.ReviewDueAt, after.DraftVersion, after.RetireInstanceId is not null);
    }

    private DateTimeOffset? NextReview(DateOnly? on, SiteRow site)
    {
        if (on is not { } date)
        {
            return null;
        }
        var at = Items.ReviewAt(date, site);
        return at <= clock.UtcNow ? throw AppException.Validation("nextReviewOn", "validation.content.review_date") : at;
    }

    private async Task ApplyAsync(ItemRow item, SiteRow site, string action, DateTimeOffset? next, Guid actor, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var months = (await sites.TypeAsync(item.Type, ct))!.ReviewMonths ?? DefaultReviewMonths;
        var nextReview = next ?? now.AddMonths(months);
        int? draft = null;
        if (action == ReviewActions.Retire)
        {
            await session.ExecAsync("update content.content_item set owner_review_instance_id = null where id = @id", new { id = item.Id }, ct);
            await RetireAsync(item with { OwnerReviewInstanceId = null }, site, actor, ct);
        }
        else
        {
            await session.ExecAsync(
                """
                update content.content_item set owner_review_instance_id = null, review_due_at = @nextReview, review_overdue_notified_at = null,
                  updated_at = @now, row_version = row_version + 1
                where id = @id
                """, new { id = item.Id, nextReview, now }, ct);
            if (action == ReviewActions.Edit)
            {
                draft = await items.DraftFromLiveAsync((await items.RowAsync(item.Id, false, ct))!, actor, ct);
            }
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ContentReviewed, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { action, nextReview = action == ReviewActions.Retire ? (DateTimeOffset?)null : nextReview, draft }), ct);
    }

    /// <summary>A publisher archives at once; anyone else sends the retirement to the site's publishers (US3 scenario 2: "archives after approval").</summary>
    private async Task RetireAsync(ItemRow item, SiteRow site, Guid actor, CancellationToken ct)
    {
        if ((await authz.CheckAsync(Permissions.ContentPublish, site.Target, ct)).Allowed)
        {
            await items.ArchiveRowAsync(item, site, "retired", ct);
            return;
        }
        var key = $"{RetirePrefix}{site.Id:N}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            await engine.PublishRouteAsync(key, RouteSources.ContentRoute,
                new RouteSpec("content retirement", [new RouteStep("retire", "retire", new CandidateRule(Permission: Permissions.ContentPublish))]), ct);
        }
        var instance = await engine.StartAsync(key, $"{item.Id:N}{Ids.New():N}", site.OrgUnitId, actor, null, actor, ct: ct);
        await session.ExecAsync("update content.content_item set retire_instance_id = @instance where id = @id", new { id = item.Id, instance = instance.Id }, ct);
    }

    // ---- Inbox (ITaskSource through ContentTaskSource) ----

    private async Task<(ItemRow Item, SiteRow Site)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (ContentReviews.ItemIdOf(task.DefinitionKey, task.BusinessKey) is not { } id || await items.RowAsync(id, forUpdate, ct) is not { } item
            || (IsReview(task.DefinitionKey) ? item.OwnerReviewInstanceId : item.RetireInstanceId) != task.InstanceId)
        {
            return null;
        }
        return (item, (await sites.RowAsync(item.SiteId, false, ct))!);
    }

    internal async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct)
    {
        if (await WaitingAsync(task, false, ct) is not var (item, site) || (item.PublishedVersion ?? item.DraftVersion) is not { } number)
        {
            return null;
        }
        var version = await items.VersionAsync(item.Id, number, ct);
        var title = version?.Variants.ToDictionary(v => v.Key, v => v.Value.Title) ?? [];
        return new TaskSubject(IsReview(task.DefinitionKey) ? "content_owner_review" : "content_retire", title, ContentTaskSource.Link(item.Id), site.OrgUnitId,
            IsReview(task.DefinitionKey) ? item.OwnerId : null);
    }

    internal async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct) =>
        await WaitingAsync(task, false, ct) is var (item, _) && (!IsReview(task.DefinitionKey) || authority == item.OwnerId);

    internal async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct)
    {
        if (await WaitingAsync(task, true, ct) is not var (item, site))
        {
            return;
        }
        var decider = done.CompletedBy ?? context.RequireUser();
        if (IsReview(task.DefinitionKey))
        {
            await ApplyAsync(item, site, done.Outcome switch
            {
                TaskOutcomes.Approve => ReviewActions.Confirm,
                TaskOutcomes.Return => ReviewActions.Edit,
                _ => ReviewActions.Retire,
            }, null, decider, ct);
            return;
        }
        await session.ExecAsync("update content.content_item set retire_instance_id = null where id = @id", new { id = item.Id }, ct);
        if (done.Outcome == TaskOutcomes.Approve && item.Status != ContentStatuses.Archived)
        {
            await items.ArchiveRowAsync(item with { RetireInstanceId = null }, site, "retired", ct);
        }
    }

    /// <summary>The owner hears a review waits; a retirement goes to the publishers' inbox badge.</summary>
    internal async Task TaskCreatedAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!IsReview(message.DefinitionKey) || ContentReviews.ItemIdOf(message.DefinitionKey, message.BusinessKey) is not { } id
            || await items.RowAsync(id, false, ct) is not { OwnerId: { } owner } item || item.OwnerReviewInstanceId != message.InstanceId)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest([owner], NotificationCategories.ApprovalWaiting, ReviewWaiting,
            ContentTaskSource.Link(id) + "?task=" + message.TaskId, DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }

    // ---- Owner leaves (US3 scenario 4) ----

    /// <summary>
    /// Items of a person who left go to their backup owner when that person is still active; otherwise they become
    /// ownerless and the communications administrators are told. An open review task of theirs is withdrawn. Audited per item.
    /// </summary>
    internal async Task OwnerLeftAsync(Guid user, CancellationToken ct)
    {
        var owned = await session.QueryAsync($"select {Items.Columns} from content.content_item where (owner_user_id = @user or backup_owner_user_id = @user) and status <> 'archived'",
            new { user }, Items.Read, ct);
        var ownerless = new List<Guid>();
        foreach (var item in owned)
        {
            var site = (await sites.RowAsync(item.SiteId, false, ct))!;
            if (item.OwnerId != user)
            {
                await session.ExecAsync("update content.content_item set backup_owner_user_id = null where id = @id", new { id = item.Id }, ct);
                continue;
            }
            Guid? next = item.BackupOwnerId is { } backup && backup != user && (await memberships.GetAsync(backup, ct))?.Status == MembershipStatus.Active ? backup : null;
            if (item.OwnerReviewInstanceId is { } instance)
            {
                await engine.WithdrawAsync(instance, ct);
            }
            await session.ExecAsync(
                """
                update content.content_item set owner_user_id = @next, backup_owner_user_id = null, owner_review_instance_id = null,
                  updated_at = @now, row_version = row_version + 1
                where id = @id
                """, new { id = item.Id, next, now = clock.UtcNow }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ContentOwnerReassigned, "content_item", item.Id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                Changes: new { from = user, to = next, reason = next is null ? "no_backup" : "backup" }), ct);
            if (next is null)
            {
                ownerless.Add(item.Id);
            }
        }
        if (ownerless.Count > 0 && await provisioning.TenantHoldersAsync(Permissions.ContentAdmin, ct) is { Count: > 0 } admins)
        {
            await notifications.SendAsync(new NotificationRequest(admins.ToArray(), NotificationCategories.RequestUpdate, OwnerMissing, "/content/health",
                new Dictionary<string, string> { ["count"] = ownerless.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                DedupeKey: $"content:owner_left:{user:N}"), ct);
        }
    }

    // ---- Health (US3 scenario 5) ----

    private async Task DemandAdminAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ContentCore, ct);
        context.RequireUser();
        await authz.DemandAsync(Permissions.ContentAdmin, AuthzTarget.Tenant, ct);
    }

    private async Task<HashSet<Guid>> ActiveAsync(CancellationToken ct) =>
        (await memberships.ListAsync(ct)).Where(m => m.Status == MembershipStatus.Active).Select(m => m.UserId).ToHashSet();

    public async Task<IReadOnlyList<SiteHealth>> HealthAsync(CancellationToken ct)
    {
        await DemandAdminAsync(ct);
        var active = await ActiveAsync(ct);
        var now = clock.UtcNow;
        var rows = await session.QueryAsync(
            "select site_id, status, owner_user_id, review_due_at from content.content_item where status in ('published', 'expired', 'scheduled', 'draft', 'in_review')",
            null, r => (Site: r.GetGuid(0), Status: r.GetString(1), Owner: r.Get<Guid?>("owner_user_id"), Due: r.Get<DateTimeOffset?>("review_due_at")), ct);
        var list = new List<SiteHealth>();
        var owners = (await provisioning.OrgHoldersAsync(Permissions.ContentAdmin, ct)).Where(h => active.Contains(h.UserId)).ToList();
        foreach (var site in (await sites.ListAsync(ct)).OrderBy(s => s.Id))
        {
            var mine = rows.Where(r => r.Site == site.Id).ToList();
            var ownerless = false;
            if (site.OrgUnitId is { } unit)
            {
                var above = (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToHashSet();
                ownerless = !owners.Any(h => h.UnitId == unit || (h.Subtree && above.Contains(h.UnitId)));
            }
            var archived = site.Status == "archived";
            list.Add(new SiteHealth(site.Id, site.Names,
                mine.Count(r => r.Status == ContentStatuses.Published && r.Due is { } d && d <= now),
                mine.Count(r => r.Owner is not { } o || !active.Contains(o)),
                mine.Count(r => r.Status == ContentStatuses.Expired),
                null, ownerless, archived, archived ? mine.Count(r => r.Status is ContentStatuses.Published or ContentStatuses.Expired) : 0));
        }
        return list;
    }

    public async Task<IReadOnlyList<HealthItem>> HealthItemsAsync(Guid siteId, string? kind, CancellationToken ct)
    {
        await DemandAdminAsync(ct);
        if (kind is null || !HealthKinds.All.Contains(kind))
        {
            throw AppException.Validation("kind", "validation.content.health_kind");
        }
        var site = await sites.RowAsync(siteId, false, ct) ?? throw AppException.NotFound();
        var active = await ActiveAsync(ct);
        var now = clock.UtcNow;
        var rows = await session.QueryAsync($"select {Items.Columns} from content.content_item where site_id = @site and status <> 'archived' order by updated_at desc, id",
            new { site = site.Id }, Items.Read, ct);
        var picked = rows.Where(i => kind switch
        {
            HealthKinds.Overdue => i.Status == ContentStatuses.Published && i.ReviewDueAt is { } d && d <= now,
            HealthKinds.Ownerless => i.OwnerId is not { } o || !active.Contains(o),
            _ => i.Status == ContentStatuses.Expired,
        }).Take(200);
        var list = new List<HealthItem>();
        foreach (var i in picked)
        {
            var version = (i.PublishedVersion ?? i.DraftVersion) is { } n ? await items.VersionAsync(i.Id, n, ct) : null;
            var title = version?.Variants.GetValueOrDefault(i.PrimaryLocale)?.Title ?? version?.Variants.Values.FirstOrDefault()?.Title ?? string.Empty;
            list.Add(new HealthItem(i.Id, i.SiteId, i.Type, i.Status, title, i.OwnerId, i.ReviewDueAt, i.ExpiredAt));
        }
        return list;
    }
}

/// <summary>Hands an owner's items on when their account is deprovisioned (spec 003, spec 009 US3 scenario 4).</summary>
public sealed class ContentOwnerHandler(ContentGovernance governance) : IMessageHandler<UserDeprovisioned>
{
    public Task HandleAsync(UserDeprovisioned message, CancellationToken ct) => governance.OwnerLeftAsync(message.UserId, ct);
}
