using System.Net;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Security.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 026 T004: parsing ranges and deciding whether an address is refused by the tenant network rules.</summary>
public sealed class NetworkRulesTests
{
    private static SecurityPolicySnapshot Policy(string[]? allow = null, string[]? block = null, string[]? admin = null) =>
        SecurityPolicySnapshot.Default with { IpAllow = allow ?? [], IpBlock = block ?? [], AdminIpAllow = admin ?? [] };

    private static NetworkArea? Refusal(SecurityPolicySnapshot policy, string? address, bool adminArea = false) =>
        NetworkRules.Refusal(policy, address is null ? null : IPAddress.Parse(address), adminArea);

    [Theory]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData(" 10.0.0.0/8 ", "10.0.0.0/8")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    public void Valid_ranges_parse_to_their_canonical_form(string text, string canonical)
    {
        Assert.True(NetworkRules.TryParseRange(text, out var range));
        Assert.Equal(canonical, NetworkRules.Canonical(range));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.5/24")] // Host bits set: ambiguous, so it is refused rather than guessed.
    [InlineData("300.1.1.1")]
    public void Invalid_ranges_are_rejected(string? text) => Assert.False(NetworkRules.TryParseRange(text, out _));

    [Fact]
    public void No_rules_refuse_nothing_even_without_an_address()
    {
        Assert.Null(Refusal(Policy(), "198.51.100.7"));
        Assert.Null(Refusal(Policy(), null, adminArea: true));
    }

    [Fact]
    public void An_allowlist_admits_only_addresses_inside_it_and_refuses_an_unknown_address()
    {
        var policy = Policy(allow: ["203.0.113.0/24"]);
        Assert.Null(Refusal(policy, "203.0.113.9"));
        Assert.Equal(NetworkArea.Tenant, Refusal(policy, "198.51.100.7"));
        Assert.Equal(NetworkArea.Tenant, Refusal(policy, null));
    }

    [Fact]
    public void The_blocklist_wins_over_the_allowlist()
    {
        var policy = Policy(allow: ["203.0.113.0/24"], block: ["203.0.113.66/32"]);
        Assert.Null(Refusal(policy, "203.0.113.65"));
        Assert.Equal(NetworkArea.Tenant, Refusal(policy, "203.0.113.66"));
    }

    [Fact]
    public void The_admin_list_applies_only_to_the_admin_area()
    {
        var policy = Policy(admin: ["10.0.0.0/8"]);
        Assert.Null(Refusal(policy, "203.0.113.9"));
        Assert.Equal(NetworkArea.Admin, Refusal(policy, "203.0.113.9", adminArea: true));
        Assert.Null(Refusal(policy, "10.20.30.40", adminArea: true));
    }

    [Fact]
    public void IPv4_mapped_IPv6_addresses_match_IPv4_ranges()
    {
        var policy = Policy(allow: ["203.0.113.0/24"]);
        Assert.Null(Refusal(policy, "::ffff:203.0.113.9"));
        Assert.Equal(NetworkArea.Tenant, Refusal(policy, "::ffff:198.51.100.7"));
    }

    [Fact]
    public void IPv6_ranges_are_honoured()
    {
        var policy = Policy(allow: ["2001:db8::/32"]);
        Assert.Null(Refusal(policy, "2001:db8:1::5"));
        Assert.Equal(NetworkArea.Tenant, Refusal(policy, "2001:db9::5"));
    }
}
