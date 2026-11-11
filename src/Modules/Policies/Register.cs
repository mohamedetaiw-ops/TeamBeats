using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

internal sealed record PolicyRow(
    Guid Id,
    string Code,
    Dictionary<string, string> Names,
    Guid? CategoryId,
    Guid Owner,
    Guid? OrgUnitId,
    ContentAudience Audience,
    string Classification,
    string? BindingLocale,
    List<RouteStepSetting> Route,
    int ReviewMonths,
    Guid[] Related,
    string Status,
    int? CurrentVersion,
    DateTimeOffset? EffectiveFrom,
    DateTimeOffset? NextReviewAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool OnboardingRequired)
{
    public const string Columns =
        "id, code, names::text, category_id, owner_user_id, org_unit_id, audience::text, classification, binding_locale, route::text, review_months, related, status, " +
        "current_version, effective_from, next_review_at, created_at, updated_at, onboarding_required";

    public static PolicyRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), PolicyJson.Read<Dictionary<string, string>>(r.GetString(2)),
        r.Get<Guid?>("category_id"), r.GetGuid(4), r.Get<Guid?>("org_unit_id"), PolicyJson.Read<ContentAudience>(r.GetString(6)), r.GetString(7),
        r.Get<string>("binding_locale"), PolicyJson.Read<List<RouteStepSetting>>(r.GetString(9)), r.GetInt32(10), r.GetFieldValue<Guid[]>(11), r.GetString(12),
        r.Get<int?>("current_version"), r.Get<DateTimeOffset?>("effective_from"), r.Get<DateTimeOffset?>("next_review_at"), r.GetFieldValue<DateTimeOffset>(16),
        r.GetFieldValue<DateTimeOffset>(17), r.GetBoolean(18));

    public AuthzTarget Target => OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;

    /// <summary>
    /// Who the policy applies to. A unit policy with no audience applies to its unit and everything below it, so a
    /// subsidiary policy is never shown to a sibling (section 7).
    /// </summary>
    public ContentAudience AppliesTo => Audience.IsEveryone && OrgUnitId is { } unit ? new ContentAudience([new AudienceClause(OrgUnits: [new UnitTarget(unit)])]) : Audience;
}

internal sealed record VersionRow(
    Guid Id,
    Guid PolicyId,
    int? Number,
    string State,
    List<string> Locales,
    Dictionary<string, string> Titles,
    List<StoredClause> Clauses,
    bool Material,
    string? ChangeNote,
    Guid[] Editors,
    List<StoredApproval> Approvals,
    Guid? InstanceId,
    DateTimeOffset? RequestedEffectiveFrom,
    DateTimeOffset? EffectiveFrom,
    Guid? SubmittedBy,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SupersededAt,
    DateTimeOffset UpdatedAt)
{
    public const string Columns =
        "id, policy_id, number, state, locales, titles::text, clauses::text, material, change_note, editors, approvals::text, instance_id, requested_effective_from, " +
        "effective_from, submitted_by, submitted_at, approved_at, superseded_at, updated_at";

    public static VersionRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.Get<int?>("number"), r.GetString(3), [.. r.GetFieldValue<string[]>(4)],
        PolicyJson.Read<Dictionary<string, string>>(r.GetString(5)), PolicyJson.Read<List<StoredClause>>(r.GetString(6)), r.GetBoolean(7), r.Get<string>("change_note"),
        r.GetFieldValue<Guid[]>(9), PolicyJson.Read<List<StoredApproval>>(r.GetString(10)), r.Get<Guid?>("instance_id"), r.Get<DateTimeOffset?>("requested_effective_from"),
        r.Get<DateTimeOffset?>("effective_from"), r.Get<Guid?>("submitted_by"), r.Get<DateTimeOffset?>("submitted_at"), r.Get<DateTimeOffset?>("approved_at"),
        r.Get<DateTimeOffset?>("superseded_at"), r.GetFieldValue<DateTimeOffset>(18));
}

/// <summary>
/// The policy register (spec 025 FR-001, FR-002). Compliance officers (<c>policy.manage</c> at the policy's unit or
/// above) create and change register entries and name an owner who holds <c>policy.author</c> there. Published
/// policies are readable by the people they apply to; drafts only by the owner, editors, compliance officers and the
/// approvers of the route. Everything is in the current tenant; another tenant's IDs are not found.
/// </summary>
public sealed class PolicyRegister(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning grants,
    IEntitlementService entitlements,
    IAudiences audiences,
    IUserDirectory users,
    ITenantMemberships memberships,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    IProcessEngine engine,
    IReportingLine reportingLine,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxNameLength = 300;
    public const int MaxRouteSteps = 10;
    public const int MaxApprovers = 20;
    public const int MaxRelated = 20;
    public const int ListMax = 1000;

    // ---- Access ----

    private readonly Dictionary<AuthzTarget, bool> manages = [];

    internal async Task<bool> ManagesAsync(PolicyRow policy, CancellationToken ct)
    {
        if (!manages.TryGetValue(policy.Target, out var allowed))
        {
            manages[policy.Target] = allowed = (await authz.CheckAsync(Permissions.PolicyManage, policy.Target, ct)).Allowed;
        }
        return allowed;
    }

    /// <summary>The owner drafts while they hold <c>policy.author</c> at the policy's unit; compliance officers may too.</summary>
    internal async Task<bool> EditsAsync(PolicyRow policy, CancellationToken ct) =>
        await ManagesAsync(policy, ct)
        || (policy.Owner == context.RequireUser() && (await authz.CheckAsync(Permissions.PolicyAuthor, policy.Target, ct)).Allowed);

    /// <summary>Whether the policy applies to the current user, which is what lets them read its versions in force.</summary>
    internal Task<bool> AppliesToMeAsync(PolicyRow policy, CancellationToken ct) => audiences.IncludesAsync(policy.AppliesTo, context.RequireUser(), ct);

    internal async Task<bool> ReadsPublishedAsync(PolicyRow policy, CancellationToken ct) =>
        policy.Owner == context.RequireUser() || await ManagesAsync(policy, ct) || await AppliesToMeAsync(policy, ct);

    internal async Task<PolicyRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {PolicyRow.Columns} from policies.policy where id = @id{(forUpdate ? " for update" : string.Empty)}",
            new { id }, PolicyRow.Read, ct);

    internal async Task<VersionRow?> VersionAsync(Guid versionId, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {VersionRow.Columns} from policies.policy_version where id = @versionId{(forUpdate ? " for update" : string.Empty)}",
            new { versionId }, VersionRow.Read, ct);

    internal async Task<VersionRow?> VersionByNumberAsync(Guid policyId, int number, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {VersionRow.Columns} from policies.policy_version where policy_id = @policyId and number = @number",
            new { policyId, number }, VersionRow.Read, ct);

    internal async Task<VersionRow?> WorkingAsync(Guid policyId, bool forUpdate, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            $"select {VersionRow.Columns} from policies.policy_version where policy_id = @policyId and state in ('draft', 'in_review'){(forUpdate ? " for update" : string.Empty)}",
            new { policyId }, VersionRow.Read, ct);

    internal async Task<VersionRow?> ScheduledAsync(Guid policyId, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {VersionRow.Columns} from policies.policy_version where policy_id = @policyId and state = 'approved'",
            new { policyId }, VersionRow.Read, ct);

    /// <summary>The policy when the current user may see it in any way, else 404 so its existence stays hidden.</summary>
    internal async Task<PolicyRow> VisibleAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        var policy = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        if (policy.CurrentVersion is not null && await ReadsPublishedAsync(policy, ct))
        {
            return policy;
        }
        if (policy.Owner == context.RequireUser() || await ManagesAsync(policy, ct) || await ReviewsAsync(policy, ct))
        {
            return policy;
        }
        throw AppException.NotFound();
    }

    private readonly Dictionary<Guid, bool> reviews = [];

    /// <summary>
    /// Whether the current user is an approver of the working version's route: a candidate of its open step, or someone
    /// who decided a step. Approvers read the draft, compare it and comment on clauses.
    /// </summary>
    internal async Task<bool> ReviewsAsync(PolicyRow policy, CancellationToken ct)
    {
        if (reviews.TryGetValue(policy.Id, out var known))
        {
            return known;
        }
        var user = context.RequireUser();
        var result = false;
        if (await WorkingAsync(policy.Id, false, ct) is { InstanceId: { } instance })
        {
            foreach (var task in await engine.InstanceTasksAsync(instance, ct))
            {
                if (task.CompletedBy == user || (task.Status == "open" && await engine.MyTaskAsync(task.Id, ct) is not null))
                {
                    result = true;
                    break;
                }
            }
        }
        return reviews[policy.Id] = result;
    }

    // ---- People ----

    private readonly Dictionary<Guid, PolicyPerson> people = [];

    internal async Task<PolicyPerson> PersonAsync(Guid id, CancellationToken ct)
    {
        if (!people.TryGetValue(id, out var person))
        {
            people[id] = person = new PolicyPerson(id, (await users.GetAsync(id, ct))?.DisplayName ?? string.Empty);
        }
        return person;
    }

    internal async Task<IReadOnlyList<ApprovalView>> ApprovalsAsync(IReadOnlyList<StoredApproval> approvals, CancellationToken ct)
    {
        var list = new List<ApprovalView>();
        foreach (var a in approvals)
        {
            list.Add(new ApprovalView(a.Step, a.StepNames, await PersonAsync(a.UserId, ct), a.OnBehalfOf is { } d ? await PersonAsync(d, ct) : null, a.At));
        }
        return list;
    }

    internal async Task<VersionView> VersionViewAsync(VersionRow v, CancellationToken ct) =>
        new(v.Id, v.Number, v.State, v.Locales, v.Titles, ClauseRules.Views(v.Clauses), v.Material, v.ChangeNote, await ApprovalsAsync(v.Approvals, ct),
            v.RequestedEffectiveFrom, v.EffectiveFrom, v.SubmittedAt, v.ApprovedAt, v.SupersededAt, v.UpdatedAt);

    internal async Task<PolicySummary> SummaryAsync(PolicyRow p, CancellationToken ct) =>
        new(p.Id, p.Code, p.Names, p.CategoryId, p.Status, p.Classification, await PersonAsync(p.Owner, ct));

    // ---- Categories ----

    public async Task<IReadOnlyList<CategoryView>> CategoriesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        context.RequireUser();
        return await session.QueryAsync("select id, names::text from policies.category order by created_at, id", null,
            r => new CategoryView(r.GetGuid(0), PolicyJson.Read<Dictionary<string, string>>(r.GetString(1))), ct);
    }

    public async Task<CategoryView> CreateCategoryAsync(CategoryRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        await authz.DemandAsync(Permissions.PolicyManage, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        Names(request.Names, "names", errors);
        errors.ThrowIfAny();
        var id = Ids.New();
        var names = Trimmed(request.Names!);
        await session.ExecAsync("insert into policies.category (tenant_id, id, names, created_at) values (platform.current_tenant_id(), @id, @names, @now)",
            new { id, names = new Jsonb(PolicyJson.Write(names)), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyChanged, "policy_category", id.ToString(), Changes: new { names }), ct);
        return new CategoryView(id, names);
    }

    // ---- Register ----

    private static void Names(IReadOnlyDictionary<string, string>? names, string field, ValidationErrors errors)
    {
        if (names is null || names.Count == 0 || names.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Trim().Length > MaxNameLength))
        {
            errors.Add(field, "validation.policies.names");
        }
    }

    private static Dictionary<string, string> Trimmed(IReadOnlyDictionary<string, string> names) => names.ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);

    private async Task ValidateAsync(PolicyRequest request, Guid? id, ValidationErrors errors, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (code is not { Length: > 0 and <= 40 } || code.Any(char.IsControl))
        {
            errors.Add("code", "validation.policies.code");
        }
        else if (await session.ScalarAsync<int>("select count(*)::int from policies.policy where lower(code) = lower(@code) and id <> @id",
            new { code, id = id ?? Guid.Empty }, ct) > 0)
        {
            errors.Add("code", "validation.policies.code_taken");
        }
        Names(request.Names, "names", errors);
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        if (request.Names?.Keys.Any(l => !enabled.Contains(l)) == true)
        {
            errors.Add("names", "validation.policies.locale");
        }
        if (request.BindingLocale is { } binding && !enabled.Contains(binding))
        {
            errors.Add("bindingLocale", "validation.policies.binding_locale");
        }
        if (request.Classification is { } classification && !ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classification", "validation.policies.classification");
        }
        if (request.ReviewMonths is { } months && months is < 1 or > 60)
        {
            errors.Add("reviewMonths", "validation.policies.review_months");
        }
        if (request.CategoryId is { } category && await session.ScalarAsync<int>("select count(*)::int from policies.category where id = @category", new { category }, ct) == 0)
        {
            errors.Add("categoryId", "validation.policies.category");
        }
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            errors.Add("orgUnitId", "validation.policies.org_unit");
        }
        audiences.Validate(request.Audience, "audience", errors);
        var route = request.Route ?? [];
        if (route.Count > MaxRouteSteps || route.Any(s => s is null))
        {
            errors.Add("route", "validation.policies.route");
        }
        else
        {
            foreach (var step in route)
            {
                var approvers = step.Approvers ?? [];
                if (step.ComplianceOfficers == approvers.Count > 0 || approvers.Count > MaxApprovers || approvers.Distinct().Count() != approvers.Count
                    || step.Names?.Values.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 100) == true)
                {
                    errors.Add("route", "validation.policies.route");
                    break;
                }
                foreach (var approver in approvers)
                {
                    if ((await memberships.GetAsync(approver, ct))?.Status != MembershipStatus.Active)
                    {
                        errors.Add("route", "validation.policies.approver");
                        break;
                    }
                }
            }
        }
        var related = request.Related ?? [];
        if (related.Count > MaxRelated || (id is { } self && related.Contains(self))
            || (related.Count > 0 && await session.ScalarAsync<int>("select count(*)::int from policies.policy where id = any(@related)", new { related = related.Distinct().ToArray() }, ct)
                != related.Distinct().Count()))
        {
            errors.Add("related", "validation.policies.related");
        }
    }

    /// <summary>The owner must be an active member who holds <c>policy.author</c> at the policy's unit.</summary>
    private async Task ValidateOwnerAsync(Guid owner, AuthzTarget target, ValidationErrors errors, CancellationToken ct)
    {
        if ((await memberships.GetAsync(owner, ct))?.Status != MembershipStatus.Active || !await grants.UserHoldsAsync(owner, Permissions.PolicyAuthor, target, ct))
        {
            errors.Add("ownerId", "validation.policies.owner");
        }
    }

    public async Task<PolicyView> CreateAsync(PolicyRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        var user = context.RequireUser();
        var target = request.OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
        await authz.DemandAsync(Permissions.PolicyManage, target, ct);
        var errors = new ValidationErrors();
        await ValidateAsync(request, null, errors, ct);
        var owner = request.OwnerId ?? user;
        await ValidateOwnerAsync(owner, target, errors, ct);
        errors.ThrowIfAny();
        var id = Ids.New();
        var now = clock.UtcNow;
        var names = Trimmed(request.Names!);
        await session.ExecAsync(
            """
            insert into policies.policy (tenant_id, id, code, names, category_id, owner_user_id, org_unit_id, audience, classification, binding_locale, route,
              review_months, related, status, created_by, created_at, updated_at, onboarding_required)
            values (platform.current_tenant_id(), @id, @code, @names, @category, @owner, @unit, @audience, @classification, @binding, @route, @months, @related,
              'draft', @user, @now, @now, @onboarding)
            """,
            new
            {
                id, code = request.Code!.Trim(), names = new Jsonb(PolicyJson.Write(names)), category = request.CategoryId, owner, unit = request.OrgUnitId,
                audience = new Jsonb(PolicyJson.Write(request.Audience ?? ContentAudience.Everyone)), classification = request.Classification ?? "internal",
                binding = request.BindingLocale, route = new Jsonb(PolicyJson.Write(request.Route ?? [])), months = request.ReviewMonths ?? 12,
                related = (request.Related ?? []).Distinct().ToArray(), user, now, onboarding = request.OnboardingRequired ?? false,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyCreated, Securables.Policy, id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { code = request.Code!.Trim(), names, owner, orgUnitId = request.OrgUnitId, request.Classification, request.BindingLocale, route = request.Route?.Count ?? 0,
                onboardingRequired = request.OnboardingRequired ?? false }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Changes the register entry (compliance officers). A version already in review keeps the route it started with.</summary>
    public async Task<PolicyView> UpdateAsync(Guid id, PolicyRequest request, CancellationToken ct)
    {
        var policy = await VisibleAsync(id, forUpdate: true, ct);
        if (!await ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        var target = request.OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
        if (request.OrgUnitId != policy.OrgUnitId)
        {
            await authz.DemandAsync(Permissions.PolicyManage, target, ct);
        }
        var errors = new ValidationErrors();
        await ValidateAsync(request, id, errors, ct);
        var owner = request.OwnerId ?? policy.Owner;
        if (owner != policy.Owner || request.OrgUnitId != policy.OrgUnitId)
        {
            await ValidateOwnerAsync(owner, target, errors, ct);
        }
        errors.ThrowIfAny();
        var names = Trimmed(request.Names!);
        await session.ExecAsync(
            """
            update policies.policy set code = @code, names = @names, category_id = @category, owner_user_id = @owner, org_unit_id = @unit, audience = @audience,
              classification = @classification, binding_locale = @binding, route = @route, review_months = @months, related = @related, updated_at = @now,
              onboarding_required = @onboarding, row_version = row_version + 1
            where id = @id
            """,
            new
            {
                id, code = request.Code!.Trim(), names = new Jsonb(PolicyJson.Write(names)), category = request.CategoryId, owner, unit = request.OrgUnitId,
                audience = new Jsonb(PolicyJson.Write(request.Audience ?? ContentAudience.Everyone)), classification = request.Classification ?? policy.Classification,
                binding = request.BindingLocale, route = new Jsonb(PolicyJson.Write(request.Route ?? [])), months = request.ReviewMonths ?? policy.ReviewMonths,
                related = (request.Related ?? []).Distinct().ToArray(), now = clock.UtcNow, onboarding = request.OnboardingRequired ?? policy.OnboardingRequired,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyChanged, Securables.Policy, id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new
            {
                before = new { policy.Code, policy.Names, policy.Owner, policy.OrgUnitId, policy.Classification, policy.BindingLocale, route = policy.Route.Count,
                    policy.OnboardingRequired },
                after = new { code = request.Code!.Trim(), names, owner, orgUnitId = request.OrgUnitId, request.Classification, request.BindingLocale, route = request.Route?.Count ?? 0,
                    onboardingRequired = request.OnboardingRequired ?? policy.OnboardingRequired },
            }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Retires a policy (compliance officers): it leaves My policies and new campaigns; its versions stay as the record.</summary>
    public async Task<PolicyView> RetireAsync(Guid id, CancellationToken ct)
    {
        var policy = await VisibleAsync(id, forUpdate: true, ct);
        if (!await ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        if (await WorkingAsync(id, false, ct) is { State: VersionStates.InReview })
        {
            throw AppException.Conflict("conflict.policies.in_review");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update policies.policy set status = 'retired', retired_at = @now, updated_at = @now, row_version = row_version + 1 where id = @id",
            new { id, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PolicyRetired, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
            Changes: new { version = policy.CurrentVersion }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Keeps the register status in step with the versions: published once a version is in force, else the working state.</summary>
    internal async Task RefreshStatusAsync(Guid id, CancellationToken ct) =>
        await session.ExecAsync(
            """
            update policies.policy p set status = case
                when p.status = 'retired' then 'retired'
                when p.current_version is not null then 'published'
                when exists (select 1 from policies.policy_version v where v.policy_id = p.id and v.state = 'approved') then 'approved'
                when exists (select 1 from policies.policy_version v where v.policy_id = p.id and v.state = 'in_review') then 'in_review'
                else 'draft' end,
              updated_at = @now
            where p.id = @id
            """, new { id, now = clock.UtcNow }, ct);

    public async Task<PolicyView> GetAsync(Guid id, CancellationToken ct)
    {
        var policy = await VisibleAsync(id, false, ct);
        var manage = await ManagesAsync(policy, ct);
        var edit = await EditsAsync(policy, ct);
        var review = await ReviewsAsync(policy, ct);
        var history = manage || policy.Owner == context.RequireUser();
        var working = edit || manage || review ? await WorkingAsync(id, false, ct) : null;
        var scheduled = await ScheduledAsync(id, ct);
        return new PolicyView(policy.Id, policy.Code, policy.Names, policy.CategoryId, await PersonAsync(policy.Owner, ct), policy.OrgUnitId, policy.Audience,
            policy.Classification, policy.BindingLocale, manage ? policy.Route : [], policy.ReviewMonths, policy.Related, policy.Status, policy.CurrentVersion,
            policy.EffectiveFrom, policy.NextReviewAt,
            working is null ? null : new WorkingVersion(working.Id, working.State, working.RequestedEffectiveFrom, working.SubmittedAt, working.UpdatedAt),
            scheduled is { Number: { } n, EffectiveFrom: { } from } && (edit || manage || history) ? new ScheduledVersion(scheduled.Id, n, from) : null,
            new PolicyRights(manage, edit && policy.Status != PolicyStatuses.Retired, review, history, await FollowsPeopleAsync(ct), await AsksAsync(policy, ct),
                history && policy.CurrentVersion is not null && await entitlements.IsEnabledAsync(Capabilities.AnalyticsCore, ct)),
            policy.CreatedAt, policy.UpdatedAt, policy.OnboardingRequired);
    }

    private async Task<bool> AsksAsync(PolicyRow policy, CancellationToken ct) =>
        policy.CurrentVersion is not null && policy.Status != PolicyStatuses.Retired && await ReadsPublishedAsync(policy, ct)
        && await entitlements.IsEnabledAsync(Capabilities.AiAssistant, ct) && await entitlements.IsEnabledAsync(Capabilities.AiPolicyQa, ct);

    /// <summary>Whether the user has people in the compliance view: <c>policy.manage</c> somewhere, or reports.</summary>
    internal async Task<bool> FollowsPeopleAsync(CancellationToken ct) =>
        (await authz.CoverageAsync(Permissions.PolicyManage, ct)).Any || (await reportingLine.DirectReportsAsync(context.RequireUser(), ct)).Count > 0;

    public async Task<RegisterRights> RightsAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        return new RegisterRights((await authz.CoverageAsync(Permissions.PolicyManage, ct)).Any);
    }

    /// <summary>
    /// The register as the current user may see it: everything at the units where they manage policies, the policies
    /// they own, and the published policies that apply to them.
    /// </summary>
    public async Task<IReadOnlyList<PolicyListItem>> ListAsync(Guid? category, string? status, string? q, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        var user = context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.PolicyManage, ct);
        var rows = await session.QueryAsync(
            $"""
            select {PolicyRow.Columns}, (select v.state from policies.policy_version v where v.policy_id = policy.id and v.state in ('draft', 'in_review')) as working
            from policies.policy
            where (@category::uuid is null or category_id = @category) and (@status::text is null or status = @status)
              and (@q::text is null or code ilike '%' || @q || '%' or names::text ilike '%' || @q || '%')
            order by lower(code), id limit @limit
            """, new { category, status, q = string.IsNullOrWhiteSpace(q) ? null : q.Trim().Replace("%", "").Replace("_", ""), limit = ListMax },
            r => (Row: PolicyRow.Read(r), Working: r.Get<string>("working")), ct);
        var list = new List<PolicyListItem>();
        foreach (var (p, working) in rows)
        {
            var manage = p.OrgUnitId is { } unit ? coverage.Covers(unit) : coverage.WholeTenant;
            var mine = p.Owner == user;
            if (!manage && !mine && (p.CurrentVersion is null || p.Status == PolicyStatuses.Retired || !await AppliesToMeAsync(p, ct)))
            {
                continue;
            }
            list.Add(new PolicyListItem(p.Id, p.Code, p.Names, p.CategoryId, await PersonAsync(p.Owner, ct), p.OrgUnitId, p.Classification, p.Status, p.CurrentVersion,
                p.EffectiveFrom, p.NextReviewAt, manage || mine ? working : null));
        }
        return list;
    }

    // ---- Reading versions ----

    /// <summary>The version in force, for the policy page.</summary>
    public async Task<PolicyReading> CurrentAsync(Guid id, CancellationToken ct)
    {
        var policy = await VisibleAsync(id, false, ct);
        if (policy.CurrentVersion is not { } number)
        {
            throw AppException.NotFound();
        }
        return await ReadingAsync(policy, number, ct);
    }

    /// <summary>
    /// A numbered version. Readers see versions that were in force, with the banner on a superseded one (US1 scenario
    /// 5); the owner, editors and compliance officers also see an approved version waiting for its date.
    /// </summary>
    public async Task<PolicyReading> NumberedAsync(Guid id, int number, CancellationToken ct)
    {
        var policy = await VisibleAsync(id, false, ct);
        return await ReadingAsync(policy, number, ct);
    }

    private async Task<PolicyReading> ReadingAsync(PolicyRow policy, int number, CancellationToken ct)
    {
        var version = await VersionByNumberAsync(policy.Id, number, ct) ?? throw AppException.NotFound();
        var inForce = version.State is VersionStates.Effective or VersionStates.Superseded;
        if (inForce ? !await ReadsPublishedAsync(policy, ct) : !await EditsAsync(policy, ct) && !await ManagesAsync(policy, ct))
        {
            throw AppException.NotFound();
        }
        return new PolicyReading(await SummaryAsync(policy, ct), await VersionViewAsync(version, ct), version.State == VersionStates.Superseded, policy.CurrentVersion,
            policy.BindingLocale);
    }

    /// <summary>Every numbered version with its approval trail, for the owner and compliance officers.</summary>
    public async Task<IReadOnlyList<VersionHistoryItem>> HistoryAsync(Guid id, CancellationToken ct)
    {
        var policy = await VisibleAsync(id, false, ct);
        if (policy.Owner != context.RequireUser() && !await ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        var versions = await session.QueryAsync($"select {VersionRow.Columns} from policies.policy_version where policy_id = @id and number is not null order by number desc",
            new { id }, VersionRow.Read, ct);
        var list = new List<VersionHistoryItem>();
        foreach (var v in versions)
        {
            list.Add(new VersionHistoryItem(v.Id, v.Number!.Value, v.State, v.Material, v.ChangeNote, v.EffectiveFrom, v.SupersededAt, await ApprovalsAsync(v.Approvals, ct)));
        }
        return list;
    }
}
