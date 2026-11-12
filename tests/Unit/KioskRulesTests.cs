using TeamBeats.Modules.Identity.Kiosk;
using TeamBeats.TenantContext;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 030 US4: what a kiosk session reaches, when it ends, and how badge codes and PINs are handled.</summary>
public sealed class KioskRulesTests
{
    private static readonly IReadOnlySet<string> LeaveOnly = new HashSet<string> { SharedDeviceRules.Leave };

    [Theory]
    [InlineData("GET", "/api/v1/me/", true)]
    [InlineData("GET", "/api/v1/me", true)]
    [InlineData("POST", "/api/v1/auth/signout", true)]
    [InlineData("GET", "/api/v1/me/leave", true)]
    [InlineData("POST", "/api/v1/hr/leave/requests", true)]
    [InlineData("DELETE", "/api/v1/me/leave", false)]
    [InlineData("GET", "/api/v1/me/payslips", false)]
    [InlineData("GET", "/api/v1/me/mail", false)]
    [InlineData("GET", "/api/v1/me/offline", false)]
    [InlineData("GET", "/api/v1/content/feed", false)]
    [InlineData("GET", "/api/v1/me/policies", false)]
    [InlineData("GET", null, false)]
    public void Kiosk_sessions_reach_only_listed_routes_of_allowed_features(string method, string? route, bool allowed) =>
        Assert.Equal(allowed, SharedDeviceRules.AllowsRoute(method, route, LeaveOnly));

    [Theory]
    [InlineData("/", true)]
    [InlineData("/leave", true)]
    [InlineData("/leave/new?type=annual", true)]
    [InlineData("/leaveother", false)]
    [InlineData("/payslips", false)]
    [InlineData("/policies", false)]
    [InlineData("/booking/scan/ABC", false)]
    public void Kiosk_navigation_keeps_pages_of_allowed_features(string href, bool allowed) =>
        Assert.Equal(allowed, SharedDeviceRules.AllowsPage(href, LeaveOnly));

    [Theory]
    [InlineData("/api/v1/me/leave", true)]
    [InlineData("/api/v1/me/calendar", false)]
    [InlineData("/api/v1/weather", false)]
    [InlineData("/api/v1/me", true)]
    [InlineData("/api/v1/hr/leave/requests/123", true)]
    public void Home_components_stay_only_when_their_data_is_reachable(string path, bool allowed) =>
        Assert.Equal(allowed, SharedDeviceRules.AllowsDataPath(path, LeaveOnly));

    [Fact]
    public void A_kiosk_session_ends_when_the_device_is_revoked_used_elsewhere_or_left_idle()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(KioskRules.Ended(true, true, 2, now.AddSeconds(-110), now));
        Assert.Equal("idle_timeout", KioskRules.Ended(true, true, 2, now.AddMinutes(-3), now));
        Assert.Equal(KioskRules.Revoked, KioskRules.Ended(false, true, 2, now, now));
        Assert.Equal(KioskRules.WrongDevice, KioskRules.Ended(true, false, 2, now, now));
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345678", true)]
    [InlineData("123", false)]
    [InlineData("123456789", false)]
    [InlineData("12a4", false)]
    [InlineData("١٢٣٤", false)]
    [InlineData(null, false)]
    public void PINs_are_4_to_8_ASCII_digits(string? pin, bool valid) => Assert.Equal(valid, KioskRules.ValidPin(pin));

    [Fact]
    public void Codes_are_random_readable_and_matched_however_they_are_typed()
    {
        var badge = KioskCodes.NewBadgeCode();
        Assert.Equal(22, badge.Length);
        Assert.StartsWith("TB", badge);
        Assert.NotEqual(badge, KioskCodes.NewBadgeCode());
        Assert.DoesNotContain(badge[2..], c => c is 'I' or 'L' or 'O' or 'U');
        var enrol = KioskCodes.NewEnrolCode();
        Assert.Matches("^[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}$", enrol);
        Assert.Equal(KioskCodes.Hash(enrol), KioskCodes.Hash(" " + enrol.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)));
        Assert.Equal(KioskCodes.Hash("AB01"), KioskCodes.Hash("abOl"));
    }

    [Fact]
    public void PIN_hashes_are_salted_and_verify_only_the_same_PIN()
    {
        var hash = KioskPins.Hash("2468");
        Assert.NotEqual(hash, KioskPins.Hash("2468"));
        Assert.DoesNotContain("2468", hash);
        Assert.True(KioskPins.Verify("2468", hash));
        Assert.False(KioskPins.Verify("2469", hash));
        Assert.False(KioskPins.Verify("2468", "not-a-hash"));
    }
}
