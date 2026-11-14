namespace TeamBeats.Entitlements.Contracts;

/// <summary>Stable capability keys (spec 005 ENT-001). Feature code checks these, never package names (constitution XXI).</summary>
public static class Capabilities
{
    public const string AiAssistant = "ai.assistant";
    public const string AiAssistantMonthlyRequests = "ai.assistant.monthly_requests";
    public const string AiAgentsBasic = "ai.agents.basic";
    public const string AiAgentsWriteTools = "ai.agents.write_tools";
    public const string AiAgentsMaxPublished = "ai.agents.max_published";
    public const string AiAgentsMaxCustom = "ai.agents.max_custom";
    public const string AiModelChoice = "ai.model_choice";

    // Spec 034 section 9: the evaluation baseline has no key; these add depth (production, experiments, business
    // outcomes and trace export are R2 and refuse until built).
    public const string AiEvalsAdvanced = "ai.evals.advanced";
    public const string AiEvalsProduction = "ai.evals.production";
    public const string AiEvalsExperiments = "ai.evals.experiments";
    public const string AiEvalsBusinessOutcomes = "ai.evals.business_outcomes";
    public const string AiEvalsTraceExport = "ai.evals.trace_export";
    public const string OrgHierarchy = "org.hierarchy";
    public const string OrgDelegatedAdmin = "org.delegated_admin";
    public const string OrgMaxUnits = "org.max_units";
    public const string AuthzCustomRoles = "authz.custom_roles";
    public const string IdentityOidc = "identity.oidc";
    public const string IdentitySaml = "identity.saml";
    public const string IdentityScim = "identity.scim";
    public const string IdentityDirectorySync = "identity.directory_sync";
    public const string WorkflowAdvanced = "workflow.advanced";
    public const string WorkflowAutomation = "workflow.automation";
    public const string WorkflowConnectorFulfilment = "workflow.connector_fulfilment";
    public const string WorkflowCatalogue = "workflow.catalogue";
    public const string WorkflowMaxServices = "workflow.max_services";
    public const string WorkflowBpmn = "workflow.bpmn";
    public const string WorkflowMaxActiveInstances = "workflow.max_active_instances";
    public const string IntegrationMicrosoft365 = "integration.microsoft365";
    public const string IntegrationOracleFusion = "integration.oracle_fusion";
    public const string IntegrationSap = "integration.sap";
    public const string IntegrationConnectors = "integration.connectors";
    public const string IntegrationCustomRest = "integration.custom_rest";
    public const string IntegrationWebhooks = "integration.webhooks";
    public const string IntegrationOnPremAgent = "integration.onprem_agent";
    public const string IntegrationMaxConnections = "integration.max_connections";

    // Spec 019 section 9: HR sync, payslips and form prefill are Workplace Plus and above; named packs are R3.
    public const string HrIntegration = "hr.integration";
    public const string HrPayslips = "hr.payslips";
    public const string HrFormPrefill = "hr.form_prefill";
    public const string HrNamedPacks = "hr.named_packs";

    // Spec 022 section 9: leave and letters in every package; the rest Workplace Plus and above.
    public const string HrLeave = "hr.leave";
    public const string HrAttendance = "hr.attendance";
    public const string HrJourneys = "hr.journeys";
    public const string HrLetters = "hr.letters";
    public const string HrDataChange = "hr.data_change";
    public const string HrExpenses = "hr.expenses";
    public const string AiReceiptReading = "ai.receipt_reading";

    // Spec 023 section 9: communities, recognition and galleries in every package; Speak Up, surveys and points Workplace
    // Plus and above. Workplace runs polls without surveys.
    public const string EngagementCommunities = "engagement.communities";
    public const string EngagementSpeakUp = "engagement.speak_up";
    public const string EngagementSurveys = "engagement.surveys";
    public const string EngagementRecognition = "engagement.recognition";
    public const string EngagementPoints = "engagement.points";
    public const string EngagementGalleries = "engagement.galleries";

    // Spec 028 section 9: courses, mandatory training and the LMS connector Workplace Plus and above; SCORM and xAPI
    // import Enterprise and Government only.
    public const string LearningCourses = "learning.courses";
    public const string LearningMandatory = "learning.mandatory";
    public const string LearningScorm = "learning.scorm";
    public const string LearningLmsConnector = "learning.lms_connector";

    // Spec 029 section 9: project and team spaces and guests Workplace Plus and above, with a limit on spaces.
    public const string SpacesProjects = "spaces.projects";
    public const string SpacesGuests = "spaces.guests";
    public const string SpacesMaxSpaces = "spaces.max_spaces";

    // Spec 031 section 9: rooms, desks, parking and equipment, and usage reports, Workplace Plus and above.
    public const string BookingRooms = "booking.rooms";
    public const string BookingDesks = "booking.desks";
    public const string BookingResources = "booking.resources";
    public const string BookingReports = "booking.reports";

    // Spec 030 section 9: the app and push everywhere, offline and kiosk Workplace Plus and above, native shell (R3) Enterprise.
    public const string MobilePwa = "mobile.pwa";
    public const string MobilePush = "mobile.push";
    public const string MobileOffline = "mobile.offline";
    public const string MobileKiosk = "mobile.kiosk";
    public const string MobileNativeShell = "mobile.native_shell";

    // Spec 035 section 9: the API and MCP read access everywhere; write scopes, event subscriptions and MCP write tools
    // Workplace Plus and above; limits on apps and monthly calls.
    public const string ApiPublic = "api.public";
    public const string ApiWriteScopes = "api.write_scopes";
    public const string ApiEventSubscriptions = "api.event_subscriptions";
    public const string ApiMcp = "api.mcp";
    public const string ApiMcpWriteTools = "api.mcp_write_tools";
    public const string ApiMaxApps = "api.max_apps";
    public const string ApiMonthlyCalls = "api.monthly_calls";
    public const string AnalyticsCore = "analytics.core";
    public const string AnalyticsAdvanced = "analytics.advanced";
    public const string AnalyticsExport = "analytics.export";
    public const string DeploymentOnPrem = "deployment.on_prem";
    public const string ExperiencePageBuilder = "experience.page_builder";
    public const string ExperienceFragments = "experience.fragments";
    public const string ExperienceApprovalRoutes = "experience.approval_routes";
    public const string ExperienceMaxCustomTemplates = "experience.max_custom_templates";
    public const string ExperienceFeatureControls = "experience.feature_controls";
    public const string ExperienceCustomComponents = "experience.custom_components";
    public const string ExperienceMaxBrands = "experience.max_brands";
    public const string ExperienceOrgBranding = "experience.org_branding";
    public const string ExperienceCustomDomain = "experience.custom_domain";
    public const string ExperienceTokenExtensions = "experience.token_extensions";
    public const string ExperiencePersonalization = "experience.personalization";
    public const string ExperienceAudienceVariants = "experience.audience_variants";
    public const string ExperienceGuidedTours = "experience.guided_tours";
    public const string ExperienceBehavioralPersonalization = "experience.behavioral_personalization";
    public const string NotificationsCore = "notifications.core";
    public const string NotificationsTenantSmtp = "notifications.tenant_smtp";
    public const string PeopleDirectory = "people.directory";
    public const string PeopleCustomFields = "people.custom_fields";
    public const string SecurityIpRules = "security.ip_rules";
    public const string SecurityIpRulesScoped = "security.ip_rules_scoped";
    public const string SecuritySessionPolicy = "security.session_policy";
    public const string SecurityAlerts = "security.alerts";
    public const string SecurityAlertsAdvanced = "security.alerts_advanced";
    public const string SecuritySiem = "security.siem";
    public const string SecurityMaxGuestSpaces = "security.max_guest_spaces";

    /// <summary>Sensitive data detection on publish and upload (spec 026 US6).</summary>
    public const string SecurityDataDetection = "security.data_detection";

    /// <summary>Compliance projects, the control library and evidence (spec 026 US7).</summary>
    public const string ComplianceProjects = "compliance.projects";

    /// <summary>Choosing a hosting region other than the platform default at tenant creation (spec 026 FR-008).</summary>
    public const string DeploymentRegionChoice = "deployment.region_choice";

    public const string ContentCore = "content.core";
    public const string ContentCriticalAck = "content.critical_ack";
    public const string ContentMultistepApproval = "content.multistep_approval";
    public const string ContentMaxSites = "content.max_sites";
    public const string ContentCampaigns = "content.campaigns";
    public const string ContentAiTranslation = "content.ai_translation";
    public const string AiAuthoring = "ai.authoring";
    public const string DocsCore = "docs.core";
    public const string DocsControlled = "docs.controlled";
    public const string DocsStorageGb = "docs.storage_gb";
    public const string DocsAssetPortal = "docs.asset_portal";
    public const string DocsViewOnlyWatermark = "docs.view_only_watermark";
    public const string DocsGuestSharing = "docs.guest_sharing";
    public const string DocsOcr = "docs.ocr";

    // Spec 011 R2: retention labels and disposition (Workplace Plus and above), legal holds (Enterprise and Government).
    public const string RecordsRetention = "records.retention";
    public const string RecordsLegalHold = "records.legal_hold";
    public const string PolicyCore = "policy.core";
    public const string PolicyCampaigns = "policy.campaigns";
    public const string AiPolicyQa = "ai.policy_qa";
    public const string SearchCore = "search.core";
    public const string SearchSemantic = "search.semantic";
    public const string SearchFederated = "search.federated";
    public const string SearchAiSummary = "search.ai_summary";
    public const string SearchSavedAlerts = "search.saved_alerts";
}

/// <summary>Central entitlement check for the current tenant (spec 005). Unknown keys fail closed.</summary>
public interface IEntitlementService
{
    Task<bool> IsEnabledAsync(string capabilityKey, CancellationToken ct = default);

    Task<long?> GetLimitAsync(string capabilityKey, CancellationToken ct = default);

    /// <summary>Throws 403 <c>entitlement.not_in_package</c>, distinct from <c>authz.denied</c> (ENT-007).</summary>
    Task DemandAsync(string capabilityKey, CancellationToken ct = default);
}

/// <summary>Used by tenant creation (spec 001 FR-004).</summary>
public interface ISubscriptionProvisioning
{
    Task<bool> PublishedPackageExistsAsync(string packageKey, CancellationToken ct = default);

    /// <summary>Subscribes the current-context tenant to the latest published version of the package.</summary>
    Task AssignInitialPackageAsync(string packageKey, CancellationToken ct = default);
}
