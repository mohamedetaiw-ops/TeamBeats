namespace TeamBeats.TenantContext;

public enum HostScopeKind
{
    Unknown,
    Platform,
    Tenant,
}

/// <summary>What the request host says about scope. A host is a routing hint, never proof of access.</summary>
public sealed record HostScope(HostScopeKind Kind, TenantInfo? Tenant)
{
    public static readonly HostScope Unknown = new(HostScopeKind.Unknown, null);
    public static readonly HostScope Platform = new(HostScopeKind.Platform, null);

    public static HostScope ForTenant(TenantInfo tenant) => new(HostScopeKind.Tenant, tenant);
}

public enum TenantLifecycleStatus
{
    Provisioning,
    Active,
    Suspended,
    Closed,
}

public sealed record TenantInfo(Guid Id, string Key, TenantLifecycleStatus Status);

/// <summary>Read access to the platform tenant registry. Implemented by the Tenancy module.</summary>
public interface ITenantRegistry
{
    Task<TenantInfo?> FindByKeyAsync(string key, CancellationToken ct);
    Task<TenantInfo?> FindByIdAsync(Guid id, CancellationToken ct);
}

/// <summary>Lists tenants for platform jobs that iterate tenants (sealing, retention).</summary>
public interface ITenantLister
{
    Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken ct);
}
