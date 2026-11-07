using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Security;
using TeamBeats.Modules.Security.Contracts;
using Xunit;

namespace TeamBeats.Unit.Tests;

/// <summary>Spec 026 T014: each posture check against the recommended baseline.</summary>
public sealed class PostureTests
{
    private static string State(IReadOnlyList<PostureCheck> checks, string key) => checks.Single(c => c.Key == key).State;

    [Fact]
    public void A_policy_at_the_baseline_passes_every_check()
    {
        var policy = SecurityPolicySnapshot.Default with
        {
            RequireMfa = true, IpAllow = ["203.0.113.0/24"], AdminIpAllow = ["203.0.113.0/28"],
            StepUpMinutes = 10, IdleMinutes = 30, AbsoluteHours = 12,
        };
        var checks = PostureService.Evaluate(policy, scopedRules: true, new SignInMethods(SsoEnabled: true, DevSignInEnabled: false));
        Assert.All(checks, c => Assert.Equal(PostureService.Passed, c.State));
    }

    [Fact]
    public void Values_weaker_than_recommended_fail()
    {
        var policy = SecurityPolicySnapshot.Default with { StepUpMinutes = 11, IdleMinutes = 31, AbsoluteHours = 13 };
        var checks = PostureService.Evaluate(policy, scopedRules: false, new SignInMethods(SsoEnabled: false, DevSignInEnabled: true));
        foreach (var key in new[] { "step_up_window", "idle_timeout", "absolute_timeout", "mfa_required", "ip_rules", "sso_configured", "dev_sign_in_absent" })
        {
            Assert.Equal(PostureService.Failed, State(checks, key));
        }
        Assert.Equal(PostureService.Unavailable, State(checks, "admin_area_rule"));
        Assert.Null(checks.Single(c => c.Key == "dev_sign_in_absent").Link);
    }
}
