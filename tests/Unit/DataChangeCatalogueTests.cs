using TeamBeats.Modules.Hr.DataChanges;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 022 T022: the data change groups and the checks on each value (FR-019). Account numbers are fictional.</summary>
public sealed class DataChangeCatalogueTests
{
    private static Dictionary<string, string?> Values(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("GB82WEST12345698765432", "GB82WEST12345698765432")]
    [InlineData("gb82 west 1234 5698 7654 32", "GB82WEST12345698765432")]
    [InlineData("GB00WEST12345698765432", null)]
    [InlineData("GB82WEST1234", null)]
    [InlineData("not an iban", null)]
    [InlineData("", null)]
    public void An_IBAN_is_kept_only_with_right_check_digits(string input, string? expected) =>
        Assert.Equal(expected, DataChangeCatalogue.NormalizeIban(input));

    [Fact]
    public void Unknown_and_missing_fields_are_errors_and_optional_ones_may_be_left_out()
    {
        var address = DataChangeCatalogue.Find(DataChangeCatalogue.Address)!;
        var (values, errors) = DataChangeCatalogue.Check(address, Values(("address_line", " 1 Example Street "), ("city", "Cairo"), ("shoe_size", "9")));
        Assert.Equal(["shoe_size", "country"], errors);
        Assert.Equal("1 Example Street", values["address_line"]);
        Assert.DoesNotContain("postal_code", values.Keys);
    }

    [Fact]
    public void Phones_counts_and_choices_are_checked()
    {
        var contact = DataChangeCatalogue.Find(DataChangeCatalogue.EmergencyContact)!;
        Assert.Equal(["phone"], DataChangeCatalogue.Check(contact, Values(("contact_name", "Omar"), ("relationship", "Brother"), ("phone", "call me"))).Errors);
        Assert.Empty(DataChangeCatalogue.Check(contact, Values(("contact_name", "Omar"), ("relationship", "Brother"), ("phone", "+971 (50) 000-0000"))).Errors);

        var dependants = DataChangeCatalogue.Find(DataChangeCatalogue.Dependants)!;
        Assert.Equal("2", DataChangeCatalogue.Check(dependants, Values(("dependants_count", "02"))).Values["dependants_count"]);
        foreach (var bad in new[] { "21", "-1", "two", "1.5" })
        {
            Assert.Equal(["dependants_count"], DataChangeCatalogue.Check(dependants, Values(("dependants_count", bad))).Errors);
        }

        var marital = DataChangeCatalogue.Find(DataChangeCatalogue.MaritalStatus)!;
        Assert.Empty(DataChangeCatalogue.Check(marital, Values(("marital_status", "married"))).Errors);
        Assert.Equal(["marital_status"], DataChangeCatalogue.Check(marital, Values(("marital_status", "complicated"))).Errors);
    }

    [Fact]
    public void Control_characters_and_long_values_are_refused()
    {
        var address = DataChangeCatalogue.Find(DataChangeCatalogue.Address)!;
        var errors = DataChangeCatalogue.Check(address, Values(("address_line", "1 Example\u0007 Street"), ("city", new string('a', 500)), ("country", "Egypt"))).Errors;
        Assert.Equal(["address_line", "city"], errors);
    }

    [Fact]
    public void Bank_details_are_sensitive_and_need_a_document_by_default()
    {
        var bank = DataChangeCatalogue.Find(DataChangeCatalogue.Bank)!;
        Assert.True(bank.Sensitive);
        Assert.True(bank.DocumentByDefault);
        Assert.False(DataChangeCatalogue.Find(DataChangeCatalogue.EmergencyContact)!.Sensitive);
        Assert.Null(DataChangeCatalogue.Find("salary"));
    }
}
