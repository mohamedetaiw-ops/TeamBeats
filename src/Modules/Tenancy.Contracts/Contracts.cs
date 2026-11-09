using TeamBeats.Primitives;

namespace TeamBeats.Modules.Tenancy.Contracts;

public enum MembershipStatus
{
    Active,
    Disabled,
}

/// <summary>Tenant membership types. Default Employee access can be removed per type (spec 004 section 6).</summary>
public static class MembershipTypes
{
    public const string Employee = "employee";
    public const string Contractor = "contractor";
    public const string Guest = "guest";

    public static readonly IReadOnlyList<string> All = [Employee, Contractor, Guest];
}

public sealed record TenantMembershipInfo(Guid UserId, string MembershipType, MembershipStatus Status, Guid? PreferredOrgUnitId);

public sealed record OrgMembershipInfo(Guid MembershipId, Guid OrgUnitId, string Type, bool IsPrimary);

/// <summary>An active membership of one user, for bulk reads such as the people directory (spec 010).</summary>
public sealed record UserOrgMembership(Guid UserId, Guid OrgUnitId, bool IsPrimary);

public sealed record TenantLocaleSettings(string DefaultLocale, IReadOnlyList<string> EnabledLocales, string TimeZone, string NumberingSystem);

/// <summary>Hierarchy reads for other modules (spec 002 plan, "Contracts published"). All calls are in the current tenant.</summary>
public interface IOrganizationHierarchy
{
    Task<bool> IsAncestorOrSelfAsync(Guid ancestorId, Guid unitId, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> GetAncestorsAndSelfAsync(Guid unitId, CancellationToken ct = default);

    /// <summary>For each requested unit, its path from the root down to the unit itself, in one read.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> GetPathsAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default);

    /// <summary>Descendants (including the unit) of each requested unit, as one set.</summary>
    Task<IReadOnlySet<Guid>> GetSubtreesAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default);

    Task<IReadOnlyList<OrgMembershipInfo>> GetActiveMembershipsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every active membership in the tenant, in active units.</summary>
    Task<IReadOnlyList<UserOrgMembership>> ListActiveMembershipsAsync(CancellationToken ct = default);

    Task<bool> UnitExistsAsync(Guid unitId, CancellationToken ct = default);

    /// <summary>Unit names by locale, for labels such as the shell's organization indicator.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyDictionary<string, string>>> GetNamesAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default);

    /// <summary>Users with an active membership in any of the units.</summary>
    Task<IReadOnlySet<Guid>> GetMemberUserIdsAsync(IEnumerable<Guid> unitIds, CancellationToken ct = default);

    /// <summary>Bumped on any hierarchy or membership change; used as part of authorization cache keys.</summary>
    Task<long> GetVersionAsync(CancellationToken ct = default);
}

/// <summary>Tenant membership operations for Identity and Administration.</summary>
public interface ITenantMemberships
{
    Task<TenantMembershipInfo?> GetAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every tenant membership, active or disabled.</summary>
    Task<IReadOnlyList<TenantMembershipInfo>> ListAsync(CancellationToken ct = default);

    Task CreateAsync(Guid userId, string membershipType, CancellationToken ct = default);

    Task<bool> HasPendingAdminAppointmentAsync(string email, CancellationToken ct = default);

    /// <summary>Activates pending administrator appointments for this email; returns how many were claimed.</summary>
    Task<int> ClaimAdminAppointmentsAsync(string email, Guid userId, CancellationToken ct = default);

    Task SetStatusAsync(Guid userId, MembershipStatus status, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, bool>> GetMembershipTypePoliciesAsync(CancellationToken ct = default);
}

/// <summary>Spec 001 branding: primary color, whether a logo is uploaded, and the tenant display name the shell shows.</summary>
public sealed record TenantBranding(string? PrimaryColor, bool HasLogo, string DisplayName = "");

public interface ITenantSettingsReader
{
    Task<TenantLocaleSettings> GetLocaleSettingsAsync(CancellationToken ct = default);

    /// <summary>Spec 001 logo and primary color, read once to seed the default brand (spec 033 section 6).</summary>
    Task<TenantBranding> GetBrandingAsync(CancellationToken ct = default);
}

// Events (spec 001 section 12, spec 002 section 12)
public sealed record TenantCreated(Guid TenantId, string Key, string PackageKey) : IIntegrationEvent;
public sealed record TenantSuspended(Guid TenantId, string? Reason) : IIntegrationEvent;
public sealed record TenantReactivated(Guid TenantId) : IIntegrationEvent;
public sealed record TenantClosed(Guid TenantId, DateTimeOffset DeletionDueAt) : IIntegrationEvent;
public sealed record TenantSettingsChanged(Guid TenantId, IReadOnlyList<string> ChangedFields) : IIntegrationEvent;
public sealed record OrganizationUnitCreated(Guid UnitId, Guid? ParentId) : IIntegrationEvent;
public sealed record OrganizationUnitMoved(Guid UnitId, Guid? OldParentId, Guid? NewParentId, int AffectedUnits) : IIntegrationEvent;
public sealed record OrganizationUnitDeactivated(Guid UnitId) : IIntegrationEvent;
public sealed record OrganizationMembershipChanged(Guid UserId, Guid OrgUnitId, string Change) : IIntegrationEvent;
public sealed record TenantMembershipDisabled(Guid UserId) : IIntegrationEvent;
