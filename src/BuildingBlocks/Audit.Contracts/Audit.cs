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
    public const string DisabledByHr = "identity.disabled_by_hr";
    public const string ReenabledByHr = "identity.reenabled_by_hr";

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
    public const string ConnectionSiteAllowed = "integration.sharepoint_site_allowed";
    public const string ConnectionSiteRemoved = "integration.sharepoint_site_removed";
    public const string OutlookConnected = "integration.outlook_connected";
    public const string OutlookDisconnected = "integration.outlook_disconnected";

    // 017 (R2): the connector runtime
    public const string ConnectorReplay = "integration.replay";
    public const string HostAllowlistChanged = "integration.allowlist_changed";
    public const string CredentialChanged = "integration.credential_changed";
    public const string ConnectionChanged = "integration.connection_changed";
    public const string ConnectionEnabled = "integration.connection_enabled";
    public const string ConnectionDisabled = "integration.connection_disabled";
    public const string ActionDefined = "integration.action_defined";
    public const string ActionChanged = "integration.action_changed";
    public const string WebhookSecretRotated = "integration.webhook_secret_rotated";
    public const string AgentEnrolled = "integration.agent_enrolled";
    public const string AgentRevoked = "integration.agent_revoked";
    public const string ClockDataUploaded = "integration.clock_data_uploaded";

    // 032
    public const string PageCreated = "page.created";
    public const string PagePublished = "page.published";
    public const string PageRolledBack = "page.rolled_back";
    public const string PageTemplateChanged = "page.template_changed";
    public const string PageSubmitted = "page.submitted";
    public const string PageApproved = "page.approved";
    public const string PageReturned = "page.returned";
    public const string PageRouteChanged = "page.route_changed";
    public const string PageScheduled = "page.scheduled";
    public const string PageScheduleCancelled = "page.schedule_cancelled";
    public const string PageScheduleFailed = "page.schedule_failed";
    public const string PageExpired = "page.expired";
    public const string FragmentChanged = "fragment.changed";
    public const string FragmentArchived = "fragment.archived";
    public const string ComponentEnabled = "component.enabled";
    public const string ComponentDisabled = "component.disabled";
    public const string ComponentUpgraded = "component.upgraded";
    public const string ComponentRolledBack = "component.rolled_back";
    public const string TemplateReleased = "template.released";
    public const string TemplateDistributed = "template.distributed";
    public const string TemplateDeprecated = "template.deprecated";
    public const string TemplateImported = "template.imported";
    public const string TemplateExported = "template.exported";
    public const string FeatureControlChanged = "experience.feature_control_changed";
    public const string RegionLockChanged = "region.lock_changed";

    // 033
    public const string ThemePublished = "theme.published";
    public const string ThemeDrafted = "theme.drafted";
    public const string ThemeSubmitted = "theme.submitted";
    public const string ThemeApproved = "theme.approved";
    public const string ThemeReturned = "theme.returned";
    public const string ThemeScheduled = "theme.scheduled";
    public const string ThemeRolledBack = "theme.rolled_back";
    public const string ThemeTokensImported = "theme.tokens_imported";
    public const string ThemeExtensionProposed = "theme.extension_proposed";
    public const string ThemeExtensionApproved = "theme.extension_approved";
    public const string ThemeExtensionRejected = "theme.extension_rejected";
    public const string ThemeExtensionRemoved = "theme.extension_removed";
    public const string BrandAssigned = "brand.assigned";
    public const string BrandUnassigned = "brand.unassigned";
    public const string ExperienceLockChanged = "experience.lock_changed";
    public const string BrandAssetUploaded = "brand.asset_uploaded";
    public const string ExperienceShareChanged = "experience.share_changed";
    public const string ExperienceDelegationChanged = "experience.delegation_changed";
    public const string ExperienceStarterCreated = "experience.starter_created";
    public const string ExperienceStarterPublished = "experience.starter_published";
    public const string TenantDomainAdded = "tenant.domain_added";
    public const string TenantDomainVerified = "tenant.domain_verified";
    public const string TenantDomainChanged = "tenant.domain_changed";
    public const string TenantDomainRemoved = "tenant.domain_removed";
    public const string TenantDomainFailed = "tenant.domain_failed";

    // 008
    public const string ShellPublished = "shell.published";
    public const string HelpPublished = "help.published";
    public const string NavigationPublished = "navigation.published";
    public const string NavigationNodeChanged = "navigation.node_changed";
    public const string PersonalizationPolicyChanged = "personalization.policy_changed";
    public const string AudienceRuleChanged = "personalization.audience_rule_changed";

    // 015
    public const string NotificationSettingsChanged = "notifications.settings_changed";
    public const string NotificationRelayTested = "notifications.relay_tested";
    public const string NotificationTemplateOverridden = "notifications.template_overridden";

    // Spec 015 section 13 (R2): signage screens of a location.
    public const string NotificationSignageScreenRegistered = "notifications.signage_screen_registered";
    public const string NotificationSignageScreenRotated = "notifications.signage_screen_rotated";
    public const string NotificationSignageScreenRevoked = "notifications.signage_screen_revoked";

    /// <summary>Spec 015 FR-018: a quick action was pressed in Teams (done, handed to TeamBeats, closed or refused).</summary>
    public const string NotificationQuickActionUsed = "notifications.quick_action_used";

    // 022
    public const string HrCalendarChanged = "hr.calendar_changed";
    public const string HrLeaveSubmitted = "hr.leave_submitted";
    public const string HrLeaveApproved = "hr.leave_approved";
    public const string HrLeaveRejected = "hr.leave_rejected";
    public const string HrLeaveCancelled = "hr.leave_cancelled";
    public const string HrLeaveShortened = "hr.leave_shortened";
    public const string HrLeaveOnBehalf = "hr.leave_on_behalf";
    public const string HrBalanceAdjusted = "hr.balance_adjusted";
    public const string HrLeaveTypeChanged = "hr.leave_type_changed";
    public const string HrLeavePolicyChanged = "hr.leave_policy_changed";
    public const string HrLeaveRecounted = "hr.leave_recounted";
    public const string HrLeaveSourceChanged = "hr.leave_source_changed";
    public const string HrLeaveHandOffRetried = "hr.leave_handoff_retried";
    public const string HrJourneyTemplateChanged = "hr.journey_template_changed";
    public const string HrJourneyStarted = "hr.journey_started";
    public const string HrJourneyRescheduled = "hr.journey_rescheduled";
    public const string HrJourneyStepCompleted = "hr.journey_step_completed";
    public const string HrJourneyCancelled = "hr.journey_cancelled";
    public const string HrAccessRemovalConfirmed = "hr.access_removal_confirmed";
    public const string HrAccessRemovalEscalated = "hr.access_removal_escalated";
    public const string HrExitInterviewRecorded = "hr.exit_interview_recorded";
    public const string HrJourneySettingsChanged = "hr.journey_settings_changed";
    public const string HrPreboardingSent = "hr.preboarding_sent";
    public const string HrAttendanceSettingsChanged = "hr.attendance_settings_changed";
    public const string HrWorkRuleChanged = "hr.work_rule_changed";
    public const string HrAttendanceExported = "hr.attendance_exported";
    public const string HrAttendanceRequested = "hr.attendance_requested";
    public const string HrAttendanceCorrected = "hr.attendance_corrected";
    public const string HrCompensationCredited = "hr.compensation_credited";
    public const string HrAttendanceRequestRejected = "hr.attendance_request_rejected";
    public const string HrLetterTypeChanged = "hr.letter_type_changed";
    public const string HrLetterSettingsChanged = "hr.letter_settings_changed";
    public const string HrLetterRequested = "hr.letter_requested";
    public const string HrLetterIssued = "hr.letter_issued";
    public const string HrLetterRejected = "hr.letter_rejected";
    public const string HrLetterVerified = "hr.letter_verified";
    public const string HrDataChangeSettingsChanged = "hr.data_change_settings_changed";
    public const string HrDataChangeRequested = "hr.data_change_requested";
    public const string HrDataChangeApproved = "hr.data_change_approved";
    public const string HrDataChangeRejected = "hr.data_change_rejected";
    public const string HrDataChangeWrittenBack = "hr.data_change_written_back";
    public const string HrDataChangeWriteFailed = "hr.data_change_write_failed";
    public const string HrExpensePolicyChanged = "hr.expense_policy_changed";
    public const string HrExpenseRatesChanged = "hr.expense_rates_changed";
    public const string HrExpenseSubmitted = "hr.expense_submitted";
    public const string HrExpenseApproved = "hr.expense_approved";
    public const string HrExpenseRejected = "hr.expense_rejected";
    public const string HrExpenseHandedOff = "hr.expense_handed_off";

    // 037
    public const string CalendarEventCreated = "calendar.event_created";
    public const string CalendarEventChanged = "calendar.event_changed";
    public const string CalendarEventCancelled = "calendar.event_cancelled";
    public const string CalendarEventDeleted = "calendar.event_deleted";
    public const string CalendarSettingsChanged = "calendar.settings_changed";
    public const string CalendarTeamViewed = "calendar.team_viewed";

    // 019
    public const string HrSettingsChanged = "hr.settings_changed";
    public const string HrMatchLinked = "hr.match_linked";
    public const string HrMatchUnlinked = "hr.match_unlinked";
    public const string HrSyncHeld = "hr.sync_held";
    public const string HrSyncConfirmed = "hr.sync_confirmed";
    public const string HrChangeApplied = "hr.change_applied";
    public const string HrPayslipViewed = "hr.payslip_viewed";
    public const string HrPayslipDownloaded = "hr.payslip_downloaded";
    public const string HrOrgCodeMapped = "hr.org_code_mapped";

    // 010
    public const string PeopleVisibilityChanged = "people.visibility_changed";
    public const string PeopleFieldSourceChanged = "people.field_source_changed";
    public const string PeopleManagerSetManually = "people.manager_set_manually";
    public const string PeopleProfileUpdated = "people.profile_updated";
    public const string PeopleReportingCycleDetected = "people.reporting_cycle_detected";
    public const string PeopleSettingsChanged = "people.settings_changed";
    public const string PeoplePhotoRemoved = "people.photo_removed";

    // Spec 026 (R1): security policy and network refusals.
    public const string SecurityPolicyChanged = "security.policy_changed";
    public const string SecurityFourEyesChanged = "security.four_eyes_changed";
    public const string SecurityChangeRequested = "security.change_requested";
    public const string SecurityChangeApproved = "security.change_approved";
    public const string SecurityChangeRejected = "security.change_rejected";
    public const string SecurityChangeWithdrawn = "security.change_withdrawn";
    public const string SecurityChangeExpired = "security.change_expired";

    // Spec 026 US8: customer-managed keys. Connected and changed by an administrator; rotated, revoked and restored as
    // the key check finds them.
    public const string SecurityKeyConnected = "security.key_connected";
    public const string SecurityKeyRotated = "security.key_rotated";
    public const string SecurityKeyRevoked = "security.key_revoked";
    public const string SecurityKeyRestored = "security.key_restored";
    public const string SecurityKeyUnreachable = "security.key_unreachable";

    // Spec 030 (R2): offline storage limits.
    public const string MobileOfflinePolicyChanged = "mobile.offline_policy_changed";

    // Spec 030 US4 (R2): kiosk devices and badges.
    public const string DeviceKioskEnrolled = "device.kiosk_enrolled";
    public const string DeviceKioskChanged = "device.kiosk_changed";
    public const string DeviceKioskRevoked = "device.kiosk_revoked";
    public const string DeviceKioskSignIn = "device.kiosk_signin";
    public const string DeviceKioskLockout = "device.kiosk_lockout";
    public const string DeviceBadgeIssued = "device.badge_issued";
    public const string DeviceBadgePinReset = "device.badge_pin_reset";
    // Spec 035 (R2): apps, credentials, consents and tokens of the public API and MCP server.
    public const string ApiAppRegistered = "api.app_registered";
    public const string ApiAppChanged = "api.app_changed";
    public const string ApiAppDisabled = "api.app_disabled";
    public const string ApiCredentialAdded = "api.credential_added";
    public const string ApiCredentialRevoked = "api.credential_revoked";
    public const string ApiConsentGranted = "api.consent_granted";
    public const string ApiConsentRevoked = "api.consent_revoked";
    public const string ApiTokenFamilyRevoked = "api.token_family_revoked";
    public const string ApiTokenRefused = "api.token_refused";
    public const string ApiSettingsChanged = "api.tenant_switch_changed";
    public const string ApiRoleAssignedToApp = "api.role_assigned_to_app";
    public const string ApiVerifiedClientChanged = "api.verified_client_changed";
    public const string ApiGalleryAppChanged = "api.gallery_app_changed";
    public const string ApiGalleryAppAdded = "api.gallery_app_added";
    public const string ApiClientBlocked = "api.client_blocked";
    public const string ApiClientUnblocked = "api.client_unblocked";
    public const string ApiTokensRevoked = "api.tokens_revoked";
    public const string ApiSubscriptionCreated = "api.subscription_created";
    public const string ApiSubscriptionPaused = "api.subscription_paused";
    public const string ApiSubscriptionResumed = "api.subscription_resumed";
    public const string ApiSubscriptionDeleted = "api.subscription_deleted";
    public const string SecurityRequestBlockedNetwork = "security.request_blocked_network";
    public const string SecurityMfaMissing = "security.mfa_missing";
    public const string SecurityStepUpCompleted = "security.step_up_completed";
    public const string SecurityStepUpFailed = "security.step_up_failed";
    public const string SecurityAlertRaised = "security.alert_raised";
    public const string SecurityAlertHandled = "security.alert_handled";

    // Spec 026 (R2): SIEM streaming.
    public const string SecuritySiemConfigured = "security.siem_configured";
    public const string GuestReviewed = "guest.reviewed";
    public const string SecurityAccessReviewStarted = "security.access_review_started";
    public const string SecurityAccessReviewDecided = "security.access_review_decided";
    public const string SecurityAccessReviewCompleted = "security.access_review_completed";
    public const string SecuritySiemDeliveryFailed = "security.siem_delivery_failed";
    public const string SecurityAlertSettingsChanged = "security.alert_settings_changed";
    public const string SecurityDetectionChanged = "security.detection_changed";
    public const string SecurityDetectionPatternChanged = "security.detection_pattern_changed";
    public const string SecuritySensitiveDataConfirmed = "security.sensitive_data_confirmed";

    // 026 R2 compliance projects (US7)
    public const string ComplianceFrameworkChanged = "compliance.framework_changed";
    public const string ComplianceProjectCreated = "compliance.project_created";
    public const string ComplianceProjectChanged = "compliance.project_changed";
    public const string ComplianceProjectShared = "compliance.project_shared";
    public const string ComplianceControlChanged = "compliance.control_changed";
    public const string ComplianceControlStatusChanged = "compliance.control_status_changed";
    public const string ComplianceEvidenceAttached = "compliance.evidence_attached";

    // 026 FR-020 and 001: platform operator access through break-glass, written to the tenant stream and the platform stream.
    public const string BreakGlassOpened = "platform.break_glass_opened";
    public const string BreakGlassEnded = "platform.break_glass_ended";
    public const string BreakGlassNetworkReset = "platform.break_glass_network_reset";
    public const string WorkflowDefinitionPublished = "workflow.definition_published";
    public const string WorkflowTaskCompleted = "workflow.task_completed";
    public const string WorkflowInstanceWithdrawn = "workflow.instance_withdrawn";

    // 024 (R2 part): incidents on connector steps
    public const string ProcessIncidentRaised = "process.incident_raised";
    public const string ProcessIncidentRetried = "process.incident_retried";
    public const string ProcessIncidentSkipped = "process.incident_skipped";
    public const string ProcessIncidentCancelled = "process.incident_cancelled";

    // 024 (R2): process models and instances started or cancelled by an operator.
    public const string ProcessModelSaved = "process.model_saved";
    public const string ProcessInstanceStarted = "process.instance_started";
    public const string ProcessInstanceCancelled = "process.instance_cancelled";
    public const string ProcessVariablesChanged = "process.variables_changed";
    public const string DecisionModelSaved = "decision.model_saved";
    public const string DecisionPublished = "decision.published";

    // Spec 016 (R1): services, requests, tasks and delegation.
    public const string ServiceCreated = "service.created";
    public const string ServiceChanged = "service.changed";
    public const string ServicePublished = "service.published";
    public const string ServiceRetired = "service.retired";
    public const string ServiceCategoryChanged = "service.category_changed";
    public const string RequestSubmitted = "request.submitted";
    public const string RequestCancelled = "request.cancelled";
    public const string RequestResubmitted = "request.resubmitted";
    public const string TaskApproved = "task.approved";
    public const string TaskRejected = "task.rejected";
    public const string TaskReturned = "task.returned";
    public const string DelegationSet = "delegation.set";
    public const string DelegationChanged = "delegation.changed";
    public const string DelegationEnded = "delegation.ended";

    // Spec 016 (R2): requests started by triggers and start service steps, chains stopped past depth 5, steps auto-approved by
    // rule and breached service levels (section 13).
    public const string RequestStartedByTrigger = "request.started_by_trigger";
    public const string RequestChainStopped = "request.chain_stopped";
    public const string RequestAutoApproved = "request.auto_approved";
    public const string RequestServiceLevelBreached = "request.sla_breached";

    // Spec 009 (R1): content lifecycle, sites and settings (section 13). Every lifecycle transition is audited (FR-002).
    public const string ContentCreated = "content.created";
    public const string ContentPublished = "content.published";
    public const string ContentScheduled = "content.scheduled";
    public const string ContentUnpublished = "content.unpublished";
    public const string ContentArchived = "content.archived";
    public const string ContentExpired = "content.expired";
    public const string ContentSettingsChanged = "content.settings_changed";

    /// <summary>Spec 017 US8: a communicator shared a content link to a Slack channel.</summary>
    public const string ContentShared = "content.shared";
    public const string ContentSubmitted = "content.submitted";
    public const string ContentApproved = "content.approved";
    public const string ContentReturned = "content.returned";
    public const string ContentWithdrawn = "content.withdrawn";
    public const string ContentTranslated = "content.translated";
    public const string ContentTranslationApproved = "content.translation_approved";
    public const string ContentCampaignCreated = "content.campaign_created";
    public const string ContentCampaignStarted = "content.campaign_started";
    public const string ContentCampaignCancelled = "content.campaign_cancelled";
    public const string ContentCampaignStepSent = "content.campaign_step_sent";
    public const string ContentRetentionApplied = "content.retention_applied";
    public const string ContentScreenCreated = "content.screen_created";
    public const string ContentScreenRotated = "content.screen_rotated";
    public const string ContentScreenRemoved = "content.screen_removed";
    public const string ContentCriticalSent = "content.critical_sent";
    public const string ContentCriticalDenied = "content.critical_denied";
    public const string ContentAcknowledged = "content.acknowledged";
    public const string ContentAckReportExported = "content.ack_report_exported";
    public const string ContentReviewed = "content.reviewed";
    public const string ContentOwnerReassigned = "content.owner_reassigned";
    public const string ContentVersionRestored = "content.version_restored";
    public const string ContentCommentModerated = "content.comment_moderated";
    public const string SiteCreated = "site.created";
    public const string SiteChanged = "site.changed";
    public const string SiteArchived = "site.archived";
    public const string SiteRestored = "site.restored";

    // 011 (R1): libraries, files and permission breaks (section 13).
    public const string DocLibraryCreated = "doc.library_created";
    public const string DocLibraryChanged = "doc.library_changed";
    public const string DocTypeChanged = "doc.type_changed";
    public const string DocSettingsChanged = "doc.settings_changed";
    public const string DocUploaded = "doc.uploaded";
    public const string DocMetadataChanged = "doc.metadata_changed";
    public const string DocDownloaded = "doc.downloaded";
    public const string DocPermissionsBroken = "doc.permissions_broken";
    public const string DocPermissionsChanged = "doc.permissions_changed";
    public const string DocCheckedOut = "doc.checked_out";
    public const string DocCheckedIn = "doc.checked_in";
    public const string DocForcedCheckIn = "doc.forced_check_in";
    public const string DocVersionCreated = "doc.version_created";
    public const string DocRestored = "doc.version_restored";
    public const string DocDeleted = "doc.deleted";
    public const string DocRestoredFromBin = "doc.undeleted";
    public const string DocPurged = "doc.purged";
    public const string DocReleaseSubmitted = "doc.release_submitted";
    public const string DocReleaseWithdrawn = "doc.release_withdrawn";
    public const string DocReleaseApproved = "doc.release_approved";
    public const string DocReleaseReturned = "doc.release_returned";
    public const string DocVersionEffective = "doc.version_effective";
    public const string DocReviewed = "doc.reviewed";
    public const string DocReadConfirmed = "doc.read_confirmed";

    // Spec 011 R2 section 13: retention labels, disposition and legal holds.
    public const string RecordsLabelCreated = "records.label_created";
    public const string RecordsLabelChanged = "records.label_changed";
    public const string RecordsLabelApplied = "records.label_applied";
    public const string RecordsDispositionApproved = "records.disposition_approved";
    public const string RecordsDispositionKept = "records.disposition_kept";
    public const string RecordsArchived = "records.archived";
    public const string RecordsHoldApplied = "records.hold_applied";
    public const string RecordsHoldReleased = "records.hold_released";
    public const string RecordsDeletionBlocked = "records.deletion_blocked";

    // Spec 011 R3: records declaration and disposition certificates.
    public const string RecordsDeclared = "records.declared";
    public const string RecordsUndeclared = "records.undeclared";
    public const string RecordsCertificateIssued = "records.certificate_issued";
    public const string DocSharedWithGuest = "doc.shared_with_guest";
    public const string DocGuestShareRevoked = "doc.guest_share_revoked";
    public const string DocGuestViewed = "doc.guest_viewed";
    public const string DocImportStarted = "doc.import_started";
    public const string DocImportCompleted = "doc.import_completed";
    public const string DocOfficeEditStarted = "doc.office_edit_started";
    public const string DocOfficeEditEnded = "doc.office_edit_ended";
    public const string DocConnectedOpened = "doc.connected_opened";
    public const string DocConnectedUploaded = "doc.connected_uploaded";
    public const string DocConnectedRenamed = "doc.connected_renamed";
    public const string DocConnectedSettings = "doc.connected_settings";
    public const string DocConnectedRemoved = "doc.connected_removed";
    public const string DocLabelMapped = "doc.label_mapped";
    public const string AssetCollectionCreated = "assets.collection_created";
    public const string AssetCollectionChanged = "assets.collection_changed";
    public const string AssetUploaded = "assets.uploaded";
    public const string AssetChanged = "assets.changed";
    public const string AssetExpired = "assets.expired";
    public const string AssetAccessRequested = "assets.access_requested";
    public const string AssetAccessDecided = "assets.access_decided";
    public const string PolicyCreated = "policy.created";
    public const string PolicyChanged = "policy.changed";
    public const string PolicyDraftSaved = "policy.draft_saved";
    public const string PolicySubmitted = "policy.submitted";
    public const string PolicyWithdrawn = "policy.withdrawn";
    public const string PolicyApproved = "policy.approved";
    public const string PolicyReturned = "policy.returned";
    public const string PolicyPublished = "policy.published";
    public const string PolicySuperseded = "policy.superseded";
    public const string PolicyRetired = "policy.retired";
    public const string PolicyCampaignLaunched = "policy.campaign_launched";
    public const string PolicyCampaignReminded = "policy.campaign_reminded";
    public const string PolicyCampaignClosed = "policy.campaign_closed";
    public const string PolicyAttested = "policy.attested";
    public const string PolicyTrainingLinked = "policy.training_linked";
    public const string PolicyReviewCompleted = "policy.review_completed";
    public const string PolicyCheckChanged = "policy.check_changed";
    public const string PolicyCheckFailed = "policy.check_failed";
    public const string PolicyCheckViewed = "policy.check_viewed";
    public const string PolicyCheckReset = "policy.check_reset";
    public const string PolicyExemptionRequested = "policy.exemption_requested";
    public const string PolicyExemptionApproved = "policy.exemption_approved";
    public const string PolicyExemptionRejected = "policy.exemption_rejected";
    public const string PolicyExemptionExpired = "policy.exemption_expired";
    public const string PolicyControlsChanged = "policy.controls_changed";
    public const string PolicyEvidenceExported = "policy.evidence_exported";
    public const string PolicyEvidenceShared = "policy.evidence_shared";
    public const string PolicyEvidenceDownloaded = "policy.evidence_downloaded";
    public const string PolicyPaperImported = "policy.paper_imported";
    public const string PolicyAttestationPaused = "policy.attestation_paused";

    // 013
    public const string AiPolicyChanged = "ai.policy_changed";
    public const string AiRouteChangeProposed = "ai.route_change_proposed";
    public const string AiRouteChangeOverridden = "ai.route_change_overridden";
    public const string AiRouteChangeApplied = "ai.route_change_applied";
    public const string AiRouteChangeRejected = "ai.route_change_rejected";
    public const string AiCatalogueChanged = "ai.catalogue_changed";
    public const string AiLoggingWindowOpened = "ai.logging_window_opened";
    public const string AiLoggingWindowClosed = "ai.logging_window_closed";
    public const string AiBudgetChanged = "ai.budget_changed";
    public const string AiBudgetReached = "ai.budget_reached";
    public const string AiOrgPolicyChanged = "ai.org_policy_changed";
    public const string AiEndpointRegistered = "ai.endpoint_registered";
    public const string AiEndpointTested = "ai.endpoint_tested";
    public const string AiEndpointEnabled = "ai.endpoint_enabled";
    public const string AiEndpointDisabled = "ai.endpoint_disabled";
    public const string AiUsageExported = "ai.usage_exported";

    // 012
    public const string SearchBestBetChanged = "search.best_bet_changed";
    public const string SearchSynonymsChanged = "search.synonyms_changed";
    public const string SearchRetrievalChangeProposed = "search.retrieval_change_proposed";
    public const string SearchRetrievalChangeDecided = "search.retrieval_change_decided";
    public const string SearchRetrievalChangeOverridden = "search.retrieval_change_overridden";
    public const string SearchRetrievalChangeCancelled = "search.retrieval_change_cancelled";
    public const string SearchFederatedChanged = "search.federated_changed";
    public const string SearchAiSummaryChanged = "search.ai_summary_changed";
    public const string SearchQueryTextChanged = "search.query_text_changed";

    // 014
    public const string AssistantHistoryDeleted = "assistant.history_deleted";
    public const string AssistantRetentionChanged = "assistant.retention_changed";
    public const string AssistantGuestsChanged = "assistant.guests_changed";
    public const string AssistantFileAdded = "assistant.file_added";
    public const string AssistantFileRefused = "assistant.file_refused";

    // Specs 020 and 027 (R1): agents and the tenant agent policy.
    public const string AgentInstalledFromTemplate = "agent.installed_from_template";
    public const string AgentCreated = "agent.created";
    public const string AgentCloned = "agent.cloned";
    public const string AgentAccessRequested = "agent.access_requested";
    public const string AgentAccessGranted = "agent.access_granted";
    public const string AgentAccessDenied = "agent.access_denied";
    public const string AgentMemoryAccepted = "agent.memory_accepted";
    public const string AgentMemoryDeleted = "agent.memory_deleted";
    public const string AgentTemplateUpdateApplied = "agent.template_update_applied";
    public const string AgentTemplateSecurityApplied = "agent.template_security_applied";
    public const string AgentAudienceChanged = "agent.audience_changed";
    public const string AgentOwnerChanged = "agent.owner_changed";
    public const string AgentPolicyChanged = "agent.policy_changed";
    public const string AgentToolCalled = "agent.tool_called";

    /// <summary>Spec 035 US3 scenarios 2 and 8: an external AI client was refused by policy, the kill switch or external access off.</summary>
    public const string AgentExternalCallRefused = "agent.external_call_refused";
    public const string AgentToolDenied = "agent.tool_denied";
    public const string AgentInjectionDetected = "agent.injection_detected";
    public const string AgentActionConfirmed = "agent.action_confirmed";
    public const string AgentActionRejected = "agent.action_rejected";

    /// <summary>An approver approved an agent action that waited in the inbox (spec 027 US4, R2).</summary>
    public const string AgentActionApproved = "agent.action_approved";

    /// <summary>An agent action waited past its due time and was cancelled (spec 027 US4 scenario 3).</summary>
    public const string AgentActionExpired = "agent.action_expired";
    public const string AgentVersionSubmitted = "agent.version_submitted";
    public const string AgentVersionApproved = "agent.version_approved";
    public const string AgentVersionRejected = "agent.version_rejected";
    public const string AgentVersionPublished = "agent.version_published";
    public const string AgentRolledBack = "agent.rolled_back";
    public const string AgentSuspended = "agent.suspended";
    public const string AgentResumed = "agent.resumed";
    public const string AgentRetired = "agent.retired";
    public const string AgentKillSwitch = "agent.kill_switch";
    public const string AgentTokensRevoked = "agent.tokens_revoked";

    // Spec 027 R2 Agent Security Center (FR-023).
    public const string AgentAlertRaised = "agent.alert_raised";
    public const string AgentAlertDismissed = "agent.alert_dismissed";
    public const string AgentIncidentOpened = "agent.incident_opened";
    public const string AgentIncidentUpdated = "agent.incident_updated";
    public const string AgentIncidentClosed = "agent.incident_closed";
    public const string AgentEvidenceExported = "agent.evidence_exported";
    public const string EvalSuiteCreated = "eval.suite_created";
    public const string EvalSuiteChanged = "eval.suite_changed";
    public const string EvalDatasetVersionCreated = "eval.dataset_version_created";
    public const string EvalDatasetImported = "eval.dataset_imported";
    public const string EvalPolicyChanged = "eval.policy_changed";
    public const string EvalPersonaCreated = "eval.persona_created";
    public const string EvalGateDecided = "eval.gate_decided";
    public const string EvalGateOverridden = "eval.gate_overridden";
    public const string EvalRubricCalibrated = "eval.rubric_calibrated";
    public const string EvalRubricDemoted = "eval.rubric_demoted";
    public const string EvalReviewVerdict = "eval.review_verdict";
    public const string EvalReviewPromoted = "eval.review_promoted";
    public const string EvalComparisonProposed = "eval.comparison_proposed";
    public const string EvalComparisonDecided = "eval.comparison_decided";
    public const string EvalComparisonStopped = "eval.comparison_stopped";
    public const string EvalOutcomeBaselineChanged = "eval.outcome_baseline_changed";

    // 023
    public const string EngagementCommunityCreated = "engagement.community_created";
    public const string EngagementCommunityChanged = "engagement.community_changed";
    public const string EngagementCommunityArchived = "engagement.community_archived";
    public const string EngagementMemberChanged = "engagement.member_changed";
    public const string EngagementPostPinned = "engagement.post_pinned";
    public const string EngagementContentApproved = "engagement.content_approved";
    public const string EngagementContentHidden = "engagement.content_hidden";
    public const string EngagementContentRemoved = "engagement.content_removed";
    public const string EngagementMemberMuted = "engagement.member_muted";
    public const string EngagementMemberUnmuted = "engagement.member_unmuted";
    public const string EngagementKeywordsChanged = "engagement.keywords_changed";
    public const string EngagementSpeakUpAnswered = "engagement.speak_up_answered";
    public const string EngagementSpeakUpDeclined = "engagement.speak_up_declined";
    public const string EngagementSurveyPublished = "engagement.survey_published";
    public const string EngagementSurveyClosed = "engagement.survey_closed";
    public const string EngagementSurveyThresholdChanged = "engagement.survey_threshold_changed";
    public const string EngagementPointsConfigChanged = "engagement.points_config_changed";
    public const string EngagementRecognitionCatalogueChanged = "engagement.recognition_catalogue_changed";
    public const string EngagementAwardOpened = "engagement.award_opened";
    public const string EngagementAwardDecided = "engagement.award_decided";
    public const string EngagementCelebrationSettingsChanged = "engagement.celebration_settings_changed";

    // 028
    public const string LearningCoursePublished = "learning.course_published";
    public const string LearningCourseRetired = "learning.course_retired";
    public const string LearningPathPublished = "learning.path_published";
    public const string LearningAttemptsGranted = "learning.attempts_granted";
    public const string LearningAssignmentCreated = "learning.assignment_created";
    public const string LearningAssignmentWithdrawn = "learning.assignment_withdrawn";
    public const string LearningEvidenceExported = "learning.evidence_exported";

    // 029
    public const string SpacesCreated = "spaces.created";
    public const string SpacesChanged = "spaces.changed";
    public const string SpacesArchived = "spaces.archived";
    public const string SpacesRestored = "spaces.restored";
    public const string SpacesMemberAdded = "spaces.member_added";
    public const string SpacesMemberRemoved = "spaces.member_removed";
    public const string SpacesOwnerReassigned = "spaces.owner_reassigned";
    public const string SpacesSettingsChanged = "spaces.settings_changed";
    public const string SpacesTemplateChanged = "spaces.template_changed";
    public const string SpacesEventChanged = "spaces.event_changed";
    public const string SpacesGuestInvited = "spaces.guest_invited";
    public const string SpacesGuestExpired = "spaces.guest_expired";
    public const string SpacesGuestRemoved = "spaces.guest_removed";

    // 031
    public const string BookingResourceCreated = "booking.resource_created";
    public const string BookingResourceRemoved = "booking.resource_removed";
    public const string BookingRulesChanged = "booking.rules_changed";
    public const string BookingCancelledByAdmin = "booking.cancelled_by_admin";
    public const string BookingSettingsChanged = "booking.settings_changed";
    public const string BookingSiteChanged = "booking.site_changed";
    public const string BookingSiteClosed = "booking.site_closed";
    public const string BookingSiteReopened = "booking.site_reopened";

    // 021
    public const string AnalyticsPrivacySettingChanged = "analytics.privacy_setting_changed";
    public const string AnalyticsEstimatesChanged = "analytics.estimates_changed";
    public const string AnalyticsExportRequested = "analytics.export_requested";
    public const string AnalyticsExportCompleted = "analytics.export_completed";
    public const string AnalyticsExportDownloaded = "analytics.export_downloaded";
    public const string AnalyticsScheduleCreated = "analytics.schedule_created";
    public const string AnalyticsScheduleChanged = "analytics.schedule_changed";

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
        ConnectionConsentRevokedDetected, ConnectionStatusChanged, ConnectionFeatureOptionsChanged, ConnectionSiteAllowed, ConnectionSiteRemoved, OutlookConnected, OutlookDisconnected,
        PageCreated, PagePublished, PageRolledBack, PageTemplateChanged, PageSubmitted, PageApproved, PageReturned, PageRouteChanged, PageScheduled, PageScheduleCancelled, PageScheduleFailed, PageExpired, FragmentChanged, FragmentArchived, ComponentEnabled, ComponentDisabled, ComponentUpgraded, ComponentRolledBack,
        TemplateReleased, TemplateDistributed, TemplateDeprecated, TemplateImported, TemplateExported, FeatureControlChanged, RegionLockChanged,
        ThemePublished, ThemeDrafted, ThemeSubmitted, ThemeApproved, ThemeReturned, ThemeScheduled, ThemeRolledBack, ThemeTokensImported, ThemeExtensionProposed, ThemeExtensionApproved, ThemeExtensionRejected, ThemeExtensionRemoved, BrandAssigned, BrandUnassigned, ExperienceLockChanged,
        NotificationSettingsChanged, NotificationRelayTested, NotificationTemplateOverridden, NotificationSignageScreenRegistered, NotificationSignageScreenRotated, NotificationSignageScreenRevoked, NotificationQuickActionUsed,
        HrCalendarChanged, HrSettingsChanged, HrMatchLinked, HrMatchUnlinked, HrSyncHeld, HrSyncConfirmed, HrChangeApplied, HrOrgCodeMapped, DisabledByHr, ReenabledByHr,
        HrPayslipViewed, HrPayslipDownloaded,
        HrLeaveSubmitted, HrLeaveApproved, HrLeaveRejected, HrLeaveCancelled, HrLeaveShortened, HrLeaveOnBehalf, HrBalanceAdjusted, HrLeaveTypeChanged, HrLeavePolicyChanged,
        HrLeaveRecounted, HrLeaveSourceChanged, HrLeaveHandOffRetried,
        HrJourneyTemplateChanged, HrJourneyStarted, HrJourneyRescheduled, HrJourneyStepCompleted, HrJourneyCancelled, HrAccessRemovalConfirmed, HrAccessRemovalEscalated, HrExitInterviewRecorded, HrJourneySettingsChanged, HrPreboardingSent, HrAttendanceSettingsChanged, HrWorkRuleChanged, HrAttendanceExported, HrAttendanceRequested, HrAttendanceCorrected, HrCompensationCredited, HrAttendanceRequestRejected, HrLetterTypeChanged, HrLetterSettingsChanged, HrLetterRequested, HrLetterIssued, HrLetterRejected, HrLetterVerified, HrDataChangeSettingsChanged, HrDataChangeRequested, HrDataChangeApproved, HrDataChangeRejected, HrDataChangeWrittenBack, HrDataChangeWriteFailed, HrExpensePolicyChanged, HrExpenseRatesChanged, HrExpenseSubmitted, HrExpenseApproved, HrExpenseRejected, HrExpenseHandedOff,
        CalendarEventCreated, CalendarEventChanged, CalendarEventCancelled, CalendarEventDeleted, CalendarSettingsChanged, CalendarTeamViewed,
        PeopleVisibilityChanged, PeopleFieldSourceChanged, PeopleManagerSetManually, PeopleProfileUpdated, PeopleReportingCycleDetected, PeopleSettingsChanged, PeoplePhotoRemoved,
        SecurityPolicyChanged, SecurityFourEyesChanged, SecurityChangeRequested, SecurityChangeApproved, SecurityChangeRejected, SecurityChangeWithdrawn,
        SecurityChangeExpired, SecurityKeyConnected, SecurityKeyRotated, SecurityKeyRevoked, SecurityKeyRestored, SecurityKeyUnreachable,
        MobileOfflinePolicyChanged, DeviceKioskEnrolled, DeviceKioskChanged, DeviceKioskRevoked, DeviceKioskSignIn, DeviceKioskLockout,
        ApiAppRegistered, ApiAppChanged, ApiAppDisabled, ApiCredentialAdded, ApiCredentialRevoked, ApiConsentGranted, ApiConsentRevoked,
        ApiTokenFamilyRevoked, ApiTokenRefused, ApiSettingsChanged, ApiRoleAssignedToApp, ApiVerifiedClientChanged, ApiGalleryAppChanged, ApiGalleryAppAdded, ApiClientBlocked, ApiClientUnblocked, ApiTokensRevoked,
        DeviceBadgeIssued, DeviceBadgePinReset, SecurityRequestBlockedNetwork, SecurityMfaMissing, SecurityStepUpCompleted, SecurityStepUpFailed, SecurityAlertRaised, SecurityAlertHandled,
        WorkflowDefinitionPublished, WorkflowTaskCompleted, WorkflowInstanceWithdrawn,
        ServiceCreated, ServiceChanged, ServicePublished, ServiceRetired, ServiceCategoryChanged, RequestSubmitted, RequestCancelled, RequestResubmitted,
        TaskApproved, TaskRejected, TaskReturned, DelegationSet, DelegationChanged, DelegationEnded,
        ContentCreated, ContentPublished, ContentScheduled, ContentUnpublished, ContentArchived, ContentExpired, ContentSettingsChanged, ContentShared, ContentSubmitted, ContentApproved, ContentReturned, ContentWithdrawn, ContentTranslated, ContentTranslationApproved,
        ContentCampaignCreated, ContentCampaignStarted, ContentCampaignCancelled, ContentCampaignStepSent,
        ContentRetentionApplied, ContentScreenCreated, ContentScreenRotated, ContentScreenRemoved,
        ContentCriticalSent, ContentCriticalDenied, ContentAcknowledged, ContentAckReportExported,
        ContentReviewed, ContentOwnerReassigned, ContentVersionRestored, ContentCommentModerated,
        SiteCreated, SiteChanged, SiteArchived, SiteRestored,
        DocLibraryCreated, DocLibraryChanged, DocTypeChanged, DocSettingsChanged, DocUploaded, DocMetadataChanged, DocDownloaded,
        DocPermissionsBroken, DocPermissionsChanged, DocCheckedOut, DocCheckedIn, DocForcedCheckIn, DocVersionCreated, DocRestored, DocDeleted,
        DocRestoredFromBin, DocPurged, DocReleaseSubmitted, DocReleaseWithdrawn, DocReleaseApproved, DocReleaseReturned, DocVersionEffective, DocReviewed,
        DocReadConfirmed,
        RecordsLabelCreated, RecordsLabelChanged, RecordsLabelApplied, RecordsDispositionApproved, RecordsDispositionKept, RecordsArchived,
        RecordsHoldApplied, RecordsHoldReleased, RecordsDeletionBlocked, RecordsDeclared, RecordsUndeclared, RecordsCertificateIssued, DocSharedWithGuest, DocGuestShareRevoked, DocGuestViewed,
        DocImportStarted, DocImportCompleted, DocOfficeEditStarted, DocOfficeEditEnded, DocConnectedOpened,
        DocConnectedUploaded, DocConnectedRenamed, DocConnectedSettings, DocConnectedRemoved, DocLabelMapped,
        AssetCollectionCreated, AssetCollectionChanged, AssetUploaded, AssetChanged, AssetExpired, AssetAccessRequested, AssetAccessDecided,
        PolicyCreated, PolicyChanged, PolicyDraftSaved, PolicySubmitted, PolicyWithdrawn, PolicyApproved, PolicyReturned, PolicyPublished, PolicySuperseded,
        PolicyRetired, PolicyCampaignLaunched, PolicyCampaignReminded, PolicyCampaignClosed, PolicyAttested, PolicyTrainingLinked, PolicyReviewCompleted, PolicyCheckChanged, PolicyCheckFailed, PolicyCheckViewed, PolicyCheckReset,
        PolicyExemptionRequested, PolicyExemptionApproved, PolicyExemptionRejected, PolicyExemptionExpired, PolicyControlsChanged,
        PolicyEvidenceExported, PolicyEvidenceShared, PolicyEvidenceDownloaded, PolicyPaperImported, PolicyAttestationPaused,
        AiPolicyChanged, AiRouteChangeProposed, AiRouteChangeOverridden, AiRouteChangeApplied, AiRouteChangeRejected, AiCatalogueChanged, AiLoggingWindowOpened, AiLoggingWindowClosed,
        SearchBestBetChanged, SearchSynonymsChanged, SearchRetrievalChangeProposed, SearchRetrievalChangeDecided, SearchRetrievalChangeOverridden,
        SearchRetrievalChangeCancelled, SearchFederatedChanged, SearchAiSummaryChanged, SearchQueryTextChanged,
        AssistantHistoryDeleted, AssistantRetentionChanged, AssistantGuestsChanged, AssistantFileAdded, AssistantFileRefused,
        AgentInstalledFromTemplate, AgentCreated, AgentCloned, AgentAccessRequested, AgentAccessGranted, AgentAccessDenied, AgentMemoryAccepted, AgentMemoryDeleted, AgentTemplateUpdateApplied, AgentTemplateSecurityApplied, AgentAudienceChanged, AgentOwnerChanged, AgentPolicyChanged, AgentToolCalled, AgentExternalCallRefused, AgentToolDenied, AgentInjectionDetected, AgentActionConfirmed, AgentActionRejected, AgentActionApproved, AgentActionExpired,
        AgentVersionSubmitted, AgentVersionApproved, AgentVersionRejected, AgentVersionPublished, AgentRolledBack, AgentSuspended, AgentResumed, AgentAlertRaised, AgentAlertDismissed, AgentIncidentOpened, AgentIncidentUpdated, AgentIncidentClosed, AgentEvidenceExported,
        AgentRetired, AgentKillSwitch, AgentTokensRevoked,
        EvalSuiteCreated, EvalSuiteChanged, EvalDatasetVersionCreated, EvalDatasetImported, EvalPolicyChanged, EvalPersonaCreated, EvalGateDecided, EvalGateOverridden,
        EvalRubricCalibrated, EvalRubricDemoted, EvalReviewVerdict, EvalReviewPromoted, EvalComparisonProposed, EvalComparisonDecided, EvalComparisonStopped, EvalOutcomeBaselineChanged,
        EngagementCommunityCreated, EngagementCommunityChanged, EngagementCommunityArchived, EngagementMemberChanged, EngagementPostPinned,
        EngagementContentApproved, EngagementContentHidden, EngagementContentRemoved, EngagementMemberMuted, EngagementMemberUnmuted, EngagementKeywordsChanged, EngagementSpeakUpAnswered, EngagementSpeakUpDeclined,
        EngagementSurveyPublished, EngagementSurveyClosed, EngagementSurveyThresholdChanged, EngagementPointsConfigChanged, EngagementRecognitionCatalogueChanged,
        EngagementAwardOpened, EngagementAwardDecided, EngagementCelebrationSettingsChanged,
        LearningCoursePublished, LearningCourseRetired, LearningPathPublished, LearningAttemptsGranted, LearningAssignmentCreated, LearningAssignmentWithdrawn, LearningEvidenceExported,
        SpacesCreated, SpacesChanged, SpacesArchived, SpacesRestored, SpacesMemberAdded, SpacesMemberRemoved, SpacesOwnerReassigned, SpacesSettingsChanged,
        SpacesTemplateChanged, SpacesEventChanged, SpacesGuestInvited, SpacesGuestExpired,
        BookingResourceCreated, BookingResourceRemoved, BookingRulesChanged, BookingCancelledByAdmin, BookingSettingsChanged, BookingSiteChanged,
        BookingSiteClosed, BookingSiteReopened,
    ];

    public static readonly IReadOnlyList<string> DeferredToLaterReleases =
    [
        RoleChanged, AddonChanged, OverrideChanged, QuotaExhausted, AuditIntegrityFailed,
    ];
}

/// <summary>One sealed event of a tenant's audit stream, in chain order (spec 026 US4).</summary>
public sealed record AuditStreamEvent(
    long Position, Guid Id, DateTimeOffset OccurredAt, string ActorType, Guid? ActorId, string Action, string Outcome, string TargetType,
    string? TargetId, Guid? TargetOrgUnitId, string? SourceIp, string? CorrelationId, System.Text.Json.JsonElement? Changes);

/// <summary>
/// The current tenant's audit stream for readers outside the Audit module (the SIEM stream of spec 026 FR-014). Only
/// sealed events are read: their chain position is gapless and never changes, so a reader keeps its place with it.
/// </summary>
public interface IAuditStream
{
    /// <summary>Up to <paramref name="limit"/> sealed events after <paramref name="afterPosition"/>, in order.</summary>
    Task<IReadOnlyList<AuditStreamEvent>> ReadAsync(long afterPosition, int limit, CancellationToken ct = default);

    /// <summary>The position of the last sealed event, 0 when none.</summary>
    Task<long> EndAsync(CancellationToken ct = default);

    /// <summary>The position just before the first sealed event at or after <paramref name="since"/>: reading after it starts there.</summary>
    Task<long> PositionBeforeAsync(DateTimeOffset since, CancellationToken ct = default);
}

/// <summary>A completed audit export (spec 006) that can still be downloaded.</summary>
public sealed record AuditExportSummary(Guid Id, string Format, long? RowCount, DateTimeOffset CompletedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Completed audit exports for other modules that keep them as evidence (spec 026 FR-023). Every call is in the current
/// tenant and needs <c>audit.export</c>, as the download route does: whoever attaches an export could download it.
/// </summary>
public interface IAuditExports
{
    /// <summary>The newest exports that are completed and not yet expired, newest first.</summary>
    Task<IReadOnlyList<AuditExportSummary>> ListAvailableAsync(int limit, CancellationToken ct = default);

    /// <summary>The export and its file, or null when it is unknown, not completed or expired. The caller disposes the stream.</summary>
    Task<(AuditExportSummary Export, Stream Content)?> OpenAsync(Guid exportId, CancellationToken ct = default);
}
