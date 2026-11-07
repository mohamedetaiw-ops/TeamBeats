using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Security;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 026 T012: tenant alert thresholds and the working hours used for out-of-hours privilege grants.</summary>
public sealed class AlertSettingsTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");

    // Sunday to Thursday, with 2026-11-12 (Thursday) a holiday.
    private static readonly WorkingCalendarSnapshot Calendar = new(Guid.NewGuid(), 1,
        [DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday],
        [new HolidayDay(new DateOnly(2026, 11, 12), false)], Dubai);

    [Theory]
    [InlineData("2026-11-09T05:00:00Z", false)] // Monday 09:00 in Dubai
    [InlineData("2026-11-09T03:59:00Z", true)] // Monday 07:59
    [InlineData("2026-11-09T14:00:00Z", true)] // Monday 18:00, the end is exclusive
    [InlineData("2026-11-06T06:00:00Z", true)] // Friday 10:00, not a working day
    [InlineData("2026-11-12T06:00:00Z", true)] // Thursday 10:00, a holiday
    public void Default_hours_are_eight_to_six_on_working_days_in_the_tenant_zone(string at, bool outOfHours) =>
        Assert.Equal(outOfHours, new AlertSettings().OutOfHours(Calendar, DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Stored_settings_override_the_defaults_and_missing_ones_keep_them()
    {
        var settings = AlertSettings.From("""{"downloadFiles":50,"workStart":"07:00"}""");
        Assert.Equal(50, settings.DownloadFiles);
        Assert.Equal(2L * 1024 * 1024 * 1024, settings.DownloadBytes);
        Assert.Equal("07:00", settings.WorkStart);
        Assert.Equal("18:00", settings.WorkEnd);
        Assert.Equal(new AlertSettings(), AlertSettings.From("{}"));
        Assert.Equal(new AlertSettings(), AlertSettings.From(null));
    }
}
