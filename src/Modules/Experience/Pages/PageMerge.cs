using System.Text.Json;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Pages;

/// <summary>One placement the merge could not take from both sides, and what it kept.</summary>
/// <param name="Kind"><c>kept_theirs</c> when both changed it (their version stays), <c>restored</c> when they removed what
/// the editor changed (the editor's version comes back), <c>removed_changed</c> when the editor removed what they changed
/// (their version stays).</param>
public sealed record MergeConflict(string PlacementId, string Kind);

public static class MergeConflictKinds
{
    public const string KeptTheirs = "kept_theirs";
    public const string Restored = "restored";
    public const string RemovedChanged = "removed_changed";
}

/// <summary>
/// Three-way merge of page drafts (spec 032 R2 T026, US6 scenario 6). Starting from the version saved meanwhile
/// ("theirs"), it applies what the editor changed since the version they started from ("base"), placement by placement:
/// a change only one side made is taken, the same change on both sides is taken once, and where both changed the same
/// placement differently theirs stays and the placement is reported. Nothing is dropped silently: a placement they removed
/// and the editor changed comes back. Template, layout and variant merge the same way; when both changed them
/// differently there is no merge and the editor chooses a whole version.
/// </summary>
public static class PageMerge
{
    private sealed record Spot(string Region, Placement Placement, int Index);

    public static (PageDefinition Merged, IReadOnlyList<MergeConflict> Conflicts) Merge(PageDefinition @base, PageDefinition mine, PageDefinition theirs)
    {
        var template = Pick(@base.Template, mine.Template, theirs.Template, Same);
        var layout = Pick(@base.Layout, mine.Layout, theirs.Layout, Same);
        var variant = Pick(@base.Variant, mine.Variant, theirs.Variant, (a, b) => a == b);
        if (template is null || layout is null || variant is null)
        {
            throw AppException.Conflict("conflict.page.merge_structure");
        }
        var b = Index(@base);
        var m = Index(mine);
        var t = Index(theirs);
        var conflicts = new List<MergeConflict>();
        // What each placement ends up as: null means it is not on the page.
        var result = new Dictionary<string, Spot?>(StringComparer.Ordinal);
        foreach (var id in b.Keys.Concat(m.Keys).Concat(t.Keys).Distinct(StringComparer.Ordinal))
        {
            b.TryGetValue(id, out var bs);
            m.TryGetValue(id, out var ms);
            t.TryGetValue(id, out var ts);
            if (Same(ms, bs) || Same(ms, ts))
            {
                result[id] = ts;
            }
            else if (Same(ts, bs))
            {
                result[id] = ms;
            }
            else if (ts is null)
            {
                result[id] = ms;
                conflicts.Add(new MergeConflict(id, MergeConflictKinds.Restored));
            }
            else
            {
                result[id] = ts;
                conflicts.Add(new MergeConflict(id, ms is null ? MergeConflictKinds.RemovedChanged : MergeConflictKinds.KeptTheirs));
            }
        }
        // Regions follow their version; within a region their order stays, and what comes from the editor goes after the
        // placement that came before it in the editor's version.
        var regions = theirs.Regions.Select(r => r.Id).Concat(mine.Regions.Select(r => r.Id)).Distinct(StringComparer.Ordinal).ToList();
        var merged = new List<PageRegion>();
        foreach (var region in regions)
        {
            var list = new List<Placement>();
            var ordered = theirs.Regions.FirstOrDefault(r => r.Id == region)?.Placements ?? [];
            foreach (var p in ordered)
            {
                if (result.TryGetValue(p.Id, out var spot) && spot is not null && spot.Region == region && ReferenceEquals(spot, t[p.Id]))
                {
                    list.Add(spot.Placement);
                }
            }
            var mineOrder = mine.Regions.FirstOrDefault(r => r.Id == region)?.Placements ?? [];
            foreach (var (p, i) in mineOrder.Select((p, i) => (p, i)))
            {
                if (!result.TryGetValue(p.Id, out var spot) || spot is null || spot.Region != region || !ReferenceEquals(spot, m[p.Id]))
                {
                    continue;
                }
                var replaced = list.FindIndex(x => x.Id == p.Id);
                if (replaced >= 0)
                {
                    list[replaced] = spot.Placement;
                    continue;
                }
                // After the placement before it in the editor's version, else before the one after it, else last.
                var before = mineOrder.Take(i).Select(x => x.Id).LastOrDefault(prev => list.Any(x => x.Id == prev));
                var after = mineOrder.Skip(i + 1).Select(x => x.Id).FirstOrDefault(next => list.Any(x => x.Id == next));
                list.Insert(before is not null ? list.FindIndex(x => x.Id == before) + 1 : after is not null ? list.FindIndex(x => x.Id == after) : list.Count,
                    spot.Placement);
            }
            if (list.Count > 0 || theirs.Regions.Any(r => r.Id == region))
            {
                merged.Add(new PageRegion(region, list));
            }
        }
        return (new PageDefinition(template, layout, variant, merged), conflicts);
    }

    private static T? Pick<T>(T @base, T mine, T theirs, Func<T?, T?, bool> same) where T : class =>
        same(mine, @base) ? theirs : same(theirs, @base) || same(mine, theirs) ? mine : null;

    private static Dictionary<string, Spot> Index(PageDefinition definition) =>
        definition.Regions.SelectMany(r => r.Placements.Select((p, i) => new Spot(r.Id, p, i)))
            .GroupBy(s => s.Placement.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static bool Same(Spot? a, Spot? b) =>
        a is null ? b is null : b is not null && a.Region == b.Region && Same(a.Placement, b.Placement);

    private static bool Same<T>(T? a, T? b) =>
        JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options);
}
