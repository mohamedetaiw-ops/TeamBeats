using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.PublicApi.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.PublicApi;

/// <summary>An app registration as the authorization server reads it.</summary>
public sealed record AppRecord(Guid Id, string ClientId, string Kind, string Name, string? Description, string? Publisher, string[] Scopes,
    string[] RedirectUris, bool AllowGuests, string Status, DateTimeOffset? TokensRevokedAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, int Version,
    string[] IpRanges, int? CallsPerMinute, bool Verified = false)
{
    public bool Active => Status == "active";

    /// <summary>False for a client on the verified list that this tenant has no registration of yet (FR-023).</summary>
    public bool Provisioned => Id != Guid.Empty;

    /// <summary>The app's call limit: its own when the tenant lowered it, else the platform default for its kind.</summary>
    public int Limit => CallsPerMinute ?? ApiRules.DefaultCallsPerMinute(Kind);

    public AppView View => new(Id, ClientId, Kind, Name, Description, Publisher, Scopes, RedirectUris, AllowGuests, Status, CreatedAt, LastUsedAt, Version,
        IpRanges, Limit, CallsPerMinute is not null, Verified);
}

/// <summary>
/// App registrations and their credentials (spec 035 US1, FR-004, FR-005). API administrators register apps; adding a
/// credential needs step-up (on the endpoint) and the secret is shown once. App administration is tenant-wide in R2.
/// </summary>
public sealed class Apps(
    IDbSession session,
    TenantContextAccessor context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    TokenFamilies families,
    VerifiedMcpClients verified)
{
    // A verified MCP client's registration takes its name, publisher, scopes and redirect URIs from the platform's
    // verified list, and is disabled while the platform has blocked or removed it (FR-023).
    private const string Columns =
        """
        a.id, a.client_id, a.kind, case when a.verified then coalesce(v.name, a.name) else a.name end, a.description,
        case when a.verified then coalesce(v.publisher, a.publisher) else a.publisher end, case when a.verified then coalesce(v.scopes, '{}') else a.scopes end,
        case when a.verified then coalesce(v.redirect_uris, '{}') else a.redirect_uris end, a.allow_guests,
        case when a.verified and v.status is distinct from 'active' then 'disabled' else a.status end, a.tokens_revoked_at, a.created_at, a.last_used_at,
        a.version, a.ip_ranges, a.calls_per_minute, a.verified
        """;

    private const string From = "publicapi.app a left join publicapi.verified_mcp_client v on a.verified and v.client_id = a.client_id";

    internal static AppRecord Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<string[]>(6), r.GetFieldValue<string[]>(7),
        r.GetBoolean(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.GetInt32(13), r.GetFieldValue<string[]>(14), r.IsDBNull(15) ? null : r.GetInt32(15), r.GetBoolean(16));

    private static CredentialView ReadCredential(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));

    public async Task<IReadOnlyList<AppView>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiView, ct);
        var rows = await session.QueryAsync($"select {Columns} from {From} order by 10, 4, 1", null, Read, ct);
        return rows.Select(r => r.View).ToList();
    }

    public async Task<AppDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiView, ct);
        var app = await FindAsync(id, ct) ?? throw AppException.NotFound();
        return await DetailOfAsync(app, ct);
    }

    public async Task<AppView> CreateAsync(AppRequest request, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        var errors = ApiRules.Check(request, create: true);
        if (request.Kind is { } kind && AppKinds.All.Contains(kind))
        {
            ApiRules.CheckScopes(request.Scopes, kind, errors);
            ApiRules.CheckRedirectUris(request.RedirectUris, kind, errors);
            ApiRules.CheckCallsPerMinute(request.CallsPerMinute, kind, errors);
        }
        ApiRules.CheckIpRanges(request.IpRanges, errors);
        await CheckPackageAsync(request.Scopes, errors, ct);
        errors.ThrowIfAny();

        // The app limit of the package (section 9), counted under a tenant lock so two registrations cannot both pass it.
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':publicapi.apps', 0))", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from publicapi.app", null, ct);
        if (await entitlements.GetLimitAsync(Capabilities.ApiMaxApps, ct) is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.api.app_limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.ApiMaxApps, ["limit"] = max });
        }

        var id = Ids.New();
        await session.ExecAsync(
            """
            insert into publicapi.app (tenant_id, id, client_id, kind, name, description, publisher, scopes, redirect_uris, allow_guests, ip_ranges,
              calls_per_minute, created_by)
            values (platform.current_tenant_id(), @id, @client, @kind, @name, @description, @publisher, @scopes, @redirects, @guests, @ranges, @limit, @by)
            """,
            new
            {
                id, client = ApiRules.NewClientId(), kind = request.Kind, name = request.Name!.Trim(), description = Blank(request.Description),
                publisher = Blank(request.Publisher), scopes = request.Scopes!.Distinct().ToArray(), redirects = (request.RedirectUris ?? []).Distinct().ToArray(),
                guests = request.AllowGuests ?? false, ranges = ApiRules.NormalizeRanges(request.IpRanges ?? []),
                limit = request.CallsPerMinute is null or 0 ? (int?)null : request.CallsPerMinute, by = context.RequireUser(),
            }, ct);
        var app = (await FindAsync(id, ct))!;
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppRegistered, "api_app", id.ToString(),
            Changes: new { app.Name, app.Kind, app.Scopes, app.RedirectUris, app.AllowGuests, app.IpRanges, app.CallsPerMinute }), ct);
        await outbox.EnqueueAsync(new AppRegistered(id, app.Kind), ct);
        return app.View;
    }

    public async Task<AppView> UpdateAsync(Guid id, AppRequest request, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        var app = await FindAsync(id, ct) ?? throw AppException.NotFound();
        if (request.Version is { } version && version != app.Version)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        if (request.Kind is { } kind && kind != app.Kind)
        {
            throw AppException.Validation("kind", "validation.api.kind_fixed");
        }
        var errors = ApiRules.Check(request, create: false);
        if (app.Verified)
        {
            // The platform's verified list owns what the client is (FR-023); the tenant keeps its switches and limits.
            VerifiedFixed(errors, "name", request.Name is { } name && name.Trim() != app.Name);
            VerifiedFixed(errors, "publisher", request.Publisher is { } publisher && Blank(publisher) != app.Publisher);
            VerifiedFixed(errors, "scopes", request.Scopes is { } scopes && !scopes.ToHashSet(StringComparer.Ordinal).SetEquals(app.Scopes));
            VerifiedFixed(errors, "redirectUris", request.RedirectUris is { } uris && !uris.ToHashSet(StringComparer.Ordinal).SetEquals(app.RedirectUris));
            errors.ThrowIfAny();
        }
        if (request.Scopes is not null)
        {
            ApiRules.CheckScopes(request.Scopes, app.Kind, errors);
            await CheckPackageAsync(request.Scopes, errors, ct);
        }
        if (request.RedirectUris is not null)
        {
            ApiRules.CheckRedirectUris(request.RedirectUris, app.Kind, errors);
        }
        ApiRules.CheckIpRanges(request.IpRanges, errors);
        ApiRules.CheckCallsPerMinute(request.CallsPerMinute, app.Kind, errors);
        errors.ThrowIfAny();
        var next = app with
        {
            Name = request.Name?.Trim() ?? app.Name,
            Description = request.Description is null ? app.Description : Blank(request.Description),
            Publisher = request.Publisher is null ? app.Publisher : Blank(request.Publisher),
            Scopes = request.Scopes?.Distinct().ToArray() ?? app.Scopes,
            RedirectUris = request.RedirectUris?.Distinct().ToArray() ?? app.RedirectUris,
            AllowGuests = request.AllowGuests ?? app.AllowGuests,
            IpRanges = request.IpRanges is null ? app.IpRanges : ApiRules.NormalizeRanges(request.IpRanges),
            CallsPerMinute = request.CallsPerMinute switch { null => app.CallsPerMinute, 0 => null, var v => v },
        };
        await session.ExecAsync(
            """
            update publicapi.app set name = @Name, description = @Description, publisher = @Publisher, scopes = @Scopes, redirect_uris = @RedirectUris,
              allow_guests = @AllowGuests, ip_ranges = @IpRanges, calls_per_minute = @CallsPerMinute, version = version + 1, updated_at = now()
            where id = @Id
            """, new { next.Id, next.Name, next.Description, next.Publisher, next.Scopes, next.RedirectUris, next.AllowGuests, next.IpRanges, next.CallsPerMinute }, ct);
        // Scopes the app no longer holds leave every consent and token family at once (scopes only narrow, FR-009).
        var removed = app.Scopes.Except(next.Scopes, StringComparer.Ordinal).ToArray();
        if (removed.Length > 0)
        {
            await session.ExecAsync("update publicapi.consent set scopes = array(select unnest(scopes) except select unnest(@removed::text[])) where app_id = @id and revoked_at is null",
                new { id, removed }, ct);
            await families.RevokeForAppAsync(id, null, "admin", ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppChanged, "api_app", id.ToString(),
            Changes: new
            {
                before = new { app.Name, app.Scopes, app.RedirectUris, app.AllowGuests, app.IpRanges, app.CallsPerMinute },
                after = new { next.Name, next.Scopes, next.RedirectUris, next.AllowGuests, next.IpRanges, next.CallsPerMinute },
            }), ct);
        return (await FindAsync(id, ct))!.View;
    }

    /// <summary>Disables an app (US6): new tokens are refused at once and every token issued before now is refused too.</summary>
    public async Task<AppView> DisableAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        var app = await FindAsync(id, ct) ?? throw AppException.NotFound();
        if (!app.Active)
        {
            return app.View;
        }
        await session.ExecAsync("update publicapi.app set status = 'disabled', tokens_revoked_at = @now, version = version + 1, updated_at = now() where id = @id",
            new { id, now = clock.UtcNow }, ct);
        await families.RevokeForAppAsync(id, null, "app_disabled", ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppDisabled, "api_app", id.ToString(), Changes: new { app.Name, app.Kind }), ct);
        await outbox.EnqueueAsync(new AppDisabled(id), ct);
        return (await FindAsync(id, ct))!.View;
    }

    public async Task<AppView> EnableAsync(Guid id, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        var app = await FindAsync(id, ct) ?? throw AppException.NotFound();
        if (app.Active)
        {
            return app.View;
        }
        await session.ExecAsync("update publicapi.app set status = 'active', version = version + 1, updated_at = now() where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppChanged, "api_app", id.ToString(), Changes: new { status = "active" }), ct);
        return (await FindAsync(id, ct))!.View;
    }

    /// <summary>
    /// A new credential (FR-005): a client secret, or a public key for private key JWT. It lasts 6 months by default and
    /// at most 12, and an app has at most two active at a time, so a second one can be added before the first is revoked.
    /// </summary>
    public async Task<IssuedCredential> AddCredentialAsync(Guid id, CredentialRequest request, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        var app = await FindAsync(id, ct) ?? throw AppException.NotFound();
        if (!AppKinds.IsConfidential(app.Kind))
        {
            throw AppException.Validation("kind", "validation.api.credential_not_for_kind");
        }
        var months = request.ExpiresInMonths ?? ApiRules.DefaultCredentialMonths;
        if (months is < 1 or > ApiRules.MaxCredentialMonths)
        {
            throw AppException.Validation("expiresInMonths", "validation.api.credential_expiry");
        }
        var kind = request.Kind ?? "secret";
        (string Json, string Thumbprint)? key = null;
        if (kind == "public_key")
        {
            key = ClientKeys.Parse(request.PublicKey, out var keyError);
            if (key is null)
            {
                throw AppException.Validation("publicKey", keyError!);
            }
        }
        else if (kind != "secret")
        {
            throw AppException.Validation("kind", "validation.api.credential_kind");
        }
        // Counted under the app's row lock, so two credentials added at once cannot both pass the limit.
        await session.ExecAsync("select 1 from publicapi.app where id = @id for update", new { id }, ct);
        var active = await session.ScalarAsync<long>("select count(*) from publicapi.app_credential where app_id = @id and revoked_at is null and expires_at > @now",
            new { id, now = clock.UtcNow }, ct);
        if (active >= ApiRules.MaxActiveCredentials)
        {
            throw AppException.Conflict("conflict.api.credential_limit");
        }
        if (key is { } k && await session.ScalarAsync<bool>(
                "select exists (select 1 from publicapi.app_credential where app_id = @id and kind = 'public_key' and hint = @hint and revoked_at is null)",
                new { id, hint = k.Thumbprint }, ct))
        {
            throw AppException.Conflict("conflict.api.public_key_registered");
        }
        var secret = kind == "secret" ? ApiRules.NewSecret() : null;
        var credentialId = Ids.New();
        var expires = clock.UtcNow.AddMonths(months);
        await session.ExecAsync(
            """
            insert into publicapi.app_credential (tenant_id, id, app_id, kind, secret_hash, public_jwk, hint, expires_at, created_by)
            values (platform.current_tenant_id(), @credentialId, @id, @kind, @hash, @jwk, @hint, @expires, @by)
            """, new
            {
                credentialId, id, kind, hash = secret is null ? null : ApiRules.HashSecret(secret), jwk = key?.Json,
                hint = secret is null ? key!.Value.Thumbprint : secret[^4..], expires, by = context.RequireUser(),
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiCredentialAdded, "api_app", id.ToString(),
            Changes: new { credential = credentialId, kind, thumbprint = key?.Thumbprint, expiresAt = expires }), ct);
        var view = await session.QuerySingleOrDefaultAsync(
            "select id, kind, hint, expires_at, created_at, last_used_at, revoked_at from publicapi.app_credential where id = @credentialId",
            new { credentialId }, ReadCredential, ct);
        return new IssuedCredential(view!, secret);
    }

    /// <summary>Revokes a credential: it is refused at once, and so are the application tokens issued with it.</summary>
    public async Task<CredentialView> RevokeCredentialAsync(Guid id, Guid credentialId, CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiManage, ct);
        _ = await FindAsync(id, ct) ?? throw AppException.NotFound();
        var changed = await session.ExecAsync(
            "update publicapi.app_credential set revoked_at = now(), revoked_by = @by where id = @credentialId and app_id = @id and revoked_at is null",
            new { id, credentialId, by = context.RequireUser() }, ct);
        var view = await session.QuerySingleOrDefaultAsync(
            "select id, kind, hint, expires_at, created_at, last_used_at, revoked_at from publicapi.app_credential where id = @credentialId and app_id = @id",
            new { credentialId, id }, ReadCredential, ct) ?? throw AppException.NotFound();
        if (changed > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ApiCredentialRevoked, "api_app", id.ToString(), Changes: new { credential = credentialId }), ct);
            await outbox.EnqueueAsync(new AppCredentialRevoked(id, credentialId), ct);
        }
        return view;
    }

    private static void VerifiedFixed(ValidationErrors errors, string field, bool changed)
    {
        if (changed)
        {
            errors.Add(field, "validation.api.verified_app_fixed");
        }
    }

    // Lookups for the authorization server and token checks: no permission needed, the tenant comes from the host.

    /// <summary>
    /// The app an authorization request names: the tenant's registration, or a client on the platform's verified list
    /// that the tenant has not registered yet (FR-023), returned unprovisioned. An unlisted URL is not a client.
    /// </summary>
    public async Task<AppRecord?> ResolveAsync(string clientId, CancellationToken ct)
    {
        if (await FindByClientIdAsync(clientId, ct) is { } app)
        {
            return app;
        }
        return await verified.FindAsync(clientId, ct) is { Active: true } listed
            ? new AppRecord(Guid.Empty, listed.ClientId, AppKinds.McpClient, listed.Name, null, listed.Publisher, listed.Scopes, listed.RedirectUris, false,
                "active", null, clock.UtcNow, null, 0, [], null, Verified: true)
            : null;
    }

    /// <summary>
    /// Registers a verified MCP client for the tenant when the first employee connects it, after the AI policy allowed it
    /// (spec 035 US3 scenario 1). It counts against the package's app limit; null when the limit is reached.
    /// </summary>
    public async Task<AppRecord?> ProvisionVerifiedAsync(AppRecord pending, Guid user, CancellationToken ct)
    {
        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':publicapi.apps', 0))", null, ct);
        if (await FindByClientIdAsync(pending.ClientId, ct) is { } existing)
        {
            return existing;
        }
        var count = await session.ScalarAsync<long>("select count(*) from publicapi.app", null, ct);
        if (await entitlements.GetLimitAsync(Capabilities.ApiMaxApps, ct) is { } max && count >= max)
        {
            return null;
        }
        var id = Ids.New();
        // The values are kept as a record of what was connected; the verified list stays the authority.
        await session.ExecAsync(
            """
            insert into publicapi.app (tenant_id, id, client_id, kind, name, publisher, scopes, redirect_uris, verified, created_by)
            values (platform.current_tenant_id(), @id, @client, 'mcp_client', @name, @publisher, @scopes, @redirects, true, @user)
            """,
            new { id, client = pending.ClientId, name = pending.Name, publisher = pending.Publisher, scopes = pending.Scopes, redirects = pending.RedirectUris, user }, ct);
        var app = (await FindAsync(id, ct))!;
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppRegistered, "api_app", id.ToString(),
            Changes: new { app.Name, app.Kind, app.ClientId, app.Scopes, app.RedirectUris, verified = true }), ct);
        await outbox.EnqueueAsync(new AppRegistered(id, app.Kind), ct);
        return app;
    }


    public Task<AppRecord?> FindByClientIdAsync(string clientId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from {From} where a.client_id = @clientId", new { clientId }, Read, ct);

    public Task<AppRecord?> FindAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from {From} where a.id = @id", new { id }, Read, ct);

    /// <summary>The live credential the secret belongs to, if any; its last use is recorded.</summary>
    public async Task<Guid?> VerifySecretAsync(Guid appId, string? secret, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(secret))
        {
            return null;
        }
        var rows = await session.QueryAsync(
            "select id, secret_hash from publicapi.app_credential where app_id = @appId and kind = 'secret' and revoked_at is null and expires_at > @now",
            new { appId, now = clock.UtcNow }, r => (Id: r.GetGuid(0), Hash: r.GetString(1)), ct);
        foreach (var row in rows)
        {
            if (ApiRules.VerifySecret(secret, row.Hash))
            {
                await session.ExecAsync("update publicapi.app_credential set last_used_at = now() where id = @id", new { id = row.Id }, ct);
                return row.Id;
            }
        }
        return null;
    }

    /// <summary>
    /// The live public key credential a client assertion was signed with (RFC 7523), if any. Each assertion is good
    /// once: its JWT ID is kept until it would have expired, and a second use is refused.
    /// </summary>
    public async Task<Guid?> VerifyAssertionAsync(AppRecord app, string? assertion, string issuer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(assertion))
        {
            return null;
        }
        var now = clock.UtcNow;
        var keys = await session.QueryAsync(
            "select id, public_jwk from publicapi.app_credential where app_id = @appId and kind = 'public_key' and revoked_at is null and expires_at > @now",
            new { appId = app.Id, now }, r => (r.GetGuid(0), r.GetString(1)), ct);
        if (await ClientKeys.ValidateAsync(assertion, app.ClientId, issuer, keys, now) is not { } valid)
        {
            return null;
        }
        await session.ExecAsync("delete from publicapi.client_assertion where app_id = @appId and expires_at < @now", new { appId = app.Id, now }, ct);
        var fresh = await session.ExecAsync(
            """
            insert into publicapi.client_assertion (tenant_id, app_id, jti, expires_at) values (platform.current_tenant_id(), @appId, @jti, @expires)
            on conflict do nothing
            """, new { appId = app.Id, jti = valid.Jti, expires = valid.Expires }, ct);
        if (fresh == 0)
        {
            return null;
        }
        await session.ExecAsync("update publicapi.app_credential set last_used_at = now() where id = @id", new { id = valid.Credential }, ct);
        return valid.Credential;
    }

    public Task<bool> CredentialLiveAsync(Guid appId, Guid credentialId, CancellationToken ct) =>
        session.ScalarAsync<bool>(
            "select exists (select 1 from publicapi.app_credential where id = @credentialId and app_id = @appId and revoked_at is null and expires_at > @now)",
            new { appId, credentialId, now = clock.UtcNow }, ct);

    public Task TouchAsync(Guid appId, CancellationToken ct) =>
        session.ExecAsync("update publicapi.app set last_used_at = now() where id = @appId and (last_used_at is null or last_used_at < now() - interval '5 minutes')",
            new { appId }, ct);

    internal async Task<AppDetail> DetailOfAsync(AppRecord app, CancellationToken ct)
    {
        var credentials = await session.QueryAsync(
            "select id, kind, hint, expires_at, created_at, last_used_at, revoked_at from publicapi.app_credential where app_id = @id order by created_at desc",
            new { id = app.Id }, ReadCredential, ct);
        var admin = await session.QuerySingleOrDefaultAsync(
            "select scopes from publicapi.consent where app_id = @id and user_id is null and revoked_at is null", new { id = app.Id },
            r => r.GetFieldValue<string[]>(0), ct);
        return new AppDetail(app.View, credentials, admin ?? []);
    }

    /// <summary>Scopes whose capability keys are not in the tenant's package cannot be registered (section 9).</summary>
    private async Task CheckPackageAsync(IReadOnlyList<string>? scopes, ValidationErrors errors, CancellationToken ct)
    {
        foreach (var capability in (scopes ?? []).Select(ScopeCatalogue.Find).OfType<ScopeDefinition>().SelectMany(s => s.Capabilities).Distinct())
        {
            if (!await entitlements.IsEnabledAsync(capability, ct))
            {
                errors.Add("scopes", "validation.api.scope_not_in_package");
                return;
            }
        }
    }

    private async Task DemandAsync(string permission, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ApiPublic, ct);
        await authz.DemandAsync(permission, AuthzTarget.Tenant, ct);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
