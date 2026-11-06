using System.Text;
using TeamBeats.Localization;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;
using Xunit;

namespace TeamBeats.Unit.Tests;

public sealed class HostResolverTests
{
    private static readonly HostResolutionOptions Shared = new() { BaseDomain = "teambeats.example" };

    [Theory]
    [InlineData("acme.teambeats.example", HostMatch.TenantKey, "acme")]
    [InlineData("ACME.TeamBeats.Example:443", HostMatch.TenantKey, "acme")]
    [InlineData("acme.teambeats.example.", HostMatch.TenantKey, "acme")]
    [InlineData("platform.teambeats.example", HostMatch.Platform, null)]
    [InlineData("teambeats.example", HostMatch.None, null)]
    [InlineData("a.b.teambeats.example", HostMatch.None, null)]
    [InlineData("acme.evil.example", HostMatch.None, null)]
    [InlineData("acmeteambeats.example", HostMatch.None, null)]
    [InlineData("", HostMatch.None, null)]
    public void Shared_mode(string host, HostMatch match, string? key)
    {
        Assert.Equal((match, key), HostResolver.Resolve(host, Shared));
    }

    [Fact]
    public void Dedicated_mode_maps_any_host_to_the_single_tenant_except_the_platform_host()
    {
        var options = new HostResolutionOptions { Mode = InstallationMode.Dedicated, DedicatedTenantKey = "gov", DedicatedPlatformHost = "ops.gov.example" };
        Assert.Equal((HostMatch.TenantKey, "gov"), HostResolver.Resolve("intranet.gov.example", options));
        Assert.Equal((HostMatch.Platform, (string?)null), HostResolver.Resolve("ops.gov.example", options));
    }
}

public sealed class LocaleResolverTests
{
    [Theory]
    [InlineData("ar", new[] { "ar", "en" }, "en", "ar")]
    [InlineData("fr", new[] { "ar", "en" }, "en", "en")]
    [InlineData(null, new[] { "ar", "en" }, "ar", "ar")]
    [InlineData("xx", new[] { "ar", "en" }, null, "en")]
    public void Preference_then_tenant_default_then_english(string? preference, string[] enabled, string? tenantDefault, string expected)
    {
        Assert.Equal(expected, LocaleResolver.Resolve(preference, enabled, tenantDefault));
    }

    [Theory]
    [InlineData("ar", "rtl")]
    [InlineData("ur", "rtl")]
    [InlineData("en", "ltr")]
    [InlineData("fr", "ltr")]
    [InlineData("hi", "ltr")]
    public void Direction(string locale, string dir) => Assert.Equal(dir, LocaleResolver.Direction(locale));
}

public sealed class TenantRulesTests
{
    [Theory]
    [InlineData("acme", true)]
    [InlineData("acme-gulf-2", true)]
    [InlineData("ab", false)]
    [InlineData("-acme", false)]
    [InlineData("acme-", false)]
    [InlineData("ac_me", false)]
    [InlineData("platform", false)]
    [InlineData("admin", false)]
    public void Key_format_and_reserved_words(string key, bool valid)
    {
        var errors = new ValidationErrors();
        TenantRules.ValidateKey(TenantRules.NormalizeKey(key), errors);
        Assert.Equal(valid, !errors.Any);
    }

    [Fact]
    public void Default_locale_must_be_enabled()
    {
        var errors = new ValidationErrors();
        TenantRules.ValidateLocales("fr", ["ar", "en"], errors);
        Assert.True(errors.Any);
    }

    [Theory]
    [InlineData(TenantLifecycleStatus.Active, TenantLifecycleStatus.Suspended, true)]
    [InlineData(TenantLifecycleStatus.Suspended, TenantLifecycleStatus.Active, true)]
    [InlineData(TenantLifecycleStatus.Closed, TenantLifecycleStatus.Active, false)]
    [InlineData(TenantLifecycleStatus.Closed, TenantLifecycleStatus.Suspended, false)]
    public void Lifecycle_transitions(TenantLifecycleStatus from, TenantLifecycleStatus to, bool allowed)
    {
        Assert.Equal(allowed, TenantRules.CanTransition(from, to));
    }
}

public sealed class LogoValidatorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

    [Fact]
    public void Accepts_png_with_magic_bytes() => Assert.Equal("png", ImageValidator.Validate("logo", "image/png", Png));

    [Fact]
    public void Rejects_png_content_type_with_wrong_bytes() =>
        Assert.Throws<AppException>(() => ImageValidator.Validate("logo", "image/png", Encoding.UTF8.GetBytes("<svg/>")));

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>")]
    [InlineData("<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg>&x;</svg>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><a href=\"javascript:alert(1)\"/></svg>")]
    public void Rejects_active_svg_content(string svg) =>
        Assert.Throws<AppException>(() => ImageValidator.Validate("logo", "image/svg+xml", Encoding.UTF8.GetBytes(svg)));

    [Fact]
    public void Accepts_plain_svg() =>
        Assert.Equal("svg", ImageValidator.Validate("logo", "image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>")));

    [Fact]
    public void Rejects_oversized_files() =>
        Assert.Throws<AppException>(() => ImageValidator.Validate("logo", "image/png", new byte[ImageValidator.MaxBytes + 1]));
}
