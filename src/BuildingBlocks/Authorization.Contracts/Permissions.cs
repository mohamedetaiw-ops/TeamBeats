namespace TeamBeats.Authorization.Contracts;

/// <summary>
/// Permission catalogue (spec 004 FR-001). Keys are <c>{area}.{action}</c> with exactly one dot (spec 004 FR-013). Modules ask for
/// permissions, never for roles (constitution III).
/// </summary>
public static class Permissions
{
    public const string TenantSettingsView = "tenant.settings_view";
    public const string TenantSettingsManage = "tenant.settings_manage";
    public const string TenantAdminsManage = "tenant.admins_manage";

    public const string OrgUnitView = "org.view";
    public const string OrgUnitManage = "org.manage";
    public const string OrgMembershipManage = "org.membership_manage";

    public const string RolesView = "authz.roles_view";
    public const string RolesManage = "authz.roles_manage";
    public const string RolesAssign = "authz.roles_assign";
    public const string AccessExplain = "authz.explain";
    public const string InheritanceRulesManage = "authz.rules_manage";

    public const string UsersView = "identity.users_view";
    public const string UsersManage = "identity.users_manage";
    public const string IdentityConfigManage = "identity.config_manage";

    public const string EntitlementsView = "entitlements.view";

    public const string AuditView = "audit.read";
    public const string AuditExport = "audit.export";
    public const string AuditRetentionManage = "audit.retention_manage";

    public const string ContentView = "content.view";
    public const string ContentEdit = "content.edit";
    public const string ContentPublish = "content.publish";
    public const string WorkflowDesign = "process.design";
    public const string WorkflowApprove = "workflow.approve";
    public const string IntegrationManage = "integration.manage";
    public const string AiPolicyManage = "ai.policy_manage";

    public const string PageView = "page.view";
    public const string PageEdit = "page.edit";
    public const string PagePublish = "page.publish";
    public const string TemplateEdit = "template.edit";
    public const string TemplateDistribute = "template.distribute";
    public const string ComponentEnable = "component.enable";
    public const string ConfigHistoryView = "config_history.view";
    public const string ThemeEdit = "theme.edit";
    public const string ThemePublish = "theme.publish";
    public const string ConfigLockManage = "config_lock.manage";
    public const string ShellEdit = "shell.edit";
    public const string NavigationEdit = "navigation.edit";
    public const string HelpEdit = "help.edit";
    public const string PersonalizationPolicy = "personalization.policy";
    public const string PersonalLayoutEdit = "personal_layout.edit";

    public const string NotificationsManage = "notifications.manage";

    public const string HrCalendarManage = "hr.calendar_manage";

    public const string PeopleView = "people.view";
    public const string PeopleHrView = "people.hr_view";
    public const string PeopleManage = "people.manage";

    // Spec 026 (R1): tenant security policy, alerts and posture.
    public const string SecurityManage = "security.manage";
    public const string SecurityView = "security.view";
    public const string SecurityRespond = "security.respond";

    public static readonly IReadOnlyList<string> All =
    [
        TenantSettingsView, TenantSettingsManage, TenantAdminsManage,
        OrgUnitView, OrgUnitManage, OrgMembershipManage,
        RolesView, RolesManage, RolesAssign, AccessExplain, InheritanceRulesManage,
        UsersView, UsersManage, IdentityConfigManage,
        EntitlementsView,
        AuditView, AuditExport, AuditRetentionManage,
        ContentView, ContentEdit, ContentPublish, WorkflowDesign, WorkflowApprove, IntegrationManage, AiPolicyManage,
        PageView, PageEdit, PagePublish, TemplateEdit, TemplateDistribute, ComponentEnable, ConfigHistoryView,
        ThemeEdit, ThemePublish, ConfigLockManage,
        ShellEdit, NavigationEdit, HelpEdit, PersonalizationPolicy, PersonalLayoutEdit,
        NotificationsManage,
        HrCalendarManage,
        PeopleView, PeopleHrView, PeopleManage,
        SecurityManage, SecurityView, SecurityRespond,
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
    public const string PageEditor = "page_editor";
    public const string PagePublisher = "page_publisher";
    public const string BrandManager = "brand_manager";
    public const string HrOfficer = "hr_officer";

}
