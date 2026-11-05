namespace TeamBeats.Authorization.Contracts;

/// <summary>
/// Permission catalogue (spec 004 FR-001). Keys are <c>{resource_type}.{action}</c>. Modules ask for
/// permissions, never for roles (constitution III).
/// </summary>
public static class Permissions
{
    public const string TenantSettingsView = "tenant.settings.view";
    public const string TenantSettingsManage = "tenant.settings.manage";
    public const string TenantAdminsManage = "tenant.admins.manage";

    public const string OrgUnitView = "org.unit.view";
    public const string OrgUnitManage = "org.unit.manage";
    public const string OrgMembershipManage = "org.membership.manage";

    public const string RolesView = "authz.role.view";
    public const string RolesManage = "authz.role.manage";
    public const string RolesAssign = "authz.role.assign";
    public const string AccessExplain = "authz.access.explain";
    public const string InheritanceRulesManage = "authz.rule.manage";

    public const string UsersView = "identity.user.view";
    public const string UsersManage = "identity.user.manage";
    public const string IdentityConfigManage = "identity.config.manage";

    public const string EntitlementsView = "entitlements.plan.view";

    public const string AuditView = "audit.event.view";
    public const string AuditExport = "audit.event.export";
    public const string AuditRetentionManage = "audit.retention.manage";

    public const string ContentView = "content.page.view";
    public const string ContentEdit = "content.page.edit";
    public const string ContentPublish = "content.page.publish";
    public const string WorkflowDesign = "workflow.definition.manage";
    public const string WorkflowApprove = "workflow.task.approve";
    public const string IntegrationManage = "integration.connector.manage";
    public const string AiPolicyManage = "ai.policy.manage";

    public static readonly IReadOnlyList<string> All =
    [
        TenantSettingsView, TenantSettingsManage, TenantAdminsManage,
        OrgUnitView, OrgUnitManage, OrgMembershipManage,
        RolesView, RolesManage, RolesAssign, AccessExplain, InheritanceRulesManage,
        UsersView, UsersManage, IdentityConfigManage,
        EntitlementsView,
        AuditView, AuditExport, AuditRetentionManage,
        ContentView, ContentEdit, ContentPublish, WorkflowDesign, WorkflowApprove, IntegrationManage, AiPolicyManage,
    ];
}

/// <summary>Keys of the built-in roles seeded into every tenant (spec 004 FR-011). Used for seeding and assignment only.</summary>
public static class BuiltInRoles
{
    public const string TenantAdministrator = "tenant_administrator";
    public const string OrganizationAdministrator = "organization_administrator";
    public const string ContentEditor = "content_editor";
    public const string ContentPublisher = "content_publisher";
    public const string WorkflowDesigner = "workflow_designer";
    public const string Approver = "approver";
    public const string IntegrationAdministrator = "integration_administrator";
    public const string AiPolicyAdministrator = "ai_policy_administrator";
    public const string SecurityOfficer = "security_officer";
    public const string Employee = "employee";
}
