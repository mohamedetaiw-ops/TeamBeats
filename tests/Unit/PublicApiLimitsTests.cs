using System.Net;
using TeamBeats.Modules.PublicApi;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Primitives;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>
/// Spec 035 T008: limits per partition with one-minute windows (FR-017), limits a tenant may only lower, and the
/// address ranges an app may call from (FR-027).
/// </summary>
public sealed class PublicApiLimitsTests
{
    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 13, 8, 0, 10, TimeSpan.Zero);
    }

    [Fact]
    public void Each_partition_counts_on_its_own_and_starts_again_on_the_next_minute()
    {
        var clock = new Clock();
        var limits = new PublicRateLimits(clock);
        Guid tenant = Guid.NewGuid(), other = Guid.NewGuid(), app = Guid.NewGuid(), user = Guid.NewGuid(), colleague = Guid.NewGuid();

        var first = limits.Take(PublicRateLimits.App(tenant, app), 2);
        Assert.Equal(new LimitResult(true, 2, 1, 50), first);
        Assert.True(limits.Take(PublicRateLimits.App(tenant, app), 2).Allowed);
        var over = limits.Take(PublicRateLimits.App(tenant, app), 2);
        Assert.False(over.Allowed);
        Assert.Equal(0, over.Remaining);

        // The same app in another tenant, the app for one person, and that person's colleague are other partitions.
        Assert.True(limits.Take(PublicRateLimits.App(other, app), 2).Allowed);
        Assert.True(limits.Take(PublicRateLimits.AppUser(tenant, app, user), 1).Allowed);
        Assert.False(limits.Take(PublicRateLimits.AppUser(tenant, app, user), 1).Allowed);
        Assert.True(limits.Take(PublicRateLimits.AppUser(tenant, app, colleague), 1).Allowed);
        Assert.True(limits.Take(PublicRateLimits.Tenant(tenant), 1).Allowed);
        Assert.True(limits.Take(PublicRateLimits.Token(tenant, "tb_x"), 1).Allowed);

        // Windows start on the minute: 50 seconds later the app may call again.
        clock.UtcNow = clock.UtcNow.AddSeconds(50);
        var next = limits.Take(PublicRateLimits.App(tenant, app), 2);
        Assert.True(next.Allowed);
        Assert.Equal(60, next.ResetSeconds);
    }

    [Theory]
    [InlineData(AppKinds.Application, 600, true)]
    [InlineData(AppKinds.Application, 601, false)]
    [InlineData(AppKinds.DelegatedWeb, 120, true)]
    [InlineData(AppKinds.DelegatedWeb, 121, false)]
    [InlineData(AppKinds.McpClient, 1, true)]
    [InlineData(AppKinds.Application, -1, false)]
    [InlineData(AppKinds.Application, 0, true)]
    public void A_tenant_may_lower_an_app_limit_but_never_raise_it(string kind, int value, bool valid)
    {
        var errors = new ValidationErrors();
        ApiRules.CheckCallsPerMinute(value, kind, errors);
        Assert.Equal(valid, !errors.Any);
    }

    [Fact]
    public void Each_instance_counts_its_share_of_the_limit_and_the_headers_speak_of_the_whole()
    {
        var limits = new PublicRateLimits(new Clock());
        var installation = new PublicApiLimits(Instances: 3);
        Assert.Equal(200, installation.Share(600));
        Assert.Equal(1, installation.Share(2));
        var key = PublicRateLimits.App(Guid.NewGuid(), Guid.NewGuid());
        var first = limits.Take(key, 6, new PublicApiLimits(Instances: 2).Share(6));
        Assert.Equal(new LimitResult(true, 6, 4, 50), first);
        limits.Take(key, 6, 3);
        Assert.True(limits.Take(key, 6, 3).Allowed);
        var over = limits.Take(key, 6, 3);
        Assert.False(over.Allowed);
        Assert.Equal(0, over.Remaining);
    }

    [Fact]
    public void The_tenant_limit_never_goes_above_the_installation()
    {
        var limits = new PublicApiLimits(1000);
        Assert.Equal(1000, limits.ForTenant(null));
        Assert.Equal(500, limits.ForTenant(500));
        Assert.Equal(1000, limits.ForTenant(5000));
    }

    [Fact]
    public void Address_ranges_allow_only_the_addresses_inside_them()
    {
        Assert.True(ApiRules.AddressAllowed([], IPAddress.Parse("198.51.100.7")));
        string[] ranges = ApiRules.NormalizeRanges(["203.0.113.0/24", "2001:db8::/32", "192.0.2.10"]);
        Assert.Equal(["203.0.113.0/24", "2001:db8::/32", "192.0.2.10/32"], ranges);
        Assert.True(ApiRules.AddressAllowed(ranges, IPAddress.Parse("203.0.113.5")));
        Assert.True(ApiRules.AddressAllowed(ranges, IPAddress.Parse("::ffff:203.0.113.5")));
        Assert.True(ApiRules.AddressAllowed(ranges, IPAddress.Parse("2001:db8::1")));
        Assert.True(ApiRules.AddressAllowed(ranges, IPAddress.Parse("192.0.2.10")));
        Assert.False(ApiRules.AddressAllowed(ranges, IPAddress.Parse("192.0.2.11")));
        Assert.False(ApiRules.AddressAllowed(ranges, IPAddress.Parse("198.51.100.7")));
        Assert.False(ApiRules.AddressAllowed(ranges, null));

        var errors = new ValidationErrors();
        ApiRules.CheckIpRanges(["203.0.113.0/24", "not an address"], errors);
        Assert.True(errors.Any);
        errors = new ValidationErrors();
        ApiRules.CheckIpRanges(Enumerable.Range(1, 21).Select(i => $"192.0.2.{i}").ToList(), errors);
        Assert.True(errors.Any);
    }

    [Theory]
    [InlineData(200, null)]
    [InlineData(304, null)]
    [InlineData(400, "client")]
    [InlineData(403, "denied")]
    [InlineData(404, "not_found")]
    [InlineData(429, "rate_limited")]
    [InlineData(500, "server")]
    [InlineData(503, "server")]
    public void Each_status_falls_in_one_error_class(int status, string? expected) => Assert.Equal(expected, CallLog.ClassOf(status));
}
