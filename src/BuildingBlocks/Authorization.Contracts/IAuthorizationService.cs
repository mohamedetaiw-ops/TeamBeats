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
/// <remarks>
/// <paramref name="Classification"/> is one of <see cref="ConditionAttributes.Classifications"/>, or null when the type has none.
/// <paramref name="Container"/> is the resource this one belongs to, such as the space of a space library (spec 029 FR-006):
/// a role assigned on the container applies to what it holds, so the container's members are the audience.
/// </remarks>
public sealed record ResourceDescriptor(string ResourceType, Guid ResourceId, Guid? OwningOrgUnitId, ResourceVisibility Visibility, string? Classification = null,
    ResourceRef? Container = null);

/// <summary>A resource named by type and ID.</summary>
public sealed record ResourceRef(string ResourceType, Guid ResourceId);

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

/// <summary>A grant that applies (or, in <see cref="Decision.NotApplied"/>, one whose condition did not hold).</summary>
public sealed record DecisionReason(string Kind, string? RoleKey = null, ScopeType? ScopeType = null, Guid? ScopeId = null, string? Rule = null,
    string? Subject = null, ConditionTrace? Condition = null);

public sealed record Decision(bool Allowed, IReadOnlyList<DecisionReason> Reasons)
{
    public static readonly Decision Deny = new(false, []);

    /// <summary>Grants that would cover the target but whose condition was not met (spec 004 US7, FR-016).</summary>
    public IReadOnlyList<DecisionReason> NotApplied { get; init; } = [];
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

    /// <summary>
    /// Creates or updates a group owned by the identity provider directory (source <c>idp</c>, spec 003 US6 scenario 6)
    /// and sets its members exactly. Returns the group ID. Product groups are never changed by this call.
    /// </summary>
    Task<Guid> SyncDirectoryGroupAsync(string key, string name, IReadOnlyCollection<Guid> memberUserIds, CancellationToken ct = default);

    /// <summary>Gives a directory group a built-in role at a scope if it does not hold it yet (group-to-role mapping).</summary>
    Task AssignBuiltInRoleToGroupAsync(Guid groupId, string builtInRoleKey, ScopeType scopeType, Guid? scopeId, CancellationToken ct = default);

    /// <summary>Whether a key names a built-in role seeded in this tenant (for validating configuration that maps to roles).</summary>
    Task<bool> IsBuiltInRoleKeyAsync(string roleKey, CancellationToken ct = default);

    /// <summary>
    /// Revokes an assignment made by the system (for example from an identity provider group). Returns false and
    /// leaves the assignment in place when it is the tenant's last tenant administrator assignment (spec 001 section 6).
    /// </summary>
    Task<bool> RevokeSystemAssignmentAsync(Guid assignmentId, string reason, CancellationToken ct = default);

    Task<IReadOnlyList<RoleAssignmentSummary>> GetUserAssignmentsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Gives a user exactly one built-in role on one resource the calling module owns, revoking any other built-in role
    /// they hold on it, or revokes them all when <paramref name="builtInRoleKey"/> is null (spec 029 space roles). The
    /// owning module checks the caller's right first and audits the membership change in its own terms.
    /// </summary>
    Task SetResourceRoleAsync(Guid userId, string resourceType, Guid resourceId, string? builtInRoleKey, Guid? grantedBy, CancellationToken ct = default);

    /// <summary>Revokes every assignment on a resource, for a resource that is deleted.</summary>
    Task ClearResourceRolesAsync(string resourceType, Guid resourceId, CancellationToken ct = default);

    /// <summary>The permissions a role of the current tenant holds; empty for an unknown key.</summary>
    Task<IReadOnlySet<string>> RolePermissionsAsync(string roleKey, CancellationToken ct = default);

    /// <summary>
    /// Users holding the permission tenant-wide through a live role assignment, directly or through a group, so a module
    /// can tell them something (for example security alerts, spec 026). Conditions on the assignment are not evaluated.
    /// </summary>
    Task<IReadOnlyList<Guid>> TenantHoldersAsync(string permission, CancellationToken ct = default);

    /// <summary>
    /// Users holding the permission through a live role assignment on one resource (spec 029: the members of a space, the
    /// audience of its library and discussion). Users only, as resource roles are given to users.
    /// </summary>
    Task<IReadOnlyList<Guid>> ResourceHoldersAsync(string permission, string resourceType, Guid resourceId, CancellationToken ct = default);

    /// <summary>The groups the user is a member of, product and directory groups alike (audiences such as spec 016).</summary>
    Task<IReadOnlySet<Guid>> GroupIdsOfAsync(Guid userId, CancellationToken ct = default);

    /// <summary>The members of the given groups of the current tenant, each once (a workflow queue or escalation target, spec 016).</summary>
    Task<IReadOnlyList<Guid>> GroupMembersAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct = default);

    /// <summary>Which of the given group IDs exist in the current tenant.</summary>
    Task<IReadOnlySet<Guid>> ExistingGroupsAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken ct = default);

    /// <summary>
    /// Whether another user of the tenant holds the permission at the target, for work done in their name (a delegate
    /// acting on behalf of an approver, spec 016 US4). Request attributes such as the network are not evaluated.
    /// </summary>
    Task<bool> UserHoldsAsync(Guid userId, string permission, AuthzTarget target, CancellationToken ct = default);

    /// <summary>Whether removing access for this user would leave the tenant without an administrator.</summary>
    Task<bool> IsLastTenantAdministratorAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Adds an ACL entry on a resource the calling module owns, for defaults it sets on creation (a unit library readable
    /// by the unit's members, spec 011). The owning module checks the caller's right first and audits the change.
    /// </summary>
    Task AddAclEntryAsync(string resourceType, Guid resourceId, string subjectType, Guid subjectId, string permission, CancellationToken ct = default);

    /// <summary>
    /// Replaces the ACL of one resource with a copy of another's, for a permission inheritance break (spec 011 FR-001:
    /// the folder starts from what it inherited, so nobody loses or gains access at the moment of the break).
    /// </summary>
    Task CopyAclAsync(string fromType, Guid fromId, string toType, Guid toId, CancellationToken ct = default);

    /// <summary>Removes every ACL entry of a resource (inheritance restored, or the resource is gone).</summary>
    Task ClearAclAsync(string resourceType, Guid resourceId, CancellationToken ct = default);

    /// <summary>
    /// Live role assignments given directly to users at tenant or organization scope whose role holds an administration
    /// permission (<see cref="Permissions.Administers"/>), for access reviews (spec 026 FR-012). Group assignments are
    /// left out: their members come from the directory.
    /// </summary>
    Task<IReadOnlyList<AdminAssignment>> AdminAssignmentsAsync(CancellationToken ct = default);
}

public sealed record AdminAssignment(Guid AssignmentId, Guid UserId, string RoleKey, ScopeType ScopeType, Guid? ScopeId);

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

/// <summary>
/// The application apps that can hold roles (spec 035 FR-011), as the module that registers them knows them. Role
/// administration asks it before giving a role to an app, so an app of another tenant or a delegated app is refused.
/// </summary>
public interface IAppPrincipalDirectory
{
    /// <summary>Whether the ID is an application app of the current tenant.</summary>
    Task<bool> IsApplicationAppAsync(Guid appId, CancellationToken ct = default);
}
