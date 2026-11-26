using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>The outcomes of a scheduled review (US5). <see cref="Revised"/> is recorded when a new version came into force first.</summary>
public static class ReviewOutcomes
{
    public const string NoChange = "no_change";
    public const string MinorUpdate = "minor_update";
    public const string MajorRevision = "major_revision";
    public const string Retire = "retire";
    public const string Revised = "revised";

    public static readonly IReadOnlySet<string> Chosen = new HashSet<string>(StringComparer.Ordinal) { NoChange, MinorUpdate, MajorRevision, Retire };
}

/// <summary>Where a policy in force stands with its review date, for the register and the policy page.</summary>
public static class ReviewStates
{
    /// <summary>The review window is open.</summary>
    public const string Open = "open";

    /// <summary>The review date is within 30 days (FR-021 "needs attention").</summary>
    public const string DueSoon = "due_soon";

    /// <summary>The review date passed with no outcome (US5 scenario 4).</summary>
    public const string Overdue = "overdue";
}

public sealed record ReviewRequest(string? Outcome, string? Note);

public sealed record ReviewView(
    Guid Id,
    int Version,
    DateTimeOffset DueAt,
    DateTimeOffset OpenedAt,
    DateTimeOffset? OverdueAt,
    string? Outcome,
    string? Note,
    PolicyPerson? Reviewer,
    DateTimeOffset? CompletedAt,
    Guid? DraftVersionId,
    DateTimeOffset? NextReviewAt,
    ReviewCause? Cause = null);

/// <summary>The regulator change a review was started for (spec 025 R3 US10 scenario 3); the link only when it is https.</summary>
public sealed record ReviewCause(Guid RegulatorChangeId, string Title, string? Link);

/// <summary>What the current user may do on the review page: answer the open review, and retire directly as a compliance officer.</summary>
public sealed record ReviewRights(bool Complete, bool Retire, bool Revise);

public sealed record ReviewsView(
    PolicySummary Policy,
    int ReviewMonths,
    int WindowDays,
    DateTimeOffset? NextReviewAt,
    string? State,
    ReviewView? Open,
    IReadOnlyList<ReviewView> History,
    ReviewRights Rights);

internal sealed record ReviewRow(
    Guid Id,
    Guid PolicyId,
    int Version,
    DateTimeOffset DueAt,
    DateTimeOffset OpenedAt,
    DateTimeOffset? OverdueAt,
    string? Outcome,
    string? Note,
    Guid? Reviewer,
    DateTimeOffset? CompletedAt,
    Guid? DraftVersionId,
    DateTimeOffset? NextReviewAt,
    Guid? RegulatorChangeId)
{
    public const string Columns = "id, policy_id, version, due_at, opened_at, overdue_at, outcome, note, reviewer_user_id, completed_at, draft_version_id, next_review_at, regulator_change_id";

    public static ReviewRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4),
        r.Get<DateTimeOffset?>("overdue_at"), r.Get<string>("outcome"), r.Get<string>("note"), r.Get<Guid?>("reviewer_user_id"), r.Get<DateTimeOffset?>("completed_at"),
        r.Get<Guid?>("draft_version_id"), r.Get<DateTimeOffset?>("next_review_at"), r.Get<Guid?>("regulator_change_id"));
}

/// <summary>
/// Scheduled reviews (spec 025 US5, FR-012). When a policy's review window opens (its review date less the window,
/// 60 days by default) the owner gets a review task in the inbox with four outcomes. No change records the reviewer
/// and date and moves the review date by the review cycle, with no new attestation. A minor update or a major
/// revision starts a draft from the version in force for the approval route; when a major revision comes into force
/// the owner is asked whether to launch re-attestation. Retire retires the policy when a compliance officer answers,
/// and otherwise asks the compliance officers to. A review date that passes with no outcome makes the review overdue:
/// the register says so and the compliance officers and the owner's manager hear of it. Everything is in the current
/// tenant; another tenant's policies and reviews are not found.
/// </summary>
public sealed class PolicyReviews(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    PolicyApprovals approvals,
    IEntitlementService entitlements,
    IAuthorizationProvisioning authz,
    IOrganizationHierarchy hierarchy,
    IReportingLine reportingLine,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
    : IPersonalTaskSource, IMessageHandler<PolicyPublished>
{
    public const int DefaultWindowDays = 60;
    public const int MinWindowDays = 1;
    public const int MaxWindowDays = 365;
    public const int DueSoonDays = 30;
    public const int MaxNoteLength = 2000;

    public const string ReviewDueTemplate = "policies.review_due";
    public const string ReviewOverdueTemplate = "policies.review_overdue";
    public const string RetireRequestedTemplate = "policies.review_retire";
    public const string ReattestTemplate = "policies.review_reattest";

    public static string ReviewsLink(Guid policyId) => $"/policies/{policyId}/reviews";

    /// <summary>The review state of a policy in force; null when it has no review date, was retired, or its review has an outcome.</summary>
    public static string? StateOf(DateTimeOffset? nextReviewAt, int windowDays, bool published, bool reviewed, DateTimeOffset now)
    {
        if (!published || reviewed || nextReviewAt is not { } due)
        {
            return null;
        }
        if (due <= now)
        {
            return ReviewStates.Overdue;
        }
        if (due <= now.AddDays(DueSoonDays))
        {
            return ReviewStates.DueSoon;
        }
        return due.AddDays(-windowDays) <= now ? ReviewStates.Open : null;
    }

    internal static string? StateOf(PolicyRow policy, bool reviewed, DateTimeOffset now) =>
        StateOf(policy.NextReviewAt, policy.ReviewWindowDays, policy.Status == PolicyStatuses.Published && policy.CurrentVersion is not null, reviewed, now);

    /// <summary>
    /// Compliance officers for the policy: <c>policy.manage</c> tenant-wide, or at the policy's unit, or above it over a
    /// subtree.
    /// </summary>
    private async Task<HashSet<Guid>> OfficersAsync(PolicyRow policy, CancellationToken ct)
    {
        var officers = (await authz.TenantHoldersAsync(Permissions.PolicyManage, ct)).ToHashSet();
        if (policy.OrgUnitId is { } unit)
        {
            var above = (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToHashSet();
            foreach (var holder in await authz.OrgHoldersAsync(Permissions.PolicyManage, ct))
            {
                if (holder.UnitId == unit || (holder.Subtree && above.Contains(holder.UnitId)))
                {
                    officers.Add(holder.UserId);
                }
            }
        }
        return officers;
    }

    private async Task<ReviewRow?> OpenRowAsync(Guid policyId, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"select {ReviewRow.Columns} from policies.policy_review where policy_id = @policyId and completed_at is null{(forUpdate ? " for update" : string.Empty)}",
            new { policyId }, ReviewRow.Read, ct);

    private async Task<ReviewView> ViewAsync(ReviewRow r, CancellationToken ct) =>
        new(r.Id, r.Version, r.DueAt, r.OpenedAt, r.OverdueAt, r.Outcome, r.Note, r.Reviewer is { } u ? await register.PersonAsync(u, ct) : null, r.CompletedAt,
            r.DraftVersionId, r.NextReviewAt, r.RegulatorChangeId is { } change
                ? await session.QuerySingleOrDefaultAsync("select id, title, link from policies.regulator_change where id = @change", new { change },
                    c => new ReviewCause(c.GetGuid(0), c.GetString(1), c.IsDBNull(2) ? null : c.GetString(2)), ct)
                : null);

    /// <summary>The owner and the compliance officers of the policy see and answer its reviews.</summary>
    private async Task<(PolicyRow Policy, bool Manages)> ReviewerAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        var policy = await register.VisibleAsync(id, forUpdate, ct);
        var manages = await register.ManagesAsync(policy, ct);
        if (policy.Owner != context.RequireUser() && !manages)
        {
            throw AppException.Denied();
        }
        return (policy, manages);
    }

    /// <summary>The review page: the cycle, the open review and the review history (US5).</summary>
    public async Task<ReviewsView> ListAsync(Guid id, CancellationToken ct)
    {
        var (policy, manages) = await ReviewerAsync(id, false, ct);
        var rows = await session.QueryAsync($"select {ReviewRow.Columns} from policies.policy_review where policy_id = @id order by due_at desc, id", new { id }, ReviewRow.Read,
            ct);
        var open = rows.FirstOrDefault(r => r.CompletedAt is null);
        var history = new List<ReviewView>();
        foreach (var r in rows.Where(r => r.CompletedAt is not null))
        {
            history.Add(await ViewAsync(r, ct));
        }
        var live = policy.Status != PolicyStatuses.Retired;
        return new ReviewsView(await register.SummaryAsync(policy, ct), policy.ReviewMonths, policy.ReviewWindowDays, policy.NextReviewAt,
            StateOf(policy, await register.ReviewedAsync(policy, ct), clock.UtcNow), open is null ? null : await ViewAsync(open, ct), history,
            new ReviewRights(open is not null && live, open is not null && live && manages, open is not null && live && await register.EditsAsync(policy, ct)));
    }

    /// <summary>Records the outcome of the open review (US5 scenarios 2 and 3).</summary>
    public async Task<ReviewsView> CompleteAsync(Guid id, Guid reviewId, ReviewRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (policy, manages) = await ReviewerAsync(id, true, ct);
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        var review = await OpenRowAsync(id, forUpdate: true, ct);
        if (review is null || review.Id != reviewId)
        {
            throw await session.ScalarAsync<int>("select count(*)::int from policies.policy_review where id = @reviewId and policy_id = @id", new { reviewId, id }, ct) > 0
                ? AppException.Conflict("conflict.policies.review_done")
                : AppException.NotFound();
        }
        var errors = new ValidationErrors();
        if (request.Outcome is not { } outcome || !ReviewOutcomes.Chosen.Contains(outcome))
        {
            errors.Add("outcome", "validation.policies.review_outcome");
            outcome = string.Empty;
        }
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > MaxNoteLength })
        {
            errors.Add("note", "validation.policies.review_note");
        }
        errors.ThrowIfAny();

        var now = clock.UtcNow;
        Guid? draft = null;
        DateTimeOffset? next = null;
        switch (outcome)
        {
            case ReviewOutcomes.NoChange:
                // The cycle keeps its rhythm from the review date; a late answer counts from today.
                next = (review.DueAt > now ? review.DueAt : now).AddMonths(policy.ReviewMonths);
                await session.ExecAsync("update policies.policy set next_review_at = @next, updated_at = @now, row_version = row_version + 1 where id = @id",
                    new { id, next, now }, ct);
                break;
            case ReviewOutcomes.MinorUpdate or ReviewOutcomes.MajorRevision:
                if (!await register.EditsAsync(policy, ct))
                {
                    throw AppException.Denied();
                }
                draft = await approvals.StartRevisionAsync(policy, outcome == ReviewOutcomes.MajorRevision, ct);
                break;
        }
        await session.ExecAsync(
            """
            update policies.policy_review set outcome = @outcome, note = @note, reviewer_user_id = @user, completed_at = @now, draft_version_id = @draft, next_review_at = @next
            where id = @reviewId
            """, new { reviewId, outcome, note, user, now, draft, next }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyReviewCompleted, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { review = reviewId, outcome, reviewer = user, version = review.Version, dueAt = review.DueAt, nextReviewAt = next, draft }), ct);
        if (outcome == ReviewOutcomes.Retire)
        {
            if (manages)
            {
                await register.RetireAsync(id, ct);
            }
            else
            {
                var officers = await OfficersAsync(policy, ct);
                officers.Remove(user);
                if (officers.Count > 0)
                {
                    await notifications.SendAsync(new NotificationRequest(officers, NotificationCategories.DocumentUpdate, RetireRequestedTemplate, ReviewsLink(id),
                        DedupeKey: $"policies.review:{reviewId:N}:retire"), ct);
                }
            }
        }
        return await ListAsync(id, ct);
    }

    /// <summary>The owner's open reviews in their inbox (US5 scenario 1).</summary>
    public async Task<IReadOnlyList<PersonalTask>> OpenForAsync(Guid userId, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select r.id, p.id, p.names::text, r.opened_at, r.due_at from policies.policy_review r join policies.policy p on p.id = r.policy_id
            where r.completed_at is null and p.owner_user_id = @userId and p.status <> 'retired' order by r.due_at, r.id
            """, new { userId },
            r => new PersonalTask("policy_review", r.GetGuid(0), PolicyJson.Read<Dictionary<string, string>>(r.GetString(2)), ReviewsLink(r.GetGuid(1)),
                r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4)), ct);
    }

    /// <summary>
    /// For one tenant: opens the reviews whose window came, telling the owner, then marks the reviews whose date passed
    /// as overdue and tells the compliance officers, the owner's manager and the owner (US5 scenarios 1 and 4).
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            return 0;
        }
        var opened = await session.QueryAsync(
            """
            insert into policies.policy_review (tenant_id, id, policy_id, version, due_at, opened_at)
            select p.tenant_id, gen_random_uuid(), p.id, p.current_version, p.next_review_at, @now from policies.policy p
            where p.status = 'published' and p.current_version is not null and p.next_review_at is not null
              and p.next_review_at - make_interval(days => p.review_window_days) <= @now
              and not exists (select 1 from policies.policy_review r where r.policy_id = p.id and (r.completed_at is null or r.due_at = p.next_review_at))
            order by p.next_review_at, p.id limit @limit
            on conflict do nothing
            returning id, policy_id, version, due_at
            """, new { now, limit }, r => (Id: r.GetGuid(0), Policy: r.GetGuid(1), Version: r.GetInt32(2), Due: r.GetFieldValue<DateTimeOffset>(3)), ct);
        foreach (var o in opened)
        {
            var policy = (await register.RowAsync(o.Policy, false, ct))!;
            await outbox.EnqueueAsync(new PolicyReviewDue(o.Policy, o.Version, o.Due, policy.Owner), ct);
            await notifications.SendAsync(new NotificationRequest([policy.Owner], NotificationCategories.DocumentUpdate, ReviewDueTemplate, ReviewsLink(o.Policy),
                DedupeKey: $"policies.review:{o.Id:N}:due"), ct);
        }

        var overdue = await session.QueryAsync(
            """
            update policies.policy_review r set overdue_at = @now
            where r.id in (
              select r2.id from policies.policy_review r2 join policies.policy p on p.id = r2.policy_id
              where r2.completed_at is null and r2.overdue_at is null and r2.due_at <= @now and p.status <> 'retired'
              order by r2.due_at, r2.id limit @limit)
            returning r.id, r.policy_id
            """, new { now, limit }, r => (Id: r.GetGuid(0), Policy: r.GetGuid(1)), ct);
        foreach (var o in overdue)
        {
            var policy = (await register.RowAsync(o.Policy, false, ct))!;
            var recipients = await OfficersAsync(policy, ct);
            recipients.Add(policy.Owner);
            if (await reportingLine.ManagerOfAsync(policy.Owner, ct) is { } manager)
            {
                recipients.Add(manager);
            }
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, ReviewOverdueTemplate, ReviewsLink(o.Policy),
                DedupeKey: $"policies.review:{o.Id:N}:overdue"), ct);
        }
        return opened.Count + overdue.Count;
    }

    /// <summary>
    /// A version came into force. An open review of an older version is answered by it ("revised"), and when it is the revision started by a
    /// major revision outcome, the owner is asked whether to launch re-attestation (US5 scenario 3).
    /// </summary>
    public async Task HandleAsync(PolicyPublished message, CancellationToken ct)
    {
        if (await register.RowAsync(message.PolicyId, false, ct) is not { } policy || await register.VersionByNumberAsync(message.PolicyId, message.Version, ct) is not { } version)
        {
            return;
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update policies.policy_review set outcome = 'revised', completed_at = @now, draft_version_id = @version, next_review_at = @next
            where policy_id = @policy and completed_at is null and version < @number
            """, new { policy = policy.Id, now, version = version.Id, number = message.Version, next = policy.NextReviewAt }, ct);
        var asked = await session.QueryAsync(
            """
            update policies.policy_review set reattest_asked_at = @now
            where policy_id = @policy and draft_version_id = @version and outcome = 'major_revision' and reattest_asked_at is null
            returning id
            """, new { policy = policy.Id, version = version.Id, now }, r => r.GetGuid(0), ct);
        if (asked.Count > 0 && message.Material)
        {
            await notifications.SendAsync(new NotificationRequest([policy.Owner], NotificationCategories.DocumentUpdate, ReattestTemplate, PolicyCampaigns.CampaignsLink(policy.Id),
                DedupeKey: $"policies.review:{asked[0]:N}:reattest"), ct);
        }
    }
}
