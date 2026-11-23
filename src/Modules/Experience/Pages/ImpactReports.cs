using System.Text.Json;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Personalization;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Pages;

public sealed record ImpactUnit(Guid Id, IReadOnlyDictionary<string, string> Names);

public sealed record ImpactAudience(Guid Id, IReadOnlyDictionary<string, string> Labels);

public sealed record ImpactPage(Guid Id, string Slug, Guid? OrgUnitId);

/// <summary>
/// What publishing a shared artifact reaches (spec 032 FR-054, US6 scenario 2): the organizations, the audiences, the pages
/// and the personal layouts. <paramref name="PersonalLayouts"/> counts every personal layout on the pages;
/// <paramref name="PersonalLayoutsTouched"/> those with a change the release no longer applies (a placement or region it
/// drops), which the viewer will see skipped until the editor brings it back. Lists stop at <see cref="ImpactReports.ListLimit"/>
/// entries; the counts are whole.
/// </summary>
public sealed record ImpactReport(
    string Artifact,
    bool TenantWide,
    int OrgUnitCount,
    IReadOnlyList<ImpactUnit> OrgUnits,
    IReadOnlyList<ImpactAudience> Audiences,
    int PageCount,
    IReadOnlyList<ImpactPage> Pages,
    int PersonalLayouts,
    int PersonalLayoutsTouched);

/// <summary>
/// Computes impact reports for page publishing, template releases and theme assignments (spec 033). The report reads only
/// this module's tables and the org hierarchy contract; callers decide who may see it. Impact reports come with
/// <c>experience.approval_routes</c>; without it publishing stays one step as in R1.
/// </summary>
public sealed class ImpactReports(IDbSession session, IOrganizationHierarchy hierarchy, IEntitlementService entitlements)
{
    public const int ListLimit = 50;

    public static class Artifacts
    {
        public const string Page = "page";
        public const string Template = "template";
        public const string Theme = "theme";
    }

    private sealed record PageLine(Guid Id, string Slug, Guid? OrgUnitId);

    public Task DemandAsync(CancellationToken ct) => entitlements.DemandAsync(Capabilities.ExperienceApprovalRoutes, ct);

    /// <summary>Publishing <paramref name="draft"/> over the page's current release.</summary>
    internal async Task<ImpactReport> ForPageAsync(PageInfo page, PageDefinition draft, PageDefinition? current, CancellationToken ct)
    {
        var removedPlacements = new HashSet<string>(StringComparer.Ordinal);
        var removedRegions = new HashSet<string>(StringComparer.Ordinal);
        if (current is not null)
        {
            var kept = draft.Regions.SelectMany(r => r.Placements).Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            removedPlacements.UnionWith(current.Regions.SelectMany(r => r.Placements).Select(p => p.Id).Where(id => !kept.Contains(id)));
            var regions = draft.Regions.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            removedRegions.UnionWith(current.Regions.Select(r => r.Id).Where(id => !regions.Contains(id)));
        }
        var (layouts, touched) = await LayoutsAsync([page.Id], ops => ops.Any(o =>
            (o.PlacementId is { } p && o.Op != LayoutEngine.Add && removedPlacements.Contains(p))
            || (o.RegionId is { } r && removedRegions.Contains(r))), ct);
        // Audiences the change reaches: placement visibility rules on either side, and region policies of the page.
        var rules = draft.Regions.Concat(current?.Regions ?? []).SelectMany(r => r.Placements)
            .Select(p => Guid.TryParse(p.VisibilityRuleRef, out var g) ? g : (Guid?)null).OfType<Guid>().ToHashSet();
        rules.UnionWith(await PolicyRulesAsync([page.Id], ct));
        var (tenantWide, units) = await UnitsAsync(page.OrgUnitId is { } u ? [u] : null, ct);
        return new ImpactReport(Artifacts.Page, tenantWide, units.Count, await NamedAsync(units, ct), await AudiencesAsync(rules, ct),
            1, [new ImpactPage(page.Id, page.Slug, page.OrgUnitId)], layouts, touched);
    }

    /// <summary>
    /// Releasing a new version of a tenant template: every page made from the template is offered it (pages keep their
    /// version until their editor changes, FR-018), within the template's distribution.
    /// </summary>
    internal async Task<ImpactReport> ForTemplateAsync(string templateId, IReadOnlyList<Guid> distribution, CancellationToken ct)
    {
        var pages = await session.QueryAsync("select id, slug, org_unit_id from experience.page where template_id = @templateId order by slug",
            new { templateId }, r => new PageLine(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("org_unit_id")), ct);
        return await ForPagesAsync(Artifacts.Template, distribution.Count == 0 ? null : distribution, pages, ct);
    }

    /// <summary>A theme assignment at <paramref name="orgUnitId"/> (null for the tenant): the pages of that scope and below.</summary>
    public async Task<ImpactReport> ForScopeAsync(Guid? orgUnitId, CancellationToken ct)
    {
        IReadOnlyList<PageLine> pages;
        if (orgUnitId is { } unit)
        {
            var subtree = (await hierarchy.GetSubtreesAsync([unit], ct)).ToArray();
            pages = await session.QueryAsync("select id, slug, org_unit_id from experience.page where org_unit_id = any(@subtree) order by slug",
                new { subtree }, r => new PageLine(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("org_unit_id")), ct);
        }
        else
        {
            pages = await session.QueryAsync("select id, slug, org_unit_id from experience.page order by slug", null,
                r => new PageLine(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("org_unit_id")), ct);
        }
        return await ForPagesAsync(Artifacts.Theme, orgUnitId is { } u ? [u] : null, pages, ct);
    }

    /// <summary>
    /// A brand release (spec 033 R2 T015): the caller has worked out which units inherit the brand and which pages they hold,
    /// so the units are listed as given, without adding their subtrees, which may use another brand.
    /// </summary>
    internal async Task<ImpactReport> ForThemeAsync(bool tenantWide, IReadOnlyList<Guid> units, IReadOnlyList<(Guid Id, string Slug, Guid? Unit)> pages,
        CancellationToken ct)
    {
        var ids = pages.Select(p => p.Id).ToArray();
        var (layouts, _) = await LayoutsAsync(ids, _ => false, ct);
        var rules = await PolicyRulesAsync(ids, ct);
        return new ImpactReport(Artifacts.Theme, tenantWide, tenantWide ? 0 : units.Count, tenantWide ? [] : await NamedAsync(units, ct),
            await AudiencesAsync(rules, ct), pages.Count, pages.Take(ListLimit).Select(p => new ImpactPage(p.Id, p.Slug, p.Unit)).ToList(), layouts, 0);
    }

    /// <summary>
    /// A template release or a theme reaches pages without changing their arrangement: a page keeps its template version until
    /// its editor changes it, and a theme changes how pages look. So no personal layout is touched; the count shows how many
    /// people's layouts sit on the pages reached.
    /// </summary>
    private async Task<ImpactReport> ForPagesAsync(string artifact, IReadOnlyList<Guid>? scopes, IReadOnlyList<PageLine> pages, CancellationToken ct)
    {
        var ids = pages.Select(p => p.Id).ToArray();
        var (layouts, _) = await LayoutsAsync(ids, _ => false, ct);
        var rules = await PolicyRulesAsync(ids, ct);
        var (tenantWide, units) = await UnitsAsync(scopes, ct);
        return new ImpactReport(artifact, tenantWide, units.Count, await NamedAsync(units, ct), await AudiencesAsync(rules, ct),
            pages.Count, pages.Take(ListLimit).Select(p => new ImpactPage(p.Id, p.Slug, p.OrgUnitId)).ToList(), layouts, 0);
    }

    /// <summary>The personal layouts on the pages, and how many <paramref name="touched"/> says no longer apply as saved.</summary>
    private async Task<(int Count, int Touched)> LayoutsAsync(Guid[] pageIds, Func<IReadOnlyList<LayoutOperation>, bool> touched, CancellationToken ct)
    {
        if (pageIds.Length == 0)
        {
            return (0, 0);
        }
        var rows = await session.QueryAsync("select operations::text from experience.personal_layout where page_id = any(@pageIds)",
            new { pageIds }, r => r.GetString(0), ct);
        var hit = rows.Count(json => touched(JsonSerializer.Deserialize<List<LayoutOperation>>(json, JsonDefaults.Options) ?? []));
        return (rows.Count, hit);
    }

    private async Task<HashSet<Guid>> PolicyRulesAsync(Guid[] pageIds, CancellationToken ct) =>
        pageIds.Length == 0 ? [] : (await session.QueryAsync(
            "select distinct audience_rule_id from experience.personalization_policy where page_id = any(@pageIds) and audience_rule_id is not null",
            new { pageIds }, r => r.GetGuid(0), ct)).ToHashSet();

    /// <summary>The org units reached: the scopes and every unit below them, or the tenant when <paramref name="scopes"/> is null.</summary>
    private async Task<(bool TenantWide, IReadOnlyList<Guid> Units)> UnitsAsync(IReadOnlyList<Guid>? scopes, CancellationToken ct)
    {
        if (scopes is null)
        {
            return (true, []);
        }
        var subtree = await hierarchy.GetSubtreesAsync(scopes, ct);
        // The scopes first, then the units below them.
        return (false, scopes.Where(subtree.Contains).Concat(subtree.Where(u => !scopes.Contains(u))).ToList());
    }

    private async Task<IReadOnlyList<ImpactUnit>> NamedAsync(IReadOnlyList<Guid> units, CancellationToken ct)
    {
        var shown = units.Take(ListLimit).ToList();
        if (shown.Count == 0)
        {
            return [];
        }
        var names = await hierarchy.GetNamesAsync(shown, ct);
        return shown.Select(u => new ImpactUnit(u, names.TryGetValue(u, out var n) ? n : new Dictionary<string, string>())).ToList();
    }

    private async Task<IReadOnlyList<ImpactAudience>> AudiencesAsync(IReadOnlySet<Guid> rules, CancellationToken ct)
    {
        if (rules.Count == 0)
        {
            return [];
        }
        var ids = rules.ToArray();
        return await session.QueryAsync("select rule_id, labels::text from experience.audience_rule where rule_id = any(@ids) order by rule_id",
            new { ids }, r => new ImpactAudience(r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1))!), ct);
    }
}
