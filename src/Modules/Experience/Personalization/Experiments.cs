using System.Security.Cryptography;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Personalization;

public static class ExperimentStatuses
{
    public const string Proposed = "proposed";
    public const string Running = "running";
    public const string Stopped = "stopped";
}

public sealed record ExperimentView(Guid Id, Guid PageId, string Slug, string Name, int ControlRelease, int VariantRelease, int Percent, Guid? OrgUnitId,
    string Status, Guid ProposedBy, DateTimeOffset ProposedAt, Guid? ApprovedBy, DateTimeOffset? StartedAt, DateTimeOffset? StoppedAt);

public sealed record ExperimentRequest(Guid? PageId, string? Name, int? VariantRelease, int? Percent, Guid? OrgUnitId);

/// <summary><paramref name="Available"/> is false when the package has no experiments; nobody takes part then.</summary>
public sealed record ExperimentChoice(bool Available, bool Consented);

public sealed record ExperimentChoiceRequest(bool? Consented);

/// <summary>The arm a viewer is in for a page, with the release they see.</summary>
public sealed record ExperimentArm(Guid ExperimentId, bool Variant, int Release);

/// <summary>
/// Governed experience experiments (spec 021 R3, US9 scenario 8, FR-022). A page publisher proposes showing one of a
/// page's earlier releases to a share of people; a tenant administrator other than the proposer approves it, which starts
/// it; either may stop it. Only people who agreed to take part, and are in the chosen unit when there is one, are
/// allocated, by a hash of the experiment and the person, so allocation is stable and nothing about a person is stored.
/// A stopped experiment shows everyone the current release again. Pages carry no mandatory content of their own (items
/// to acknowledge and policies live in specs 009 and 025, critical alerts in the shell), so a page experiment never
/// changes what someone must read. Results are counts per arm in spec 021 analytics, never per person.
/// </summary>
public sealed class Experiments(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IAnalyticsEvents analytics,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxPercent = 50;

    private const string Select = """
        select e.id, e.page_id, p.slug, e.name, coalesce(p.current_release, 0), e.variant_release, e.percent, e.org_unit_id, e.status, e.proposed_by,
               e.proposed_at, e.approved_by, e.started_at, e.stopped_at, p.org_unit_id
        from experience.experiment e join experience.page p on p.id = e.page_id
        """;

    private static (ExperimentView View, Guid? PageUnit) Map(System.Data.Common.DbDataReader r) =>
        (new ExperimentView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetGuid(7), r.GetString(8), r.GetGuid(9), r.GetFieldValue<DateTimeOffset>(10), r.IsDBNull(11) ? null : r.GetGuid(11),
            r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13)),
            r.IsDBNull(14) ? null : r.GetGuid(14));

    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private async Task<bool> AdministersAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct)).Allowed;

    /// <summary>Every experiment, newest first, for tenant administrators and page publishers of the whole tenant.</summary>
    public async Task<IReadOnlyList<ExperimentView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExperiments, ct);
        context.RequireUser();
        if (!await AdministersAsync(ct) && !(await authz.CheckAsync(Permissions.PagePublish, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        return [.. (await session.QueryAsync(Select + " order by e.proposed_at desc, e.id limit 200", null, Map, ct)).Select(r => r.View)];
    }

    private async Task<(ExperimentView View, Guid? PageUnit)> RowAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(Select + " where e.id = @id for update of e", new { id }, Map, ct) is { View: not null } row ? row : throw AppException.NotFound();

    public async Task<ExperimentView> ProposeAsync(ExperimentRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExperiments, ct);
        var by = context.RequireUser();
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 120)
        {
            errors.Add("name", "validation.experience.experiment_name");
        }
        if (request.Percent is not (>= 1 and <= MaxPercent))
        {
            errors.Add("percent", "validation.experience.experiment_percent");
        }
        if (request.PageId is not { } pageId)
        {
            errors.Add("pageId", "validation.experience.experiment_page");
            errors.ThrowIfAny();
            return null!;
        }
        var page = await session.QuerySingleOrDefaultAsync("select current_release, org_unit_id from experience.page where id = @pageId", new { pageId },
            r => (Current: r.IsDBNull(0) ? (int?)null : r.GetInt32(0), Unit: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1)), ct);
        if (page == default)
        {
            throw AppException.NotFound();
        }
        await authz.DemandAsync(Permissions.PagePublish, Target(page.Unit), ct);
        if (page.Current is not { } current)
        {
            errors.Add("pageId", "validation.experience.experiment_page");
        }
        else if (request.VariantRelease is not { } variant || variant == current
            || !await session.ScalarAsync<bool>("select exists (select 1 from experience.page_release where page_id = @pageId and release = @variant)",
                new { pageId, variant = request.VariantRelease ?? 0 }, ct))
        {
            errors.Add("variantRelease", "validation.experience.experiment_release");
        }
        // The scope is a unit inside the page's own scope, so the experiment never reaches people the page does not.
        if (request.OrgUnitId is { } unit && page.Unit is { } pageUnit && !(await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).Contains(pageUnit))
        {
            errors.Add("orgUnitId", "validation.experience.experiment_scope");
        }
        if (request.OrgUnitId is { } known && (await hierarchy.GetAncestorsAndSelfAsync(known, ct)).Count == 0)
        {
            errors.Add("orgUnitId", "validation.experience.experiment_scope");
        }
        errors.ThrowIfAny();
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.experiment where page_id = @pageId and status <> 'stopped')", new { pageId }, ct))
        {
            throw AppException.Conflict("validation.experience.experiment_open");
        }
        var id = Guid.CreateVersion7();
        await session.ExecAsync(
            """
            insert into experience.experiment (tenant_id, id, page_id, name, variant_release, percent, org_unit_id, status, proposed_by, proposed_at)
            values (platform.current_tenant_id(), @id, @pageId, @name, @variant, @percent, @unit, 'proposed', @by, @now)
            """, new { id, pageId, name, variant = request.VariantRelease!.Value, percent = request.Percent!.Value, unit = request.OrgUnitId, by, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperimentProposed, "experiment", id.ToString(),
            Changes: new { page = pageId, variant = request.VariantRelease, percent = request.Percent, unit = request.OrgUnitId }), ct);
        return (await RowAsync(id, ct)).View;
    }

    /// <summary>A tenant administrator other than the proposer approves, and the experiment starts.</summary>
    public async Task<ExperimentView> ApproveAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExperiments, ct);
        var by = context.RequireUser();
        await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
        var (view, _) = await RowAsync(id, ct);
        if (view.Status != ExperimentStatuses.Proposed)
        {
            throw AppException.Conflict("validation.experience.experiment_state");
        }
        if (view.ProposedBy == by)
        {
            throw AppException.Validation("id", "validation.experience.experiment_own");
        }
        await session.ExecAsync("update experience.experiment set status = 'running', approved_by = @by, started_at = @now where id = @id",
            new { id, by, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperimentApproved, "experiment", id.ToString()), ct);
        return (await RowAsync(id, ct)).View;
    }

    /// <summary>The proposer's peers who publish the page, or a tenant administrator, stop it; everyone sees the current release again.</summary>
    public async Task<ExperimentView> StopAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AnalyticsExperiments, ct);
        var by = context.RequireUser();
        var (view, pageUnit) = await RowAsync(id, ct);
        if (!await AdministersAsync(ct))
        {
            await authz.DemandAsync(Permissions.PagePublish, Target(pageUnit), ct);
        }
        if (view.Status == ExperimentStatuses.Stopped)
        {
            throw AppException.Conflict("validation.experience.experiment_state");
        }
        await session.ExecAsync("update experience.experiment set status = 'stopped', stopped_by = @by, stopped_at = @now where id = @id",
            new { id, by, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperimentStopped, "experiment", id.ToString(), Changes: new { from = view.Status }), ct);
        return (await RowAsync(id, ct)).View;
    }

    public async Task<ExperimentChoice> ChoiceAsync(CancellationToken ct)
    {
        var user = context.RequireUser();
        if (!await entitlements.IsEnabledAsync(Capabilities.AnalyticsExperiments, ct))
        {
            return new ExperimentChoice(false, false);
        }
        return new ExperimentChoice(true, await ConsentedAsync(user, ct));
    }

    public async Task<ExperimentChoice> ChooseAsync(ExperimentChoiceRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (request.Consented is not { } consented)
        {
            throw AppException.Validation("consented", "validation.experience.experiment_choice");
        }
        if (!await entitlements.IsEnabledAsync(Capabilities.AnalyticsExperiments, ct))
        {
            throw AppException.Validation("consented", "validation.experience.experiment_unavailable");
        }
        await session.ExecAsync(
            """
            insert into experience.experiment_consent (tenant_id, user_id, consented, updated_at) values (platform.current_tenant_id(), @user, @consented, @now)
            on conflict (tenant_id, user_id) do update set consented = excluded.consented, updated_at = excluded.updated_at
            """, new { user, consented, now = clock.UtcNow }, ct);
        return new ExperimentChoice(true, consented);
    }

    private async Task<bool> ConsentedAsync(Guid user, CancellationToken ct) =>
        await session.ScalarAsync<bool?>("select consented from experience.experiment_consent where user_id = @user", new { user }, ct) == true;

    /// <summary>Whether a person falls in the variant: a stable hash of the experiment and the person, below the share.</summary>
    public static bool InVariant(Guid experimentId, Guid user, int percent)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{experimentId:N}:{user:N}"));
        return BitConverter.ToUInt32(hash, 0) % 100 < (uint)percent;
    }

    /// <summary>
    /// The arm of the signed-in viewer for a page with a running experiment, or null when there is none, the package has
    /// no experiments, or the viewer did not agree or is outside the scope. Records the exposure for spec 021 analytics.
    /// </summary>
    public async Task<ExperimentArm?> ArmAsync(Guid pageId, IReadOnlySet<Guid> viewerUnits, CancellationToken ct)
    {
        if (context.ActorType != ActorType.User || context.ActorId is not { } user || context.SharedDevice is not null)
        {
            return null;
        }
        var running = await session.QuerySingleOrDefaultAsync(
            "select id, variant_release, percent, org_unit_id from experience.experiment where page_id = @pageId and status = 'running'", new { pageId },
            r => (Id: r.GetGuid(0), Variant: r.GetInt32(1), Percent: r.GetInt32(2), Unit: r.IsDBNull(3) ? (Guid?)null : r.GetGuid(3)), ct);
        if (running == default || running.Unit is { } unit && !viewerUnits.Contains(unit)
            || !await entitlements.IsEnabledAsync(Capabilities.AnalyticsExperiments, ct) || !await ConsentedAsync(user, ct))
        {
            return null;
        }
        var variant = InVariant(running.Id, user, running.Percent);
        await analytics.EmitAsync(AnalyticsEvents.ExperimentExposure,
            new Dictionary<string, object?> { ["experimentId"] = running.Id, ["arm"] = variant ? "variant" : "control" }, ct);
        return new ExperimentArm(running.Id, variant, running.Variant);
    }
}
