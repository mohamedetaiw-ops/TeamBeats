using TeamBeats.Modules.People;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 010 T006: search normalization, transliteration skeletons, phone digits, location keys and cycle detection.</summary>
public sealed class PeopleSearchTextTests
{
    [Theory]
    [InlineData("مُحَمَّد", "محمد")]
    [InlineData("أحمد", "احمد")]
    [InlineData("إبراهيم", "ابراهيم")]
    [InlineData("سارة", "ساره")]
    [InlineData("مصطفى", "مصطفي")]
    [InlineData("عـــلي", "علي")]
    [InlineData("  José   García ", "jose garcia")]
    [InlineData("Al-Mansouri", "al mansouri")]
    public void Normalize_folds_letter_variants_diacritics_case_and_spacing(string input, string expected) =>
        Assert.Equal(expected, SearchText.Normalize(input));

    [Theory]
    [InlineData("mohamed")]
    [InlineData("mohammed")]
    [InlineData("muhammad")]
    [InlineData("mohammad")]
    public void Common_spellings_of_one_name_share_a_skeleton(string spelling) =>
        Assert.Equal("mhmd", SearchText.Skeleton(spelling));

    [Fact]
    public void Skeletons_cover_each_Latin_word_and_skip_other_scripts()
    {
        Assert.Equal(" ysf ahmd ", SearchText.Skeletons("Youssef Ahmed"));
        Assert.Equal(" ysf ", SearchText.Skeletons("Yusuf يوسف"));
        Assert.Equal(string.Empty, SearchText.Skeletons("محمد"));
        Assert.NotEqual(SearchText.Skeleton("mona"), SearchText.Skeleton("mohamed"));
    }

    [Theory]
    [InlineData("+971 4 555 7321", "97145557321")]
    [InlineData("٠٥٠ ١٢٣", "050123")]
    [InlineData("ext. 42", "42")]
    [InlineData(null, "")]
    public void Digits_keep_only_numbers_in_any_script(string? input, string expected) => Assert.Equal(expected, SearchText.Digits(input));

    [Theory]
    [InlineData("Dubai HQ", "dubai hq")]
    [InlineData("  dubai   hq ", "dubai hq")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Location_keys_ignore_case_and_spacing(string? input, string? expected) => Assert.Equal(expected, SearchText.LocationKey(input));

    [Fact]
    public void Cycles_are_found_and_people_outside_them_are_not()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid(), e = Guid.NewGuid();
        var proposed = new Dictionary<Guid, Guid?> { [a] = b, [b] = c, [c] = a, [d] = a, [e] = null };
        var cycles = ReportingLine.FindCycles(proposed);
        Assert.Equal(new HashSet<Guid> { a, b, c }, cycles);
        Assert.Empty(ReportingLine.FindCycles(new Dictionary<Guid, Guid?> { [a] = b, [b] = null, [d] = a }));
    }

    [Fact]
    public void Field_defaults_follow_section_6()
    {
        var defaults = ProfileFields.Defaults().ToDictionary(p => p.Key);
        Assert.Equal(FieldVisibility.Everyone, defaults[ProfileFields.WorkPhone].Visibility);
        Assert.Equal(FieldVisibility.ManagersAndHr, defaults[ProfileFields.MobilePhone].Visibility);
        Assert.Equal(FieldVisibility.ManagersAndHr, defaults[ProfileFields.HireDate].Visibility);
        Assert.Equal(FieldVisibility.Hr, defaults[ProfileFields.EmployeeNumber].Visibility);
        // Spec 019: grade, cost centre and employment type come from HR; the rest from the directory until HR maps them.
        string[] fromHr = [ProfileFields.Grade, ProfileFields.CostCentre, ProfileFields.EmploymentType];
        Assert.All(fromHr, k => Assert.Equal((FieldSources.Hr, FieldVisibility.Hr), (defaults[k].Source, defaults[k].Visibility)));
        Assert.All(defaults.Values.Where(p => p.Key != ProfileFields.TimeZone && !fromHr.Contains(p.Key)), p => Assert.Equal(FieldSources.Directory, p.Source));
    }
}
