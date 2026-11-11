# Spec 017 evidence (R2)

The spec stays Approved, not Implemented. The R2 runtime, connection lifecycle, custom REST, webhooks, the on-premises agent with Active Directory, the ticketing, Slack, clock import, finance hand-off and Google Workspace packs and the KPI feed are built. SC-002 (a new REST system in a service route in under an hour) is a usability target that tests cannot prove; the acceptance test walks the same path without developer help, but no timed session with an administrator has been run. The R3 packs of section 2 are not built. The spec 017 sections of `docs/architecture/r2-implementation-notes.md` (slices 1 to 20) hold the defaults, deviations and gaps.

| Requirement | Status | Evidence |
|---|---|---|
| FR-001 external systems only through connectors with canonical contracts | Met | `ConnectorRuntimeTests`, `TicketingPackTests`, `FinanceHandOffTests`, `GoogleWorkspaceTests` (canonical outputs only); the module rules in `ArchitectureTests` |
| FR-002 tenant-owned connections with features turned on separately | Met | `CustomRestConnectorTests.A_connection_starts_disabled_is_tested_enabled_and_calls_the_system_with_only_the_declared_inputs`, `ConnectorRuntimeTests.A_connection_of_another_tenant_is_missing_and_its_runs_are_never_visible`, `CrossTenantSuite` |
| FR-003 secret references only, step-up on save | Met | `CustomRestConnectorTests.Settings_and_action_definitions_are_validated_and_secrets_must_be_references_of_the_tenant`, `Changing_credentials_and_settings_is_audited_apart_and_deleting_needs_confirmation_when_used`, `WebhookTests.Settings_refuse_undeclared_fields_hosts_off_the_allowlist_and_secret_values` |
| FR-004 test per feature | Met | `CustomRestConnectorTests`, `TicketingPackTests`, `SyncAgentTests.A_quiet_agent_marks_the_connection_degraded_until_it_is_back_and_the_test_reports_it`, `GoogleWorkspaceTests.Settings_are_checked_and_the_package_and_tenant_boundary_hold`, `connections.spec.ts` |
| FR-005 durable jobs, retry with jitter, idempotency, rate limit, circuit breaker | Met | `ConnectorRuntimeTests` (retry, rate limit, circuit, jobs), `ConnectorDownTests` |
| FR-006 dead letters replayable with the original key | Met | `ConnectorRuntimeTests.An_operator_sees_history_without_payloads_and_replays_a_dead_letter_with_its_original_key`, `Dead_letters_stop_being_replayable_after_14_days_and_history_is_purged_after_90` |
| FR-007 run history; payload capture off by default and time-boxed | Met | `ConnectorRuntimeTests`, `PayloadCaptureTests` |
| FR-008 declared schemas, side effect, business errors, idempotency | Met | `CustomRestConnectorTests.Declared_4xx_answers_are_business_errors_refused_credentials_stop_and_server_errors_follow_the_retry_rules`, `ServiceAutomationTests.Publishing_checks_each_step_against_the_actions_declared_fields_and_errors` |
| FR-009 steps and agent tools call actions by name with mapped fields | Met | `ServiceAutomationTests`, `ConnectorToolTests` |
| FR-010 outcome unknown stops and raises an incident for writes | Met | `ConnectorRuntimeTests.A_timeout_after_sending_stops_a_plain_write_as_outcome_unknown_but_an_idempotent_write_is_retried_with_the_same_key`, `ServiceAutomationTests.An_unknown_outcome_pauses_the_request_until_the_owner_retries_skips_or_cancels`, `FinanceHandOffTests.Without_an_idempotency_header_a_timeout_after_sending_stops_as_unknown` |
| FR-011 custom REST without code, tested with sample data | Met | `CustomRestConnectorTests`, `connections.spec.ts` (custom REST, en and ar, axe) |
| FR-012 allowlist and refused addresses after DNS | Met | `CustomRestConnectorTests.Calls_stop_before_leaving_when_the_host_leaves_the_allowlist_or_resolves_to_a_refused_address`, `ConnectorRuntimeTests.The_connector_http_handler_refuses_loopback_and_private_addresses_before_connecting`, `SafeHttpTests` (unit) |
| FR-013 signed, retried, logged outbound webhooks | Met | `WebhookTests.An_event_is_sent_signed_with_only_the_selected_fields_and_logged_as_a_run`, `A_failed_delivery_is_retried_with_the_same_delivery_id_and_a_turned_off_webhook_gets_nothing` |
| FR-014 inbound signature, window and delivery ID before mapping | Met | `WebhookTests.A_signed_delivery_becomes_an_event_with_only_the_declared_fields_and_refusals_are_counted`, `A_process_message_needs_its_correlation_key_and_a_body_over_one_megabyte_is_refused` |
| FR-015 rotation stops the old secret unless an overlap of at most 24 hours | Met | `WebhookTests.A_rotated_secret_stops_the_old_one_at_once_unless_an_overlap_is_set`, `During_an_overlap_an_outbound_delivery_carries_a_signature_for_each_valid_secret` |
| FR-016 outbound-only agent, one-time enrolment, bound certificate, revocation within 1 minute | Met | `SyncAgentTests` (enrolment once, signed requests, revocation at once, tenant and package) |
| FR-017 one agent at a time per connection | Met | `SyncAgentTests.One_agent_at_a_time_holds_a_job_and_its_records_are_kept_for_the_directory`, `A_lapsed_lease_puts_the_job_back_and_it_fails_after_three_attempts` |
| FR-018 AD over LDAPS into the canonical directory contract | Met | `OnPremDirectorySyncTests`, `SyncAgentProgramTests`, `SyncAgentMappingTests` (unit) |
| FR-019 document repository contract as the extension point for Google Drive | Met | `GoogleWorkspaceTests.Drive_is_read_only_inside_listed_shared_drives_and_content_opens_as_the_user` |
| FR-020 SFTP and CSV clock import with mapping and unmatched rows | Met | `ClockImportTests` |
| FR-021 finance hand-off with mapped fields, external reference and status | Met | `FinanceHandOffTests` |
| FR-022 KPI feed with metric, value, unit, period, location and source time | Met | `KpiFeedTests` |
| FR-023 disabling stops calls within 1 minute; dependents degraded | Met | `ConnectorRuntimeTests.Disabled_connections_unknown_actions_and_packages_without_connectors_never_reach_the_system` (the next attempt stops), `A_job_on_a_disabled_connection_waits_and_runs_once_the_connection_is_enabled`, `WebhookTests.A_failed_delivery_is_retried_with_the_same_delivery_id_and_a_turned_off_webhook_gets_nothing` |
| FR-024 connectors never call models | Met | `ArchitectureTests.Connectors_never_call_models` |
| SC-001 connector-down suite on every build | Met | `ConnectorDownTests` (`Suite=ConnectorDown`: revoked, throttled, down, partial failure, duplicate, for custom REST and Slack), run in the integration suite of every CI build |
| SC-002 a new REST system in a service route in under 1 hour | Not proven | `connections.spec.ts` (custom REST without code) and `ServiceAutomationTests` cover the path; no timed session yet |
| Overhead under 30 ms p95 | Met | `ConnectorPerformanceTests.Connector_overhead_stays_under_30_ms_at_p95` |
| 1,000 queued jobs a minute | Met | `ConnectorPerformanceTests.A_thousand_queued_jobs_run_within_a_minute_in_batches` |
| 20,000 directory users through the agent under 30 minutes | Met | `OnPremDirectorySyncTests.Twenty_thousand_users_sent_by_the_agent_are_applied_well_within_thirty_minutes` |
| Cross-tenant and package denial | Met | Every pack test class above has a tenant and package case; `CrossTenantSuite` |
