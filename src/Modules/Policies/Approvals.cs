using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Drafting, approval and publishing of policy versions (spec 025 US1, FR-002 to FR-005). The owner, or a compliance
/// officer, writes the draft; submitting runs the policy's route on the process engine (spec 024) with tasks in the
/// inbox (spec 016), never with the owner, the editors or the submitter as approvers. Approvers compare the draft with
/// the version in force and comment on clauses. Final approval gives the version its number and effective date; on
/// that date it comes into force and the version before it is superseded.
/// </summary>
public sealed class PolicyApprovals(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IProcessEngine engine,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string RoutePrefix = "policies.approval.";
    public const int MaxCommentLength = 2000;

    public const string ApprovalWaiting = "policies.approval_waiting";
    public const string VersionApproved = "policies.version_approved";
    public const string VersionReturned = "policies.version_returned";

    public static string Link(Guid id) => $"/policies/{id}";

    public static string ReviewLink(Guid id) => $"/policies/{id}/review";

    internal static bool IsApproval(string definitionKey) => definitionKey.StartsWith(RoutePrefix, StringComparison.Ordinal);

    /// <summary>The version an instance belongs to: its business key starts with the version ID.</summary>
    internal static Guid? VersionIdOf(string businessKey) =>
        businessKey.Length >= 32 && Guid.TryParseExact(businessKey[..32], "N", out var id) ? id : null;

    internal static CandidateRule Candidates(RouteStepSetting step) =>
        step.ComplianceOfficers ? new CandidateRule(Permission: Permissions.PolicyManage) : new CandidateRule(Users: step.Approvers);

    /// <summary>A route with no steps has one: the compliance officers at the policy's unit.</summary>
    internal static IReadOnlyList<RouteStepSetting> StepsOf(PolicyRow policy) => policy.Route.Count > 0 ? policy.Route : [new RouteStepSetting(ComplianceOfficers: true)];

    /// <summary>One engine definition per policy and set of steps, so a change of approvers applies to the next submission only.</summary>
    private async Task<string> RouteKeyAsync(PolicyRow policy, CancellationToken ct)
    {
        var steps = StepsOf(policy);
        var shape = string.Join(";", steps.Select(s => s.ComplianceOfficers ? "m" : "u:" + string.Join(",", (s.Approvers ?? []).Order())));
        var key = $"{RoutePrefix}{policy.Id:N}.{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..12]}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            await engine.PublishRouteAsync(key, RouteSources.PolicyRoute,
                new RouteSpec($"policy approval {policy.Id:N}", steps.Select((s, i) => new RouteStep($"s{i}", s.Names?.GetValueOrDefault("en") ?? $"step {i + 1}", Candidates(s))).ToList()),
                ct);
        }
        return key;
    }

    private async Task<DateTimeOffset> StartOfAsync(DateOnly day, CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);
    }

    private async Task<DateOnly> TodayAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, zone));
    }

    private async Task<PolicyRow> EditableAsync(Guid id, CancellationToken ct)
    {
        var policy = await register.VisibleAsync(id, forUpdate: true, ct);
        if (!await register.EditsAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        return policy;
    }

    // ---- Draft ----

    /// <summary>The working version, for the owner, editors, compliance officers and the route's approvers.</summary>
    public async Task<VersionView> DraftAsync(Guid id, CancellationToken ct)
    {
        var policy = await register.VisibleAsync(id, false, ct);
        if (!await register.EditsAsync(policy, ct) && !await register.ManagesAsync(policy, ct) && !await register.ReviewsAsync(policy, ct))
        {
            throw AppException.NotFound();
        }
        var working = await register.WorkingAsync(id, false, ct) ?? throw AppException.NotFound();
        return await register.VersionViewAsync(working, ct);
    }

    /// <summary>Creates or replaces the working draft. A version in review cannot change: withdraw it first.</summary>
    public async Task<VersionView> SaveDraftAsync(Guid id, DraftRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var policy = await EditableAsync(id, ct);
        var working = await register.WorkingAsync(id, forUpdate: true, ct);
        if (working is { State: VersionStates.InReview })
        {
            throw AppException.Conflict("conflict.policies.in_review");
        }
        var errors = new ValidationErrors();
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        var (locales, titles, clauses) = ClauseRules.Normalize(request, enabled, errors);
        errors.ThrowIfAny();
        var now = clock.UtcNow;
        var note = string.IsNullOrWhiteSpace(request.ChangeNote) ? null : request.ChangeNote.Trim();
        var args = new
        {
            id = working?.Id ?? Ids.New(), policy = id, locales = locales.ToArray(), titles = new Jsonb(PolicyJson.Write(titles)), clauses = new Jsonb(PolicyJson.Write(clauses)),
            material = request.Material ?? working?.Material ?? true, note, user, now,
        };
        if (working is null)
        {
            await session.ExecAsync(
                """
                insert into policies.policy_version (tenant_id, id, policy_id, state, locales, titles, clauses, material, change_note, editors, created_at, updated_at)
                values (platform.current_tenant_id(), @id, @policy, 'draft', @locales, @titles, @clauses, @material, @note, array[@user], @now, @now)
                """, args, ct);
        }
        else
        {
            await session.ExecAsync(
                """
                update policies.policy_version set locales = @locales, titles = @titles, clauses = @clauses, material = @material, change_note = @note,
                  editors = case when @user = any(editors) then editors else editors || @user end, updated_at = @now
                where id = @id
                """, args, ct);
        }
        await register.RefreshStatusAsync(id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyDraftSaved, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = args.id, locales, clauses = clauses.Count, args.material }), ct);
        return await register.VersionViewAsync((await register.VersionAsync(args.id, false, ct))!, ct);
    }

    /// <summary>
    /// Submits the draft (US1 scenario 1). Every clause must be complete in every locale so the variants are approved
    /// together; the effective date is today or later, in the tenant's time zone.
    /// </summary>
    public async Task<PolicyView> SubmitAsync(Guid id, SubmitRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var policy = await EditableAsync(id, ct);
        var working = await register.WorkingAsync(id, forUpdate: true, ct) ?? throw AppException.Conflict("conflict.policies.no_draft");
        if (working.State != VersionStates.Draft)
        {
            throw AppException.Conflict("conflict.policies.in_review");
        }
        if (await register.ScheduledAsync(id, ct) is not null)
        {
            throw AppException.Conflict("conflict.policies.scheduled");
        }
        var errors = new ValidationErrors();
        ClauseRules.Complete(working.Locales, working.Clauses, policy.BindingLocale, errors);
        DateTimeOffset? from = null;
        if (request.EffectiveOn is { } day)
        {
            if (day < await TodayAsync(ct))
            {
                errors.Add("effectiveOn", "validation.policies.effective_on");
            }
            else
            {
                from = await StartOfAsync(day, ct);
            }
        }
        errors.ThrowIfAny();
        var key = await RouteKeyAsync(policy, ct);
        // The owner and everyone who edited the draft may never approve it (FR-004); the engine leaves out the submitter.
        var excluded = working.Editors.Append(policy.Owner).Distinct().ToArray();
        var instance = await engine.StartAsync(key, $"{working.Id:N}{Ids.New():N}", policy.OrgUnitId, user, excluded, policy.Owner, ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            update policies.policy_version set state = 'in_review', instance_id = @instance, requested_effective_from = @from, submitted_by = @user, submitted_at = @now,
              approvals = '[]', updated_at = @now
            where id = @version
            """, new { version = working.Id, instance = instance.Id, from, user, now }, ct);
        await register.RefreshStatusAsync(id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicySubmitted, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = working.Id, effectiveFrom = from, working.Material, instance = instance.Id, steps = StepsOf(policy).Count }), ct);
        return await register.GetAsync(id, ct);
    }

    /// <summary>The submitter or a compliance officer takes the version back; it is a draft again.</summary>
    public async Task<PolicyView> WithdrawAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, forUpdate: true, ct);
        var working = await register.WorkingAsync(id, forUpdate: true, ct);
        if (working is not { State: VersionStates.InReview, InstanceId: { } instance })
        {
            throw AppException.Conflict("conflict.policies.not_in_review");
        }
        if (working.SubmittedBy != user && !await register.ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        await engine.WithdrawAsync(instance, ct);
        await session.ExecAsync("update policies.policy_version set state = 'draft', updated_at = @now where id = @version", new { version = working.Id, now = clock.UtcNow }, ct);
        await register.RefreshStatusAsync(id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyWithdrawn, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = working.Id, instance }), ct);
        return await register.GetAsync(id, ct);
    }

    // ---- Compare and clause comments ----

    /// <summary>The working version against the version in force, clause by clause, for its editors and approvers.</summary>
    public async Task<CompareView> CompareAsync(Guid id, CancellationToken ct)
    {
        var policy = await register.VisibleAsync(id, false, ct);
        if (!await register.EditsAsync(policy, ct) && !await register.ManagesAsync(policy, ct) && !await register.ReviewsAsync(policy, ct))
        {
            throw AppException.NotFound();
        }
        var working = await register.WorkingAsync(id, false, ct) ?? throw AppException.NotFound();
        var current = policy.CurrentVersion is { } n ? await register.VersionByNumberAsync(id, n, ct) : null;
        var locales = working.Locales.Union(current?.Locales ?? []).ToList();
        var titlesChanged = current is not null && !locales.All(l => current.Titles.GetValueOrDefault(l) == working.Titles.GetValueOrDefault(l));
        return new CompareView(current?.Number, working.Id, working.Number, ClauseRules.Compare(current?.Clauses, working.Clauses, locales), titlesChanged);
    }

    private async Task<(PolicyRow Policy, VersionRow Version, bool Edits, bool Reviews)> CommentableAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        var policy = await register.VisibleAsync(id, false, ct);
        var version = await register.VersionAsync(versionId, false, ct);
        if (version is null || version.PolicyId != id)
        {
            throw AppException.NotFound();
        }
        var edits = await register.EditsAsync(policy, ct) || await register.ManagesAsync(policy, ct);
        var reviews = version.State is VersionStates.Draft or VersionStates.InReview && await register.ReviewsAsync(policy, ct);
        if (!edits && !reviews)
        {
            throw AppException.NotFound();
        }
        return (policy, version, edits, reviews);
    }

    public async Task<IReadOnlyList<CommentView>> CommentsAsync(Guid id, Guid versionId, CancellationToken ct)
    {
        var (_, version, _, _) = await CommentableAsync(id, versionId, ct);
        var rows = await session.QueryAsync(
            "select id, clause_key, author_user_id, text, created_at, resolved_at, resolved_by from policies.clause_comment where version_id = @versionId order by created_at, id",
            new { versionId = version.Id },
            r => (Id: r.GetGuid(0), Key: r.GetString(1), Author: r.GetGuid(2), Text: r.GetString(3), At: r.GetFieldValue<DateTimeOffset>(4),
                Resolved: r.Get<DateTimeOffset?>("resolved_at"), By: r.Get<Guid?>("resolved_by")), ct);
        var list = new List<CommentView>();
        foreach (var c in rows)
        {
            list.Add(new CommentView(c.Id, version.Id, c.Key, await register.PersonAsync(c.Author, ct), c.Text, c.At, c.Resolved,
                c.By is { } by ? await register.PersonAsync(by, ct) : null));
        }
        return list;
    }

    /// <summary>An approver, the owner, an editor or a compliance officer comments on a clause of a version under review or returned (US1 scenario 2).</summary>
    public async Task<CommentView> AddCommentAsync(Guid id, Guid versionId, CommentRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (policy, version, _, _) = await CommentableAsync(id, versionId, ct);
        if (version.State is not (VersionStates.Draft or VersionStates.InReview))
        {
            throw AppException.Conflict("conflict.policies.version_closed");
        }
        var text = request.Text?.Trim();
        var errors = new ValidationErrors();
        if (text is not { Length: > 0 and <= MaxCommentLength })
        {
            errors.Add("text", "validation.policies.comment");
        }
        if (request.ClauseKey is not { } key || version.Clauses.All(c => c.Key != key))
        {
            errors.Add("clauseKey", "validation.policies.clause_key");
        }
        errors.ThrowIfAny();
        var commentId = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into policies.clause_comment (tenant_id, id, version_id, clause_key, author_user_id, text, created_at)
            values (platform.current_tenant_id(), @commentId, @version, @key, @user, @text, @now)
            """, new { commentId, version = version.Id, key = request.ClauseKey, user, text, now }, ct);
        if (policy.Owner != user)
        {
            await notifications.SendAsync(new NotificationRequest([policy.Owner], NotificationCategories.Comment, "policies.clause_comment", ReviewLink(id),
                DedupeKey: $"policies.comment:{commentId:N}"), ct);
        }
        return new CommentView(commentId, version.Id, request.ClauseKey!, await register.PersonAsync(user, ct), text!, now, null, null);
    }

    /// <summary>The owner, an editor or a compliance officer marks a comment as dealt with.</summary>
    public async Task<CommentView> ResolveCommentAsync(Guid id, Guid versionId, Guid commentId, CancellationToken ct)
    {
        var user = context.RequireUser();
        var (_, version, edits, _) = await CommentableAsync(id, versionId, ct);
        if (!edits)
        {
            throw AppException.Denied();
        }
        var now = clock.UtcNow;
        if (await session.ExecAsync("update policies.clause_comment set resolved_at = @now, resolved_by = @user where id = @commentId and version_id = @version and resolved_at is null",
            new { commentId, version = version.Id, user, now }, ct) == 0
            && await session.ScalarAsync<int>("select count(*)::int from policies.clause_comment where id = @commentId and version_id = @version", new { commentId, version = version.Id }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        return (await CommentsAsync(id, versionId, ct)).Single(c => c.Id == commentId);
    }

    // ---- Decisions (from the task inbox) ----

    /// <summary>Records one approved step in the version's approval trail.</summary>
    internal async Task RecordApprovalAsync(VersionRow version, PolicyRow policy, UserTaskInfo done, CancellationToken ct)
    {
        var steps = StepsOf(policy);
        var index = done.ElementId.StartsWith('s') && int.TryParse(done.ElementId[1..], out var i) && i < steps.Count ? i : -1;
        var approval = new StoredApproval(done.ElementId, index >= 0 && steps[index].Names is { Count: > 0 } names ? new Dictionary<string, string>(names) : null,
            done.CompletedBy ?? context.RequireUser(), done.OnBehalfOf, done.CompletedAt ?? clock.UtcNow);
        await session.ExecAsync("update policies.policy_version set approvals = approvals || @approval where id = @id",
            new { id = version.Id, approval = new Jsonb(PolicyJson.Write(new[] { approval })) }, ct);
    }

    /// <summary>All steps approved (US1 scenario 3): the next number and the effective date, at once or on the date.</summary>
    internal async Task ApprovedAsync(PolicyRow policy, VersionRow version, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var number = (await session.ScalarAsync<int?>("select max(number) from policies.policy_version where policy_id = @id", new { id = policy.Id }, ct) ?? 0) + 1;
        var from = version.RequestedEffectiveFrom is { } at && at > now ? at : now;
        await session.ExecAsync(
            "update policies.policy_version set state = 'approved', number = @number, effective_from = @from, approved_at = @now, updated_at = @now where id = @id",
            new { id = version.Id, number, from, now }, ct);
        await register.RefreshStatusAsync(policy.Id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyApproved, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = number, versionId = version.Id, effectiveFrom = from, approver = decider, version.Material }), ct);
        var recipients = new[] { policy.Owner, version.SubmittedBy }.OfType<Guid>().Distinct().ToArray();
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, VersionApproved, Link(policy.Id),
            DedupeKey: $"policies.version:{version.Id:N}:approved"), ct);
        if (from <= now)
        {
            await MakeEffectiveAsync(policy.Id, now, ct);
        }
    }

    /// <summary>Returned or rejected: the version is a draft again, and the owner and submitter hear it; clause comments stay with it.</summary>
    internal async Task ReturnedAsync(PolicyRow policy, VersionRow version, string? outcome, Guid decider, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await session.ExecAsync("update policies.policy_version set state = 'draft', updated_at = @now where id = @id", new { id = version.Id, now }, ct);
        await register.RefreshStatusAsync(policy.Id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyReturned, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { versionId = version.Id, outcome, decider }), ct);
        var recipients = new[] { policy.Owner, version.SubmittedBy }.OfType<Guid>().Distinct().ToArray();
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, VersionReturned, ReviewLink(policy.Id),
            DedupeKey: $"policies.version:{version.Id:N}:returned:{version.InstanceId:N}"), ct);
    }

    /// <summary>
    /// Puts an approved version in force: the version in force becomes superseded, the register moves to the new number
    /// and review date, and <see cref="PolicyPublished"/> tells search and the campaigns.
    /// </summary>
    internal async Task<bool> MakeEffectiveAsync(Guid id, DateTimeOffset now, CancellationToken ct)
    {
        if (await register.RowAsync(id, forUpdate: true, ct) is not { } policy || await register.ScheduledAsync(id, ct) is not { Number: { } number, EffectiveFrom: { } from } version
            || from > now)
        {
            return false;
        }
        var previous = policy.CurrentVersion;
        if (previous is { } old)
        {
            await session.ExecAsync("update policies.policy_version set state = 'superseded', superseded_at = @now, updated_at = @now where policy_id = @id and number = @old",
                new { id, old, now }, ct);
        }
        await session.ExecAsync("update policies.policy_version set state = 'effective', updated_at = @now where id = @version", new { version = version.Id, now }, ct);
        await session.ExecAsync(
            """
            update policies.policy set current_version = @number, effective_from = @from, next_review_at = @review, updated_at = @now, row_version = row_version + 1
            where id = @id
            """, new { id, number, from, review = from.AddMonths(policy.ReviewMonths), now }, ct);
        await register.RefreshStatusAsync(id, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyPublished, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = number, effectiveFrom = from, superseded = previous, version.Material }), ct);
        if (previous is { } replaced)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PolicySuperseded, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
                Changes: new { version = replaced, by = number }), ct);
            await outbox.EnqueueAsync(new PolicySuperseded(id, replaced, number), ct);
        }
        await outbox.EnqueueAsync(new PolicyPublished(id, number, previous, version.Material, policy.OrgUnitId), ct);
        return true;
    }

    /// <summary>Approved versions whose effective date came, for one tenant.</summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        var moved = 0;
        foreach (var id in await session.QueryAsync(
            "select policy_id from policies.policy_version where state = 'approved' and effective_from <= @now order by effective_from, id limit @limit",
            new { now, limit }, r => r.GetGuid(0), ct))
        {
            moved += await MakeEffectiveAsync(id, now, ct) ? 1 : 0;
        }
        return moved;
    }
}

/// <summary>
/// Policy approvals in the spec 016 task inbox. Named approvers hear that a version waits; the decision moves the
/// version on in the same transaction.
/// </summary>
public sealed class PolicyTaskSource(
    ITenantContext context,
    IProcessEngine engine,
    PolicyRegister register,
    PolicyApprovals approvals,
    INotificationSender notifications) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public bool Handles(string definitionKey) => PolicyApprovals.IsApproval(definitionKey);

    private async Task<(PolicyRow Policy, VersionRow Version)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (PolicyApprovals.VersionIdOf(task.BusinessKey) is not { } versionId || await register.VersionAsync(versionId, forUpdate, ct) is not { } version
            || version.State != VersionStates.InReview || version.InstanceId != task.InstanceId
            || await register.RowAsync(version.PolicyId, forUpdate, ct) is not { } policy)
        {
            return null;
        }
        return (policy, version);
    }

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not var (policy, version))
        {
            return null;
        }
        var title = version.Titles.ToDictionary(t => t.Key, t => $"{policy.Code} {t.Value}");
        return new TaskSubject("policy_approval", title, PolicyApprovals.ReviewLink(policy.Id), policy.OrgUnitId, version.SubmittedBy);
    }

    /// <summary>The engine already keeps them out; this holds the line for the person whose authority a delegate uses.</summary>
    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is var (policy, version)
        && authority != policy.Owner && authority != version.SubmittedBy && !version.Editors.Contains(authority);

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not var (policy, version))
        {
            return;
        }
        var decider = done.CompletedBy ?? context.RequireUser();
        if (done.Outcome == TaskOutcomes.Approve)
        {
            await approvals.RecordApprovalAsync(version, policy, done, ct);
        }
        var instance = await engine.GetInstanceAsync(task.InstanceId, ct);
        if (instance is not { Status: "completed" })
        {
            return; // The next step is open.
        }
        if (instance.Outcome == InstanceOutcomes.Approved)
        {
            await approvals.ApprovedAsync(policy, version, decider, ct);
        }
        else
        {
            await approvals.ReturnedAsync(policy, version, instance.Outcome, decider, ct);
        }
    }

    /// <summary>Named approvers of the step that just opened hear that a version waits for them.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!PolicyApprovals.IsApproval(message.DefinitionKey) || PolicyApprovals.VersionIdOf(message.BusinessKey) is not { } versionId
            || await register.VersionAsync(versionId, false, ct) is not { State: VersionStates.InReview } version || version.InstanceId != message.InstanceId
            || await register.RowAsync(version.PolicyId, false, ct) is not { } policy
            || !message.ElementId.StartsWith('s') || !int.TryParse(message.ElementId[1..], out var index))
        {
            return;
        }
        var steps = PolicyApprovals.StepsOf(policy);
        if (index >= steps.Count)
        {
            return;
        }
        var named = (steps[index].Approvers ?? []).Where(a => a != policy.Owner && a != version.SubmittedBy && !version.Editors.Contains(a)).ToArray();
        if (named.Length > 0)
        {
            await notifications.SendAsync(new NotificationRequest(named, NotificationCategories.ApprovalWaiting, PolicyApprovals.ApprovalWaiting,
                PolicyApprovals.ReviewLink(policy.Id) + "?task=" + message.TaskId, DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
        }
    }
}
