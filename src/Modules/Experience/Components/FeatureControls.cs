using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Personalization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Components;

public static class FeatureControlSubjects
{
    public const string Component = "component";
    public const string Template = "template";
}

/// <param name="SubjectId">A component id (such as <c>reference.alpha.text</c>) or a template id.</param>
/// <param name="OrgUnitId">The org unit whose pages (and those below it) the switch covers; with no unit and no audience, the whole tenant.</param>
/// <param name="AudienceId">An audience rule (spec 008): the switch applies to viewers in it. Components only.</param>
public sealed record FeatureControlRequest(string? SubjectKind, string? SubjectId, Guid? OrgUnitId, Guid? AudienceId, bool? Enabled);

public sealed record FeatureControlView(Guid Id, string SubjectKind, string SubjectId, Guid? OrgUnitId, Guid? AudienceId, bool Enabled, int Version,
    Guid ChangedBy, DateTimeOffset ChangedAt);

public sealed record FeatureControlVersion(int Version, bool? Enabled, Guid ChangedBy, DateTimeOffset ChangedAt);

/// <summary>
/// Experience feature controls (spec 032 R2 T030, FR-057): switches that turn a component or a template on or off for the
/// whole tenant, an org unit and the units below it, or (components only) an audience. They are versioned configuration:
/// every change is a new version kept with who made it. The picker, validation, the renderer and page creation read them.
/// The most specific switch wins: for a viewer, an audience switch they are in (off wins when two disagree), then the
/// nearest unit up the page's org tree, then the tenant switch. A switch can never make available what the package does
/// not include, and the tenant's own component enablement (R1) still turns a component off everywhere. Switches need
/// <c>experience.feature_controls</c>; without it they are not read, and <c>feature_control.manage</c> for the tenant.
/// </summary>
public sealed class FeatureControls(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ComponentRegistry registry,
    PlatformCatalogue catalogue,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxControls = 500;

    private const string Select =
        "select id, subject_kind, subject_id, org_unit_id, audience_id, enabled, version, changed_by, changed_at from experience.feature_control";

    private static FeatureControlView Read(Npgsql.NpgsqlDataReader r) => new(r.Get<Guid>("id"), r.Get<string>("subject_kind")!, r.Get<string>("subject_id")!,
        r.Get<Guid?>("org_unit_id"), r.Get<Guid?>("audience_id"), r.Get<bool>("enabled"), r.Get<int>("version"), r.Get<Guid>("changed_by"),
        r.Get<DateTimeOffset>("changed_at"));

    private async Task DemandAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceFeatureControls, ct);
        await authz.DemandAsync(Permissions.FeatureControlManage, AuthzTarget.Tenant, ct);
    }

    public async Task<IReadOnlyList<FeatureControlView>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(ct);
        return await session.QueryAsync(Select + " order by subject_kind, subject_id, changed_at", null, Read, ct);
    }

    /// <summary>Creates or changes the switch for a subject and scope; each change is a new version.</summary>
    public async Task<FeatureControlView> SetAsync(FeatureControlRequest request, CancellationToken ct)
    {
        await DemandAsync(ct);
        var errors = new ValidationErrors();
        var kind = request.SubjectKind;
        if (kind is not (FeatureControlSubjects.Component or FeatureControlSubjects.Template))
        {
            errors.Add("subjectKind", "validation.feature_control.subject_invalid");
        }
        else if (kind == FeatureControlSubjects.Component && registry.All.All(m => m.Id != request.SubjectId))
        {
            errors.Add("subjectId", "validation.feature_control.subject_unknown");
        }
        else if (kind == FeatureControlSubjects.Template && (request.SubjectId is null || !catalogue.Templates.ContainsKey(request.SubjectId)
            && !await session.ScalarAsync<bool>("select exists (select 1 from experience.template where template_id = @id)", new { id = request.SubjectId }, ct)))
        {
            errors.Add("subjectId", "validation.feature_control.subject_unknown");
        }
        if (request.Enabled is null)
        {
            errors.Add("enabled", "validation.feature_control.enabled_required");
        }
        if (request.OrgUnitId is not null && request.AudienceId is not null)
        {
            errors.Add("audienceId", "validation.feature_control.one_scope");
        }
        if (request.AudienceId is not null && kind == FeatureControlSubjects.Template)
        {
            errors.Add("audienceId", "validation.feature_control.audience_components_only");
        }
        errors.ThrowIfAny();
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.page.org_unit_unknown");
        }
        if (request.AudienceId is { } audience
            && !await session.ScalarAsync<bool>("select exists (select 1 from experience.audience_rule where rule_id = @audience)", new { audience }, ct))
        {
            throw AppException.Validation("audienceId", "validation.feature_control.audience_unknown");
        }
        // A switch never turns on what the package leaves out (FR-057, spec 005).
        if (request.Enabled == true && kind == FeatureControlSubjects.Component)
        {
            foreach (var capability in registry.All.Where(m => m.Id == request.SubjectId).SelectMany(m => m.Requires.Capabilities).Distinct())
            {
                if (!await entitlements.IsEnabledAsync(capability, ct))
                {
                    throw AppException.Validation("enabled", "validation.feature_control.not_in_package");
                }
            }
        }

        var existing = await session.QuerySingleOrDefaultAsync(Select +
            " where subject_kind = @kind and subject_id = @subject and org_unit_id is not distinct from @unit::uuid and audience_id is not distinct from @audience::uuid for update",
            new { kind, subject = request.SubjectId, unit = request.OrgUnitId, audience = request.AudienceId }, Read, ct);
        if (existing is null && await session.ScalarAsync<long>("select count(*) from experience.feature_control", null, ct) >= MaxControls)
        {
            throw AppException.Validation("subjectId", "validation.feature_control.too_many");
        }
        var id = existing?.Id ?? Ids.New();
        var version = (existing?.Version ?? 0) + 1;
        var user = context.RequireUser();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into experience.feature_control (tenant_id, id, subject_kind, subject_id, org_unit_id, audience_id, enabled, version, changed_by, changed_at)
            values (platform.current_tenant_id(), @id, @kind, @subject, @unit, @audience, @enabled, @version, @user, @now)
            on conflict (tenant_id, id) do update set enabled = excluded.enabled, version = excluded.version, changed_by = excluded.changed_by, changed_at = excluded.changed_at
            """, new { id, kind, subject = request.SubjectId, unit = request.OrgUnitId, audience = request.AudienceId, enabled = request.Enabled, version, user, now }, ct);
        await InsertVersionAsync(id, version, request.Enabled, ct);
        await AuditAsync(id, kind!, request.SubjectId!, request.OrgUnitId, request.AudienceId, existing?.Enabled, request.Enabled, ct);
        return (await session.QuerySingleOrDefaultAsync(Select + " where id = @id", new { id }, Read, ct))!;
    }

    /// <summary>Removes a switch, so the next one up applies; the removal is kept as a version.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var control = await session.QuerySingleOrDefaultAsync(Select + " where id = @id for update", new { id }, Read, ct) ?? throw AppException.NotFound();
        await InsertVersionAsync(id, control.Version + 1, null, ct);
        await session.ExecAsync("delete from experience.feature_control where id = @id", new { id }, ct);
        await AuditAsync(id, control.SubjectKind, control.SubjectId, control.OrgUnitId, control.AudienceId, control.Enabled, null, ct);
    }

    public async Task<IReadOnlyList<FeatureControlVersion>> VersionsAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(ct);
        var versions = await session.QueryAsync(
            "select version, enabled, changed_by, changed_at from experience.feature_control_version where control_id = @id order by version desc",
            new { id }, r => new FeatureControlVersion(r.Get<int>("version"), r.Get<bool?>("enabled"), r.Get<Guid>("changed_by"), r.Get<DateTimeOffset>("changed_at")), ct);
        return versions.Count == 0 ? throw AppException.NotFound() : versions;
    }

    /// <summary>
    /// Whether a subject is switched off for pages of <paramref name="orgUnitId"/> and, at render, for the viewer:
    /// <paramref name="inAudience"/> says whether the viewer is in an audience rule. Null when no switch applies.
    /// </summary>
    internal async Task<Func<string, bool>> OffAsync(string kind, Guid? orgUnitId, CancellationToken ct, Func<Guid, bool>? inAudience = null)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ExperienceFeatureControls, ct))
        {
            return _ => false;
        }
        var controls = await session.QueryAsync(Select + " where subject_kind = @kind", new { kind }, Read, ct);
        if (controls.Count == 0)
        {
            return _ => false;
        }
        // Nearest first (the closure is ordered by depth): the unit itself, then its parent, and so on up to the root.
        var path = orgUnitId is { } unit ? (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToList() : [];
        var bySubject = controls.GroupBy(c => c.SubjectId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        return subject =>
        {
            if (!bySubject.TryGetValue(subject, out var list))
            {
                return false;
            }
            if (inAudience is not null)
            {
                var matching = list.Where(c => c.AudienceId is { } a && inAudience(a)).ToList();
                if (matching.Count > 0)
                {
                    return matching.Any(c => !c.Enabled);
                }
            }
            foreach (var step in path)
            {
                if (list.FirstOrDefault(c => c.OrgUnitId == step) is { } own)
                {
                    return !own.Enabled;
                }
            }
            return list.FirstOrDefault(c => c.OrgUnitId is null && c.AudienceId is null) is { } tenant && !tenant.Enabled;
        };
    }

    private Task InsertVersionAsync(Guid id, int version, bool? enabled, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into experience.feature_control_version (tenant_id, control_id, version, enabled, changed_by, changed_at)
            values (platform.current_tenant_id(), @id, @version, @enabled, @user, @now)
            """, new { id, version, enabled, user = context.RequireUser(), now = clock.UtcNow }, ct);

    private Task AuditAsync(Guid id, string kind, string subject, Guid? unit, Guid? audience, bool? before, bool? after, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditActions.FeatureControlChanged, "feature_control", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { control = $"{kind}:{subject}", scope = unit?.ToString() ?? (audience is { } a ? "audience:" + a : "tenant"), before, after }), ct);
}
