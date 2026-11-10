using System.Net;
using TeamBeats.Modules.Integrations.Runtime;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 017 FR-012: which addresses and hosts connector calls may reach.</summary>
public sealed class SafeHttpTests
{
    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.20.30.40")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.10")]
    [InlineData("192.168.1.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.9")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2001:db8::1")]
    [InlineData("::10.0.0.1")]
    public void Internal_and_reserved_addresses_are_refused(string address) => Assert.False(SafeHttp.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("193.0.0.1")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Public_addresses_are_allowed(string address) => Assert.True(SafeHttp.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("API.example.com.", true)]
    [InlineData("acme.service-now.example", true)]
    [InlineData("a.b.service-now.example", true)]
    [InlineData("service-now.example", false)]
    [InlineData("evilservice-now.example", false)]
    [InlineData("api.example.com.evil.example", false)]
    [InlineData("example.com", false)]
    public void Hosts_match_exact_entries_and_subdomain_wildcards(string host, bool allowed) =>
        Assert.Equal(allowed, SafeHttp.HostAllowed(host, ["api.example.com", "*.service-now.example"]));
}
