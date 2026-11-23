using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Compliance.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

public sealed record ControlRef(string? Framework, string? Code);

public sealed record PolicyControlsRequest(IReadOnlyList<ControlRef>? Controls);

/// <summary>
/// A control a policy maps to, with its framework and titles from the library. <paramref name="Known"/> is false when
/// the tenant removed the control or its framework from the library after the mapping was made.
/// </summary>
public sealed record MappedControl(string Framework, string? FrameworkVersion, IReadOnlyDictionary<string, string>? FrameworkNames, string Code,
    IReadOnlyDictionary<string, string>? Titles, bool Known);

public sealed record PolicyControlsView(PolicySummary Policy, IReadOnlyList<MappedControl> Controls, bool CanEdit);

/// <summary>A policy mapped to a control, with where it stands: version in force, effective date, review date and review state.</summary>
public sealed record ControlPolicy(PolicySummary Policy, int? CurrentVersion, DateTimeOffset? EffectiveFrom, DateTimeOffset? NextReviewAt, string? ReviewState);

public sealed record ControlPoliciesView(MappedControl Control, IReadOnlyList<ControlPolicy> Policies);

/// <summary>A framework of the library as the mapping editor offers it.</summary>
public sealed record LibraryFramework(string Key, string Version, IReadOnlyDictionary<string, string> Names, IReadOnlyList<ControlInfo> Controls);

/// <summary>
/// Control and regulation mapping (spec 025 US8 scenario 3, FR-015): compliance officers map a policy to controls of
/// the spec 026 control library (platform sets such as ISO 27001:2022 Annex A, or the tenant's own frameworks for
/// regulations); a control lists the policies mapped to it with their status, as far as the reader may see them.
/// Needs <c>policy.evidence</c>. A control the library does not have is refused.
/// </summary>
public sealed class PolicyControls(
    IDbSession session,
    ITenantContext context,
    PolicyRegister register,
    IControlLibrary library,
    IEntitlementService entitlements,
    IAuthorizationService authz,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxControls = 100;

    private IReadOnlyList<ControlFrameworkInfo>? frameworks;

    private async Task<IReadOnlyList<ControlFrameworkInfo>> FrameworksAsync(CancellationToken ct) => frameworks ??= await library.FrameworksAsync(ct);

    private async Task<MappedControl> DescribeAsync(string framework, string code, CancellationToken ct)
    {
        var set = (await FrameworksAsync(ct)).FirstOrDefault(f => f.Key == framework);
        var control = set?.Controls.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));
        return new MappedControl(framework, set?.Version, set?.Names, control?.Code ?? code, control?.Titles, control is not null);
    }

    private async Task<IReadOnlyList<(string Framework, string Code)>> StoredAsync(Guid policyId, CancellationToken ct) =>
        await session.QueryAsync("select framework_key, control_code from policies.policy_control where policy_id = @policyId order by framework_key, control_code",
            new { policyId }, r => (r.GetString(0), r.GetString(1)), ct);

    /// <summary>The controls a policy maps to, described from the library, for callers that already checked access.</summary>
    internal async Task<IReadOnlyList<MappedControl>> OfAsync(Guid policyId, CancellationToken ct)
    {
        var list = new List<MappedControl>();
        foreach (var (framework, code) in await StoredAsync(policyId, ct))
        {
            list.Add(await DescribeAsync(framework, code, ct));
        }
        return list;
    }

    /// <summary>The controls a policy maps to, for anyone who sees the policy.</summary>
    public async Task<PolicyControlsView> ListAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        context.RequireUser();
        var policy = await register.VisibleAsync(id, false, ct);
        var list = new List<MappedControl>();
        foreach (var (framework, code) in await StoredAsync(id, ct))
        {
            list.Add(await DescribeAsync(framework, code, ct));
        }
        return new PolicyControlsView(await register.SummaryAsync(policy, ct), list, await register.ManagesAsync(policy, ct) && policy.Status != PolicyStatuses.Retired);
    }

    /// <summary>Replaces the controls a policy maps to. Compliance officers only; every control must be in the library.</summary>
    public async Task<PolicyControlsView> SaveAsync(Guid id, PolicyControlsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        var user = context.RequireUser();
        var policy = await register.VisibleAsync(id, forUpdate: true, ct);
        if (!await register.ManagesAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        if (policy.Status == PolicyStatuses.Retired)
        {
            throw AppException.Conflict("conflict.policies.retired");
        }
        var errors = new ValidationErrors();
        var wanted = new List<(string Framework, string Code)>();
        var input = request.Controls ?? [];
        if (input.Count > MaxControls)
        {
            errors.Add("controls", "validation.policies.too_many_controls");
        }
        else
        {
            foreach (var c in input)
            {
                var described = c.Framework is { Length: > 0 } f && c.Code is { Length: > 0 } code ? await DescribeAsync(f.Trim(), code.Trim(), ct) : null;
                if (described is not { Known: true })
                {
                    errors.Add("controls", "validation.policies.control_unknown");
                    break;
                }
                if (!wanted.Contains((described.Framework, described.Code)))
                {
                    wanted.Add((described.Framework, described.Code));
                }
            }
        }
        errors.ThrowIfAny();

        var stored = await StoredAsync(id, ct);
        var added = wanted.Except(stored).ToList();
        var removed = stored.Except(wanted).ToList();
        var now = clock.UtcNow;
        foreach (var (framework, code) in removed)
        {
            await session.ExecAsync("delete from policies.policy_control where policy_id = @id and framework_key = @framework and control_code = @code",
                new { id, framework, code }, ct);
        }
        foreach (var (framework, code) in added)
        {
            await session.ExecAsync(
                """
                insert into policies.policy_control (tenant_id, policy_id, framework_key, control_code, added_by, added_at)
                values (platform.current_tenant_id(), @id, @framework, @code, @user, @now)
                """, new { id, framework, code, user, now }, ct);
        }
        if (added.Count + removed.Count > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.PolicyControlsChanged, Securables.Policy, id.ToString(), TargetOrgUnitId: policy.OrgUnitId,
                Changes: new
                {
                    added = added.Select(a => $"{a.Framework}:{a.Code}").ToArray(),
                    removed = removed.Select(r => $"{r.Framework}:{r.Code}").ToArray(),
                }), ct);
        }
        return await ListAsync(id, ct);
    }

    /// <summary>The policies mapped to one control with their status (US8 scenario 3), as far as the reader may see each one.</summary>
    public async Task<ControlPoliciesView> ForControlAsync(string? framework, string? code, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        context.RequireUser();
        if (framework is not { Length: > 0 } || code is not { Length: > 0 })
        {
            throw AppException.NotFound();
        }
        var control = await DescribeAsync(framework, code, ct);
        if (!control.Known)
        {
            throw AppException.NotFound();
        }
        var ids = await session.QueryAsync(
            "select policy_id from policies.policy_control where framework_key = @framework and control_code = @code order by policy_id",
            new { framework = control.Framework, code = control.Code }, r => r.GetGuid(0), ct);
        var list = new List<ControlPolicy>();
        var now = clock.UtcNow;
        foreach (var policyId in ids)
        {
            PolicyRow policy;
            try
            {
                policy = await register.VisibleAsync(policyId, false, ct);
            }
            catch (AppException e) when (e.Status is 403 or 404)
            {
                continue;
            }
            var manages = await register.ManagesAsync(policy, ct);
            list.Add(new ControlPolicy(await register.SummaryAsync(policy, ct), policy.CurrentVersion, policy.EffectiveFrom, policy.NextReviewAt,
                manages || policy.Owner == context.ActorId ? PolicyReviews.StateOf(policy, await register.ReviewedAsync(policy, ct), now) : null));
        }
        return new ControlPoliciesView(control, list.OrderBy(p => p.Policy.Code, StringComparer.Ordinal).ToList());
    }

    /// <summary>The library the mapping editor offers, for people who manage policies somewhere.</summary>
    public async Task<IReadOnlyList<LibraryFramework>> LibraryAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyEvidence, ct);
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        context.RequireUser();
        if (!(await authz.CoverageAsync(Permissions.PolicyManage, ct)).Any)
        {
            throw AppException.Denied();
        }
        return (await FrameworksAsync(ct)).Select(f => new LibraryFramework(f.Key, f.Version, f.Names, f.Controls)).ToList();
    }
}
