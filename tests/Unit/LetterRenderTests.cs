using TeamBeats.Modules.Hr.Letters;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 T021: letter placeholders, filling, Arabic digits, the printed page and verification codes (FR-021, FR-022).</summary>
public sealed class LetterRenderTests
{
    [Fact]
    public void Placeholders_are_found_once_each_and_unknown_ones_are_left_as_written()
    {
        var template = "Dear {addressee}, {name} ({name}) earns {salary}. {Not_one} {}";
        Assert.Equal(["addressee", "name", "salary"], LetterRender.Placeholders(template));
        Assert.Equal("Dear Bank, Mona (Mona) earns {salary}. {Not_one} {}",
            LetterRender.Fill(template, new Dictionary<string, string> { ["addressee"] = "Bank", ["name"] = "Mona" }));
    }

    [Fact]
    public void A_filled_value_is_never_filled_again()
    {
        Assert.Equal("{salary}", LetterRender.Fill("{name}", new Dictionary<string, string> { ["name"] = "{salary}", ["salary"] = "1" }));
    }

    [Fact]
    public void Arabic_dates_use_Arabic_Indic_digits_only_when_the_tenant_does()
    {
        var date = new DateOnly(2027, 2, 8);
        Assert.Equal("8 February 2027", LetterRender.FormatDate(date, "en", true));
        Assert.DoesNotContain("8", LetterRender.FormatDate(date, "ar", true), StringComparison.Ordinal);
        Assert.Contains("٢٠٢٧", LetterRender.FormatDate(date, "ar", true), StringComparison.Ordinal);
        Assert.Contains("2027", LetterRender.FormatDate(date, "ar", false), StringComparison.Ordinal);
        Assert.Equal("رقم ١٢٣-LT", LetterRender.ArabicDigits("رقم 123-LT"));
    }

    [Fact]
    public void The_page_encodes_everything_runs_right_to_left_in_Arabic_and_loads_nothing()
    {
        var html = LetterRender.Html("ar", "<b>Co</b>", "Line one <script>x</script>\nline two\n\nSecond paragraph", [("Reference", "LT-000001")]);
        Assert.Contains("dir=\"rtl\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;b&gt;Co&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Contains("<p>Line one &lt;script&gt;x&lt;/script&gt;<br>line two</p><p>Second paragraph</p>", html, StringComparison.Ordinal);
        Assert.Contains("<bdi dir=\"ltr\">LT-000001</bdi>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("http", html, StringComparison.Ordinal);
        Assert.DoesNotContain("src=", html, StringComparison.Ordinal);
        Assert.Contains("dir=\"ltr\"", LetterRender.Html("en", "Co", "x", []), StringComparison.Ordinal);
    }

    [Fact]
    public void Codes_have_three_groups_of_four_without_look_alikes_and_normalize_as_typed()
    {
        var codes = Enumerable.Range(0, 200).Select(_ => LetterRender.NewCode()).ToList();
        Assert.All(codes, c => Assert.Matches("^[A-HJKMNP-TV-Z2-9]{4}-[A-HJKMNP-TV-Z2-9]{4}-[A-HJKMNP-TV-Z2-9]{4}$", c));
        Assert.Equal(200, codes.Distinct().Count());
        Assert.Equal("ABCD2345WXYZ", LetterRender.NormalizeCode(" abcd-2345 wxyz "));
        Assert.Null(LetterRender.NormalizeCode("ABCD-2345-WXY0"));
        Assert.Null(LetterRender.NormalizeCode("ABCD-2345"));
        Assert.Null(LetterRender.NormalizeCode(null));
        Assert.Equal(LetterRender.CodeHash("ABCD2345WXYZ"), LetterRender.CodeHash(LetterRender.NormalizeCode("abcd-2345-wxyz")!));
    }
}
