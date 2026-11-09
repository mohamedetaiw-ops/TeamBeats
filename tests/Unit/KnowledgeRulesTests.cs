using TeamBeats.Modules.Content;
using TeamBeats.Modules.Documents;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 011 US4: section anchors for citation links and the template sections.</summary>
public sealed class KnowledgeRulesTests
{
    private static Block Heading(string text, int level = 2) => new(BlockTypes.Heading, [new Inline(text)], Level: level);

    private static Block Para(string text) => new(BlockTypes.Paragraph, [new Inline(text)]);

    [Theory]
    [InlineData("How do I reset my password?", "how-do-i-reset-my-password")]
    [InlineData("  Step 2: Check   the brakes ", "step-2-check-the-brakes")]
    [InlineData("كيف أعيد تعيين كلمة المرور؟", "كيف-أعيد-تعيين-كلمة-المرور")]
    [InlineData("???", "section")]
    [InlineData("", "section")]
    public void Anchors_are_the_heading_words_in_lower_case_joined_by_hyphens(string heading, string anchor) =>
        Assert.Equal(anchor, BodyValidator.Anchor(heading));

    [Fact]
    public void Long_headings_give_anchors_of_at_most_sixty_characters_without_a_trailing_hyphen()
    {
        var anchor = BodyValidator.Anchor(string.Join(' ', Enumerable.Repeat("abcdefghi", 10)));
        Assert.True(anchor.Length <= 60);
        Assert.False(anchor.EndsWith('-'));
    }

    [Fact]
    public void Sections_carry_the_text_up_to_the_next_heading_and_number_repeated_anchors()
    {
        var body = new BodyDocument([Para("Intro before any heading."), Heading("Steps"), Para("One."), Para("Two."), Heading("Notes", 3), Heading("Steps"), Para("Again.")]);
        var sections = BodyValidator.Sections(body);
        Assert.Equal(["steps", "notes", "steps-2"], sections.Select(s => s.Anchor).ToArray());
        Assert.Equal(3, sections[1].Level);
        Assert.Equal("", sections[1].Text);
        Assert.Contains("One.", sections[0].Text);
        Assert.Contains("Two.", sections[0].Text);
        Assert.DoesNotContain("Intro", sections[0].Text);
        Assert.Equal("Again.", sections[2].Text);
    }

    [Fact]
    public void The_SOP_template_has_the_five_sections_of_the_spec_and_the_FAQ_starts_empty()
    {
        Assert.Equal(["purpose", "scope", "responsibilities", "steps", "related_documents"], KnowledgeTemplates.Sections[KnowledgeTemplates.Sop]);
        Assert.Empty(KnowledgeTemplates.Sections[KnowledgeTemplates.Faq]);
        Assert.Equal(["sop", "howto", "faq", "troubleshooting"], KnowledgeTemplates.Sections.Keys);
    }
}
