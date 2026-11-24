using TeamBeats.Modules.Integrations.Sms;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 015 US6 scenario 2: directory phone numbers as people type them become E.164 numbers, or nothing.</summary>
public sealed class PhoneNumberTests
{
    [Theory]
    [InlineData("+971 50 000 0001", "+971500000001")]
    [InlineData("+971-50-000-0001", "+971500000001")]
    [InlineData("00971 (50) 000 0001", "+971500000001")]
    [InlineData(" +20 100 000 0000 ", "+201000000000")]
    public void Typed_numbers_in_international_form_become_E164(string typed, string expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("050 000 0001")]
    [InlineData("+0 50 000 0001")]
    [InlineData("+12345")]
    [InlineData("+1234567890123456")]
    [InlineData("00")]
    public void Local_short_or_malformed_numbers_are_refused(string? typed) => Assert.Null(PhoneNumbers.Normalize(typed));

    [Fact]
    public void E164_needs_a_plus_and_8_to_15_digits()
    {
        Assert.True(PhoneNumbers.IsE164("+97150000"));
        Assert.True(PhoneNumbers.IsE164("+123456789012345"));
        Assert.False(PhoneNumbers.IsE164("+9715000"));
        Assert.False(PhoneNumbers.IsE164("971500000001"));
        Assert.False(PhoneNumbers.IsE164("+97150000a001"));
    }
}
