using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Security;

/// <summary>One posture check: <c>passed</c>, <c>failed</c>, or <c>unavailable</c> when the plan does not include the control.</summary>
public sealed record PostureCheck(string Key, string State, string? Link);

/// <summary>Passed checks out of the checks that apply; the spec gives no score formula (plan, Posture).</summary>
public sealed record PostureView(int Passed, int Total, IReadOnlyList<PostureCheck> Checks, string Region = "");

/// <summary>
/// The security posture page (spec 026 FR-017): the settings that are weaker than the recommended baseline, each with a
/// link to the page that fixes it. Readable by tenant administrators and security officers.
/// </summary>
public sealed class PostureService(IAuthorizationService authz, IEntitlementService entitlements, PolicyStore store, ISignInMethods signIns,
    BreakGlassService breakGlass, TeamBeats.Modules.Tenancy.Contracts.ITenantSettingsReader settings, InstallationPosture installation)
{
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string Unavailable = "unavailable";

    // Recommended values: the spec 026 defaults (section 6).
    public const int RecommendedIdleMinutes = 30;
    public const int RecommendedAbsoluteHours = 12;
    public const int RecommendedStepUpMinutes = 10;

    private const string PolicyLink = "/admin/security";
    private const string IdentityLink = "/admin/identity";
    private const string AuditLink = "/admin/audit?action=" + TeamBeats.Audit.Contracts.AuditActions.BreakGlassOpened;

    public async Task<PostureView> GetAsync(CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct)).Allowed
            && !(await authz.CheckAsync(Permissions.SecurityView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var checks = await ChecksAsync(ct);
        var applicable = checks.Where(c => c.State != Unavailable).ToList();
        return new PostureView(applicable.Count(c => c.State == Passed), applicable.Count, checks, await settings.GetRegionAsync(ct));
    }

    /// <summary>The checks of the current tenant; the caller decides who may see them (analytics security trends read them too).</summary>
    public async Task<IReadOnlyList<PostureCheck>> ChecksAsync(CancellationToken ct)
    {
        var policy = await store.ReadAsync(ct) ?? SecurityPolicySnapshot.Default;
        var scoped = await entitlements.IsEnabledAsync(Capabilities.SecurityIpRulesScoped, ct);
        var methods = await signIns.CurrentAsync(ct);
        return [.. Evaluate(policy, scoped, methods), PlatformAccess(await breakGlass.AnyOpenAsync(ct)), .. installation.Checks()];
    }

    /// <summary>Spec 026 FR-020: fails while a platform operator has a break-glass session open on the tenant; links to the audit entries.</summary>
    public static PostureCheck PlatformAccess(bool open) => new("no_platform_access", open ? Failed : Passed, AuditLink);

    public static IReadOnlyList<PostureCheck> Evaluate(SecurityPolicySnapshot policy, bool scopedRules, SignInMethods methods)
    {
        static string State(bool ok) => ok ? Passed : Failed;
        return
        [
            new("sso_configured", State(methods.SsoEnabled), IdentityLink),
            new("mfa_required", State(policy.RequireMfa), PolicyLink),
            new("ip_rules", State(policy.IpAllow.Count > 0), PolicyLink),
            new("admin_area_rule", scopedRules ? State(policy.AdminIpAllow.Count > 0) : Unavailable, PolicyLink),
            new("step_up_window", State(policy.StepUpMinutes <= RecommendedStepUpMinutes), PolicyLink),
            new("idle_timeout", State(policy.IdleMinutes <= RecommendedIdleMinutes), PolicyLink),
            new("absolute_timeout", State(policy.AbsoluteHours <= RecommendedAbsoluteHours), PolicyLink),
            // Development sign-in is a configuration of the host, not something a tenant administrator can fix: no link.
            new("dev_sign_in_absent", State(!methods.DevSignInEnabled), null),
        ];
    }
}

/// <summary>
/// The hardening self check of a customer-hosted install (spec 026 R3 slice 3, <c>docs/security/hardening-guide.md</c>):
/// installation settings the customer's IT team owns, shown on the posture page only when <c>Platform:Hosting</c> is
/// <c>customer</c>. A platform-hosted install is checked by the platform's own operations, so its tenants see none of
/// these. No link: the fix is in the installation's configuration, as the guide describes.
/// </summary>
public sealed class InstallationPosture(Microsoft.Extensions.Configuration.IConfiguration configuration, Microsoft.Extensions.Hosting.IHostEnvironment environment)
{
    public const string HostingSetting = "Platform:Hosting";
    public const string CustomerHosted = "customer";

    public bool CustomerHostedInstall => string.Equals(configuration[HostingSetting], CustomerHosted, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<PostureCheck> Checks() => CustomerHostedInstall ? Evaluate(configuration, environment.EnvironmentName) : [];

    public static IReadOnlyList<PostureCheck> Evaluate(Microsoft.Extensions.Configuration.IConfiguration configuration, string environmentName)
    {
        static string State(bool ok) => ok ? PostureService.Passed : PostureService.Failed;
        return
        [
            new("install_production_mode", State(string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase)), null),
            new("install_database_tls", State(DatabaseEncrypted(configuration["ConnectionStrings:App"])), null),
            new("install_durable_messaging", State(string.Equals(configuration["Messaging:Transport"], "RabbitMq", StringComparison.OrdinalIgnoreCase)), null),
            new("install_private_addresses_blocked", State(!bool.TryParse(configuration[SafeHttp.AllowPrivateSetting], out var allow) || !allow), null),
        ];
    }

    /// <summary>TLS to the database is required, or the database is on the same host (a Unix socket or loopback).</summary>
    public static bool DatabaseEncrypted(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }
        try
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
            if (b.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull)
            {
                return true;
            }
            var host = b.Host ?? "";
            return host.Split(',').All(h => h.StartsWith('/') || h is "localhost" or "127.0.0.1" or "::1");
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
