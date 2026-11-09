using TeamBeats.Modules.Content;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 009 slice 1: the body allowlist, audience matching and validation, out of date variants and the reader's locale.</summary>
public sealed class ContentRulesTests
{
    private static string[] Keys(ValidationErrors errors)
    {
        try
        {
            errors.ThrowIfAny();
            return [];
        }
        catch (AppException e)
        {
            return e.Errors!.SelectMany(kv => kv.Value).ToArray();
        }
    }

    private static readonly IReadOnlySet<string> Hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "youtube.com" };

    private static string[] Errors(BodyDocument body)
    {
        var errors = new ValidationErrors();
        BodyValidator.Validate(body, Hosts, "body", errors);
        return Keys(errors);
    }

    private static Block Para(string text, string? link = null, params string[] marks) => new(BlockTypes.Paragraph, [new Inline(text, marks.Length == 0 ? null : marks, link)]);

    [Fact]
    public void Allowed_blocks_and_marks_pass()
    {
        var body = new BodyDocument([
            new Block(BlockTypes.Heading, [new Inline("Title")], Level: 2),
            Para("Bold and italic", null, "bold", "italic"),
            new Block(BlockTypes.List, Ordered: true, Items: [[new Inline("One")], [new Inline("Two")]]),
            new Block(BlockTypes.Table, Rows: [[[new Inline("A")], []]]),
            new Block(BlockTypes.Image, AssetId: Guid.NewGuid(), Alt: "Team photo"),
            new Block(BlockTypes.Link, Url: "/news/1", Text: "Read more"),
            new Block(BlockTypes.Embed, Url: "https://www.youtube.com/embed/x"),
        ]);
        Assert.Empty(Errors(body));
        Assert.Contains("Team photo", BodyValidator.PlainText(body));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,hi")]
    [InlineData("//evil.example/x")]
    [InlineData("/\\evil")]
    [InlineData("vbscript:x")]
    public void Unsafe_links_are_refused(string link) => Assert.Equal(["validation.content.body_link"], Errors(new BodyDocument([Para("x", link)])));

    [Theory]
    [InlineData("https://intranet.example/a")]
    [InlineData("mailto:hr@example.com")]
    [InlineData("/people/42")]
    public void Safe_links_pass(string link) => Assert.Empty(Errors(new BodyDocument([Para("x", link)])));

    [Theory]
    [InlineData("http://youtube.com/embed/x")]
    [InlineData("https://notyoutube.com/x")]
    [InlineData("https://user@youtube.com/x")]
    [InlineData("https://youtube.com.evil.example/x")]
    public void Embeds_off_the_allowlist_are_refused(string url) =>
        Assert.Equal(["validation.content.body_embed"], Errors(new BodyDocument([new Block(BlockTypes.Embed, Url: url)])));

    [Fact]
    public void Unknown_blocks_marks_and_stray_fields_are_refused()
    {
        Assert.Equal(["validation.content.body_block"], Errors(new BodyDocument([new Block("script", Text: "x")])));
        Assert.Equal(["validation.content.body_block"], Errors(new BodyDocument([Para("x", null, "underline")])));
        Assert.Equal(["validation.content.body_block"], Errors(new BodyDocument([new Block(BlockTypes.Heading, [new Inline("x")], Level: 1)])));
        Assert.Equal(["validation.content.body_block"], Errors(new BodyDocument([new Block(BlockTypes.Paragraph, [new Inline("x")], Url: "https://a.example")])));
        Assert.Equal(["validation.content.body_block"], Errors(new BodyDocument([new Block(BlockTypes.Image)])));
    }

    [Fact]
    public void Bodies_over_the_size_limits_are_refused()
    {
        Assert.Equal(["validation.content.body_too_large"], Errors(new BodyDocument(Enumerable.Range(0, BodyValidator.MaxBlocks + 1).Select(_ => Para("x")).ToList())));
        Assert.Equal(["validation.content.body_too_large"], Errors(new BodyDocument([Para(new string('a', BodyValidator.MaxCharacters + 1))])));
    }

    private static AudienceFacts Facts(Guid unit, Guid parent, string? location = "Dubai", string? type = "employee") =>
        new(new HashSet<Guid> { unit }, new HashSet<Guid> { unit, parent }, location, new HashSet<Guid>(), new HashSet<string> { "employee" }, type);

    [Fact]
    public void Audiences_match_any_clause_and_every_criterion_in_a_clause()
    {
        var dubai = Guid.NewGuid();
        var gulf = Guid.NewGuid();
        var me = Facts(dubai, gulf);
        Assert.True(AudienceRules.Matches(ContentAudience.Everyone, me));
        Assert.True(AudienceRules.Matches(new ContentAudience([new AudienceClause(OrgUnits: [new UnitTarget(gulf)])]), me));
        Assert.False(AudienceRules.Matches(new ContentAudience([new AudienceClause(OrgUnits: [new UnitTarget(gulf, Subtree: false)])]), me));
        Assert.False(AudienceRules.Matches(new ContentAudience([new AudienceClause(OrgUnits: [new UnitTarget(gulf)], Locations: ["Cairo"])]), me));
        Assert.True(AudienceRules.Matches(new ContentAudience([
            new AudienceClause(Locations: ["Cairo"]),
            new AudienceClause(Locations: ["dubai"], MembershipTypes: ["employee"]),
        ]), me));
        Assert.False(AudienceRules.Matches(new ContentAudience([new AudienceClause(MembershipTypes: ["contractor"])]), me));
        Assert.False(AudienceRules.Matches(new ContentAudience([new AudienceClause(Locations: ["Dubai"])]), Facts(dubai, gulf, location: null)));
    }

    private static string[] AudienceErrors(ContentAudience audience)
    {
        var errors = new ValidationErrors();
        AudienceRules.Validate(audience, "audience", errors);
        return Keys(errors);
    }

    [Fact]
    public void Audience_validation_refuses_empty_unsupported_and_bad_values()
    {
        Assert.Empty(AudienceErrors(new ContentAudience([new AudienceClause(Roles: ["employee"])])));
        Assert.Equal(["validation.content.audience_empty_clause"], AudienceErrors(new ContentAudience([new AudienceClause()])));
        Assert.Equal(["validation.content.audience_not_supported"], AudienceErrors(new ContentAudience([new AudienceClause(Channels: ["teams"])])));
        Assert.Equal(["validation.content.audience_value"], AudienceErrors(new ContentAudience([new AudienceClause(MembershipTypes: ["robot"])])));
        Assert.Equal(["validation.content.audience_value"], AudienceErrors(new ContentAudience([new AudienceClause(Groups: [Guid.Empty])])));
        Assert.Equal(["validation.content.audience_too_large"],
            AudienceErrors(new ContentAudience(Enumerable.Range(0, AudienceRules.MaxClauses + 1).Select(_ => new AudienceClause(Roles: ["employee"])).ToList())));
    }

    private static ContentVariant V(string title, bool outOfDate = false) => new(title, null, BodyDocument.Empty, outOfDate);

    [Fact]
    public void A_variant_falls_behind_when_the_primary_changes_and_catches_up_when_edited()
    {
        var before = new Dictionary<string, ContentVariant> { ["en"] = V("Hello"), ["ar"] = V("مرحبا") };
        var primaryEdited = Items.MarkOutOfDate(new() { ["en"] = V("Hello all"), ["ar"] = V("مرحبا") }, "en", before);
        Assert.True(primaryEdited["ar"].OutOfDate);
        Assert.False(primaryEdited["en"].OutOfDate);

        var untouched = Items.MarkOutOfDate(new() { ["en"] = V("Hello all"), ["ar"] = V("مرحبا") }, "en", primaryEdited);
        Assert.True(untouched["ar"].OutOfDate);

        var translated = Items.MarkOutOfDate(new() { ["en"] = V("Hello all"), ["ar"] = V("مرحبا بالجميع") }, "en", untouched);
        Assert.False(translated["ar"].OutOfDate);

        var both = Items.MarkOutOfDate(new() { ["en"] = V("Hi"), ["ar"] = V("أهلا") }, "en", before);
        Assert.False(both["ar"].OutOfDate);
    }

    [Fact]
    public void The_reader_gets_their_locale_then_the_tenant_default_then_the_primary()
    {
        var variants = new Dictionary<string, ContentVariant> { ["fr"] = V("Bonjour"), ["ar"] = V("مرحبا") };
        Assert.Equal("ar", Items.Pick(variants, "ar", "en", "fr").Locale);
        Assert.Equal("fr", Items.Pick(variants, "en", "en", "fr").Locale);
        Assert.Equal("ar", Items.Pick(variants, "en", "ar", "fr").Locale);
        Assert.Equal("ar", Items.Pick(variants, "hi", "en", "ur").Locale);
    }
}
