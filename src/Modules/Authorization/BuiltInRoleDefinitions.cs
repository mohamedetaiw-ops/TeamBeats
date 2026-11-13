using TeamBeats.Authorization.Contracts;
using P = TeamBeats.Authorization.Contracts.Permissions;

namespace TeamBeats.Modules.Authorization;

/// <summary>A built-in role. Only roles marked <see cref="AssignableToApps"/> can be given to an application app (spec 035 FR-011).</summary>
public sealed record RoleDefinition(string Key, bool Delegable, bool DelegatedAdmin, IReadOnlyList<string> Permissions, bool AssignableToApps = false);

/// <summary>Built-in roles seeded into every tenant (spec 004 FR-011). Platform Operator is not a tenant role.</summary>
public static class BuiltInRoleDefinitions
{
    public static readonly IReadOnlyList<RoleDefinition> All =
    [
        new(BuiltInRoles.TenantAdministrator, Delegable: false, DelegatedAdmin: false, P.All.Where(p => !P.ResourceOnly.Contains(p)).ToList()),
        new(BuiltInRoles.OrganizationAdministrator, Delegable: true, DelegatedAdmin: true,
        [
            P.OrgUnitView, P.OrgUnitManage, P.OrgMembershipManage, P.RolesView, P.RolesAssign, P.UsersView, P.AuditView,
            P.ContentView, P.ContentAuthor, P.ContentEdit, P.ContentPublish, P.PageView, P.PageEdit, P.PagePublish, P.TemplateEdit,
            P.ThemeEdit, P.ThemePublish, P.NavigationEdit, P.PersonalizationPolicy, P.PeopleView, P.PeopleManage,
            P.ServiceManage, P.ServiceRequestOnBehalf, P.DocView, P.DocContribute, P.DocManage, P.KbEdit,
            P.EngagementModerate, P.EngagementSurveyPublish, P.EngagementRecognitionApprove, P.LearningManage, P.LearningReport,
        ]),
        new(BuiltInRoles.ContentEditor, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.ContentView, P.ContentAuthor, P.ContentEdit, P.KbEdit]),
        new(BuiltInRoles.ContentPublisher, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.ContentView, P.ContentAuthor, P.ContentEdit, P.ContentPublish, P.KbEdit]),
        new(BuiltInRoles.WorkflowDesigner, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.WorkflowDesign, P.ServiceManage]),
        new(BuiltInRoles.Approver, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.WorkflowApprove]),
        new(BuiltInRoles.IntegrationAdministrator, Delegable: false, DelegatedAdmin: false, [P.OrgUnitView, P.IntegrationManage, P.IntegrationOperate, P.ApiManage, P.ApiView]),
        new(BuiltInRoles.AiPolicyAdministrator, Delegable: false, DelegatedAdmin: false, [P.OrgUnitView, P.AiPolicyManage, P.AiPolicyView, P.AgentPublish, P.AgentView, P.AgentSuspend, P.EvalPolicyManage, P.EvalView]),
        new(BuiltInRoles.SecurityOfficer, Delegable: false, DelegatedAdmin: false,
            [P.OrgUnitView, P.AuditView, P.AuditExport, P.UsersView, P.RolesView, P.AccessExplain, P.ConfigHistoryView, P.SecurityView, P.SecurityRespond, P.AiPolicyView,
             P.AgentView, P.AgentSecurityView, P.AgentSuspend, P.EvalView, P.ApiView]),
        new(BuiltInRoles.Employee, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.PersonalLayoutEdit, P.PeopleView, P.EngagementParticipate, P.LearningTake, P.SpacesCreate, P.BookingBook]),
        new(BuiltInRoles.PageEditor, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.PageEdit]),
        new(BuiltInRoles.PagePublisher, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.PageEdit, P.PagePublish]),
        new(BuiltInRoles.BrandManager, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.ThemeEdit]),
        new(BuiltInRoles.HrOfficer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.HrCalendarManage, P.HrIntegrationManage, P.HrManage, P.PeopleView, P.PeopleHrView]),
        new(BuiltInRoles.FinanceOfficer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PeopleView, P.FinanceProcess]),
        new(BuiltInRoles.AgentDesigner, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.AgentDesign, P.AgentView, P.EvalDesign]),
        new(BuiltInRoles.EvaluationReviewer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.EvalReview]),
        new(BuiltInRoles.EngagementAdministrator, Delegable: true, DelegatedAdmin: false,
            [P.OrgUnitView, P.PeopleView, P.EngagementParticipate, P.EngagementModerate, P.EngagementManage, P.EngagementSurveyPublish, P.EngagementRecognitionApprove]),
        new(BuiltInRoles.LearningAdministrator, Delegable: true, DelegatedAdmin: false,
            [P.OrgUnitView, P.PeopleView, P.LearningTake, P.LearningManage, P.LearningReport]),
        new(BuiltInRoles.ComplianceOfficer, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PeopleView, P.PolicyAuthor, P.PolicyManage, P.LearningReport, P.LearningAssignLinked, P.ComplianceManage, P.ComplianceView]),
        new(BuiltInRoles.SpacesAdministrator, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PeopleView, P.SpacesCreate, P.SpacesManage]),
        new(BuiltInRoles.FacilitiesAdministrator, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PeopleView, P.BookingBook, P.BookingManage, P.BookingReport]),
        new(BuiltInRoles.FacilitiesAnalyst, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.BookingReport]),
        new(BuiltInRoles.DeviceAdministrator, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PeopleView, P.DeviceManage]),
        new(BuiltInRoles.KioskUser, Delegable: true, DelegatedAdmin: false, [P.KioskSignIn]),
        // Spec 035 FR-011: what an integration usually needs to read, and nothing an app may not hold.
        new(BuiltInRoles.AppReader, Delegable: true, DelegatedAdmin: false, [P.OrgUnitView, P.PageView, P.ContentView, P.DocView, P.PeopleView], AssignableToApps: true),
        // Spec 035 US1 scenario 4 and requests:submit: the request roles an app can hold, never any approval or management.
        new(BuiltInRoles.AppRequestReader, Delegable: true, DelegatedAdmin: false, [P.ServiceRequestRead], AssignableToApps: true),
        new(BuiltInRoles.AppRequestFiler, Delegable: true, DelegatedAdmin: false, [P.ServiceRequestRead, P.ServiceRequestOnBehalf], AssignableToApps: true),
        // Spec 029 FR-002: the space roles. Only Spaces assigns them, on one space (see ModuleAssignedRoles).
        new(BuiltInRoles.SpaceOwner, Delegable: false, DelegatedAdmin: false, [P.SpacesView, P.SpacesContribute, P.SpacesOwn, P.DocView, P.DocContribute, P.DocManage]),
        new(BuiltInRoles.SpaceMember, Delegable: false, DelegatedAdmin: false, [P.SpacesView, P.SpacesContribute, P.DocView, P.DocContribute]),
        new(BuiltInRoles.SpaceViewer, Delegable: false, DelegatedAdmin: false, [P.SpacesView, P.DocView]),
    ];

    /// <summary>
    /// Roles a module assigns on its own resources and keeps in step with its own records (spec 029 space roles). The
    /// role administration endpoints refuse them, so a space's members are always the ones the space shows.
    /// </summary>
    public static readonly IReadOnlySet<string> ModuleAssignedRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        BuiltInRoles.SpaceOwner, BuiltInRoles.SpaceMember, BuiltInRoles.SpaceViewer,
    };
}
