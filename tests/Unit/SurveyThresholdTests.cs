using TeamBeats.Modules.Engagement;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 023 FR-009: which survey groups show. A group under the threshold is hidden, and so is the next smallest while
/// the hidden ones could be worked out from the total. Anonymous results show nothing before close.
/// </summary>
public sealed class SurveyThresholdTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-00000000000c");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-00000000000d");

    private static HashSet<Guid> Visible(Dictionary<Guid, int> counts, int threshold = 5, bool anonymous = true, bool closed = true) =>
        Surveys.VisibleGroups(counts, threshold, anonymous, closed);

    [Fact]
    public void Every_group_at_or_over_the_threshold_shows_when_none_is_hidden() =>
        Assert.Equal([A, B], Visible(new() { [A] = 5, [B] = 9 }).Order());

    [Fact]
    public void One_hidden_group_takes_the_next_smallest_with_it()
    {
        var visible = Visible(new() { [A] = 2, [B] = 6, [C] = 9 });
        Assert.Equal([C], visible.ToList());
    }

    [Fact]
    public void Hidden_groups_together_under_the_threshold_take_more_with_them()
    {
        // A and B are hidden with 2 between them, under 5, so C (the smallest shown) hides too; then 8 hidden, enough.
        var visible = Visible(new() { [A] = 1, [B] = 1, [C] = 6, [D] = 7 });
        Assert.Equal([D], visible.ToList());
    }

    [Fact]
    public void Two_hidden_groups_that_together_reach_the_threshold_leave_the_rest_alone() =>
        Assert.Equal([C, D], Visible(new() { [A] = 3, [B] = 4, [C] = 6, [D] = 7 }).Order());

    [Fact]
    public void Nothing_shows_before_close_or_under_the_threshold_overall()
    {
        Assert.Empty(Visible(new() { [A] = 10 }, closed: false));
        Assert.Empty(Visible(new() { [A] = 2, [B] = 2 }));
    }

    [Fact]
    public void Named_surveys_show_every_group() =>
        Assert.Equal([A, B], Visible(new() { [A] = 1, [B] = 1 }, anonymous: false, closed: false).Order());
}
