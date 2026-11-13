using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>
/// Consents (spec 035 US2, FR-013, FR-029). An employee consents alone only to the scopes the tenant marks
/// user-consentable (by default the read scopes); every other scope needs an API administrator's consent for the tenant.
/// Guests authorize an app only when the app allows guests. Revoking a consent ends its token families at once.
/// </summary>
public sealed class Consents(
    IDbSession session,
    TenantContextAccessor context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    ITenantMemberships memberships,
    IAuditWriter audit,
    IOutbox outbox,
    Apps apps,
    ApiSettings settings,
    TokenFamilies families)
{
    /// <summary>The scopes of a request that neither the person nor an administrator has consented to yet.</summary>
    public async Task<IReadOnlyList<string>> MissingAsync(Guid appId, Guid userId, IReadOnlyList<string> scopes, CancellationToken ct)
    {
        var granted = await GrantedAsync(appId, userId, ct);
        return scopes.Where(s => !granted.Contains(s)).ToList();
    }

    /// <summary>Whether a consent still covers every scope a token family was issued with.</summary>
    public async Task<bool> CoversAsync(Guid appId, Guid userId, IReadOnlyList<string> scopes, CancellationToken ct) =>
        (await MissingAsync(appId, userId, scopes, ct)).Count == 0;

    /// <summary>What the consent page shows (FR-013). The redirect URI must be one the app registered, as at the authorization endpoint.</summary>
    public async Task<ConsentPrompt> PromptAsync(string? clientId, string? redirectUri, string? scope, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        var user = context.RequireUser();
        var app = await DelegatedAppAsync(clientId, redirectUri, ct);
        var requested = Requested(app, scope);
        var granted = await GrantedAsync(app.Id, user, ct);
        var userConsent = (await settings.CurrentAsync(ct)).UserConsentScopes.ToHashSet(StringComparer.Ordinal);
        var views = requested.Select(s => new ConsentScopeView(s, ScopeCatalogue.Find(s)!.DescriptionKey, userConsent.Contains(s), granted.Contains(s))).ToList();
        var guest = !app.AllowGuests && await IsGuestAsync(user, ct);
        return new ConsentPrompt(app.ClientId, app.Name, app.Publisher, app.Description, app.Kind, views, views.Any(v => !v.Granted && !v.UserConsent), guest);
    }

    /// <summary>The person consents to the requested scopes they may consent to; scopes that need an administrator are refused.</summary>
    public async Task<ConsentPrompt> GrantAsync(ConsentGrantRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        var user = context.RequireUser();
        var app = await DelegatedAppAsync(request.ClientId, request.RedirectUri, ct);
        if (!app.AllowGuests && await IsGuestAsync(user, ct))
        {
            throw AppException.Denied("authz.denied.api_guest");
        }
        var requested = Requested(app, string.Join(' ', request.Scopes ?? []));
        var userConsent = (await settings.CurrentAsync(ct)).UserConsentScopes.ToHashSet(StringComparer.Ordinal);
        var granted = await GrantedAsync(app.Id, user, ct);
        if (requested.Any(s => !granted.Contains(s) && !userConsent.Contains(s)))
        {
            throw AppException.Denied("authz.denied.api_admin_consent");
        }
        var existing = await session.QuerySingleOrDefaultAsync(
            "select id, scopes from publicapi.consent where app_id = @app and user_id = @user and revoked_at is null for update",
            new { app = app.Id, user }, r => (Id: r.GetGuid(0), Scopes: r.GetFieldValue<string[]>(1)), ct);
        var scopes = (existing.Scopes ?? []).Union(requested.Where(userConsent.Contains), StringComparer.Ordinal).ToArray();
        if (existing == default)
        {
            await session.ExecAsync(
                """
                insert into publicapi.consent (tenant_id, id, app_id, user_id, scopes, granted_by)
                values (platform.current_tenant_id(), @id, @app, @user, @scopes, @user)
                """, new { id = Ids.New(), app = app.Id, user, scopes }, ct);
        }
        else if (scopes.Length != existing.Scopes!.Length)
        {
            await session.ExecAsync("update publicapi.consent set scopes = @scopes, granted_at = now() where id = @id", new { id = existing.Id, scopes }, ct);
        }
        if (existing == default || scopes.Length != existing.Scopes!.Length)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ApiConsentGranted, "api_app", app.Id.ToString(),
                Changes: new { consent = "user", scopes }), ct);
            await outbox.EnqueueAsync(new AppConsentGranted(app.Id, user, scopes), ct);
        }
        return await PromptAsync(request.ClientId, request.RedirectUri, string.Join(' ', requested), ct);
    }

    /// <summary>The connected apps page (FR-029): every app the person authorized, with scopes, consent date and last use.</summary>
    public async Task<IReadOnlyList<ConnectedAppView>> MineAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        var user = context.RequireUser();
        return await session.QueryAsync(
            """
            select c.id, a.id, a.name, a.publisher, c.scopes, c.granted_at, c.last_used_at
            from publicapi.consent c join publicapi.app a on a.id = c.app_id
            where c.user_id = @user and c.revoked_at is null
            order by a.name, c.id
            """, new { user }, r => new ConnectedAppView(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetFieldValue<string[]>(4), r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6)), ct);
    }

    /// <summary>The person revokes their consent: the app's tokens for them are refused at once.</summary>
    public async Task RevokeMineAsync(Guid consentId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        var user = context.RequireUser();
        var app = await session.QuerySingleOrDefaultAsync(
            "update publicapi.consent set revoked_at = now(), revoked_by = @user where id = @consentId and user_id = @user and revoked_at is null returning app_id",
            new { consentId, user }, r => r.GetGuid(0), ct);
        if (app == Guid.Empty)
        {
            throw AppException.NotFound();
        }
        await families.RevokeForAppAsync(app, user, "consent_revoked", ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiConsentRevoked, "api_app", app.ToString(), Changes: new { consent = "user" }), ct);
        await outbox.EnqueueAsync(new AppConsentRevoked(app, user), ct);
    }

    /// <summary>An API administrator consents for the whole tenant (FR-013); the scopes replace the earlier admin consent.</summary>
    public async Task<AppDetail> GrantAdminAsync(Guid appId, AdminConsentRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(Permissions.ApiManage, AuthzTarget.Tenant, ct);
        var app = await apps.FindAsync(appId, ct) ?? throw AppException.NotFound();
        var scopes = (request.Scopes ?? []).Distinct().ToArray();
        if (!AppKinds.IsDelegated(app.Kind))
        {
            throw AppException.Validation("scopes", "validation.api.admin_consent_not_for_kind");
        }
        if (scopes.Any(s => !app.Scopes.Contains(s)))
        {
            throw AppException.Validation("scopes", "validation.api.scope_not_registered");
        }
        var by = context.RequireUser();
        var revoked = await session.ExecAsync("update publicapi.consent set revoked_at = now(), revoked_by = @by where app_id = @appId and user_id is null and revoked_at is null",
            new { appId, by }, ct);
        if (scopes.Length > 0)
        {
            await session.ExecAsync(
                "insert into publicapi.consent (tenant_id, id, app_id, user_id, scopes, granted_by) values (platform.current_tenant_id(), @id, @appId, null, @scopes, @by)",
                new { id = Ids.New(), appId, scopes, by }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.ApiConsentGranted, "api_app", appId.ToString(), Changes: new { consent = "admin", scopes }), ct);
            await outbox.EnqueueAsync(new AppConsentGranted(appId, null, scopes), ct);
        }
        else if (revoked > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ApiConsentRevoked, "api_app", appId.ToString(), Changes: new { consent = "admin" }), ct);
            await outbox.EnqueueAsync(new AppConsentRevoked(appId, null), ct);
        }
        if (revoked > 0)
        {
            // Families that relied on admin consent for a scope it no longer covers are refused at their next use.
            await families.RevokeForAppAsync(appId, null, "consent_revoked", ct);
        }
        return await apps.DetailOfAsync(app, ct);
    }

    /// <summary>The live delegated app a consent screen or authorization request names, with a redirect URI it registered.</summary>
    internal async Task<AppRecord> DelegatedAppAsync(string? clientId, string? redirectUri, CancellationToken ct)
    {
        var app = string.IsNullOrEmpty(clientId) ? null : await apps.FindByClientIdAsync(clientId, ct);
        if (app is null || !app.Active || !AppKinds.IsDelegated(app.Kind) || redirectUri is null || !app.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            throw AppException.NotFound();
        }
        return app;
    }

    /// <summary>The requested scopes, or every delegated scope of the app when none is named; unknown or unregistered ones are refused.</summary>
    internal static IReadOnlyList<string> Requested(AppRecord app, string? scope)
    {
        var requested = (scope ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (requested.Count == 0)
        {
            return app.Scopes;
        }
        if (requested.Any(s => !app.Scopes.Contains(s, StringComparer.Ordinal)))
        {
            throw AppException.Validation("scope", "validation.api.scope_not_registered");
        }
        return requested;
    }

    internal async Task<bool> IsGuestAsync(Guid user, CancellationToken ct) =>
        await memberships.GetAsync(user, ct) is not { MembershipType: not MembershipTypes.Guest };

    private async Task<HashSet<string>> GrantedAsync(Guid appId, Guid userId, CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            "select scopes from publicapi.consent where app_id = @appId and (user_id = @userId or user_id is null) and revoked_at is null",
            new { appId, userId }, r => r.GetFieldValue<string[]>(0), ct);
        return rows.SelectMany(s => s).ToHashSet(StringComparer.Ordinal);
    }

    public Task TouchAsync(Guid appId, Guid userId, CancellationToken ct) =>
        session.ExecAsync(
            """
            update publicapi.consent set last_used_at = now()
            where app_id = @appId and user_id = @userId and revoked_at is null and (last_used_at is null or last_used_at < now() - interval '5 minutes')
            """, new { appId, userId }, ct);
}
