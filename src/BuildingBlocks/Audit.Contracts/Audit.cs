namespace TeamBeats.Audit.Contracts;

public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
}

/// <summary>Properties marked sensitive are never written to audit change summaries (constitution IV, AUD-005).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute;

/// <summary>
/// One audit record. Tenant, actor, correlation ID, source IP and time are filled from context by the writer.
/// <paramref name="Changes"/> is a typed summary; properties marked <see cref="SensitiveAttribute"/> are dropped.
/// </summary>
public sealed record AuditEntry(
    string Action,
    string TargetType,
    string? TargetId,
    AuditOutcome Outcome = AuditOutcome.Success,
    Guid? TargetOrgUnitId = null,
    object? Changes = null);

/// <summary>The single audit write contract (AUD-001). Writes join the caller's transaction.</summary>
public interface IAuditWriter
{
    /// <summary>Writes to the current tenant's audit stream.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>Writes to the platform stream (tenant creation, lifecycle, break-glass).</summary>
    Task WritePlatformAsync(AuditEntry entry, Guid? subjectTenantId, CancellationToken ct = default);
}

/// <summary>Audit action names from specs 001 to 006. The contract test asserts each one is emitted by a test.</summary>
public static class AuditActions
{
    // 001
    public const string TenantCreated = "tenant.created";
    public const string TenantStatusChanged = "tenant.status_changed";
    public const string TenantSettingsChanged = "tenant.settings_changed";
    public const string TenantAdminAppointed = "tenant.admin_appointed";

    // 002
    public const string OrgUnitCreated = "org.unit_created";
    public const string OrgUnitRenamed = "org.unit_renamed";
    public const string OrgUnitMoved = "org.unit_moved";
    public const string OrgUnitDeactivated = "org.unit_deactivated";
    public const string OrgDirectoryMappingChanged = "org.directory_mapping_changed";
    public const string OrgDirectoryMappingApproved = "org.directory_mapping_approved";
    public const string OrgDirectoryMappingApplied = "org.directory_mapping_applied";
    public const string OrgMembershipAdded = "org.membership_added";
    public const string OrgMembershipEnded = "org.membership_ended";
    public const string OrgMembershipPrimaryChanged = "org.membership_primary_changed";

    // 003
    public const string SignInSucceeded = "auth.sign_in_succeeded";
    public const string SignInFailed = "auth.sign_in_failed";
    public const string SignInRefused = "auth.sign_in_refused";
    public const string SessionRevoked = "auth.session_revoked";
    public const string IdpConfigChanged = "auth.idp_config_changed";
    public const string DirectoryConnectionChanged = "identity.directory_connection_changed";
    public const string UserSynced = "identity.user_synced";
    public const string DisabledByDirectory = "identity.disabled_by_directory";
    public const string ReenabledByDirectory = "identity.reenabled_by_directory";

    // 004
    public const string RoleAssigned = "authz.role_assigned";
    public const string RoleRevoked = "authz.role_revoked";
    public const string RoleDefined = "authz.role_defined";
    public const string RoleChanged = "authz.role_changed";
    public const string ResourcePermissionChanged = "authz.resource_permission_changed";
    public const string InheritanceRuleChanged = "authz.inheritance_rule_changed";

    // 005
    public const string PackagePublished = "ent.package_published";
    public const string SubscriptionChanged = "ent.subscription_changed";
    public const string AddonChanged = "ent.addon_changed";
    public const string OverrideChanged = "ent.override_changed";
    public const string QuotaExhausted = "ent.quota_exhausted";

    // 006
    public const string AuditExported = "audit.exported";
    public const string AuditRetentionChanged = "audit.retention_changed";
    public const string AuditPurged = "audit.purged";
    public const string AuditIntegrityFailed = "audit.integrity_failed";
    public const string MembershipDisabled = "identity.membership_disabled";

    // 006 Connections area (R1)
    public const string ConnectionCreated = "integration.connection_created";
    public const string ConnectionDeleted = "integration.connection_deleted";
    public const string ConnectionFeatureEnabled = "integration.feature_enabled";
    public const string ConnectionFeatureDisabled = "integration.feature_disabled";
    public const string ConnectionConsentGranted = "integration.consent_granted";
    public const string ConnectionConsentRevokedDetected = "integration.consent_revoked_detected";
    public const string ConnectionStatusChanged = "integration.status_changed";
    public const string ConnectionFeatureOptionsChanged = "integration.feature_options_changed";
    public const string OutlookConnected = "integration.outlook_connected";
    public const string OutlookDisconnected = "integration.outlook_disconnected";

    // 032
    public const string PageCreated = "page.created";
    public const string PagePublished = "page.published";
    public const string PageRolledBack = "page.rolled_back";
    public const string PageTemplateChanged = "page.template_changed";
    public const string ComponentEnabled = "component.enabled";
    public const string ComponentDisabled = "component.disabled";
    public const string TemplateReleased = "template.released";
    public const string TemplateDistributed = "template.distributed";
    public const string TemplateDeprecated = "template.deprecated";
    public const string RegionLockChanged = "region.lock_changed";

    // 033
    public const string ThemePublished = "theme.published";
    public const string BrandAssigned = "brand.assigned";
    public const string BrandUnassigned = "brand.unassigned";
    public const string ExperienceLockChanged = "experience.lock_changed";
    public const string BrandAssetUploaded = "brand.asset_uploaded";

    // 008
    public const string ShellPublished = "shell.published";
    public const string NavigationPublished = "navigation.published";
    public const string NavigationNodeChanged = "navigation.node_changed";
    public const string PersonalizationPolicyChanged = "personalization.policy_changed";
    public const string AudienceRuleChanged = "personalization.audience_rule_changed";

    /// <summary>Events that R0 must emit and test. R2 items (add-ons, overrides, quotas, IdP config UI) are listed separately.</summary>
    public static readonly IReadOnlyList<string> RequiredInR0 =
    [
        TenantCreated, TenantStatusChanged, TenantSettingsChanged, TenantAdminAppointed,
        OrgUnitCreated, OrgUnitRenamed, OrgUnitMoved, OrgUnitDeactivated,
        OrgMembershipAdded, OrgMembershipEnded, OrgMembershipPrimaryChanged,
        SignInSucceeded, SignInRefused, SessionRevoked,
        RoleAssigned, RoleRevoked, RoleDefined, ResourcePermissionChanged, InheritanceRuleChanged,
        PackagePublished, SubscriptionChanged,
        AuditExported, AuditRetentionChanged, AuditPurged, MembershipDisabled,
    ];

    /// <summary>Events added in R1 that must be emitted and tested.</summary>
    public static readonly IReadOnlyList<string> RequiredInR1 =
    [
        OrgDirectoryMappingChanged, OrgDirectoryMappingApproved, OrgDirectoryMappingApplied,
        SignInFailed, IdpConfigChanged, DirectoryConnectionChanged, UserSynced, DisabledByDirectory, ReenabledByDirectory,
        ConnectionCreated, ConnectionDeleted, ConnectionFeatureEnabled, ConnectionFeatureDisabled, ConnectionConsentGranted,
        ConnectionConsentRevokedDetected, ConnectionStatusChanged, ConnectionFeatureOptionsChanged, OutlookConnected, OutlookDisconnected,
        PageCreated, PagePublished, PageRolledBack, PageTemplateChanged, ComponentEnabled, ComponentDisabled,
        TemplateReleased, TemplateDistributed, TemplateDeprecated, RegionLockChanged,
        ThemePublished, BrandAssigned, BrandUnassigned, ExperienceLockChanged,
    ];

    public static readonly IReadOnlyList<string> DeferredToLaterReleases =
    [
        RoleChanged, AddonChanged, OverrideChanged, QuotaExhausted, AuditIntegrityFailed,
    ];
}
