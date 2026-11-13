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
    public const string ProcessOperate = "process.operate";
    public const string ProcessAnalyze = "process.analyze";
    public const string DecisionDesign = "decision.design";
    public const string IntegrationManage = "integration.manage";

    /// <summary>Spec 017 (R2): run history, health, replay of failed runs and pausing a sync, without changing connections.</summary>
    public const string IntegrationOperate = "integration.operate";
    public const string AiPolicyManage = "ai.policy_manage";

    // Spec 013 (R1): security officers view the model policy, route changes and AI usage.
    public const string AiPolicyView = "ai.policy_view";

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

    /// <summary>Spec 019: HR sync results, unmatched people, held runs and org codes, at tenant scope.</summary>
    public const string HrIntegrationManage = "hr.integration_manage";

    /// <summary>Spec 022 R2: leave types and policies, leave on behalf, balance adjustments and leave types of the people in scope.</summary>
    public const string HrManage = "hr.manage";

    /// <summary>Spec 022 R2: expense policy, exchange rates and the finance step of expense claims of the people in scope.</summary>
    public const string FinanceProcess = "finance.process";

    public const string PeopleView = "people.view";
    public const string PeopleHrView = "people.hr_view";
    public const string PeopleManage = "people.manage";

    // Spec 026 (R1): tenant security policy, alerts and posture.
    public const string SecurityManage = "security.manage";
    public const string SecurityView = "security.view";
    public const string SecurityRespond = "security.respond";

    // Spec 026 US7 (R2): compliance officers manage the control library and compliance projects in their org scope;
    // viewers read projects, controls and evidence.
    public const string ComplianceManage = "compliance.manage";
    public const string ComplianceView = "compliance.view";

    // Spec 016 (R1): service catalogue and requests.
    public const string ServiceManage = "service.manage";
    public const string ServiceRequestOnBehalf = "service.request_on_behalf";

    /// <summary>Spec 035 US1 scenario 4 (R2): read the requests of people in the units held, for apps given a request role.</summary>
    public const string ServiceRequestRead = "service.request_read";

    // Spec 009 (R1): authors write drafts at site scope; critical announcements and content settings need their own keys.
    public const string ContentAuthor = "content.author";
    public const string ContentPublishCritical = "content.publish_critical";
    public const string ContentAdmin = "content.admin";

    // Spec 011 (R1): reading libraries, contributing files, managing libraries and writing knowledge articles. Reading is
    // a .view permission so tenant-wide libraries are open to every member and unit libraries follow their ACL.
    public const string DocView = "doc.view";
    public const string DocContribute = "doc.contribute";
    public const string DocManage = "doc.manage";
    public const string KbEdit = "kb.edit";

    // Spec 025 (R1): policy owners draft and revise their policies; compliance officers manage the register, campaigns
    // and the compliance view at tenant or org scope.
    public const string PolicyAuthor = "policy.author";
    public const string PolicyManage = "policy.manage";

    /// <summary>Best bets, synonyms, rebuilds and later the search settings of spec 012 (search administrator, tenant scope).</summary>
    public const string SearchManage = "search.manage";

    // Specs 020 and 027 (R1): agent designers adapt agents in their org scope; publishers review and publish them;
    // security officers see agents and their runs and can suspend them.
    public const string AgentDesign = "agent.design";
    public const string AgentPublish = "agent.publish";
    public const string AgentView = "agent.view";
    public const string AgentSecurityView = "agent.security_view";
    public const string AgentSuspend = "agent.suspend";

    // Spec 034 (R1): designers build suites and datasets for agents in their scope, reviewers label calibration sets,
    // the AI Policy Administrator sets the tenant evaluation policy, and security officers read every result.
    public const string EvalDesign = "eval.design";
    public const string EvalReview = "eval.review";
    public const string EvalPolicyManage = "eval.policy_manage";
    public const string EvalView = "eval.view";

    // Spec 023 (R2): employees take part in communities, polls, surveys and recognition; moderators review content in
    // their org scope; engagement administrators configure engagement for the tenant; survey authors publish to an
    // audience in scope; recognition approvers choose the team and star of the period.
    public const string EngagementParticipate = "engagement.participate";
    public const string EngagementModerate = "engagement.moderate";
    public const string EngagementManage = "engagement.manage";
    public const string EngagementSurveyPublish = "engagement.survey_publish";
    public const string EngagementRecognitionApprove = "engagement.recognition_approve";

    // Spec 021 (R1): adoption basics and frontend error reports for the whole tenant.
    public const string AnalyticsView = "analytics.view";

    // Spec 028 (R2): employees take courses; learning administrators build and assign them in their org scope; managers
    // see their direct reports (through the manager relation); compliance officers report on their scope; policy owners
    // link a course to a policy version.
    public const string LearningTake = "learning.take";
    public const string LearningManage = "learning.manage";
    public const string LearningTeamView = "learning.team_view";
    public const string LearningReport = "learning.report";
    public const string LearningAssignLinked = "learning.assign_linked";

    // Spec 029 (R2): people create spaces where the tenant allows; spaces administrators set the tenant rules and see
    // every space's metadata. Inside a space, view, contribute and own come only from the space roles, which are built-in
    // roles assigned on the space itself.
    public const string SpacesCreate = "spaces.create";
    public const string SpacesManage = "spaces.manage";
    public const string SpacesView = "spaces.view";
    public const string SpacesContribute = "spaces.contribute";
    public const string SpacesOwn = "spaces.own";

    // Spec 031 (R2): employees book rooms, desks, parking and equipment within the rules; facilities administrators
    // define sites, resources and rules and cancel bookings in their location scope; facilities analysts see usage counts.
    public const string BookingBook = "booking.book";
    public const string BookingManage = "booking.manage";
    public const string BookingReport = "booking.report";

    // Spec 030 US4 (R2): frontline people sign in on a shared kiosk device; device administrators enrol and revoke
    // kiosks and issue badges. Kiosk sign-in is given by group through the kiosk user role.
    public const string KioskSignIn = "kiosk.signin";
    public const string DeviceManage = "device.manage";

    // Spec 035 (R2): API administrators register apps, issue credentials and give admin consent; API viewers see apps,
    // consents, usage and call logs.
    public const string ApiManage = "api.manage";
    public const string ApiView = "api.view";

    /// <summary>
    /// Permissions that make a role an administrative one (spec 026 US5 scenario 2): they change who can do what, how
    /// people sign in, or tenant-wide configuration. Granting a role that holds any of them is a privilege grant.
    /// </summary>
    public static readonly IReadOnlySet<string> Privileged = new HashSet<string>(StringComparer.Ordinal)
    {
        TenantSettingsManage, TenantAdminsManage, OrgUnitManage, RolesManage, RolesAssign, InheritanceRulesManage, UsersManage,
        IdentityConfigManage, AuditRetentionManage, IntegrationManage, AiPolicyManage, SecurityManage, ApiManage,
    };

    /// <summary>
    /// Permissions granted only on one resource by the role of that resource (spec 029 FR-002: owner, member and viewer
    /// of a space). The tenant administrator does not hold them tenant-wide, so a private or secret space stays with its
    /// members (spec 029 FR-003); spaces administrators see metadata through <see cref="SpacesManage"/>.
    /// </summary>
    public static readonly IReadOnlySet<string> ResourceOnly = new HashSet<string>(StringComparer.Ordinal) { SpacesView, SpacesContribute, SpacesOwn };

    /// <summary>
    /// Permissions no role assignable to apps may hold (spec 035 FR-011, spec 004 FR-017): administration of access,
    /// identity, security and the API itself, audit export and AI policy stay with people.
    /// </summary>
    public static bool ExcludedFromApps(string permission) =>
        permission.StartsWith("authz.", StringComparison.Ordinal) || permission.StartsWith("identity.", StringComparison.Ordinal)
        || permission.StartsWith("security.", StringComparison.Ordinal) || permission.StartsWith("api.", StringComparison.Ordinal)
        || permission is AuditExport or AiPolicyManage;

    /// <summary>
    /// Administration permissions for access reviews (spec 026 FR-012): the same set apps may never hold, so a role with
    /// any of them is an administrator role.
    /// </summary>
    public static bool Administers(string permission) => ExcludedFromApps(permission);

    public static readonly IReadOnlyList<string> All =
    [
        TenantSettingsView, TenantSettingsManage, TenantAdminsManage,
        OrgUnitView, OrgUnitManage, OrgMembershipManage,
        RolesView, RolesManage, RolesAssign, AccessExplain, InheritanceRulesManage,
        UsersView, UsersManage, IdentityConfigManage,
        EntitlementsView,
        AuditView, AuditExport, AuditRetentionManage,
        ContentView, ContentEdit, ContentPublish, WorkflowDesign, WorkflowApprove, ProcessOperate, ProcessAnalyze, DecisionDesign, IntegrationManage, IntegrationOperate, AiPolicyManage, AiPolicyView,
        PageView, PageEdit, PagePublish, TemplateEdit, TemplateDistribute, ComponentEnable, ConfigHistoryView,
        ThemeEdit, ThemePublish, ConfigLockManage,
        ShellEdit, NavigationEdit, HelpEdit, PersonalizationPolicy, PersonalLayoutEdit,
        NotificationsManage,
        HrCalendarManage, HrIntegrationManage, HrManage, FinanceProcess,
        PeopleView, PeopleHrView, PeopleManage,
        SecurityManage, SecurityView, SecurityRespond,
        ComplianceManage, ComplianceView,
        ServiceManage, ServiceRequestOnBehalf, ServiceRequestRead,
        ContentAuthor, ContentPublishCritical, ContentAdmin,
        DocView, DocContribute, DocManage, KbEdit,
        PolicyAuthor, PolicyManage,
        SearchManage,
        AgentDesign, AgentPublish, AgentView, AgentSecurityView, AgentSuspend,
        EvalDesign, EvalReview, EvalPolicyManage, EvalView,
        AnalyticsView,
        EngagementParticipate, EngagementModerate, EngagementManage, EngagementSurveyPublish, EngagementRecognitionApprove,
        LearningTake, LearningManage, LearningTeamView, LearningReport, LearningAssignLinked,
        SpacesCreate, SpacesManage, SpacesView, SpacesContribute, SpacesOwn,
        BookingBook, BookingManage, BookingReport,
        KioskSignIn, DeviceManage,
        ApiManage, ApiView,
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
    public const string FinanceOfficer = "finance_officer";
    public const string ComplianceOfficer = "compliance_officer";
    public const string ProcessOperator = "process_operator";
    public const string ProcessAnalyst = "process_analyst";
    public const string AgentDesigner = "agent_designer";
    public const string EvaluationReviewer = "evaluation_reviewer";

    /// <summary>Spec 035 FR-011: the read-only role an API administrator can give an application app.</summary>
    public const string AppReader = "app_reader";

    /// <summary>Spec 035 US1 scenario 4: an app that reads the service requests of people in its units.</summary>
    public const string AppRequestReader = "app_request_reader";

    /// <summary>Spec 035 section 6 <c>requests:submit</c>: an app that also files requests for people in its units.</summary>
    public const string AppRequestFiler = "app_request_filer";
    public const string EngagementAdministrator = "engagement_administrator";
    public const string LearningAdministrator = "learning_administrator";
    public const string SpacesAdministrator = "spaces_administrator";
    public const string FacilitiesAdministrator = "facilities_administrator";
    public const string FacilitiesAnalyst = "facilities_analyst";
    public const string DeviceAdministrator = "device_administrator";
    public const string KioskUser = "kiosk_user";

    // Spec 029: the space roles, assigned only on a space by the Spaces module.
    public const string SpaceOwner = "space_owner";
    public const string SpaceMember = "space_member";
    public const string SpaceViewer = "space_viewer";

}

/// <summary>Maps a module's resource role to the built-in role that carries it, so modules never name roles (spec 029 FR-002).</summary>
public static class ResourceRoles
{
    /// <summary>The built-in role for a space role: owner, member, or viewer for anything else.</summary>
    public static string Space(string role) => role switch
    {
        "owner" => BuiltInRoles.SpaceOwner,
        "member" => BuiltInRoles.SpaceMember,
        _ => BuiltInRoles.SpaceViewer,
    };
}
