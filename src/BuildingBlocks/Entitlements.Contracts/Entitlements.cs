namespace TeamBeats.Entitlements.Contracts;

/// <summary>Stable capability keys (spec 005 ENT-001). Feature code checks these, never package names (constitution XXI).</summary>
public static class Capabilities
{
    public const string AiAssistant = "ai.assistant";
    public const string AiAssistantMonthlyRequests = "ai.assistant.monthly_requests";
    public const string AiAgentsBasic = "ai.agents.basic";
    public const string AiAgentsWriteTools = "ai.agents.write_tools";
    public const string AiModelChoice = "ai.model_choice";
    public const string OrgHierarchy = "org.hierarchy";
    public const string OrgDelegatedAdmin = "org.delegated_admin";
    public const string OrgMaxUnits = "org.max_units";
    public const string AuthzCustomRoles = "authz.custom_roles";
    public const string IdentityOidc = "identity.oidc";
    public const string IdentitySaml = "identity.saml";
    public const string IdentityScim = "identity.scim";
    public const string IdentityDirectorySync = "identity.directory_sync";
    public const string WorkflowAdvanced = "workflow.advanced";
    public const string IntegrationMicrosoft365 = "integration.microsoft365";
    public const string IntegrationOracleFusion = "integration.oracle_fusion";
    public const string IntegrationSap = "integration.sap";
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
