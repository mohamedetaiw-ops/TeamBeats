using System.Text.Json;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 032 R2 T026 (US6 scenario 6): the three-way merge of page drafts keeps both editors' changes.</summary>
public sealed class PageMergeTests
{
    private static Placement P(string id, string text = "x", int? span = null) =>
        new(id, "reference.alpha.text@1", span, JsonSerializer.SerializeToElement(new { body = new { en = text } }));

    private static PageDefinition Page(string variant = "standard", params (string Region, Placement[] Placements)[] regions) =>
        new(new TemplateRef("T01", "1"), new LayoutRef("L01", "1"), variant, regions.Select(r => new PageRegion(r.Region, r.Placements)).ToList());

    private static string[] Ids(PageDefinition page, string region) => page.Regions.Single(r => r.Id == region).Placements.Select(p => p.Id).ToArray();

    [Fact]
    public void Changes_on_different_placements_are_both_kept_in_order()
    {
        var @base = Page("standard", ("main", [P("a"), P("b")]), ("side", [P("s")]));
        var mine = Page("standard", ("main", [P("a", "mine"), P("b"), P("c")]), ("side", [P("s")]));
        var theirs = Page("standard", ("main", [P("a"), P("b", span: 6)]), ("side", []));
        var (merged, conflicts) = PageMerge.Merge(@base, mine, theirs);
        Assert.Empty(conflicts);
        Assert.Equal(["a", "b", "c"], Ids(merged, "main"));
        Assert.Equal("mine", merged.Regions[0].Placements[0].Props!.Value.GetProperty("body").GetProperty("en").GetString());
        Assert.Equal(6, merged.Regions[0].Placements[1].Span);
        Assert.Empty(Ids(merged, "side"));
    }

    [Fact]
    public void A_placement_both_changed_keeps_theirs_and_one_they_removed_comes_back()
    {
        var @base = Page("standard", ("main", [P("a"), P("b"), P("c")]));
        var mine = Page("standard", ("main", [P("a", "mine"), P("b", "mine too")]));
        var theirs = Page("standard", ("main", [P("a", "theirs"), P("c", "theirs")]));
        var (merged, conflicts) = PageMerge.Merge(@base, mine, theirs);
        Assert.Equal(["a", "b", "c"], Ids(merged, "main"));
        Assert.Equal("theirs", merged.Regions[0].Placements[0].Props!.Value.GetProperty("body").GetProperty("en").GetString());
        Assert.Contains(new MergeConflict("a", MergeConflictKinds.KeptTheirs), conflicts);
        Assert.Contains(new MergeConflict("b", MergeConflictKinds.Restored), conflicts);
        Assert.Contains(new MergeConflict("c", MergeConflictKinds.RemovedChanged), conflicts);
    }

    [Fact]
    public void A_move_to_another_region_and_the_same_change_twice_merge_cleanly()
    {
        var @base = Page("standard", ("main", [P("a"), P("b")]), ("side", []));
        var mine = Page("wide", ("main", [P("b")]), ("side", [P("a")]));
        var theirs = Page("standard", ("main", [P("a"), P("b"), P("n")]), ("side", []));
        var (merged, conflicts) = PageMerge.Merge(@base, mine, theirs);
        Assert.Empty(conflicts);
        Assert.Equal("wide", merged.Variant);
        Assert.Equal(["b", "n"], Ids(merged, "main"));
        Assert.Equal(["a"], Ids(merged, "side"));
        Assert.Empty(PageMerge.Merge(@base, theirs, theirs).Conflicts);
    }

    [Fact]
    public void Both_changing_the_look_differently_cannot_merge()
    {
        var @base = Page("standard", ("main", [P("a")]));
        var ex = Assert.Throws<AppException>(() => PageMerge.Merge(@base, Page("wide", ("main", [P("a")])), Page("compact", ("main", [P("a")]))));
        Assert.Equal("conflict.page.merge_structure", ex.DetailKey);
    }
}
