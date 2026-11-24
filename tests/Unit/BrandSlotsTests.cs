using TeamBeats.Modules.Experience.Themes;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 033 R2 T018: the brand's slots per language and the fallback to every language.</summary>
public sealed class BrandSlotsTests
{
    [Theory]
    [InlineData("logo", true)]
    [InlineData("logo.ar", true)]
    [InlineData("lockup.ur", true)]
    [InlineData("sign_in_image.fr", true)]
    [InlineData("favicon", true)]
    [InlineData("favicon.ar", false)]
    [InlineData("logo.xx", false)]
    [InlineData("logo.", false)]
    [InlineData(".ar", false)]
    [InlineData("title", false)]
    public void Image_slots_take_a_supported_language_except_the_browser_icon(string slot, bool image) =>
        Assert.Equal(image, BrandSlots.IsImage(slot));

    [Theory]
    [InlineData("title", true)]
    [InlineData("title.ar", true)]
    [InlineData("title.xx", false)]
    [InlineData("logo", false)]
    public void Title_slots_are_the_title_and_its_languages(string slot, bool title) => Assert.Equal(title, BrandSlots.IsTitle(slot));

    [Fact]
    public void A_language_takes_its_own_value_else_the_one_for_every_language()
    {
        var assets = new Dictionary<string, string> { ["title"] = "Acme", ["title.ar"] = "أكمي", ["logo.ar"] = "tenant:logo" };
        Assert.Equal("أكمي", BrandSlots.Pick(assets, "title", "ar"));
        Assert.Equal("Acme", BrandSlots.Pick(assets, "title", "en"));
        Assert.Null(BrandSlots.Pick(assets, "logo", "en"));
        var arabic = BrandSlots.Present(assets, "ar");
        Assert.Equal("/api/v1/tenant/branding/logo", arabic.LogoUrl);
        Assert.Null(arabic.LockupUrl);
    }

    [Theory]
    [InlineData("Acme Gulf", true)]
    [InlineData("  ", false)]
    [InlineData("Tab\there", false)]
    public void Titles_are_short_single_line_text(string title, bool valid) => Assert.Equal(valid, BrandSlots.IsValidTitle(title));

    [Fact]
    public void Titles_keep_to_sixty_characters()
    {
        Assert.True(BrandSlots.IsValidTitle(new string('a', 60)));
        Assert.False(BrandSlots.IsValidTitle(new string('a', 61)));
    }
}
