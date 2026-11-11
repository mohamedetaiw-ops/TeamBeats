using TeamBeats.Authorization.Contracts;
using P = TeamBeats.Authorization.Contracts.Permissions;

namespace TeamBeats.Modules.Authorization;

public sealed record RoleDefinition(string Key, bool Delegable, bool DelegatedAdmin, IReadOnlyList<string> Permissions);

/// <summary>Built-in roles seeded into every tenant (spec 004 FR-011). Platform Operator is not a tenant role.</summary>
public static class BuiltInRoleDefinitions
{
    public static readonly IReadOnlyList<RoleDefinition> All =
    [
        new(BuiltInRoles.TenantAdministrator, Delegable: false, DelegatedAdmin: false, P.All),
        new(BuiltInRoles.OrganizationAdministrator, Delegable: true, DelegatedAdmin: true,
        [
            P.OrgUnitView, P.OrgUnitManage, P.OrgMembershipManage, P.RolesView, P.RolesAssign, P.UsersView, P.AuditView,
            P.ContentView, P.ContentAuthor, P.ContentEdit, P.ContentPublish, P.PageView, P.PageEdit, P.PagePublish, P.TemplateEdit,
            P.ThemeEdit, P.ThemePublish, P.NavigationEdit, P.PersonalizationPolicy, P.PeopleView, P.PeopleManage,
            P.ServiceManage, P.ServiceRequestOnBehalf, P.DocView, P.DocContribute, P.DocManage, P.KbEdit,
        ]),
        new(BuiltInRoles.ContentEditor, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.ContentView, P.ContentAuthor, P.ContentEdit, P.KbEdit]),
        new(BuiltInRoles.ContentPublisher, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.ContentView, P.ContentAuthor, P.ContentEdit, P.ContentPublish, P.KbEdit]),
        new(BuiltInRoles.WorkflowDesigner, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.WorkflowDesign, P.ServiceManage]),
        new(BuiltInRoles.Approver, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.WorkflowApprove]),
        new(BuiltInRoles.IntegrationAdministrator, Delegable: false, DelegatedAdmin: false, [P.OrgUnitView, P.IntegrationManage, P.IntegrationOperate]),
        new(BuiltInRoles.AiPolicyAdministrator, Delegable: false, DelegatedAdmin: false, [P.OrgUnitView, P.AiPolicyManage, P.AiPolicyView, P.AgentPublish, P.AgentView, P.AgentSuspend, P.EvalPolicyManage, P.EvalView]),
        new(BuiltInRoles.SecurityOfficer, Delegable: false, DelegatedAdmin: false,
            [P.OrgUnitView, P.AuditView, P.AuditExport, P.UsersView, P.RolesView, P.AccessExplain, P.ConfigHistoryView, P.SecurityView, P.SecurityRespond, P.AiPolicyView,
             P.AgentView, P.AgentSecurityView, P.AgentSuspend, P.EvalView]),
        new(BuiltInRoles.Employee, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.PersonalLayoutEdit, P.PeopleView]),
        new(BuiltInRoles.PageEditor, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.PageEdit]),
        new(BuiltInRoles.PagePublisher, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.PageEdit, P.PagePublish]),
        new(BuiltInRoles.BrandManager, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.ThemeEdit]),
        new(BuiltInRoles.HrOfficer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.HrCalendarManage, P.HrIntegrationManage, P.HrManage, P.PeopleView, P.PeopleHrView]),
        new(BuiltInRoles.AgentDesigner, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.AgentDesign, P.AgentView, P.EvalDesign]),
        new(BuiltInRoles.EvaluationReviewer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.EvalReview]),
        new(BuiltInRoles.ComplianceOfficer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PeopleView, P.PolicyAuthor, P.PolicyManage]),
    ];
}
