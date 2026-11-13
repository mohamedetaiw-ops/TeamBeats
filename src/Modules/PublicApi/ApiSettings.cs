using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// The tenant's API settings (spec 035 section 6, FR-013, FR-028): token lifetimes the tenant may only lower than the
/// platform defaults, the scopes employees may consent to alone, and the tenant switch for the public API.
/// </summary>
public sealed class ApiSettings(IDbSession session, TenantContextAccessor context, IAuthorizationService authz, IEntitlementService entitlements, IAuditWriter audit,
    PublicApiLimits limits)
{
    /// <summary>The settings as the authorization server reads them; no permission needed.</summary>
    public async Task<ApiSettingsView> CurrentAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync(
            "select access_token_minutes, refresh_token_days, user_consent_scopes, api_enabled, version, tenant_calls_per_minute from publicapi.setting", null,
            r => new ApiSettingsView(r.GetInt32(0), r.GetInt32(1), r.IsDBNull(2) ? ApiRules.DefaultUserConsentScopes : r.GetFieldValue<string[]>(2),
                r.GetBoolean(3), r.GetInt32(4), r.IsDBNull(5) ? null : r.GetInt32(5)), ct)
        ?? new ApiSettingsView(ApiRules.DefaultAccessTokenMinutes, ApiRules.DefaultRefreshTokenDays, ApiRules.DefaultUserConsentScopes, true, 0);

    public async Task<ApiSettingsView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(Permissions.ApiView, AuthzTarget.Tenant, ct);
        return await CurrentAsync(ct);
    }

    public async Task<ApiSettingsView> UpdateAsync(ApiSettingsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(Permissions.ApiManage, AuthzTarget.Tenant, ct);
        var current = await CurrentAsync(ct);
        if (request.Version is { } version && version != current.Version)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        var errors = new ValidationErrors();
        if (request.AccessTokenMinutes is < 5 or > ApiRules.MaxAccessTokenMinutes)
        {
            errors.Add("accessTokenMinutes", "validation.api.access_token_minutes");
        }
        if (request.RefreshTokenDays is < 1 or > ApiRules.DefaultRefreshTokenDays)
        {
            errors.Add("refreshTokenDays", "validation.api.refresh_token_days");
        }
        // Only read scopes are user-consentable at most; write scopes always need an API administrator (FR-013).
        if (request.UserConsentScopes is { } scopes && scopes.Any(s => ScopeCatalogue.Find(s) is not { UserConsent: true }))
        {
            errors.Add("userConsentScopes", "validation.api.user_consent_scope");
        }
        // The tenant's limit for all apps may only be lowered below the installation's (FR-017).
        if (request.TenantCallsPerMinute is { } tenantLimit && tenantLimit != 0 && (tenantLimit < 1 || tenantLimit > limits.TenantCallsPerMinute))
        {
            errors.Add("tenantCallsPerMinute", "validation.api.tenant_calls_per_minute");
        }
        errors.ThrowIfAny();
        var next = new ApiSettingsView(request.AccessTokenMinutes ?? current.AccessTokenMinutes, request.RefreshTokenDays ?? current.RefreshTokenDays,
            request.UserConsentScopes?.Distinct().ToList() ?? current.UserConsentScopes, request.ApiEnabled ?? current.ApiEnabled, current.Version + 1,
            request.TenantCallsPerMinute switch { null => current.TenantCallsPerMinute, 0 => null, var v => v });
        await session.ExecAsync(
            """
            insert into publicapi.setting (tenant_id, access_token_minutes, refresh_token_days, user_consent_scopes, api_enabled, version, tenant_calls_per_minute,
              updated_by, updated_at)
            values (platform.current_tenant_id(), @access, @refresh, @scopes, @enabled, @version, @tenantLimit, @by, now())
            on conflict (tenant_id) do update set access_token_minutes = excluded.access_token_minutes, refresh_token_days = excluded.refresh_token_days,
              user_consent_scopes = excluded.user_consent_scopes, api_enabled = excluded.api_enabled, version = excluded.version,
              tenant_calls_per_minute = excluded.tenant_calls_per_minute,
              updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """,
            new
            {
                access = next.AccessTokenMinutes, refresh = next.RefreshTokenDays, scopes = next.UserConsentScopes.ToArray(), enabled = next.ApiEnabled,
                version = next.Version, tenantLimit = next.TenantCallsPerMinute, by = context.RequireUser(),
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiSettingsChanged, "api_settings", null,
            Changes: new
            {
                before = new { current.AccessTokenMinutes, current.RefreshTokenDays, current.UserConsentScopes, current.ApiEnabled, current.TenantCallsPerMinute },
                after = new { next.AccessTokenMinutes, next.RefreshTokenDays, next.UserConsentScopes, next.ApiEnabled, next.TenantCallsPerMinute },
            }), ct);
        return next;
    }
}
