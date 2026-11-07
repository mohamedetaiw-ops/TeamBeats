using TeamBeats.Modules.Notifications;
using TeamBeats.Modules.Notifications.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 015 rules without a database: quiet hours windows, retry backoff, template parameters, categories.</summary>
public sealed class NotificationRulesTests
{
    private static readonly TimeZoneInfo Dubai = TimeZoneInfo.FindSystemTimeZoneById("Asia/Dubai");

    [Theory]
    [InlineData("23:30", true)]
    [InlineData("03:00", true)]
    [InlineData("07:00", false)]
    [InlineData("12:00", false)]
    [InlineData("22:00", true)]
    public void A_window_past_midnight_covers_late_evening_and_early_morning(string local, bool inside) =>
        Assert.Equal(inside, QuietHoursWindow.Contains(TimeOnly.Parse(local), new TimeOnly(22, 0), new TimeOnly(7, 0)));

    [Fact]
    public void A_daytime_window_does_not_wrap() =>
        Assert.False(QuietHoursWindow.Contains(new TimeOnly(23, 0), new TimeOnly(12, 0), new TimeOnly(14, 0)));

    [Fact]
    public void Inside_an_evening_window_delivery_waits_until_the_next_morning_in_the_users_zone()
    {
        var now = new DateTimeOffset(2026, 11, 7, 19, 0, 0, TimeSpan.Zero); // 23:00 in Dubai
        var release = QuietHoursWindow.ReleaseAt(now, new TimeOnly(22, 0), new TimeOnly(7, 0), Dubai);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero), release); // 07:00 Dubai
    }

    [Fact]
    public void Inside_the_morning_part_delivery_waits_until_the_same_mornings_end()
    {
        var now = new DateTimeOffset(2026, 11, 7, 1, 0, 0, TimeSpan.Zero); // 05:00 in Dubai
        Assert.Equal(new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero), QuietHoursWindow.ReleaseAt(now, new TimeOnly(22, 0), new TimeOnly(7, 0), Dubai));
    }

    [Fact]
    public void Outside_the_window_delivery_goes_now()
    {
        var now = new DateTimeOffset(2026, 11, 7, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(now, QuietHoursWindow.ReleaseAt(now, new TimeOnly(22, 0), new TimeOnly(7, 0), Dubai));
    }

    [Fact]
    public void Backoff_grows_and_then_stays_at_three_hours()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), EmailDelivery.BackoffAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(5), EmailDelivery.BackoffAfter(2));
        Assert.Equal(TimeSpan.FromHours(3), EmailDelivery.BackoffAfter(5));
        Assert.Equal(TimeSpan.FromHours(3), EmailDelivery.BackoffAfter(40));
    }

    [Fact]
    public void Template_parameters_are_filled_as_plain_text_and_missing_ones_become_empty()
    {
        var filled = NotificationTemplates.Fill("{who} said <b>{what}</b> {missing}.", new Dictionary<string, string> { ["who"] = "<script>", ["what"] = "hi" });
        Assert.Equal("<script> said <b>hi</b> .", filled);
        Assert.Equal(["count", "label"], NotificationTemplates.ParametersOf(new RenderedText("From {label}", "{count} {label}")).Order().ToArray());
    }

    [Fact]
    public void Platform_mandatory_categories_are_fixed()
    {
        var mandatory = NotificationCategories.All.Where(c => c.Mandatory).Select(c => c.Key).ToArray();
        Assert.Equal(["security_notice", "critical_announcement", "attestation_due", "approval_escalation", "site_emergency"], mandatory);
        var effective = new EffectiveNotificationSettings(null, null, new Dictionary<string, bool> { ["comment"] = true }, new HashSet<string> { "announcement" });
        Assert.True(effective.IsMandatory("announcement"));
        Assert.True(effective.EmailByDefault(NotificationCategories.Find("comment")!));
        Assert.False(effective.EmailByDefault(NotificationCategories.Find("document_update")!));
    }
}
