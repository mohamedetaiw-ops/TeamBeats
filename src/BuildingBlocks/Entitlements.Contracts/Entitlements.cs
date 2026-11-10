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
    public const string WorkflowCatalogue = "workflow.catalogue";
    public const string WorkflowMaxServices = "workflow.max_services";
    public const string IntegrationMicrosoft365 = "integration.microsoft365";
    public const string IntegrationOracleFusion = "integration.oracle_fusion";
    public const string IntegrationSap = "integration.sap";
    public const string IntegrationConnectors = "integration.connectors";
    public const string IntegrationCustomRest = "integration.custom_rest";
    public const string IntegrationWebhooks = "integration.webhooks";
    public const string IntegrationOnPremAgent = "integration.onprem_agent";
    public const string IntegrationMaxConnections = "integration.max_connections";
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
