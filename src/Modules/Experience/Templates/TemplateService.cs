using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Messaging;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Templates;

/// <summary>A new tenant template, copied from a platform template (spec 032 US5).</summary>
public sealed record CreateTemplateRequest(string? Id, string? BasedOn, IReadOnlyDictionary<string, string>? Names,
    IReadOnlyDictionary<string, string>? Descriptions);

/// <summary>The editable parts of a tenant template: names, the variants it offers, and each region's policy and lock (FR-019).</summary>
public sealed record TemplateDraftRequest(IReadOnlyDictionary<string, string>? Names, IReadOnlyDictionary<string, string>? Descriptions,
    IReadOnlyList<string>? Variants, IReadOnlyList<TemplateRegion>? Regions);

public sealed record TemplateDistributionRequest(IReadOnlyList<Guid>? OrgUnitIds);

public sealed record TemplateVersionView(int Version, string State, DateTimeOffset UpdatedAt, DateTimeOffset? ReleasedAt);

public sealed record TemplateView(string Id, string BasedOn, string BasedOnVersion, IReadOnlyList<Guid> Distribution, TemplateDefinition Latest,
    IReadOnlyList<TemplateVersionView> Versions);

/// <summary>
/// Tenant templates (spec 032 US5): copies of platform templates with their own names, variants, region policies and locks,
/// versioned draft, released and deprecated (FR-021), and distributed to org subtrees (FR-020). Pages keep the version
/// they were made from (FR-018). Template designers need <c>template.edit</c> for the whole tenant and distribution needs
/// <c>template.distribute</c>; templates scoped to one organization's designers are a later step.
/// </summary>
public sealed partial class TemplateService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    PlatformCatalogue catalogue,
    TemplateStore store,
    IAuditWriter audit,
    IOutbox outbox,
    ILocalizer localizer)
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 300;
    public const int MaxDistribution = 50;

    public async Task<IReadOnlyList<TemplateView>> ListAsync(CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        var ids = await session.QueryAsync("select template_id from experience.template order by created_at", null, r => r.GetString(0), ct);
        var result = new List<TemplateView>();
        foreach (var id in ids)
        {
            result.Add((await ViewAsync(id, ct))!);
        }
        return result;
    }

    public async Task<TemplateView> GetAsync(string id, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        return await ViewAsync(id, ct) ?? throw AppException.NotFound();
    }

    public async Task<TemplateView> CreateAsync(CreateTemplateRequest request, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        var errors = new ValidationErrors();
        if (request.Id is null || !TemplateId().IsMatch(request.Id))
        {
            errors.Add("id", "validation.template.id_invalid");
        }
        if (request.BasedOn is null || !catalogue.Templates.TryGetValue(request.BasedOn, out var platform) || platform.State != TemplateStates.Released)
        {
            errors.Add("basedOn", "validation.page.template_unknown");
            platform = null;
        }
        ValidateTexts(errors, request.Names, request.Descriptions);
        errors.ThrowIfAny();

        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('experience.template:' || @t::text))", new { t = context.RequireTenant() }, ct);
        if (await session.ScalarAsync<int?>("select 1 from experience.template where template_id = @id", new { id = request.Id }, ct) is not null)
        {
            throw AppException.Conflict("conflict.template.id_taken");
        }
        var count = await session.ScalarAsync<long>("select count(*) from experience.template", null, ct);
        var limit = await entitlements.GetLimitAsync(Capabilities.ExperienceMaxCustomTemplates, ct);
        if (limit is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.template.limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.ExperienceMaxCustomTemplates, ["limit"] = max });
        }

        var actor = context.RequireUser();
        var stored = new StoredTemplate(Clean(request.Names!), Clean(request.Descriptions ?? new Dictionary<string, string>()), platform!.Layout,
            platform.Variants, platform.Regions);
        await session.ExecAsync(
            """
            insert into experience.template (tenant_id, template_id, based_on, based_on_version, created_by)
            values (@t, @id, @base, @baseVersion, @actor)
            """, new { t = context.RequireTenant(), id = request.Id, @base = platform.Id, baseVersion = platform.Version, actor }, ct);
        await InsertVersionAsync(request.Id!, 1, stored, ct);
        return (await ViewAsync(request.Id!, ct))!;
    }

    /// <summary>Saves the draft. When the latest version is released or deprecated, the draft is a new version (FR-021).</summary>
    public async Task<TemplateView> SaveDraftAsync(string id, TemplateDraftRequest request, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        var (version, state, current) = await LatestAsync(id, forUpdate: true, ct) ?? throw AppException.NotFound();
        var platform = catalogue.Templates[(await HeadAsync(id, ct))!.Value.BasedOn];
        var errors = new ValidationErrors();
        ValidateTexts(errors, request.Names, request.Descriptions);
        if (request.Variants is not { Count: > 0 } variants || variants.Distinct().Count() != variants.Count || variants.Any(v => !platform.Variants.Contains(v)))
        {
            errors.Add("variants", "validation.template.variants_invalid");
        }
        var regions = request.Regions ?? [];
        var layout = catalogue.Layouts[current.Layout];
        if (regions.Count != layout.Regions.Count || layout.Regions.Any(lr => regions.Count(r => r.Id == lr.Id) != 1))
        {
            errors.Add("regions", "validation.template.regions_mismatch");
        }
        foreach (var region in regions)
        {
            if (region.Policy is not { } p || p.AllowedCategories is null || p.AllowedCategories.Count > ComponentRegistry.Categories.Count
                || p.AllowedCategories.Any(c => !ComponentRegistry.Categories.Contains(c)) || p.MinPlacements < 0 || p.MaxPlacements < 1
                || p.MaxPlacements > Pages.PageValidator.MaxPlacementsPerPage || p.MinPlacements > p.MaxPlacements
                || !RegionProtection.All.Contains(p.Protection))
            {
                errors.Add($"regions.{region.Id}", "validation.template.region_policy_invalid");
            }
        }
        errors.ThrowIfAny();

        var stored = new StoredTemplate(Clean(request.Names!), Clean(request.Descriptions ?? new Dictionary<string, string>()), current.Layout,
            request.Variants!, layout.Regions.Select(lr => regions.Single(r => r.Id == lr.Id)).ToList());
        if (state == TemplateStates.Draft)
        {
            await session.ExecAsync(
                "update experience.template_version set definition = @d, updated_by = @u, updated_at = now() where template_id = @id and version = @v",
                new { d = Jsonb.From(stored), u = context.RequireUser(), id, v = version }, ct);
        }
        else
        {
            await InsertVersionAsync(id, version + 1, stored, ct);
        }
        // A changed lock is recorded with the region and both values (spec 032 section 13).
        foreach (var region in stored.Regions)
        {
            var before = current.Regions.FirstOrDefault(r => r.Id == region.Id)?.Policy.Protection;
            if (before != region.Policy.Protection)
            {
                await audit.WriteAsync(new AuditEntry(AuditActions.RegionLockChanged, "template", id,
                    Changes: new { region = region.Id, before, after = region.Policy.Protection }), ct);
            }
        }
        return (await ViewAsync(id, ct))!;
    }

    public async Task<TemplateView> ReleaseAsync(string id, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        var (version, state, _) = await LatestAsync(id, forUpdate: true, ct) ?? throw AppException.NotFound();
        if (state != TemplateStates.Draft)
        {
            throw AppException.Conflict("conflict.template.no_draft");
        }
        await session.ExecAsync(
            "update experience.template_version set state = 'released', released_at = now() where template_id = @id and version = @v",
            new { id, v = version }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TemplateReleased, "template", id, Changes: new { version }), ct);
        await outbox.EnqueueAsync(new TemplateReleased(id, version), ct);
        return (await ViewAsync(id, ct))!;
    }

    /// <summary>Deprecated versions keep their pages working, but new pages cannot use them (FR-021).</summary>
    public async Task<TemplateView> DeprecateAsync(string id, CancellationToken ct)
    {
        await DemandDesignerAsync(ct);
        if (await HeadAsync(id, ct) is null)
        {
            throw AppException.NotFound();
        }
        var versions = await session.QueryAsync(
            "update experience.template_version set state = 'deprecated' where template_id = @id and state = 'released' returning version",
            new { id }, r => r.GetInt32(0), ct);
        if (versions.Count == 0)
        {
            throw AppException.Conflict("conflict.template.not_released");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.TemplateDeprecated, "template", id, Changes: new { versions }), ct);
        await outbox.EnqueueAsync(new TemplateDeprecated(id, versions), ct);
        return (await ViewAsync(id, ct))!;
    }

    /// <summary>Which org subtrees may use the template; an empty list is the whole tenant (FR-020).</summary>
    public async Task<TemplateView> DistributeAsync(string id, TemplateDistributionRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await authz.DemandAsync(Permissions.TemplateDistribute, AuthzTarget.Tenant, ct);
        var head = await HeadAsync(id, ct) ?? throw AppException.NotFound();
        var units = (request.OrgUnitIds ?? []).Distinct().ToList();
        if (units.Count > MaxDistribution)
        {
            throw AppException.Validation("orgUnitIds", "validation.template.distribution_too_long");
        }
        foreach (var unit in units)
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                throw AppException.Validation("orgUnitIds", "validation.page.org_unit_unknown");
            }
        }
        await session.ExecAsync("update experience.template set distribution = @d where template_id = @id", new { d = units.ToArray(), id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.TemplateDistributed, "template", id,
            Changes: new { before = head.Distribution, after = units }), ct);
        return (await ViewAsync(id, ct))!;
    }

    /// <summary>
    /// Whether a page at <paramref name="orgUnitId"/> (null for the whole tenant) may use the template. Platform templates
    /// may be used anywhere; a tenant template only inside its distribution (FR-020).
    /// </summary>
    public async Task<bool> UsableAtAsync(TemplateDefinition template, Guid? orgUnitId, CancellationToken ct)
    {
        if (TemplateStore.IsPlatform(catalogue, template.Id))
        {
            return true;
        }
        var head = await HeadAsync(template.Id, ct);
        if (head is null)
        {
            return false;
        }
        if (head.Value.Distribution.Count == 0)
        {
            return true;
        }
        return orgUnitId is { } unit && (await hierarchy.GetSubtreesAsync(head.Value.Distribution, ct)).Contains(unit);
    }

    /// <summary>
    /// Released tenant templates the caller may build pages with: distributed to the whole tenant, or to a subtree where the
    /// caller can edit pages. Others are left out, so an editor elsewhere never learns they exist (US5 scenario 4).
    /// </summary>
    public async Task<IReadOnlyList<TemplateDefinition>> ForChooserAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.PageEdit, ct);
        var heads = await session.QueryAsync("select template_id, distribution from experience.template order by created_at", null,
            r => (Id: r.GetString(0), Distribution: r.GetFieldValue<Guid[]>(1)), ct);
        var result = new List<TemplateDefinition>();
        foreach (var head in heads)
        {
            var visible = head.Distribution.Length == 0
                || coverage.WholeTenant
                || (await hierarchy.GetSubtreesAsync(head.Distribution, ct)).Overlaps(coverage.Units);
            if (visible && await store.CurrentAsync(head.Id, ct) is { } current)
            {
                result.Add(current);
            }
        }
        return result;
    }

    /// <summary>The template's name in the caller's language, falling back to any name it has.</summary>
    public string NameOf(TemplateDefinition template, string locale) => template.Names is { Count: > 0 } names
        ? names.GetValueOrDefault(locale) ?? names.GetValueOrDefault(locale.Split('-')[0]) ?? names.Values.First()
        : localizer.Get(template.NameKey, locale);

    public string DescriptionOf(TemplateDefinition template, string locale) => template.Names is { Count: > 0 }
        ? template.Descriptions?.GetValueOrDefault(locale) ?? template.Descriptions?.GetValueOrDefault(locale.Split('-')[0]) ?? ""
        : localizer.Get(template.DescriptionKey, locale);

    private async Task DemandDesignerAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await authz.DemandAsync(Permissions.TemplateEdit, AuthzTarget.Tenant, ct);
    }

    private static void ValidateTexts(ValidationErrors errors, IReadOnlyDictionary<string, string>? names, IReadOnlyDictionary<string, string>? descriptions)
    {
        if (names is null || names.Count == 0 || names.Count > 10 || names.Any(n => !Locale().IsMatch(n.Key) || string.IsNullOrWhiteSpace(n.Value)
            || n.Value.Trim().Length > MaxNameLength))
        {
            errors.Add("names", "validation.template.names_invalid");
        }
        if (descriptions is not null && (descriptions.Count > 10 || descriptions.Any(d => !Locale().IsMatch(d.Key) || d.Value is null
            || d.Value.Trim().Length > MaxDescriptionLength)))
        {
            errors.Add("descriptions", "validation.template.descriptions_invalid");
        }
    }

    private static IReadOnlyDictionary<string, string> Clean(IReadOnlyDictionary<string, string> texts) =>
        texts.Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);

    private Task InsertVersionAsync(string id, int version, StoredTemplate stored, CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into experience.template_version (tenant_id, template_id, version, state, definition, updated_by)
            values (@t, @id, @v, 'draft', @d, @u)
            """, new { t = context.RequireTenant(), id, v = version, d = Jsonb.From(stored), u = context.RequireUser() }, ct);

    private async Task<(string BasedOn, string BasedOnVersion, IReadOnlyList<Guid> Distribution)?> HeadAsync(string id, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select based_on, based_on_version, distribution from experience.template where template_id = @id", new { id },
            r => (r.GetString(0), r.GetString(1), (IReadOnlyList<Guid>)r.GetFieldValue<Guid[]>(2)), ct);
        return row == default ? null : row;
    }

    private async Task<(int Version, string State, TemplateDefinition Definition)?> LatestAsync(string id, bool forUpdate, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select version, state, definition::text from experience.template_version where template_id = @id order by version desc limit 1"
            + (forUpdate ? " for update" : ""),
            new { id }, r => (r.GetInt32(0), r.GetString(1), r.GetString(2)), ct);
        return row == default ? null : (row.Item1, row.Item2, TemplateStore.Map(id, row.Item1, row.Item2, row.Item3));
    }

    private async Task<TemplateView?> ViewAsync(string id, CancellationToken ct)
    {
        var head = await HeadAsync(id, ct);
        if (head is null || await LatestAsync(id, forUpdate: false, ct) is not { } latest)
        {
            return null;
        }
        var versions = await session.QueryAsync(
            "select version, state, updated_at, released_at from experience.template_version where template_id = @id order by version desc",
            new { id }, r => new TemplateVersionView(r.GetInt32(0), r.GetString(1), r.Get<DateTimeOffset>("updated_at"), r.Get<DateTimeOffset?>("released_at")), ct);
        return new TemplateView(id, head.Value.BasedOn, head.Value.BasedOnVersion, head.Value.Distribution, latest.Definition, versions);
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{2,39}$")]
    private static partial Regex TemplateId();

    [GeneratedRegex("^[a-z]{2}(-[A-Z]{2})?$")]
    private static partial Regex Locale();
}
