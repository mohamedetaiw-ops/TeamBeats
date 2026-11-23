using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Themes;

/// <param name="Source">draft when the brand's draft is shown, else current.</param>
/// <param name="Inherits">The scope uses this brand, so the preview shows the brand with the scope's own overrides and locks.</param>
/// <param name="Variants">Custom properties per variant key (<c>light.comfortable</c> and so on), as the theme CSS would serve them.</param>
public sealed record BrandScopePreview(string Scope, IReadOnlyDictionary<string, string> ScopeNames, string Source, bool Inherits,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Variants, IReadOnlyList<ContrastFinding> Findings);

public sealed partial class ThemeService
{
    /// <summary>
    /// Previews a brand as one organization would see it (spec 033 R2 T016, FR-008, FR-023): the effective theme at the scope,
    /// with the brand's draft in place of the version in force when there is one (unless <paramref name="source"/> is current), in every mode and density, with the contrast
    /// checks of that result. Nothing is saved, and the theme employees get does not change. For people who edit themes.
    /// </summary>
    public async Task<BrandScopePreview> PreviewAsync(Guid id, string? scope, string? source, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ThemeEdit, AuthzTarget.Tenant, ct);
        await EnsureSeededAsync(ct);
        var brand = await BrandAsync(id, null, ct) ?? throw AppException.NotFound();
        var unit = await ReadableScopeAsync(scope, ct);
        if (source is not (null or "draft" or "current"))
        {
            throw AppException.Validation("source", "validation.theme.preview_source");
        }
        var draft = source == "current" ? null : await session.QuerySingleOrDefaultAsync(DraftSelect + " where brand_id = @id", new { id },
            r => new BrandCandidate(r.GetString(0), Tokens(r.GetString(1)), Assets(r.GetString(2)), r.GetBoolean(3)), ct);
        var tokens = draft?.Tokens ?? brand.Tokens;
        var strict = draft?.Strict ?? brand.Strict;
        var (layers, effective) = await LayersAsync(unit, ct, null, (id, tokens));
        var locks = await LocksForAsync(unit, ct);
        var resolved = ThemeEngine.Resolve(layers, locks.ToDictionary(l => l.Key, l => (l.Value.Depth, l.Value.Scope)));
        var names = unit is { } u ? (await hierarchy.GetNamesAsync([u], ct)).GetValueOrDefault(u) ?? new Dictionary<string, string>()
            : new Dictionary<string, string>();
        var variants = ThemeEngine.VariantKeys().ToDictionary(v => v.Key, v => (IReadOnlyDictionary<string, string>)ThemeEngine.Properties(resolved.Variants[v.Key]));
        return new BrandScopePreview(ScopeName(unit), names, draft is null ? "current" : "draft", effective.Id == id, variants,
            ThemeEngine.Check(resolved, strict));
    }
}
