using Microsoft.Extensions.Configuration;
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

    private static Microsoft.Extensions.Configuration.IConfiguration Config(params (string Key, string? Value)[] values) =>
        new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void A_customer_hosted_install_is_checked_against_the_hardening_guide()
    {
        // Spec 026 R3: the hardening self check of a customer-hosted install.
        var hardened = InstallationPosture.Evaluate(Config(
            ("ConnectionStrings:App", "Host=db.internal;Database=teambeats;Username=tb_app;SSL Mode=VerifyFull"),
            ("Messaging:Transport", "RabbitMq"),
            ("Integrations:Network:AllowPrivateAddresses", "false")), "Production");
        Assert.All(hardened, c => Assert.Equal(PostureService.Passed, c.State));
        Assert.All(hardened, c => Assert.Null(c.Link));

        var weak = InstallationPosture.Evaluate(Config(
            ("ConnectionStrings:App", "Host=db.internal;Database=teambeats;Username=tb_app"),
            ("Messaging:Transport", "InMemory"),
            ("Integrations:Network:AllowPrivateAddresses", "true")), "Staging");
        Assert.All(weak, c => Assert.Equal(PostureService.Failed, c.State));
        Assert.Equal(["install_production_mode", "install_database_tls", "install_durable_messaging", "install_private_addresses_blocked"], weak.Select(c => c.Key));
    }

    [Theory]
    [InlineData("Host=/var/run/postgresql;Database=teambeats", true)]
    [InlineData("Host=localhost;Database=teambeats", true)]
    [InlineData("Host=db.internal;Database=teambeats;SSL Mode=Require", true)]
    [InlineData("Host=db.internal;Database=teambeats;SSL Mode=Prefer", false)]
    [InlineData("Host=db1.internal,localhost;Database=teambeats", false)]
    [InlineData(null, false)]
    public void The_database_must_be_reached_over_tls_unless_it_is_on_the_same_host(string? connection, bool encrypted) =>
        Assert.Equal(encrypted, InstallationPosture.DatabaseEncrypted(connection));

    [Fact]
    public void A_platform_hosted_install_shows_no_installation_checks()
    {
        var env = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Staging" };
        Assert.Empty(new InstallationPosture(Config(), env).Checks());
        Assert.Equal(4, new InstallationPosture(Config(("Platform:Hosting", "customer")), env).Checks().Count);
    }
}
