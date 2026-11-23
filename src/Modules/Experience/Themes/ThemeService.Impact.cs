using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

/// <summary>A token a child scope overrides, so a change to it in the brand does not reach that scope.</summary>
public sealed record OverriddenValue(string Scope, IReadOnlyDictionary<string, string> ScopeNames, string Token, TokenValue Value);

/// <summary>
/// What publishing a brand's draft reaches (spec 033 R2 T015, US2 scenario 4, FR-024). <paramref name="Report"/> holds the
/// inheriting organizations, the pages and the audience variants on them, from the spec 032 impact service.
/// <paramref name="Excluded"/> are the units below that use another brand, so the change stops there; <paramref name="Overridden"/>
/// the values that child scopes override among the tokens the draft changes. The preview is the effective theme of one
/// representative scope with the draft applied, per mode.
/// </summary>
public sealed record BrandImpact(
    ImpactReport Report,
    IReadOnlyList<string> Changes,
    IReadOnlyList<ImpactUnit> Excluded,
    IReadOnlyList<OverriddenValue> Overridden,
    string PreviewScope,
    IReadOnlyDictionary<string, string> PreviewNames,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Preview);

public sealed partial class ThemeService
{
    /// <summary>What a theme assignment at a scope reaches (spec 032 R2 T024, spec 033), for those who publish themes there.</summary>
    public async Task<ImpactReport> ImpactAsync(string scope, ImpactReports impacts, CancellationToken ct)
    {
        var unit = await ScopeAsync(scope, Permissions.ThemePublish, ct);
        await impacts.DemandAsync(ct);
        return await impacts.ForScopeAsync(unit, ct);
    }

    /// <summary>
    /// The impact of publishing the brand's draft, for those who publish themes (T015). A scope inherits the brand when the
    /// nearest brand assignment above it, or on it, is this brand. <paramref name="previewScope"/> picks the scope to preview
    /// among those; by default it is the first inheriting scope that overrides a changed token, else the first assigned one.
    /// </summary>
    public async Task<BrandImpact> BrandImpactAsync(Guid id, string? previewScope, ImpactReports impacts, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemePublish, AuthzTarget.Tenant, ct);
        var inForce = await BrandAsync(id, null, ct) ?? throw AppException.NotFound();
        await impacts.DemandAsync(ct);
        var draft = await DraftCandidateAsync(id, ct);
        var changes = draft.Tokens.Keys.Union(inForce.Tokens.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(token => !SameValue(draft.Tokens.GetValueOrDefault(token), inForce.Tokens.GetValueOrDefault(token))).ToList();

        var assignments = await AssignmentsAsync(ct);
        var branded = assignments.Where(a => a.BrandId is not null).ToDictionary(a => a.Scope ?? Guid.Empty, a => a.BrandId!.Value);
        var tenantWide = branded.TryGetValue(Guid.Empty, out var tenantBrand) && tenantBrand == id;
        var pages = await session.QueryAsync("select id, slug, org_unit_id from experience.page order by slug", null,
            r => (Id: r.GetGuid(0), Slug: r.GetString(1), Unit: r.Get<Guid?>("org_unit_id")), ct);

        // Every unit that matters: those with pages, those with assignments, and those below a unit assigned this brand.
        var assignedUnits = branded.Where(b => b.Key != Guid.Empty && b.Value == id).Select(b => b.Key).ToList();
        var candidates = new HashSet<Guid>(await hierarchy.GetSubtreesAsync(assignedUnits, ct));
        candidates.UnionWith(pages.Select(p => p.Unit).OfType<Guid>());
        candidates.UnionWith(assignments.Select(a => a.Scope).OfType<Guid>());
        var paths = candidates.Count == 0 ? new Dictionary<Guid, IReadOnlyList<Guid>>() : await hierarchy.GetPathsAsync(candidates, ct);

        // The brand in effect at a unit: the nearest assignment with a brand on its path, else the tenant's.
        Guid? EffectiveAt(Guid? unit)
        {
            if (unit is { } u && paths.TryGetValue(u, out var path))
            {
                foreach (var scope in path.Reverse())
                {
                    if (branded.TryGetValue(scope, out var b))
                    {
                        return b;
                    }
                }
            }
            return branded.TryGetValue(Guid.Empty, out var t) ? t : null;
        }

        bool Inherits(Guid? unit) => EffectiveAt(unit) == id;
        var inheriting = paths.Keys.Where(u => Inherits(u))
            .OrderBy(u => paths[u].Count).ThenBy(u => u).ToList();
        var reached = pages.Where(p => Inherits(p.Unit)).Select(p => (p.Id, p.Slug, p.Unit)).ToList();
        var report = await impacts.ForThemeAsync(tenantWide, tenantWide ? [] : inheriting, reached, ct);

        // Units using another brand directly below the area the brand reaches: the change stops there.
        var excluded = branded.Where(b => b.Key != Guid.Empty && b.Value != id && paths.TryGetValue(b.Key, out var path)
                && (path.Count > 1 ? Inherits(path[^2]) : tenantWide))
            .Select(b => b.Key).ToList();

        var overridden = new List<(Guid? Scope, string Token, TokenValue Value)>();
        foreach (var a in assignments.Where(a => a.Overrides.Count > 0 && Inherits(a.Scope)))
        {
            overridden.AddRange(a.Overrides.Where(o => changes.Contains(o.Key, StringComparer.Ordinal)).Select(o => (a.Scope, o.Key, o.Value)));
        }
        var named = await hierarchy.GetNamesAsync(excluded.Concat(overridden.Select(o => o.Scope).OfType<Guid>()).Distinct(), ct);
        IReadOnlyDictionary<string, string> NamesOf(Guid? unit) =>
            unit is { } u && named.TryGetValue(u, out var n) ? n : new Dictionary<string, string>();

        // The scope to preview: the one asked for when it inherits, else one that overrides a changed value, else the first reached.
        Guid? preview = previewScope is null || previewScope == TenantScope ? null
            : Guid.TryParse(previewScope, out var asked) ? asked : throw AppException.Validation("scope", "validation.theme.preview_scope");
        if (previewScope is not null && !Inherits(preview))
        {
            throw AppException.Validation("scope", "validation.theme.preview_scope");
        }
        if (previewScope is null)
        {
            preview = overridden.Select(o => o.Scope).FirstOrDefault(s => s is not null)
                ?? (tenantWide ? null : inheriting.Cast<Guid?>().FirstOrDefault());
        }
        var previewNames = preview is { } p0 ? (await hierarchy.GetNamesAsync([p0], ct)).GetValueOrDefault(p0) ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();
        var resolved = await ResolveAsync(preview, ct, null, (id, draft.Tokens));
        var properties = ThemeModes.Modes.ToDictionary(m => m,
            m => (IReadOnlyDictionary<string, string>)ThemeEngine.Properties(resolved.Variants[$"{m}.{ThemeModes.Comfortable}"]));

        return new BrandImpact(report, changes,
            excluded.Take(ImpactReports.ListLimit).Select(u => new ImpactUnit(u, NamesOf(u))).ToList(),
            overridden.Take(ImpactReports.ListLimit).Select(o => new OverriddenValue(ScopeName(o.Scope), NamesOf(o.Scope), o.Token, o.Value)).ToList(),
            ScopeName(preview), previewNames, properties);
    }

    private static bool SameValue(TokenValue? a, TokenValue? b) =>
        a is null ? b is null : b is not null && System.Text.Json.JsonSerializer.Serialize(a, JsonDefaults.Options) == System.Text.Json.JsonSerializer.Serialize(b, JsonDefaults.Options);
}
