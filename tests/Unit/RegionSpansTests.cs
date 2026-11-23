using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Pages;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 032 R2 T029 (FR-007): regions resize within the layout's limits and the regions of a row never overlap.</summary>
public sealed class RegionSpansTests
{
    private static readonly LayoutDefinition Layout = new("L03", "1", "layout.L03", [
        new LayoutRegion("aside", 4, "contained", 3, 5),
        new LayoutRegion("main", 8, "contained", 7, 9),
        new LayoutRegion("band", 12, "fluid"),
    ], "stack");

    private static PageDefinition Page(Dictionary<string, int>? spans) =>
        new(new TemplateRef("T03", "1"), new LayoutRef("L03", "1"), "standard", [], spans);

    [Fact]
    public void Rows_follow_the_renderer_and_only_declared_regions_resize()
    {
        var rows = RegionSpans.Rows(Layout);
        Assert.Equal([["aside", "main"], ["band"]], rows.Select(r => r.Select(x => x.Id).ToArray()).ToArray());
        Assert.True(RegionSpans.Resizable(Layout.Regions[0]));
        Assert.False(RegionSpans.Resizable(Layout.Regions[2]));
    }

    [Fact]
    public void Widths_inside_the_limits_that_keep_the_row_apply()
    {
        var page = Page(new() { ["aside"] = 3, ["main"] = 9 });
        Assert.Empty(RegionSpans.Findings(Layout, page));
        Assert.Equal([3, 9, 12], RegionSpans.Apply(Layout, page).Regions.Select(r => r.Span));
        Assert.Equal([4, 8, 12], RegionSpans.Apply(Layout, Page(null)).Regions.Select(r => r.Span));
    }

    [Fact]
    public void Widths_past_the_limits_overlapping_or_on_unknown_regions_are_refused_and_never_applied()
    {
        Assert.Contains(("region_span_invalid", "aside"), RegionSpans.Findings(Layout, Page(new() { ["aside"] = 6, ["main"] = 6 })));
        Assert.Contains(("region_span_overlap", "aside"), RegionSpans.Findings(Layout, Page(new() { ["aside"] = 5, ["main"] = 9 })));
        Assert.Contains(("region_span_overlap", "aside"), RegionSpans.Findings(Layout, Page(new() { ["aside"] = 3 })));
        Assert.Contains(("region_span_invalid", "band"), RegionSpans.Findings(Layout, Page(new() { ["band"] = 8 })));
        Assert.Contains(("region_unknown", "nope"), RegionSpans.Findings(Layout, Page(new() { ["nope"] = 4 })));
        Assert.Equal([4, 8, 12], RegionSpans.Apply(Layout, Page(new() { ["aside"] = 5, ["main"] = 9 })).Regions.Select(r => r.Span));
    }

    [Fact]
    public void Widths_merge_as_one_choice()
    {
        var @base = Page(null);
        var mine = Page(new() { ["aside"] = 3, ["main"] = 9 });
        Assert.Equal(mine.Spans, PageMerge.Merge(@base, mine, @base).Merged.Spans);
        Assert.Equal(mine.Spans, PageMerge.Merge(@base, @base, mine).Merged.Spans);
        Assert.Throws<TeamBeats.Primitives.AppException>(() => PageMerge.Merge(@base, mine, Page(new() { ["aside"] = 5, ["main"] = 7 })));
    }
}
