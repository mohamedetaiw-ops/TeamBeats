namespace TeamBeats.Primitives;

/// <summary>
/// Stable machine-readable problem codes (RFC 9457 "type" suffixes). Each code is also the
/// resource key suffix used to localize the problem title: <c>problem.{code}</c>.
/// </summary>
public static class ProblemCodes
{
    public const string NotFound = "not_found";
    public const string Unauthenticated = "auth.unauthenticated";
    public const string AuthzDenied = "authz.denied";
    public const string EntitlementNotInPackage = "entitlement.not_in_package";
    public const string EntitlementQuotaExhausted = "entitlement.quota_exhausted";
    public const string EntitlementExpired = "entitlement.expired";
    public const string TenantSuspended = "tenant.suspended";
    public const string TenantClosed = "tenant.closed";
    public const string TenantUnavailable = "tenant.unavailable";
    public const string ValidationFailed = "validation.failed";
    public const string Conflict = "conflict";
    public const string SignInRefused = "auth.sign_in_refused";

    /// <summary>The resource existed but is no longer available, such as the profile of a disabled member.</summary>
    public const string Gone = "gone";

    /// <summary>The tenant's network rules refuse the caller's address (spec 026 US1).</summary>
    public const string NetworkNotAllowed = "security.network_not_allowed";

    public static string TypeUri(string code) => $"urn:teambeats:problem:{code}";
}
