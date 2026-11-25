using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Security;

/// <summary>Where the tenant's customer-managed key stands after the last check (spec 026 US8).</summary>
public static class KeyStatuses
{
    /// <summary>The key answered: the data key unwraps.</summary>
    public const string Active = "active";

    /// <summary>The key service did not answer. The tenant stays available, for at most <see cref="TenantKeyService.UnreachableLimit"/>.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The key service refused the key: the tenant is unavailable until it answers again. Nothing is deleted.</summary>
    public const string Revoked = "revoked";
}

/// <summary>A key connection to make or change. <paramref name="SecretRef"/> is a secret manager reference.</summary>
public sealed record TenantKeyRequest(string? Provider, string? KeyUri, string? SecretRef, string? DirectoryId = null, string? ClientId = null);

/// <summary>The key page: the connection, if any, what the last check found, and a change waiting for approval.</summary>
public sealed record TenantKeyView(
    bool Connected,
    string? Provider,
    string? KeyUri,
    string? DirectoryId,
    string? ClientId,
    string? SecretRef,
    string? KeyVersion,
    string? Status,
    DateTimeOffset? StatusSince,
    DateTimeOffset? CheckedAt,
    DateTimeOffset? LastOkAt,
    string? LastError,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? RotatedAt,
    bool CanManage,
    PendingChangeView? PendingChange = null);

/// <summary>
/// The key status of each tenant for the request gate, read under that tenant with a 30 second cache so the gate stays
/// cheap; a check or change here clears the entry.
/// </summary>
public sealed class TenantKeyStore(IDbSession session, TenantContextAccessor context, IMemoryCache cache)
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private static string Key(Guid tenant) => $"security.key_status:{tenant}";

    public async Task<string?> StatusAsync(Guid tenant, CancellationToken ct)
    {
        if (cache.TryGetValue(Key(tenant), out string? cached))
        {
            return cached;
        }
        var previous = context.TenantId;
        context.SetTenant(tenant);
        try
        {
            var status = await session.ScalarAsync<string?>("select status from security.tenant_key", null, ct);
            cache.Set(Key(tenant), status, CacheFor);
            return status;
        }
        finally
        {
            context.SetTenant(previous);
        }
    }

    public void Forget(Guid tenant) => cache.Remove(Key(tenant));
}

/// <summary>
/// Customer-managed keys (spec 026 US8, FR-007). The tenant connects a key in Azure Key Vault or a Vault transit engine;
/// the tenant's data key (256 random bits, made once) is wrapped with it, and only the wrapped form is stored. A check
/// every 5 minutes unwraps it: a refusal by the key service makes the tenant unavailable (the request gate answers 503)
/// until the key answers again, a new key version is wrapped onto at once (rotation without downtime), and the plaintext
/// data key is held in memory for at most 15 minutes, so a revocation takes effect within 15 minutes on every instance.
/// Changing the key keeps the same data key, unwrapped with the old key and wrapped with the new one. Connecting and
/// changing need <c>security.manage</c>, step-up, the Enterprise and Government capability, and four-eyes when on.
/// </summary>
public sealed class TenantKeyService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IKeyManagement keys,
    IFourEyes fourEyes,
    IAuditWriter audit,
    AlertService alerts,
    TenantKeyStore store,
    IMemoryCache cache,
    IClock clock) : ITenantDataKeys
{
    public const int DataKeyBytes = 32;
    public static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan HoldDataKeyFor = TimeSpan.FromMinutes(15);

    /// <summary>A key service that has not answered for this long counts as revoked.</summary>
    public static readonly TimeSpan UnreachableLimit = TimeSpan.FromHours(24);

    private static string DataKeyEntry(Guid tenant) => $"security.data_key:{tenant}";

    private sealed record Row(
        KeyReference Key, Guid DataKeyId, string Version, byte[] Wrapped, string Status, DateTimeOffset StatusSince, DateTimeOffset CheckedAt,
        DateTimeOffset LastOkAt, string? LastError, DateTimeOffset ConnectedAt, DateTimeOffset? RotatedAt);

    private Task<Row?> ReadAsync(CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            """
            select provider, key_uri, secret_ref, directory_id, client_id, data_key_id, key_version, wrapped_data_key, status, status_since, checked_at,
                   last_ok_at, last_error, connected_at, rotated_at
            from security.tenant_key
            """, null,
            r => new Row(new KeyReference(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)),
                r.GetGuid(5), r.GetString(6), r.GetFieldValue<byte[]>(7), r.GetString(8), r.GetFieldValue<DateTimeOffset>(9), r.GetFieldValue<DateTimeOffset>(10),
                r.GetFieldValue<DateTimeOffset>(11), r.IsDBNull(12) ? null : r.GetString(12), r.GetFieldValue<DateTimeOffset>(13),
                r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14)), ct);

    private async Task<bool> CanAsync(string permission, CancellationToken ct) => (await authz.CheckAsync(permission, AuthzTarget.Tenant, ct)).Allowed;

    public async Task<TenantKeyView> GetAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SecurityCustomerKeys, ct);
        var manage = await CanAsync(Permissions.SecurityManage, ct);
        if (!manage && !await CanAsync(Permissions.SecurityView, ct))
        {
            throw AppException.Denied();
        }
        var row = await ReadAsync(ct);
        var pending = await fourEyes.PendingAsync(FourEyesKinds.Keys, ct);
        return row is null
            ? new TenantKeyView(false, null, null, null, null, null, null, null, null, null, null, null, null, null, manage, pending)
            : new TenantKeyView(true, row.Key.Provider, row.Key.KeyUri, row.Key.DirectoryId, row.Key.ClientId, row.Key.SecretRef, row.Version, row.Status,
                row.StatusSince, row.CheckedAt, row.LastOkAt, row.LastError, row.ConnectedAt, row.RotatedAt, manage, pending);
    }

    /// <summary>Connects the key, or moves the data key to another key. Answers the page, with the pending change when held.</summary>
    public async Task<TenantKeyView> SaveAsync(TenantKeyRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.SecurityCustomerKeys, ct);
        await authz.DemandAsync(Permissions.SecurityManage, AuthzTarget.Tenant, ct);
        var key = await keys.ValidateAsync(new KeyReference(request.Provider ?? "", request.KeyUri ?? "", request.SecretRef, request.DirectoryId, request.ClientId), ct);
        var current = await ReadAsync(ct);
        var fields = Changed(current?.Key, key);
        if (fields.Count == 0)
        {
            return await GetAsync(ct);
        }
        var normalized = new TenantKeyRequest(key.Provider, key.KeyUri, key.SecretRef, key.DirectoryId, key.ClientId);
        // Spec 026 FR-016: key changes are a critical setting.
        if (await fourEyes.HoldAsync(FourEyesKinds.Keys, normalized, fields, ct) is { } held)
        {
            return await GetAsync(ct) with { PendingChange = held };
        }

        var tenant = context.RequireTenant();
        var now = clock.UtcNow;
        byte[] dataKey;
        Guid dataKeyId;
        if (current is null)
        {
            dataKey = RandomNumberGenerator.GetBytes(DataKeyBytes);
            dataKeyId = Ids.New();
        }
        else
        {
            // The same data key moves to the new key, so nothing encrypted with it has to change.
            var old = await keys.UnwrapAsync(current.Key, current.Version, current.Wrapped, ct);
            if (old.Access != KeyAccess.Ok || old.Data is not { Length: DataKeyBytes })
            {
                throw AppException.Conflict("conflict.keys.current_unavailable");
            }
            dataKey = old.Data;
            dataKeyId = current.DataKeyId;
        }
        try
        {
            var wrapped = await keys.WrapAsync(key, dataKey, ct);
            var back = wrapped.Access == KeyAccess.Ok ? await keys.UnwrapAsync(key, wrapped.Version!, wrapped.Data!, ct) : wrapped;
            if (wrapped.Access != KeyAccess.Ok || back.Access != KeyAccess.Ok || back.Data is null || !CryptographicOperations.FixedTimeEquals(back.Data, dataKey))
            {
                // The key must both wrap and unwrap before the tenant depends on it.
                throw AppException.Validation("keyUri", $"validation.keys.{back.ErrorClass ?? wrapped.ErrorClass ?? "invalid_response"}");
            }
            await session.ExecAsync(
                """
                insert into security.tenant_key (tenant_id, provider, key_uri, directory_id, client_id, secret_ref, data_key_id, key_version, wrapped_data_key,
                  status, status_since, checked_at, last_ok_at, connected_at, connected_by)
                values (platform.current_tenant_id(), @provider, @uri, @directory, @client, @secret, @dataKeyId, @version, @wrapped,
                  'active', @now, @now, @now, @now, @actor)
                on conflict (tenant_id) do update set provider = excluded.provider, key_uri = excluded.key_uri, directory_id = excluded.directory_id,
                  client_id = excluded.client_id, secret_ref = excluded.secret_ref, key_version = excluded.key_version, wrapped_data_key = excluded.wrapped_data_key,
                  status = 'active', status_since = case when security.tenant_key.status = 'active' then security.tenant_key.status_since else @now end,
                  checked_at = @now, last_ok_at = @now, last_error = null, connected_at = @now, connected_by = @actor
                """,
                new
                {
                    provider = key.Provider, uri = key.KeyUri, directory = key.DirectoryId, client = key.ClientId, secret = key.SecretRef, dataKeyId,
                    version = wrapped.Version, wrapped = wrapped.Data, now, actor = context.ActorId,
                }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.SecurityKeyConnected, "tenant_key", tenant.ToString(), Changes: new
            {
                provider = key.Provider, keyUri = key.KeyUri, keyVersion = wrapped.Version, fields,
                previous = current is null ? null : new { provider = current.Key.Provider, keyUri = current.Key.KeyUri, keyVersion = current.Version },
            }), ct);
            cache.Set(DataKeyEntry(tenant), new TenantDataKey(dataKeyId, dataKey), HoldDataKeyFor);
            store.Forget(tenant);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }
        return await GetAsync(ct);
    }

    private static List<string> Changed(KeyReference? current, KeyReference next)
    {
        var fields = new List<string>();
        if (current?.Provider != next.Provider) fields.Add("provider");
        if (current?.KeyUri != next.KeyUri) fields.Add("keyUri");
        if (current?.SecretRef != next.SecretRef) fields.Add("secretRef");
        if (current?.DirectoryId != next.DirectoryId) fields.Add("directoryId");
        if (current?.ClientId != next.ClientId) fields.Add("clientId");
        return fields;
    }

    /// <summary>
    /// Checks the key for the current tenant: unwraps the data key, wraps it onto a new key version, and records a
    /// revocation, a restore or an outage, each audited once. Answers the status, or null without a key.
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        var row = await ReadAsync(ct);
        if (row is null)
        {
            return null;
        }
        var tenant = context.RequireTenant();
        var now = clock.UtcNow;
        var unwrapped = await keys.UnwrapAsync(row.Key, row.Version, row.Wrapped, ct);
        if (unwrapped.Access == KeyAccess.Ok && unwrapped.Data is { Length: DataKeyBytes } dataKey)
        {
            var version = row.Version;
            var wrapped = row.Wrapped;
            DateTimeOffset? rotated = row.RotatedAt;
            var latest = await keys.CurrentVersionAsync(row.Key, ct);
            if (latest.Access == KeyAccess.Ok && latest.Version is { } newer && newer != row.Version)
            {
                var rewrapped = await keys.WrapAsync(row.Key, dataKey, ct);
                if (rewrapped.Access == KeyAccess.Ok && rewrapped.Data is not null)
                {
                    await audit.WriteAsync(new AuditEntry(AuditActions.SecurityKeyRotated, "tenant_key", tenant.ToString(), Changes: new
                    {
                        keyUri = row.Key.KeyUri, from = row.Version, to = rewrapped.Version,
                    }), ct);
                    version = rewrapped.Version!;
                    wrapped = rewrapped.Data;
                    rotated = now;
                }
            }
            if (row.Status != KeyStatuses.Active)
            {
                await audit.WriteAsync(new AuditEntry(AuditActions.SecurityKeyRestored, "tenant_key", tenant.ToString(), Changes: new
                {
                    keyUri = row.Key.KeyUri, was = row.Status, since = row.StatusSince,
                }), ct);
            }
            await session.ExecAsync(
                """
                update security.tenant_key set key_version = @version, wrapped_data_key = @wrapped, rotated_at = @rotated, status = 'active',
                  status_since = case when status = 'active' then status_since else @now end, checked_at = @now, last_ok_at = @now, last_error = null
                """, new { version, wrapped, rotated, now }, ct);
            cache.Set(DataKeyEntry(tenant), new TenantDataKey(row.DataKeyId, dataKey), HoldDataKeyFor);
            store.Forget(tenant);
            return KeyStatuses.Active;
        }

        var error = unwrapped.ErrorClass ?? "invalid_response";
        var revoked = unwrapped.Access == KeyAccess.Denied || now - row.LastOkAt >= UnreachableLimit;
        var status = revoked ? KeyStatuses.Revoked : KeyStatuses.Unreachable;
        if (status != row.Status)
        {
            await audit.WriteAsync(new AuditEntry(revoked ? AuditActions.SecurityKeyRevoked : AuditActions.SecurityKeyUnreachable, "tenant_key", tenant.ToString(),
                AuditOutcome.Failure, Changes: new { keyUri = row.Key.KeyUri, errorClass = error, lastOkAt = row.LastOkAt }), ct);
            await alerts.RaiseAsync(revoked ? AlertKinds.KeyRevoked : AlertKinds.KeyUnreachable, null, new { keyUri = row.Key.KeyUri, errorClass = error },
                $"{(revoked ? AlertKinds.KeyRevoked : AlertKinds.KeyUnreachable)}:{now:O}", ct);
        }
        await session.ExecAsync(
            """
            update security.tenant_key set status = @status, status_since = case when status = @status then status_since else @now end, checked_at = @now,
              last_error = @error
            """, new { status, now, error }, ct);
        if (revoked)
        {
            cache.Remove(DataKeyEntry(tenant));
        }
        store.Forget(tenant);
        return status;
    }

    public async Task<TenantDataKey?> CurrentAsync(CancellationToken ct = default)
    {
        var tenant = context.RequireTenant();
        if (cache.TryGetValue(DataKeyEntry(tenant), out TenantDataKey? held) && held is not null)
        {
            return held;
        }
        var row = await ReadAsync(ct);
        if (row is null)
        {
            return null;
        }
        if (row.Status == KeyStatuses.Revoked)
        {
            throw Unavailable();
        }
        var unwrapped = await keys.UnwrapAsync(row.Key, row.Version, row.Wrapped, ct);
        if (unwrapped.Access != KeyAccess.Ok || unwrapped.Data is not { Length: DataKeyBytes })
        {
            throw Unavailable();
        }
        var key = new TenantDataKey(row.DataKeyId, unwrapped.Data);
        cache.Set(DataKeyEntry(tenant), key, HoldDataKeyFor);
        return key;
    }

    public async Task<TenantDataKey?> FindAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            return await CurrentAsync(ct) is { } key && key.Id == id ? key : null;
        }
        catch (AppException ex) when (ex.Code == ProblemCodes.TenantKeyUnavailable)
        {
            return null;
        }
    }

    public static AppException Unavailable() => new(ProblemCodes.TenantKeyUnavailable, 503);
}

/// <summary>Applies a held key change once another administrator approves it (spec 026 FR-016).</summary>
public sealed class TenantKeyApplier(TenantKeyService keys) : IFourEyesApplier
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Kind => FourEyesKinds.Keys;

    public string Permission => Permissions.SecurityManage;

    public async Task ApplyAsync(JsonElement request, CancellationToken ct) => await keys.SaveAsync(request.Deserialize<TenantKeyRequest>(Json)!, ct);
}

/// <summary>
/// While the tenant's key is revoked, every request to the tenant answers 503 <c>tenant.key_unavailable</c>, except the
/// availability notice the sign-in page reads and the health checks (spec 026 US8 scenario 2). It runs after the network
/// rules and before any module code; it comes back on its own once a check finds the key again.
/// </summary>
public static class TenantKeyGate
{
    public const string AvailabilityPath = "/api/v1/site/availability";

    public static IApplicationBuilder UseTenantKeys(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var services = http.RequestServices;
            var context = services.GetRequiredService<TenantContextAccessor>();
            if (context.Host.Kind != HostScopeKind.Tenant || context.Host.Tenant is not { } tenant
                || http.Request.Path.StartsWithSegments(AvailabilityPath) || http.Request.Path.StartsWithSegments("/health"))
            {
                await next(http);
                return;
            }
            if (await services.GetRequiredService<TenantKeyStore>().StatusAsync(tenant.Id, http.RequestAborted) == KeyStatuses.Revoked)
            {
                throw TenantKeyService.Unavailable();
            }
            await next(http);
        });
}

/// <summary>Checks the customer-managed key of every active tenant that has one, every 5 minutes (spec 026 US8).</summary>
public sealed class TenantKeyWorker(IServiceScopeFactory scopes, ILogger<TenantKeyWorker> logger) : BackgroundService
{
    public const string InProcessKey = "Security:Keys:InProcess";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(TenantKeyService.CheckEvery, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<TenantInfo> tenants;
        try
        {
            await using var listScope = scopes.CreateAsyncScope();
            tenants = await listScope.ServiceProvider.GetRequiredService<ITenantLister>().ListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Key checks could not list tenants");
            return;
        }
        foreach (var tenant in tenants.Where(t => t.Status == TenantLifecycleStatus.Active))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var context = scope.ServiceProvider.GetRequiredService<TenantContextAccessor>();
                context.SetTenant(tenant.Id);
                context.SetActor(ActorType.System, null);
                await scope.ServiceProvider.GetRequiredService<TenantKeyService>().CheckAsync(ct);
                await scope.ServiceProvider.GetRequiredService<IDbSession>().CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Key check failed for tenant {TenantId}", tenant.Id);
            }
        }
    }
}
