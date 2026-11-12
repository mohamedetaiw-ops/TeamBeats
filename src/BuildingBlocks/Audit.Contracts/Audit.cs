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
    public const string ComponentEnabled = "component.enabled";
    public const string ComponentDisabled = "component.disabled";
    public const string ComponentUpgraded = "component.upgraded";
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

    // 015
    public const string NotificationSettingsChanged = "notifications.settings_changed";
    public const string NotificationRelayTested = "notifications.relay_tested";
    public const string NotificationTemplateOverridden = "notifications.template_overridden";

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

    // Spec 026 (R1): security policy and network refusals.
    public const string SecurityPolicyChanged = "security.policy_changed";
    public const string SecurityRequestBlockedNetwork = "security.request_blocked_network";
    public const string SecurityMfaMissing = "security.mfa_missing";
    public const string SecurityStepUpCompleted = "security.step_up_completed";
    public const string SecurityStepUpFailed = "security.step_up_failed";
    public const string SecurityAlertRaised = "security.alert_raised";
    public const string SecurityAlertHandled = "security.alert_handled";
    public const string WorkflowDefinitionPublished = "workflow.definition_published";
    public const string WorkflowTaskCompleted = "workflow.task_completed";
    public const string WorkflowInstanceWithdrawn = "workflow.instance_withdrawn";

    // 024 (R2 part): incidents on connector steps
    public const string ProcessIncidentRaised = "process.incident_raised";
    public const string ProcessIncidentRetried = "process.incident_retried";
    public const string ProcessIncidentSkipped = "process.incident_skipped";
    public const string ProcessIncidentCancelled = "process.incident_cancelled";

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

    // 013
    public const string AiPolicyChanged = "ai.policy_changed";
    public const string AiRouteChangeProposed = "ai.route_change_proposed";
    public const string AiRouteChangeOverridden = "ai.route_change_overridden";
    public const string AiRouteChangeApplied = "ai.route_change_applied";
    public const string AiRouteChangeRejected = "ai.route_change_rejected";
    public const string AiCatalogueChanged = "ai.catalogue_changed";
    public const string AiLoggingWindowOpened = "ai.logging_window_opened";
    public const string AiLoggingWindowClosed = "ai.logging_window_closed";

    // 012
    public const string SearchBestBetChanged = "search.best_bet_changed";
    public const string SearchSynonymsChanged = "search.synonyms_changed";
    public const string SearchRetrievalChangeProposed = "search.retrieval_change_proposed";
    public const string SearchRetrievalChangeDecided = "search.retrieval_change_decided";
    public const string SearchRetrievalChangeOverridden = "search.retrieval_change_overridden";
    public const string SearchRetrievalChangeCancelled = "search.retrieval_change_cancelled";

    // 014
    public const string AssistantHistoryDeleted = "assistant.history_deleted";
    public const string AssistantRetentionChanged = "assistant.retention_changed";

    // Specs 020 and 027 (R1): agents and the tenant agent policy.
    public const string AgentInstalledFromTemplate = "agent.installed_from_template";
    public const string AgentTemplateUpdateApplied = "agent.template_update_applied";
    public const string AgentAudienceChanged = "agent.audience_changed";
    public const string AgentOwnerChanged = "agent.owner_changed";
    public const string AgentPolicyChanged = "agent.policy_changed";
    public const string AgentToolCalled = "agent.tool_called";
    public const string AgentToolDenied = "agent.tool_denied";
    public const string AgentInjectionDetected = "agent.injection_detected";
    public const string AgentActionConfirmed = "agent.action_confirmed";
    public const string AgentActionRejected = "agent.action_rejected";
    public const string AgentVersionSubmitted = "agent.version_submitted";
    public const string AgentVersionApproved = "agent.version_approved";
    public const string AgentVersionRejected = "agent.version_rejected";
    public const string AgentVersionPublished = "agent.version_published";
    public const string AgentRolledBack = "agent.rolled_back";
    public const string AgentSuspended = "agent.suspended";
    public const string AgentResumed = "agent.resumed";
    public const string AgentRetired = "agent.retired";
    public const string AgentKillSwitch = "agent.kill_switch";
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

    // 021
    public const string AnalyticsPrivacySettingChanged = "analytics.privacy_setting_changed";

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
        PageCreated, PagePublished, PageRolledBack, PageTemplateChanged, ComponentEnabled, ComponentDisabled, ComponentUpgraded,
        TemplateReleased, TemplateDistributed, TemplateDeprecated, RegionLockChanged,
        ThemePublished, BrandAssigned, BrandUnassigned, ExperienceLockChanged,
        NotificationSettingsChanged, NotificationRelayTested, NotificationTemplateOverridden,
        HrCalendarChanged, HrSettingsChanged, HrMatchLinked, HrMatchUnlinked, HrSyncHeld, HrSyncConfirmed, HrChangeApplied, HrOrgCodeMapped, DisabledByHr, ReenabledByHr,
        HrPayslipViewed, HrPayslipDownloaded,
        HrLeaveSubmitted, HrLeaveApproved, HrLeaveRejected, HrLeaveCancelled, HrLeaveShortened, HrLeaveOnBehalf, HrBalanceAdjusted, HrLeaveTypeChanged, HrLeavePolicyChanged,
        HrLeaveRecounted, HrLeaveSourceChanged, HrLeaveHandOffRetried,
        HrJourneyTemplateChanged, HrJourneyStarted, HrJourneyRescheduled, HrJourneyStepCompleted, HrJourneyCancelled, HrAccessRemovalConfirmed, HrAccessRemovalEscalated, HrExitInterviewRecorded, HrJourneySettingsChanged, HrPreboardingSent, HrAttendanceSettingsChanged, HrWorkRuleChanged, HrAttendanceExported, HrAttendanceRequested, HrAttendanceCorrected, HrCompensationCredited, HrAttendanceRequestRejected, HrLetterTypeChanged, HrLetterSettingsChanged, HrLetterRequested, HrLetterIssued, HrLetterRejected, HrLetterVerified, HrDataChangeSettingsChanged, HrDataChangeRequested, HrDataChangeApproved, HrDataChangeRejected, HrDataChangeWrittenBack, HrDataChangeWriteFailed, HrExpensePolicyChanged, HrExpenseRatesChanged, HrExpenseSubmitted, HrExpenseApproved, HrExpenseRejected, HrExpenseHandedOff,
        CalendarEventCreated, CalendarEventChanged, CalendarEventCancelled, CalendarEventDeleted, CalendarSettingsChanged, CalendarTeamViewed,
        PeopleVisibilityChanged, PeopleFieldSourceChanged, PeopleManagerSetManually, PeopleProfileUpdated, PeopleReportingCycleDetected, PeopleSettingsChanged,
        SecurityPolicyChanged, SecurityRequestBlockedNetwork, SecurityMfaMissing, SecurityStepUpCompleted, SecurityStepUpFailed, SecurityAlertRaised, SecurityAlertHandled,
        WorkflowDefinitionPublished, WorkflowTaskCompleted, WorkflowInstanceWithdrawn,
        ServiceCreated, ServiceChanged, ServicePublished, ServiceRetired, ServiceCategoryChanged, RequestSubmitted, RequestCancelled, RequestResubmitted,
        TaskApproved, TaskRejected, TaskReturned, DelegationSet, DelegationChanged, DelegationEnded,
        ContentCreated, ContentPublished, ContentScheduled, ContentUnpublished, ContentArchived, ContentExpired, ContentSettingsChanged, ContentShared, ContentSubmitted, ContentApproved, ContentReturned, ContentWithdrawn,
        ContentCriticalSent, ContentCriticalDenied, ContentAcknowledged, ContentAckReportExported,
        ContentReviewed, ContentOwnerReassigned, ContentVersionRestored, ContentCommentModerated,
        SiteCreated, SiteChanged,
        DocLibraryCreated, DocLibraryChanged, DocTypeChanged, DocSettingsChanged, DocUploaded, DocMetadataChanged, DocDownloaded,
        DocPermissionsBroken, DocPermissionsChanged, DocCheckedOut, DocCheckedIn, DocForcedCheckIn, DocVersionCreated, DocRestored, DocDeleted,
        DocRestoredFromBin, DocPurged, DocReleaseSubmitted, DocReleaseWithdrawn, DocReleaseApproved, DocReleaseReturned, DocVersionEffective, DocReviewed,
        DocReadConfirmed,
        PolicyCreated, PolicyChanged, PolicyDraftSaved, PolicySubmitted, PolicyWithdrawn, PolicyApproved, PolicyReturned, PolicyPublished, PolicySuperseded,
        PolicyRetired, PolicyCampaignLaunched, PolicyCampaignReminded, PolicyCampaignClosed, PolicyAttested,
        AiPolicyChanged, AiRouteChangeProposed, AiRouteChangeOverridden, AiRouteChangeApplied, AiRouteChangeRejected, AiCatalogueChanged, AiLoggingWindowOpened, AiLoggingWindowClosed,
        SearchBestBetChanged, SearchSynonymsChanged, SearchRetrievalChangeProposed, SearchRetrievalChangeDecided, SearchRetrievalChangeOverridden,
        SearchRetrievalChangeCancelled,
        AssistantHistoryDeleted, AssistantRetentionChanged,
        AgentInstalledFromTemplate, AgentTemplateUpdateApplied, AgentAudienceChanged, AgentOwnerChanged, AgentPolicyChanged, AgentToolCalled, AgentToolDenied, AgentInjectionDetected, AgentActionConfirmed, AgentActionRejected,
        AgentVersionSubmitted, AgentVersionApproved, AgentVersionRejected, AgentVersionPublished, AgentRolledBack, AgentSuspended, AgentResumed,
        AgentRetired, AgentKillSwitch,
        EvalSuiteCreated, EvalSuiteChanged, EvalDatasetVersionCreated, EvalDatasetImported, EvalPolicyChanged, EvalPersonaCreated, EvalGateDecided, EvalGateOverridden,
        EvalRubricCalibrated, EvalRubricDemoted,
        EngagementCommunityCreated, EngagementCommunityChanged, EngagementCommunityArchived, EngagementMemberChanged, EngagementPostPinned,
        EngagementContentApproved, EngagementContentHidden, EngagementContentRemoved, EngagementMemberMuted, EngagementMemberUnmuted, EngagementKeywordsChanged, EngagementSpeakUpAnswered, EngagementSpeakUpDeclined,
        EngagementSurveyPublished, EngagementSurveyClosed, EngagementSurveyThresholdChanged, EngagementPointsConfigChanged, EngagementRecognitionCatalogueChanged,
        EngagementAwardOpened, EngagementAwardDecided, EngagementCelebrationSettingsChanged,
    ];

    public static readonly IReadOnlyList<string> DeferredToLaterReleases =
    [
        RoleChanged, AddonChanged, OverrideChanged, QuotaExhausted, AuditIntegrityFailed,
    ];
}
