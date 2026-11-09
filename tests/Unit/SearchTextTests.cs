using TeamBeats.Modules.Search;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 012 FR-005 and plan: Arabic and English normalization, query terms, safe query text, passages, rank fusion and snippets.</summary>
public sealed class SearchTextTests
{
    [Theory]
    [InlineData("إجازة", "اجازه")]
    [InlineData("أَجَازَة", "اجازه")]
    [InlineData("آمنة", "امنه")]
    [InlineData("مستشفى", "مستشفي")]
    [InlineData("مسؤول", "مسوول")]
    [InlineData("طـــلب", "طلب")]
    [InlineData("Café RÉSUMÉ", "cafe resume")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_folds_arabic_letter_forms_diacritics_accents_and_case(string? input, string expected) =>
        Assert.Equal(expected, SearchText.Normalize(input));

    [Fact]
    public void Terms_are_the_distinct_words_in_typed_order_and_at_most_twelve()
    {
        Assert.Equal(["annual", "leave", "2026"], SearchText.Terms("Annual-leave, ANNUAL leave 2026!"));
        Assert.Equal(["طلب", "اجازه"], SearchText.Terms("طلب إجازة"));
        Assert.Equal(SearchText.MaxTerms, SearchText.Terms(string.Join(' ', Enumerable.Range(0, 30).Select(i => "w" + i))).Count);
        Assert.Empty(SearchText.Terms("&|!()"));
    }

    [Fact]
    public void Arabic_is_detected_by_its_letters()
    {
        Assert.True(SearchText.IsArabic("سياسة السفر"));
        Assert.False(SearchText.IsArabic("Travel policy"));
        Assert.False(SearchText.IsArabic(null));
    }

    [Fact]
    public void Query_text_quotes_every_word_so_typed_operators_are_never_interpreted()
    {
        Assert.Equal("'annual' & 'leave'", SearchText.TsQuery([["annual"], ["leave"]], all: true));
        Assert.Equal("'annual' | 'leave'", SearchText.TsQuery([["annual"], ["leave"]], all: false));
        Assert.Equal("('leave' | 'vacation') & 'form'", SearchText.TsQuery([["leave", "vacation"], ["form"]], all: true));
        Assert.Equal("'annual' & 'lea':*", SearchText.TsQuery([["annual"], ["lea"]], all: true, prefix: true));
        Assert.Equal("'o''brien' & 'a\\\\b'", SearchText.TsQuery([["o'brien"], ["a\\b"]], all: true));
        Assert.Equal(string.Empty, SearchText.TsQuery([[""]], all: true));
    }

    [Fact]
    public void Short_text_is_one_passage_and_empty_text_none()
    {
        Assert.Equal(["Annual leave policy."], SearchText.Passages("  Annual   leave\npolicy. "));
        Assert.Empty(SearchText.Passages(null));
        Assert.Empty(SearchText.Passages("   "));
    }

    [Fact]
    public void Long_text_is_cut_at_spaces_into_overlapping_passages()
    {
        var words = Enumerable.Range(0, 1000).Select(i => $"word{i:D4}").ToArray();
        var text = string.Join(' ', words);
        var passages = SearchText.Passages(text, size: 400, overlap: 60);
        Assert.True(passages.Count > 1);
        Assert.All(passages, p => Assert.True(p.Length <= 400, $"{p.Length} characters"));
        // Every passage starts and ends on whole words, the first word starts the text and the last ends it.
        Assert.All(passages, p => Assert.All(p.Split(' '), w => Assert.Contains(w, words)));
        Assert.StartsWith("word0000", passages[0]);
        Assert.EndsWith("word0999", passages[^1]);
        // Neighbours share words.
        for (var i = 1; i < passages.Count; i++)
        {
            Assert.Contains(passages[i].Split(' ')[0], passages[i - 1].Split(' '));
        }
        Assert.Equal(3, SearchText.Passages(text, size: 400, overlap: 60, max: 3).Count);
    }

    [Fact]
    public void Rank_fusion_rewards_items_high_in_both_lists()
    {
        var scores = SearchText.Fuse<string>([["a", "b", "c"], ["c", "a", "d"]]);
        Assert.Equal(1.0 / 61 + 1.0 / 62, scores["a"], 10);
        Assert.Equal(1.0 / 63 + 1.0 / 61, scores["c"], 10);
        Assert.Equal(1.0 / 62, scores["b"], 10);
        Assert.Equal(["a", "c", "b", "d"], scores.OrderByDescending(s => s.Value).Select(s => s.Key).ToArray());
        Assert.Empty(SearchText.Fuse<string>([[], []]));
    }

    [Fact]
    public void Snippet_shows_the_words_around_the_first_match()
    {
        var text = string.Join(' ', Enumerable.Range(0, 100).Select(i => i == 60 ? "Overtime" : "filler" + i));
        var snippet = SearchText.Snippet(text, ["overtime"], words: 10);
        Assert.Contains("Overtime", snippet);
        Assert.StartsWith("… ", snippet);
        Assert.EndsWith(" …", snippet);
        Assert.Equal("one two three", SearchText.Snippet("one two three", ["two"]));
        Assert.StartsWith("filler0 ", SearchText.Snippet(text, ["absent"], words: 10));
        Assert.Contains("الإجازة", SearchText.Snippet(string.Join(' ', Enumerable.Repeat("نص", 50)) + " الإجازة السنوية", ["الاجازه"], words: 10));
    }

    [Fact]
    public void Query_keys_and_languages_are_read_as_the_search_box_reads_them()
    {
        Assert.Equal("payslip", SearchText.QueryKey("  PAYSLIP! "));
        Assert.Equal("annual leave", SearchText.QueryKey("Annual, leave"));
        Assert.Equal("اجازه", SearchText.QueryKey("إجازة"));
        Assert.Equal(string.Empty, SearchText.QueryKey("!!"));
        Assert.Equal("ar", SearchText.QueryLanguage("طلب إجازة"));
        Assert.Equal("en", SearchText.QueryLanguage("leave request"));
    }

    [Fact]
    public void Synonyms_expand_each_term_into_a_group_with_the_typed_word_first()
    {
        IReadOnlyCollection<string>[] sets = [["leave", "vacation", "اجازه"], ["form", "request"]];
        var groups = SearchText.Expand(["leave", "form", "2026"], sets);
        Assert.Equal(["leave", "vacation", "اجازه"], groups[0]);
        Assert.Equal(["form", "request"], groups[1]);
        Assert.Equal(["2026"], groups[2]);
        Assert.Equal("('leave' | 'vacation' | 'اجازه') & ('form' | 'request') & '2026'", SearchText.TsQuery(groups, all: true));
        Assert.Equal(["vacation"], SearchText.Expand(["vacation"], [])[0]);
    }
}
