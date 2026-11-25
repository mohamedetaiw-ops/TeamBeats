using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Integrations.Hr;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 019 section 2 with spec 022 FR-016: the personal email HR maps is checked like the work email and kept only for pre-boarding.</summary>
public sealed class HrMappingTests
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Statuses = new()
    {
        [HrEmployeeStatuses.Active] = ["Active"],
        [HrEmployeeStatuses.Terminated] = ["Left"],
    };

    private static (HrEmployee? Employee, HrProblem? Problem) Map(string? personal) =>
        HrMapping.Map(new Dictionary<string, string?> { [HrFields.RecordId] = "R1", [HrFields.Status] = "Active", [HrFields.PersonalEmail] = personal }, Statuses, null);

    [Fact]
    public void A_personal_email_is_trimmed_and_lowercased()
    {
        var (employee, problem) = Map("  Joiner.Home@Example.ORG ");
        Assert.Null(problem);
        Assert.Equal("joiner.home@example.org", employee!.PersonalEmail);
        Assert.Equal("joiner.home@example.org", HrSync.ToFields(employee)[HrFields.PersonalEmail]);
    }

    [Theory]
    [InlineData("no-at-sign")]
    [InlineData("two@@example.org")]
    [InlineData("@example.org")]
    [InlineData("has space@example.org")]
    public void A_malformed_personal_email_is_a_bad_email(string value) => Assert.Equal(HrProblemReasons.BadEmail, Map(value).Problem!.Reason);

    [Fact]
    public void A_personal_email_longer_than_an_address_can_be_is_too_long() =>
        Assert.Equal(HrProblemReasons.TooLong, Map(new string('a', 250) + "@example.org").Problem!.Reason);

    [Fact]
    public void No_personal_email_is_fine_and_other_modules_never_see_one()
    {
        Assert.Null(Map(null).Employee!.PersonalEmail);
        var visible = HrSync.Visible(new Dictionary<string, string> { [HrFields.RecordId] = "R1", [HrFields.PersonalEmail] = "x@example.org" });
        Assert.False(visible.ContainsKey(HrFields.PersonalEmail));
        Assert.Contains(HrFields.PersonalEmail, HrFields.All);
    }

    /// <summary>Spec 019 R3 US8 scenario 2: SuccessFactors OData dates, with or without an offset; anything else in that shape is a bad date.</summary>
    [Theory]
    [InlineData("/Date(1709251200000)/", "2024-03-01")]
    [InlineData("/Date(1709251200000+0000)/", "2024-03-01")]
    [InlineData("/Date(-86400000)/", "1969-12-31")]
    [InlineData("/Date(abc)/", null)]
    [InlineData("/Date(99999999999999999)/", null)]
    public void OData_dates_are_read_as_dates(string hire, string? expected)
    {
        var (employee, problem) = HrMapping.Map(new Dictionary<string, string?> { [HrFields.RecordId] = "R1", [HrFields.Status] = "Active", [HrFields.HireDate] = hire }, Statuses, null);
        if (expected is null)
        {
            Assert.Equal(HrProblemReasons.BadDate, problem!.Reason);
        }
        else
        {
            Assert.Equal(DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), employee!.HireDate);
        }
    }
}
