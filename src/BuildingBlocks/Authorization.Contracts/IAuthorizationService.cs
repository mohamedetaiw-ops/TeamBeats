namespace TeamBeats.Authorization.Contracts;

public enum ScopeType
{
    Tenant,
    OrgUnit,
    OrgSubtree,
    Resource,
}

public enum ResourceVisibility
{
    /// <summary>Every active tenant member can view.</summary>
    Tenant,

    /// <summary>Only explicit grants (ACL entries, covering role assignments, inheritance rules) allow viewing.</summary>
    Restricted,
}

/// <summary>Describes a resource for a decision. Supplied by the owning module, so authorization never reads its tables.</summary>
public sealed record ResourceDescriptor(string ResourceType, Guid ResourceId, Guid? OwningOrgUnitId, ResourceVisibility Visibility);

/// <summary>What a decision is about.</summary>
public abstract record AuthzTarget
{
    public static readonly AuthzTarget Tenant = new TenantTarget();

    public static AuthzTarget OrgUnit(Guid unitId) => new OrgUnitTarget(unitId);

    public static AuthzTarget Resource(ResourceDescriptor descriptor) => new ResourceTarget(descriptor);
}

public sealed record TenantTarget : AuthzTarget;

public sealed record OrgUnitTarget(Guid UnitId) : AuthzTarget;

public sealed record ResourceTarget(ResourceDescriptor Descriptor) : AuthzTarget;

public sealed record DecisionReason(string Kind, string? RoleKey = null, ScopeType? ScopeType = null, Guid? ScopeId = null, string? Rule = null, string? Subject = null);

public sealed record Decision(bool Allowed, IReadOnlyList<DecisionReason> Reasons)
{
    public static readonly Decision Deny = new(false, []);
}

/// <summary>Set of org units where a permission applies, or the whole tenant.</summary>
public sealed record OrgCoverage(bool WholeTenant, IReadOnlySet<Guid> Units)
{
    public static readonly OrgCoverage None = new(false, new HashSet<Guid>());

    public bool Any => WholeTenant || Units.Count > 0;

    public bool Covers(Guid? unitId) => WholeTenant || (unitId is { } u && Units.Contains(u));
}

/// <summary>
/// The central policy service (constitution III, spec 004 FR-001). Decisions are for the current
/// authenticated user in the current tenant. Every decision first verifies active tenant membership.
/// </summary>
public interface IAuthorizationService
{
    Task<Decision> CheckAsync(string permission, AuthzTarget target, CancellationToken ct = default);

    /// <summary>Throws 403 <c>authz.denied</c> when not allowed.</summary>
    Task DemandAsync(string permission, AuthzTarget target, CancellationToken ct = default);

    /// <summary>For resources: throws 404 when not allowed, so existence is not revealed (spec 004 FR-009).</summary>
    Task DemandVisibleAsync(string permission, AuthzTarget target, CancellationToken ct = default);

    /// <summary>Whether the user holds the permission at any scope (navigation and menus).</summary>
    Task<bool> HoldsAnywhereAsync(string permission, CancellationToken ct = default);

    /// <summary>Org units (or whole tenant) where the permission applies through role assignments.</summary>
    Task<OrgCoverage> CoverageAsync(string permission, CancellationToken ct = default);

    /// <summary>Batch evaluation for search, feeds and AI retrieval (spec 004 FR-012).</summary>
    Task<IReadOnlyList<ResourceDescriptor>> FilterAsync(string permission, IEnumerable<ResourceDescriptor> resources, CancellationToken ct = default);

    /// <summary>Platform operator gate. Platform operators are not tenant roles (spec 004 FR-011).</summary>
    void DemandPlatformOperator();
}

/// <summary>Called by Tenancy and Identity during tenant creation and first sign-in.</summary>
public interface IAuthorizationProvisioning
{
    Task SeedTenantRolesAsync(CancellationToken ct = default);

    /// <summary>Assigns a built-in role if the user does not hold it at that scope yet; returns the active assignment's ID.</summary>
    Task<Guid> AssignBuiltInRoleAsync(Guid userId, string builtInRoleKey, ScopeType scopeType, Guid? scopeId, Guid? grantedBy, CancellationToken ct = default);

    /// <summary>Whether a key names a built-in role seeded in this tenant (for validating configuration that maps to roles).</summary>
    Task<bool> IsBuiltInRoleKeyAsync(string roleKey, CancellationToken ct = default);

    /// <summary>
    /// Revokes an assignment made by the system (for example from an identity provider group). Returns false and
    /// leaves the assignment in place when it is the tenant's last tenant administrator assignment (spec 001 section 6).
    /// </summary>
    Task<bool> RevokeSystemAssignmentAsync(Guid assignmentId, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignmentSummary>> GetUserAssignmentsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Whether removing access for this user would leave the tenant without an administrator.</summary>
    Task<bool> IsLastTenantAdministratorAsync(Guid userId, CancellationToken ct = default);
}

public sealed record RoleAssignmentSummary(Guid AssignmentId, string RoleKey, ScopeType ScopeType, Guid? ScopeId);

/// <summary>Event published when grants change, for caches, search and AI retrieval.</summary>
public sealed record RoleAssignmentChanged(Guid AssignmentId, Guid SubjectId, string RoleKey, string Change) : Primitives.IIntegrationEvent;

public sealed record ResourcePermissionChanged(string ResourceType, Guid ResourceId) : Primitives.IIntegrationEvent;

/// <summary>
/// Owning modules register one provider per resource type so authorization can describe a resource (owning unit,
/// visibility) without reading the owner's tables (constitution XIV).
/// </summary>
public interface IResourceDescriptorProvider
{
    string ResourceType { get; }

    /// <summary>Permission needed to manage the resource's ACL.</summary>
    string ManagePermission { get; }

    Task<ResourceDescriptor?> DescribeAsync(Guid resourceId, CancellationToken ct);
}
