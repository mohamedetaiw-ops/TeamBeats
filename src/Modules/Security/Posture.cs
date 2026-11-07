using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Security;

/// <summary>One posture check: <c>passed</c>, <c>failed</c>, or <c>unavailable</c> when the plan does not include the control.</summary>
public sealed record PostureCheck(string Key, string State, string? Link);

/// <summary>Passed checks out of the checks that apply; the spec gives no score formula (plan, Posture).</summary>
public sealed record PostureView(int Passed, int Total, IReadOnlyList<PostureCheck> Checks);

/// <summary>
/// The security posture page (spec 026 FR-017): the settings that are weaker than the recommended baseline, each with a
/// link to the page that fixes it. Readable by tenant administrators and security officers.
/// </summary>
public sealed class PostureService(IAuthorizationService authz, IEntitlementService entitlements, PolicyStore store, ISignInMethods signIns)
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

    public async Task<PostureView> GetAsync(CancellationToken ct)
    {
        if (!(await authz.CheckAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct)).Allowed
            && !(await authz.CheckAsync(Permissions.SecurityView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var policy = await store.ReadAsync(ct) ?? SecurityPolicySnapshot.Default;
        var scoped = await entitlements.IsEnabledAsync(Capabilities.SecurityIpRulesScoped, ct);
        var methods = await signIns.CurrentAsync(ct);
        var checks = Evaluate(policy, scoped, methods);
        var applicable = checks.Where(c => c.State != Unavailable).ToList();
        return new PostureView(applicable.Count(c => c.State == Passed), applicable.Count, checks);
    }

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
