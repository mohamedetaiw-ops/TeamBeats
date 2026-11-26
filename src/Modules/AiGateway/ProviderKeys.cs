using Microsoft.Extensions.Configuration;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.AiGateway;

public sealed record ProviderKeyRequest(string? SecretRef);

/// <summary>
/// A tenant's own provider key as the page shows it (spec 013 FR-020): the secret manager reference and the last test,
/// never the key. <see cref="Offered"/> is false in shared installs, where the setting does not exist.
/// </summary>
public sealed record ProviderKeyView(bool Offered, bool Editable, string Provider, string? SecretRef, DateTimeOffset? LastTestAt, bool? LastTestPassed,
    bool InUse, DateTimeOffset? SetAt);

/// <summary>Checks that a provider accepts a key. Implemented by the provider's adapter.</summary>
public interface IProviderKeyProbe
{
    string Provider { get; }

    /// <summary>Null when the key works; otherwise the resource key of what failed.</summary>
    Task<string?> TestAsync(string apiKey, CancellationToken ct);
}

/// <summary>
/// Tenant provider keys (spec 013 R3, FR-020). Only installs that set <c>Ai:TenantProviderKeys</c> (dedicated and
/// on-premises) offer them. The AI Policy Administrator saves a secret manager reference of the tenant to its own
/// OpenRouter key; calls use it only after a test passed, and then instead of the platform key. The key is read from the
/// secret manager at call time and never returned, logged or cached across tenants.
/// </summary>
public sealed class ProviderKeys(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    ISecretStore secrets,
    IEnumerable<IProviderKeyProbe> probes,
    IAuditWriter audit,
    IClock clock,
    IConfiguration configuration)
{
    public const string AllowedSetting = "Ai:TenantProviderKeys";
    public const string OpenRouter = "openrouter";

    private string? key;
    private bool loaded;

    public bool Offered => configuration.GetValue<bool>(AllowedSetting);

    private sealed record Row(string SecretRef, DateTimeOffset? LastTestAt, bool? LastTestPassed, DateTimeOffset SetAt);

    private Task<Row?> RowAsync(string provider, CancellationToken ct, bool forUpdate = false) =>
        session.QuerySingleOrDefaultAsync(
            "select secret_ref, last_test_at, last_test_passed, set_at from ai.provider_key where provider = @provider" + (forUpdate ? " for update" : string.Empty),
            new { provider }, r => new Row(r.GetString(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetBoolean(2),
                r.GetFieldValue<DateTimeOffset>(3)), ct);

    private static void Known(string provider)
    {
        if (provider != OpenRouter)
        {
            throw AppException.NotFound();
        }
    }

    private async Task<bool> CanManageAsync(CancellationToken ct) => (await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed;

    private async Task DemandManageAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        if (!Offered)
        {
            throw AppException.Conflict("conflict.ai.provider_keys_not_offered");
        }
    }

    public async Task<ProviderKeyView> GetAsync(string provider, CancellationToken ct)
    {
        Known(provider);
        var manage = await CanManageAsync(ct);
        if (!manage && !(await authz.CheckAsync(Permissions.AiPolicyView, AuthzTarget.Tenant, ct)).Allowed)
        {
            throw AppException.Denied();
        }
        var row = Offered ? await RowAsync(provider, ct) : null;
        return new ProviderKeyView(Offered, Offered && manage, provider, row?.SecretRef, row?.LastTestAt, row?.LastTestPassed, row?.LastTestPassed == true, row?.SetAt);
    }

    /// <summary>Saves the reference. A new or changed reference is untested, so calls keep the platform key until a test passes.</summary>
    public async Task<ProviderKeyView> PutAsync(string provider, ProviderKeyRequest request, CancellationToken ct)
    {
        Known(provider);
        await DemandManageAsync(ct);
        var reference = request.SecretRef?.Trim();
        if (SecretReferences.NameFor(context.RequireTenant(), reference) is null)
        {
            var errors = new ValidationErrors();
            errors.Add("secretRef", "validation.ai.provider_key_secret");
            errors.ThrowIfAny();
        }
        var current = await RowAsync(provider, ct, forUpdate: true);
        if (current?.SecretRef != reference)
        {
            await session.ExecAsync(
                """
                insert into ai.provider_key (tenant_id, provider, secret_ref, set_by, set_at) values (platform.current_tenant_id(), @provider, @reference, @by, @now)
                on conflict (tenant_id, provider) do update set secret_ref = @reference, last_test_at = null, last_test_passed = null, set_by = @by, set_at = @now
                """, new { provider, reference, by = context.RequireUser(), now = clock.UtcNow }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.AiProviderKeySet, "ai_provider_key", provider, Changes: new { secretRef = reference, replaced = current is not null }), ct);
        }
        return await GetAsync(provider, ct);
    }

    /// <summary>Asks the provider whether it accepts the key now. A failed test stops calls using it.</summary>
    public async Task<ProviderKeyView> TestAsync(string provider, CancellationToken ct)
    {
        Known(provider);
        await DemandManageAsync(ct);
        var row = await RowAsync(provider, ct, forUpdate: true) ?? throw AppException.NotFound();
        var value = await secrets.GetTenantSecretAsync(context.RequireTenant(), row.SecretRef, ct);
        var failure = value is null
            ? "ai.provider_key.checks.secret_missing"
            : probes.FirstOrDefault(p => p.Provider == provider) is { } probe ? await probe.TestAsync(value, ct) : "ai.provider_key.checks.no_adapter";
        var passed = failure is null;
        await session.ExecAsync("update ai.provider_key set last_test_at = @now, last_test_passed = @passed where provider = @provider",
            new { provider, now = clock.UtcNow, passed }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AiProviderKeyTested, "ai_provider_key", provider, Changes: new { passed, failure }), ct);
        return await GetAsync(provider, ct);
    }

    /// <summary>Removes the reference; calls go back to the platform key. The secret itself stays in the secret manager.</summary>
    public async Task DeleteAsync(string provider, CancellationToken ct)
    {
        Known(provider);
        await authz.DemandAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct);
        var removed = await session.ExecAsync("delete from ai.provider_key where provider = @provider", new { provider }, ct);
        if (removed > 0)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AiProviderKeyRemoved, "ai_provider_key", provider), ct);
        }
    }

    /// <summary>
    /// The tenant's tested OpenRouter key for this request, read once from the secret manager; null in shared installs,
    /// without a passed test, or when the secret is gone (then the platform key is used).
    /// </summary>
    internal async Task<string?> ActiveAsync(CancellationToken ct)
    {
        if (loaded)
        {
            return key;
        }
        loaded = true;
        if (!Offered)
        {
            return null;
        }
        var row = await RowAsync(OpenRouter, ct);
        key = row?.LastTestPassed == true ? await secrets.GetTenantSecretAsync(context.RequireTenant(), row!.SecretRef, ct) : null;
        return key;
    }
}
