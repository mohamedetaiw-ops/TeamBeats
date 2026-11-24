namespace TeamBeats.Modules.Experience.Themes;

public sealed record MoveBrand(Guid Id, string Name);

/// <summary>How an organization's theme changes when it moves under another parent (spec 033 US5, FR-025).</summary>
public sealed record ThemeMoveImpact(MoveBrand Before, MoveBrand After, IReadOnlyList<string> LocksGained, IReadOnlyList<string> LocksLost,
    int TokensChanged, IReadOnlyList<string> Tokens);

public sealed partial class ThemeService
{
    /// <summary>
    /// The organization's theme with its current parents and with the new ones: the brand in effect, the locks that start
    /// and stop applying and the tokens whose value changes in any mode. Its own assignment and overrides move with it.
    /// The caller decides who may see it.
    /// </summary>
    internal async Task<ThemeMoveImpact> MoveImpactAsync(Guid unit, Guid? newParent, CancellationToken ct)
    {
        await EnsureSeededAsync(ct);
        var before = await ChainAsync(unit, ct);
        var after = new List<Guid?> { null };
        if (newParent is { } p)
        {
            after.AddRange((await hierarchy.GetAncestorsAndSelfAsync(p, ct)).Reverse().Select(id => (Guid?)id));
        }
        after.Add(unit);

        async Task<(ResolvedTheme Theme, BrandRow Brand, Dictionary<string, (int Depth, Guid? Scope)> Locks)> At(List<Guid?> chain)
        {
            var (layers, brand) = await LayersAsync(unit, ct, null, null, chain);
            var locks = await LocksForAsync(unit, ct, chain);
            return (ThemeEngine.Resolve(layers, locks), brand, locks);
        }
        var (oldTheme, oldBrand, oldLocks) = await At(before);
        var (newTheme, newBrand, newLocks) = await At(after);
        // A lock the organization set itself moves with it; only inherited ones start or stop.
        var inheritedOld = oldLocks.Where(l => l.Value.Scope != unit).Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        var inheritedNew = newLocks.Where(l => l.Value.Scope != unit).Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        var changed = TokenSchema.All.Select(t => t.Name)
            .Where(name => ThemeEngine.VariantKeys().Any(v => oldTheme.Variants[v.Key][name] != newTheme.Variants[v.Key][name]))
            .ToList();
        return new ThemeMoveImpact(new MoveBrand(oldBrand.Id, oldBrand.Name), new MoveBrand(newBrand.Id, newBrand.Name),
            inheritedNew.Except(inheritedOld).Order(StringComparer.Ordinal).ToList(),
            inheritedOld.Except(inheritedNew).Order(StringComparer.Ordinal).ToList(),
            changed.Count, changed.Take(50).ToList());
    }
}
