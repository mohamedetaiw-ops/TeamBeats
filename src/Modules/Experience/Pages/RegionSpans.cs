using TeamBeats.Modules.Experience.Catalogue;

namespace TeamBeats.Modules.Experience.Pages;

/// <summary>
/// Resizable regions (spec 032 R2 T029, FR-007). A layout declares how far each region may be resized
/// (<see cref="LayoutRegion.MinSpan"/>, <see cref="LayoutRegion.MaxSpan"/>); a page keeps the widths its editor chose in
/// <see cref="PageDefinition.Spans"/>. Regions that share a row (consecutive regions of the same section whose layout widths
/// fit 12 columns, as the renderer draws them) must keep the row's total, so a wider region always makes its neighbour
/// narrower and regions never overlap.
/// </summary>
public static class RegionSpans
{
    public static int Min(LayoutRegion region) => region.MinSpan ?? region.Span;

    public static int Max(LayoutRegion region) => region.MaxSpan ?? region.Span;

    public static bool Resizable(LayoutRegion region) => Min(region) < Max(region);

    /// <summary>The layout's rows: consecutive regions of one section mode while their layout widths fit 12 columns.</summary>
    public static List<List<LayoutRegion>> Rows(LayoutDefinition layout)
    {
        var rows = new List<List<LayoutRegion>>();
        var used = 0;
        foreach (var region in layout.Regions)
        {
            if (rows.Count > 0 && rows[^1][0].Section == region.Section && used + region.Span <= 12)
            {
                rows[^1].Add(region);
                used += region.Span;
            }
            else
            {
                rows.Add([region]);
                used = region.Span;
            }
        }
        return rows;
    }

    /// <summary>The layout with the page's widths applied where they are valid; any other width stays the layout's.</summary>
    public static LayoutDefinition Apply(LayoutDefinition layout, PageDefinition page)
    {
        if (page.Spans is not { Count: > 0 } || Findings(layout, page).Count > 0)
        {
            return layout;
        }
        return layout with
        {
            Regions = layout.Regions.Select(r => page.Spans.TryGetValue(r.Id, out var span) ? r with { Span = span } : r).ToList(),
        };
    }

    /// <summary>Codes (without the <c>validation.page.</c> prefix) and regions for widths the layout does not allow.</summary>
    public static List<(string Code, string Region)> Findings(LayoutDefinition layout, PageDefinition page)
    {
        var result = new List<(string, string)>();
        if (page.Spans is not { Count: > 0 } spans)
        {
            return result;
        }
        var regions = layout.Regions.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var (id, span) in spans)
        {
            if (!regions.TryGetValue(id, out var region))
            {
                result.Add(("region_unknown", id));
            }
            else if (span < Min(region) || span > Max(region))
            {
                result.Add(("region_span_invalid", id));
            }
        }
        if (result.Count > 0)
        {
            return result;
        }
        foreach (var row in Rows(layout))
        {
            if (row.Sum(r => spans.TryGetValue(r.Id, out var s) ? s : r.Span) != row.Sum(r => r.Span))
            {
                result.Add(("region_span_overlap", row[0].Id));
            }
        }
        return result;
    }
}
