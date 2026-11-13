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
    string[] RedirectUris, bool AllowGuests, string Status, DateTimeOffset? TokensRevokedAt, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, int Version)
{
    public bool Active => Status == "active";

    public AppView View => new(Id, ClientId, Kind, Name, Description, Publisher, Scopes, RedirectUris, AllowGuests, Status, CreatedAt, LastUsedAt, Version);
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
    TokenFamilies families)
{
    internal const string Columns =
        "id, client_id, kind, name, description, publisher, scopes, redirect_uris, allow_guests, status, tokens_revoked_at, created_at, last_used_at, version";

    internal static AppRecord Read(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetFieldValue<string[]>(6), r.GetFieldValue<string[]>(7),
        r.GetBoolean(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTimeOffset>(10), r.GetFieldValue<DateTimeOffset>(11),
        r.IsDBNull(12) ? null : r.GetFieldValue<DateTimeOffset>(12), r.GetInt32(13));

    private static CredentialView ReadCredential(Npgsql.NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
        r.GetFieldValue<DateTimeOffset>(3), r.GetFieldValue<DateTimeOffset>(4), r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));

    public async Task<IReadOnlyList<AppView>> ListAsync(CancellationToken ct)
    {
        await DemandAsync(Permissions.ApiView, ct);
        var rows = await session.QueryAsync($"select {Columns} from publicapi.app order by status, name, id", null, Read, ct);
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
        }
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
            insert into publicapi.app (tenant_id, id, client_id, kind, name, description, publisher, scopes, redirect_uris, allow_guests, created_by)
            values (platform.current_tenant_id(), @id, @client, @kind, @name, @description, @publisher, @scopes, @redirects, @guests, @by)
            """,
            new
            {
                id, client = ApiRules.NewClientId(), kind = request.Kind, name = request.Name!.Trim(), description = Blank(request.Description),
                publisher = Blank(request.Publisher), scopes = request.Scopes!.Distinct().ToArray(), redirects = (request.RedirectUris ?? []).Distinct().ToArray(),
                guests = request.AllowGuests ?? false, by = context.RequireUser(),
            }, ct);
        var app = (await FindAsync(id, ct))!;
        await audit.WriteAsync(new AuditEntry(AuditActions.ApiAppRegistered, "api_app", id.ToString(),
            Changes: new { app.Name, app.Kind, app.Scopes, app.RedirectUris, app.AllowGuests }), ct);
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
        if (request.Scopes is not null)
        {
            ApiRules.CheckScopes(request.Scopes, app.Kind, errors);
            await CheckPackageAsync(request.Scopes, errors, ct);
        }
        if (request.RedirectUris is not null)
        {
            ApiRules.CheckRedirectUris(request.RedirectUris, app.Kind, errors);
        }
        errors.ThrowIfAny();
        var next = app with
        {
            Name = request.Name?.Trim() ?? app.Name,
            Description = request.Description is null ? app.Description : Blank(request.Description),
            Publisher = request.Publisher is null ? app.Publisher : Blank(request.Publisher),
            Scopes = request.Scopes?.Distinct().ToArray() ?? app.Scopes,
            RedirectUris = request.RedirectUris?.Distinct().ToArray() ?? app.RedirectUris,
            AllowGuests = request.AllowGuests ?? app.AllowGuests,
        };
        await session.ExecAsync(
            """
            update publicapi.app set name = @Name, description = @Description, publisher = @Publisher, scopes = @Scopes, redirect_uris = @RedirectUris,
              allow_guests = @AllowGuests, version = version + 1, updated_at = now()
            where id = @Id
            """, new { next.Id, next.Name, next.Description, next.Publisher, next.Scopes, next.RedirectUris, next.AllowGuests }, ct);
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
                before = new { app.Name, app.Scopes, app.RedirectUris, app.AllowGuests },
                after = new { next.Name, next.Scopes, next.RedirectUris, next.AllowGuests },
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

    // Lookups for the authorization server and token checks: no permission needed, the tenant comes from the host.

    public Task<AppRecord?> FindByClientIdAsync(string clientId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from publicapi.app where client_id = @clientId", new { clientId }, Read, ct);

    public Task<AppRecord?> FindAsync(Guid id, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {Columns} from publicapi.app where id = @id", new { id }, Read, ct);

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
