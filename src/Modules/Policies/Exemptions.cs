using System.Security.Cryptography;
using System.Text;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

public static class ExemptionScopes
{
    public const string Person = "person";
    public const string Unit = "unit";
}

public static class ExemptionStatuses
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Expired = "expired";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Pending, Approved, Rejected, Withdrawn, Expired };
}

/// <summary>A request for an exemption. Scope is <c>person</c> (the requester unless a compliance officer names someone) or <c>unit</c>.</summary>
public sealed record ExemptionRequest(string? Scope, Guid? UserId, Guid? UnitId, string? Reason, DateOnly? EndsOn);

public sealed record ExemptionView(
    Guid Id,
    PolicySummary Policy,
    int? Version,
    string Scope,
    PolicyPerson? Person,
    Guid? UnitId,
    IReadOnlyDictionary<string, string>? UnitNames,
    string Reason,
    DateOnly EndsOn,
    DateTimeOffset EndsAt,
    string Status,
    PolicyPerson RequestedBy,
    DateTimeOffset RequestedAt,
    IReadOnlyList<ApprovalView> Approvals,
    PolicyPerson? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ClosedAt,
    bool CanWithdraw);

/// <summary>What the current user may do on a policy's exemptions: ask for themselves, and as a compliance officer for someone else or a unit.</summary>
public sealed record ExemptionRights(bool Request, bool RequestFor, bool SeesAll);

public sealed record PolicyExemptionsView(PolicySummary Policy, IReadOnlyList<ExemptionView> Items, ExemptionRights Rights);

/// <summary>An approved exemption that covers a person, for the compliance views.</summary>
public sealed record ExemptionCover(Guid ExemptionId, Guid PolicyId, string Scope, Guid? Subject, Guid? UnitId, DateOnly EndsOn, DateTimeOffset EndsAt);

internal sealed record ExemptionRow(
    Guid Id,
    Guid PolicyId,
    int? Version,
    string Scope,
    Guid? Subject,
    Guid? Unit,
    string Reason,
    DateOnly EndsOn,
    DateTimeOffset EndsAt,
    string Status,
    Guid RequestedBy,
    DateTimeOffset RequestedAt,
    Guid? InstanceId,
    IReadOnlyList<StoredApproval> Approvals,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? ClosedAt)
{
    public const string Columns =
        "id, policy_id, version, scope, subject_user_id, org_unit_id, reason, ends_on, ends_at, status, requested_by, requested_at, instance_id, approvals::text, decided_by, decided_at, closed_at";

    public static ExemptionRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<int?>("version"), r.GetString(3), r.Get<Guid?>("subject_user_id"),
        r.Get<Guid?>("org_unit_id"), r.GetString(6), r.GetFieldValue<DateOnly>(7), r.GetFieldValue<DateTimeOffset>(8), r.GetString(9), r.GetGuid(10),
        r.GetFieldValue<DateTimeOffset>(11), r.Get<Guid?>("instance_id"), PolicyJson.Read<List<StoredApproval>>(r.GetString(13)) ?? [], r.Get<Guid?>("decided_by"),
        r.Get<DateTimeOffset?>("decided_at"), r.Get<DateTimeOffset?>("closed_at"));

    /// <summary>The person the exemption is for, who hears about it; for a unit, the requester.</summary>
    public Guid Holder => Subject ?? RequestedBy;
}

/// <summary>
/// Exemptions (spec 025 US7, FR-014). A person the policy applies to asks for an exemption with a reason and an end
/// date; a compliance officer may ask for someone else or for a unit with its subtree. The request runs the exemption
/// route on the process engine: the policy owner, then the compliance officers at the policy's unit, never the
/// requester or the person exempted. Approved exemptions form the register and show on the person's compliance view;
/// 14 days before the end date the person and the owner hear of it, and on the end date the exemption closes on its own.
/// An exemption does not take the person out of attestation campaigns. Needs <c>policy.exemptions</c>.
/// </summary>
public sealed class PolicyExemptions(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IEntitlementService entitlements,
    IAuthorizationService authz,
    IAudiences audiences,
    IUserDirectory users,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    IProcessEngine engine,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string RoutePrefix = "policies.exemption.";
    public const int MaxReasonLength = 2000;
    public const int MaxDays = 365;
    public const int ReminderDays = 14;
    public const int RegisterMax = 500;

    public const string ApprovedTemplate = "policies.exemption_approved";
    public const string RejectedTemplate = "policies.exemption_rejected";
    public const string WaitingTemplate = "policies.exemption_waiting";
    public const string ExpiringTemplate = "policies.exemption_expiring";
    public const string ExpiredTemplate = "policies.exemption_expired";

    public static string Link(Guid policyId) => $"/policies/{policyId}/exemptions";

    public static string Link(Guid policyId, Guid exemptionId) => $"{Link(policyId)}?exemption={exemptionId}";

    internal static bool IsExemption(string definitionKey) => definitionKey.StartsWith(RoutePrefix, StringComparison.Ordinal);

    internal static Guid? IdOf(string businessKey) => businessKey.Length >= 32 && Guid.TryParseExact(businessKey[..32], "N", out var id) ? id : null;

    internal async Task<ExemptionRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {ExemptionRow.Columns} from policies.exemption where id = @id{(forUpdate ? " for update" : string.Empty)}",
            new { id }, ExemptionRow.Read, ct);

    private async Task<(DateOnly Today, TimeZoneInfo Zone)> TodayAsync(CancellationToken ct)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById((await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone);
        return (DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, zone)), zone);
    }

    /// <summary>The end date counts in full: the exemption closes when the next day starts in the tenant's time zone.</summary>
    private static DateTimeOffset EndOf(DateOnly day, TimeZoneInfo zone) =>
        new(TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), zone), TimeSpan.Zero);

    private async Task<ExemptionView> ViewAsync(ExemptionRow e, PolicyRow policy, bool canWithdraw, CancellationToken ct)
    {
        var unitNames = e.Unit is { } unit ? (await hierarchy.GetNamesAsync([unit], ct)).GetValueOrDefault(unit) : null;
        return new ExemptionView(e.Id, await register.SummaryAsync(policy, ct), e.Version, e.Scope, e.Subject is { } s ? await register.PersonAsync(s, ct) : null, e.Unit,
            unitNames, e.Reason, e.EndsOn, e.EndsAt, e.Status, await register.PersonAsync(e.RequestedBy, ct), e.RequestedAt, await register.ApprovalsAsync(e.Approvals, ct),
            e.DecidedBy is { } d ? await register.PersonAsync(d, ct) : null, e.DecidedAt, e.ClosedAt, canWithdraw);
    }

    private bool MayWithdraw(ExemptionRow e, bool manages) => e.Status == ExemptionStatuses.Pending && (manages || e.RequestedBy == context.ActorId);

    /// <summary>The exemptions of a policy: all of them for the owner and compliance officers, otherwise the ones the person asked for or holds.</summary>
    public async Task<PolicyExemptionsView> ListAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyExemptions, ct);
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var manages = await register.ManagesAsync(policy, ct);
        var all = manages || policy.Owner == user;
        var rows = await session.QueryAsync(
            $"""
            select {ExemptionRow.Columns} from policies.exemption
            where policy_id = @id and (@all or subject_user_id = @user or requested_by = @user)
            order by case status when 'pending' then 0 when 'approved' then 1 else 2 end, requested_at desc, id
            """, new { id, all, user }, ExemptionRow.Read, ct);
        var items = new List<ExemptionView>();
        foreach (var e in rows)
        {
            items.Add(await ViewAsync(e, policy, MayWithdraw(e, manages), ct));
        }
        var live = policy.Status != PolicyStatuses.Retired && policy.CurrentVersion is not null;
        return new PolicyExemptionsView(await register.SummaryAsync(policy, ct), items,
            new ExemptionRights(live && await register.AppliesToMeAsync(policy, ct), live && manages, all));
    }

    /// <summary>
    /// The exemption register across policies for compliance officers (US7 scenario 2): the exemptions of every policy
    /// they manage, pending and approved unless a status is asked.
    /// </summary>
    public async Task<IReadOnlyList<ExemptionView>> RegisterAsync(string? status, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyExemptions, ct);
        context.RequireUser();
        if (status is not null && !ExemptionStatuses.All.Contains(status))
        {
            var invalid = new ValidationErrors();
            invalid.Add("status", "validation.policies.exemption_status");
            invalid.ThrowIfAny();
        }
        if (!(await authz.CoverageAsync(Permissions.PolicyManage, ct)).Any)
        {
            throw AppException.Denied();
        }
        var statuses = status is null ? new[] { ExemptionStatuses.Pending, ExemptionStatuses.Approved } : [status];
        var rows = await session.QueryAsync(
            $"select {ExemptionRow.Columns} from policies.exemption where status = any(@statuses) order by ends_at, id limit @max",
            new { statuses, max = RegisterMax }, ExemptionRow.Read, ct);
        var list = new List<ExemptionView>();
        foreach (var e in rows)
        {
            if (await register.RowAsync(e.PolicyId, false, ct) is { } policy && await register.ManagesAsync(policy, ct))
            {
                list.Add(await ViewAsync(e, policy, MayWithdraw(e, true), ct));
            }
        }
        return list;
    }

    /// <summary>Asks for an exemption (US7 scenario 1); the request goes to the policy owner, then to the compliance officers.</summary>
    public async Task<ExemptionView> RequestAsync(Guid id, ExemptionRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyExemptions, ct);
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, forUpdate: true, ct);
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        if (policy.CurrentVersion is null)
        {
            throw AppException.Conflict("conflict.policies.not_published");
        }
        var manages = await register.ManagesAsync(policy, ct);
        var errors = new ValidationErrors();
        var scope = request.Scope ?? ExemptionScopes.Person;
        Guid? subject = null;
        Guid? unit = null;
        switch (scope)
        {
            case ExemptionScopes.Person:
                subject = request.UserId ?? user;
                if (subject != user && !manages)
                {
                    throw AppException.Denied();
                }
                if (await users.GetAsync(subject.Value, ct) is null || !await audiences.IncludesAsync(policy.AppliesTo, subject.Value, ct))
                {
                    if (subject == user)
                    {
                        throw AppException.Denied();
                    }
                    errors.Add("userId", "validation.policies.exemption_person");
                }
                break;
            case ExemptionScopes.Unit:
                if (!manages)
                {
                    throw AppException.Denied();
                }
                unit = request.UnitId;
                if (unit is not { } u || !await hierarchy.UnitExistsAsync(u, ct))
                {
                    errors.Add("unitId", "validation.policies.org_unit");
                }
                break;
            default:
                errors.Add("scope", "validation.policies.exemption_scope");
                break;
        }
        var reason = request.Reason?.Trim();
        if (reason is not { Length: > 0 and <= MaxReasonLength })
        {
            errors.Add("reason", "validation.policies.exemption_reason");
        }
        var (today, zone) = await TodayAsync(ct);
        if (request.EndsOn is not { } endsOn || endsOn < today || endsOn > today.AddDays(MaxDays))
        {
            errors.Add("endsOn", "validation.policies.exemption_ends_on");
            endsOn = today;
        }
        errors.ThrowIfAny();

        var target = subject ?? unit!.Value;
        if (await session.ScalarAsync<int>(
                "select count(*)::int from policies.exemption where policy_id = @id and coalesce(subject_user_id, org_unit_id) = @target and status in ('pending', 'approved')",
                new { id, target }, ct) > 0)
        {
            throw AppException.Conflict("conflict.policies.exemption_exists");
        }

        var exemptionId = Ids.New();
        var now = clock.UtcNow;
        var key = await RouteKeyAsync(policy, user, subject, ct);
        var excluded = subject is { } s ? new[] { s } : [];
        var instance = await engine.StartAsync(key, $"{exemptionId:N}{Ids.New():N}", policy.OrgUnitId, user, excluded, subject ?? user, ct: ct);
        await session.ExecAsync(
            """
            insert into policies.exemption (tenant_id, id, policy_id, version, scope, subject_user_id, org_unit_id, reason, ends_on, ends_at, status, requested_by,
              requested_at, instance_id)
            values (platform.current_tenant_id(), @exemptionId, @id, @version, @scope, @subject, @unit, @reason, @endsOn, @endsAt, 'pending', @user, @now, @instance)
            """, new { exemptionId, id, version = policy.CurrentVersion, scope, subject, unit, reason, endsOn, endsAt = EndOf(endsOn, zone), user, now, instance = instance.Id },
            ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyExemptionRequested, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { exemption = exemptionId, scope, person = subject, unit, endsOn, requestedBy = user, instance = instance.Id }), ct);
        var row = (await RowAsync(exemptionId, false, ct))!;
        return await ViewAsync(row, policy, MayWithdraw(row, manages), ct);
    }

    /// <summary>
    /// One engine definition per shape of the route: the owner's step unless the owner asked or is the person exempted,
    /// then the compliance officers at the policy's unit.
    /// </summary>
    private async Task<string> RouteKeyAsync(PolicyRow policy, Guid requester, Guid? subject, CancellationToken ct)
    {
        var withOwner = policy.Owner != requester && policy.Owner != subject;
        var shape = withOwner ? $"o:{policy.Owner:N};m" : "m";
        var key = $"{RoutePrefix}{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..16]}";
        if (await engine.GetDefinitionAsync(key, null, ct) is null)
        {
            var steps = new List<RouteStep>();
            if (withOwner)
            {
                steps.Add(new RouteStep("owner", "policy owner", new CandidateRule(Users: [policy.Owner])));
            }
            steps.Add(new RouteStep("compliance", "compliance officers", new CandidateRule(Permission: Permissions.PolicyManage)));
            await engine.PublishRouteAsync(key, RouteSources.PolicyRoute, new RouteSpec("policy exemption", steps), ct);
        }
        return key;
    }

    /// <summary>The requester, or a compliance officer, takes back a pending request.</summary>
    public async Task<ExemptionView> WithdrawAsync(Guid id, Guid exemptionId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyExemptions, ct);
        context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var row = await RowAsync(exemptionId, forUpdate: true, ct);
        var manages = await register.ManagesAsync(policy, ct);
        if (row is null || row.PolicyId != id || (!manages && policy.Owner != context.ActorId && row.RequestedBy != context.ActorId && row.Subject != context.ActorId))
        {
            throw AppException.NotFound();
        }
        if (row.Status != ExemptionStatuses.Pending)
        {
            throw AppException.Conflict("conflict.policies.exemption_closed");
        }
        if (!MayWithdraw(row, manages))
        {
            throw AppException.Denied();
        }
        if (row.InstanceId is { } instance)
        {
            await engine.WithdrawAsync(instance, ct);
        }
        await session.ExecAsync("update policies.exemption set status = 'withdrawn', closed_at = @now where id = @exemptionId", new { exemptionId, now = clock.UtcNow }, ct);
        var updated = (await RowAsync(exemptionId, false, ct))!;
        return await ViewAsync(updated, policy, false, ct);
    }

    // ---- Decisions (from the task inbox) ----

    internal async Task RecordApprovalAsync(ExemptionRow row, UserTaskInfo done, CancellationToken ct)
    {
        var approval = new StoredApproval(done.ElementId, null, done.CompletedBy ?? context.RequireUser(), done.OnBehalfOf, done.CompletedAt ?? clock.UtcNow);
        await session.ExecAsync("update policies.exemption set approvals = approvals || @approval where id = @id",
            new { id = row.Id, approval = new Jsonb(PolicyJson.Write(new[] { approval })) }, ct);
    }

    /// <summary>The route ended: approved goes into the register (US7 scenario 2); rejected or returned closes the request.</summary>
    internal async Task DecidedAsync(ExemptionRow row, PolicyRow policy, string? outcome, Guid decider, string? comment, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var approved = outcome == InstanceOutcomes.Approved;
        var status = approved ? ExemptionStatuses.Approved : ExemptionStatuses.Rejected;
        await session.ExecAsync(
            """
            update policies.exemption set status = @status, decided_by = @decider, decided_at = @now, decision_comment = @comment,
              closed_at = case when @approved then null else @now end
            where id = @id
            """, new { id = row.Id, status, decider, now, comment, approved }, ct);
        var recipients = new[] { row.RequestedBy, row.Holder }.Distinct().ToArray();
        if (approved)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PolicyExemptionApproved, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
                Changes: new { exemption = row.Id, row.Scope, person = row.Subject, unit = row.Unit, row.EndsOn, approver = decider }), ct);
            await outbox.EnqueueAsync(new ExemptionApproved(policy.Id, row.Id, row.Scope, row.Subject, row.Unit, row.EndsAt), ct);
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, ApprovedTemplate, Link(policy.Id, row.Id),
                DedupeKey: $"policies.exemption:{row.Id:N}:approved"), ct);
        }
        else
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PolicyExemptionRejected, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
                Changes: new { exemption = row.Id, outcome, decider }), ct);
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.DocumentUpdate, RejectedTemplate, Link(policy.Id, row.Id),
                DedupeKey: $"policies.exemption:{row.Id:N}:rejected"), ct);
        }
    }

    // ---- Coverage for the compliance views ----

    /// <summary>The approved exemptions of one policy, for the compliance view to mark the people they cover.</summary>
    internal async Task<IReadOnlyList<ExemptionCover>> ActiveOnAsync(Guid policyId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyExemptions, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            "select id, policy_id, scope, subject_user_id, org_unit_id, ends_on, ends_at from policies.exemption where policy_id = @policyId and status = 'approved'",
            new { policyId }, ReadCover, ct);
    }

    private static ExemptionCover ReadCover(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.Get<Guid?>("subject_user_id"),
        r.Get<Guid?>("org_unit_id"), r.GetFieldValue<DateOnly>(5), r.GetFieldValue<DateTimeOffset>(6));

    /// <summary>The approved exemptions that cover one person, theirs or their units' (US7 scenario 2), for the per-person view.</summary>
    internal async Task<IReadOnlyList<ExemptionView>> CoveringAsync(Guid userId, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyExemptions, ct))
        {
            return [];
        }
        var units = new HashSet<Guid>();
        foreach (var m in await hierarchy.GetActiveMembershipsAsync(userId, ct))
        {
            units.UnionWith(await hierarchy.GetAncestorsAndSelfAsync(m.OrgUnitId, ct));
        }
        var rows = await session.QueryAsync(
            $"select {ExemptionRow.Columns} from policies.exemption where status = 'approved' and (subject_user_id = @userId or org_unit_id = any(@units)) order by ends_at, id",
            new { userId, units = units.ToArray() }, ExemptionRow.Read, ct);
        var list = new List<ExemptionView>();
        foreach (var e in rows)
        {
            if (await register.RowAsync(e.PolicyId, false, ct) is { } policy)
            {
                list.Add(await ViewAsync(e, policy, false, ct));
            }
        }
        return list;
    }

    // ---- Maintenance ----

    /// <summary>
    /// For one tenant: tells the person and the owner 14 days before an exemption ends, then closes the exemptions whose
    /// end date passed and tells them it ended (US7 scenario 3).
    /// </summary>
    public async Task<int> RunDueAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyExemptions, ct))
        {
            return 0;
        }
        var soon = await session.QueryAsync(
            $"""
            update policies.exemption set reminded_at = @now
            where id in (select id from policies.exemption where status = 'approved' and reminded_at is null and ends_at > @now and ends_at <= @soon
                         order by ends_at, id limit @limit)
            returning {ExemptionRow.Columns}
            """, new { now, soon = now.AddDays(ReminderDays), limit }, ExemptionRow.Read, ct);
        foreach (var e in soon)
        {
            if (await register.RowAsync(e.PolicyId, false, ct) is { } policy)
            {
                await notifications.SendAsync(new NotificationRequest(new[] { e.Holder, policy.Owner }.Distinct().ToArray(), NotificationCategories.DocumentUpdate,
                    ExpiringTemplate, Link(policy.Id, e.Id), DedupeKey: $"policies.exemption:{e.Id:N}:expiring"), ct);
            }
        }

        var ended = await session.QueryAsync(
            $"""
            update policies.exemption set status = 'expired', closed_at = @now
            where id in (select id from policies.exemption where status = 'approved' and ends_at <= @now order by ends_at, id limit @limit)
            returning {ExemptionRow.Columns}
            """, new { now, limit }, ExemptionRow.Read, ct);
        foreach (var e in ended)
        {
            if (await register.RowAsync(e.PolicyId, false, ct) is not { } policy)
            {
                continue;
            }
            await audit.WriteAsync(new AuditEntry(AuditActions.PolicyExemptionExpired, Securables.Policy, policy.Id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
                Changes: new { exemption = e.Id, e.Scope, person = e.Subject, unit = e.Unit, e.EndsOn }), ct);
            await outbox.EnqueueAsync(new ExemptionExpired(policy.Id, e.Id, e.Scope, e.Subject, e.Unit), ct);
            await notifications.SendAsync(new NotificationRequest(new[] { e.Holder, policy.Owner }.Distinct().ToArray(), NotificationCategories.DocumentUpdate,
                ExpiredTemplate, Link(policy.Id, e.Id), DedupeKey: $"policies.exemption:{e.Id:N}:expired"), ct);
        }
        return soon.Count + ended.Count;
    }
}

/// <summary>Exemption requests in the spec 016 task inbox; the decision moves the request on in the same transaction.</summary>
public sealed class ExemptionTaskSource(
    ITenantContext context,
    IProcessEngine engine,
    PolicyRegister register,
    PolicyExemptions exemptions,
    INotificationSender notifications) : ITaskSource, IMessageHandler<UserTaskCreated>
{
    public bool Handles(string definitionKey) => PolicyExemptions.IsExemption(definitionKey);

    private async Task<(ExemptionRow Row, PolicyRow Policy)?> WaitingAsync(UserTaskInfo task, bool forUpdate, CancellationToken ct)
    {
        if (PolicyExemptions.IdOf(task.BusinessKey) is not { } id || await exemptions.RowAsync(id, forUpdate, ct) is not { } row
            || row.Status != ExemptionStatuses.Pending || row.InstanceId != task.InstanceId || await register.RowAsync(row.PolicyId, false, ct) is not { } policy)
        {
            return null;
        }
        return (row, policy);
    }

    public async Task<TaskSubject?> DescribeAsync(UserTaskInfo task, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, false, ct) is not var (row, policy))
        {
            return null;
        }
        var title = policy.Names.ToDictionary(n => n.Key, n => $"{policy.Code} {n.Value}");
        return new TaskSubject("policy_exemption", title, PolicyExemptions.Link(policy.Id, row.Id), policy.OrgUnitId, row.RequestedBy);
    }

    /// <summary>The engine already keeps the requester and the person out; this holds the line for a delegate's authority.</summary>
    public async Task<bool> MayDecideAsync(UserTaskInfo task, Guid authority, CancellationToken ct = default) =>
        await WaitingAsync(task, false, ct) is var (row, _) && authority != row.RequestedBy && authority != row.Subject;

    public async Task DecidedAsync(UserTaskInfo task, UserTaskInfo done, CancellationToken ct = default)
    {
        if (await WaitingAsync(task, true, ct) is not var (row, policy))
        {
            return;
        }
        if (done.Outcome == TaskOutcomes.Approve)
        {
            await exemptions.RecordApprovalAsync(row, done, ct);
        }
        var instance = await engine.GetInstanceAsync(task.InstanceId, ct);
        if (instance is not { Status: "completed" })
        {
            return; // The compliance step is open.
        }
        await exemptions.DecidedAsync(row, policy, instance.Outcome, done.CompletedBy ?? context.RequireUser(), done.Comment, ct);
    }

    /// <summary>The policy owner hears that a request waits for them; the compliance step goes to the inbox of every officer.</summary>
    public async Task HandleAsync(UserTaskCreated message, CancellationToken ct)
    {
        if (!PolicyExemptions.IsExemption(message.DefinitionKey) || message.ElementId != "owner" || PolicyExemptions.IdOf(message.BusinessKey) is not { } id
            || await exemptions.RowAsync(id, false, ct) is not { Status: ExemptionStatuses.Pending } row || row.InstanceId != message.InstanceId
            || await register.RowAsync(row.PolicyId, false, ct) is not { } policy)
        {
            return;
        }
        await notifications.SendAsync(new NotificationRequest([policy.Owner], NotificationCategories.ApprovalWaiting, PolicyExemptions.WaitingTemplate,
            PolicyExemptions.Link(policy.Id, row.Id), DedupeKey: "task:" + message.TaskId.ToString("N")), ct);
    }
}
