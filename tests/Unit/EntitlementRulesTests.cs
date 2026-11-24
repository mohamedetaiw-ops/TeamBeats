using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Entitlements;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 005 R2 rules: the quota ceiling per enforcement and the contract state over time.</summary>
public sealed class EntitlementRulesTests
{
    [Theory]
    [InlineData(10000L, "deny", 0, 10000L)]
    [InlineData(10000L, "grace", 10, 11000L)]
    [InlineData(15L, "grace", 10, 17L)]
    [InlineData(10000L, "warn", 0, null)]
    [InlineData(null, "deny", 0, null)]
    public void The_ceiling_follows_the_enforcement(long? limit, string mode, int pct, long? ceiling) =>
        Assert.Equal(ceiling, QuotaMeter.Ceiling(limit, mode, pct));

    [Fact]
    public void A_contract_is_active_then_in_grace_then_read_only()
    {
        var ends = new DateTimeOffset(2027, 6, 30, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(SubscriptionState.Active, SubscriptionState.For(null, 30, ends).State);
        Assert.Equal(SubscriptionState.Active, SubscriptionState.For(ends, 30, ends.AddSeconds(-1)).State);
        Assert.Equal(SubscriptionState.Grace, SubscriptionState.For(ends, 30, ends).State);
        Assert.Equal(SubscriptionState.Grace, SubscriptionState.For(ends, 30, ends.AddDays(30).AddSeconds(-1)).State);
        var readOnly = SubscriptionState.For(ends, 30, ends.AddDays(30));
        Assert.Equal(SubscriptionState.ReadOnly, readOnly.State);
        Assert.Equal(ends.AddDays(30), readOnly.GraceEndsAt);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", false)]
    [InlineData("POST", true)]
    [InlineData("DELETE", true)]
    public void Writes_are_every_method_but_reads(string method, bool write) => Assert.Equal(write, ExpiryGuard.IsWrite(method));
}
