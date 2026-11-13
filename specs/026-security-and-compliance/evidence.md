# Spec 026 evidence (R1 and R2)

R1 covers User Stories 1, 2 and 5 (basic), FR-017 and FR-025 (plan). The spec stays Approved, not Implemented, because guests, SIEM, data detection, compliance projects, keys and four-eyes are R2 and R3.

| Requirement | Status | Evidence |
|---|---|---|
| US1.1 tenant allowlist refuses, no session, audited | Met | `SecurityNetworkTests.An_allowlist_refuses_other_addresses_before_any_module_and_audits_once_a_minute`, `NetworkRulesTests` |
| US1.2 admin area list | Met | `The_admin_area_list_guards_admin_endpoints_only_and_a_self_lockout_needs_the_address_typed_back`, `The_Workplace_plan_keeps_tenant_rules_but_refuses_the_admin_area_list` |
| US1.3 self lock-out warning, step-up and typed confirmation | Met | Same test; `EntraSignInTests.Sensitive_actions_need_a_recent_sign_in_and_step_up_asks_the_provider_for_a_fresh_one`; preview in `The_preview_counts_people_whose_recent_sign_ins_the_proposed_rules_would_refuse` and `security.spec.ts` |
| US1.4 and FR-002 trusted proxies only | Met | `Only_the_proxy_appended_address_counts_so_a_spoofed_leftmost_entry_is_ignored` |
| US1.5 role-level network zones | Met | `A_network_zone_condition_limits_one_grant_to_named_networks_and_the_explainer_names_it`, `GrantConditionTests` |
| FR-001 blocklist and tenant isolation | Met | `The_blocklist_wins_over_the_allowlist`, `Rules_of_one_tenant_never_reach_another_and_a_session_cannot_cross_hosts` |
| US2.1 and FR-004 MFA required with claim mapping | Met | `EntraSignInTests.Required_MFA_refuses_a_sign_in_without_the_claim_after_one_has_shown_it` |
| US2.2 and FR-005 step-up on sensitive actions | Met | `EntraSignInTests.Sensitive_actions_need_a_recent_sign_in_and_step_up_asks_the_provider_for_a_fresh_one` |
| US2.3 providers without forced re-authentication | Not needed in R1 | Entra ID honours `prompt=login` and `max_age` (r1-implementation-notes) |
| US2.4 and FR-003 session rules, sign out everywhere | Met | `SecuritySessionTests` (idle, absolute, concurrent limit, sign out everywhere, administrator revoke and tenant isolation) |
| FR-003 remember-device period | Partly met | Stored and validated in the policy; Entra ID decides when to ask for the second factor again |
| US5.1 download volume alert | Met | `SecurityAlertTests.Download_volume_above_the_threshold_raises_one_alert_per_user_and_hour_with_the_volume_and_libraries`; fires once spec 011 publishes `DocumentDownloaded` |
| US5.2 admin grant out of hours | Met | `Granting_an_administrative_role_out_of_hours_raises_an_alert_on_Workplace_Plus_only` |
| US5.3 guest opens a Confidential item | R2 | With guests (US3) |
| US5.4 handling audited | Met | `Security_officers_handle_alerts_with_an_audited_outcome_and_others_cannot`, `security.spec.ts` |
| US5 officers are told | Met | `A_new_alert_notifies_alert_handlers_and_shows_as_a_badge_on_the_security_section` |
| FR-013 tenant thresholds | Partly met | Thresholds live in the policy and the rules use them (`AlertSettingsTests`); the screen to change them is open |
| FR-017 posture page | Met | `SecurityPostureTests`, `PostureTests`, `security.spec.ts` |
| FR-018 policy changes audited with before and after | Met | `SecurityNetworkTests` assertions on `security.policy_changed` |
| FR-025 secure rendering | Met for R1 | `SecureRenderingTests`, `secure-rendering.spec.ts`, every e2e test fails on a policy violation; rich text and embeds do not exist yet (r1-implementation-notes) |
| Package split (section 9) | Met | `The_Workplace_plan_keeps_tenant_rules_but_refuses_the_admin_area_list`, `Granting_an_administrative_role_out_of_hours_raises_an_alert_on_Workplace_Plus_only`, `The_admin_area_check_does_not_count_on_a_plan_without_scoped_rules` |
| Arabic and English, RTL, accessibility | Met | `security.spec.ts` in en and ar with axe |

## R2

R2 adds guests and guest spaces (US3), SIEM streaming (US4), the full alert set (US5), sensitive data detection (US6), compliance projects (US7), hosting region and break-glass. The spec stays Approved, not Implemented: customer-managed keys (FR-007), four-eyes approval (FR-016) and tenant export with a deletion certificate (FR-019) are R3. The spec 026 sections of `docs/architecture/r2-implementation-notes.md` (slices 1 to 6) hold the defaults, deviations and gaps. Test classes are in `tests/Integration` unless marked e2e. The e2e suites (`security.spec.ts`, `guests.spec.ts`, `compliance.spec.ts`) run in English and Arabic with axe.

| Requirement | Status | Evidence |
|---|---|---|
| FR-008 region chosen at creation, data kept in it | Met | `ResidencyBreakGlassTests.Region_is_chosen_at_creation_within_the_offered_regions_and_the_package` (offered regions, package capability, rollback, posture page); each region is its own deployment, so its database, storage, backups and indexes stay there (implementation notes, slice 6); `security.spec.ts` shows the region |
| FR-009 guests with sponsors, expiry, read-only default, contribution option, watermark, nothing outside their spaces | Met | `SpaceGuestsTests.An_invited_partner_signs_in_as_a_guest_and_sees_only_their_space`, `SpaceGuestsTests.Guest_access_ends_on_its_date_and_the_owners_are_told_a_week_before`, `SecurityGuestTests.A_guests_pdf_download_carries_their_address_and_the_time`, `SecurityGuestTests.A_guest_gets_the_reduced_shell_and_a_home_listing_their_spaces`, `guests.spec.ts` |
| FR-009 guests never get the Employee role | Met | `AuthorizationTests.Membership_policies_are_readable_and_guests_never_get_the_default_employee_role` |
| FR-010 guest sign-in only through the allowed methods, no product passwords | Met | Guests sign in through the tenant identity provider (spec 003); domain rules in `SecurityGuestTests.Guest_domain_rules_decide_which_addresses_an_owner_may_invite` |
| FR-011 Restricted content never shared to guests | Met | `SpaceGuestsTests.Classified_files_stay_closed_to_guests_unless_the_tenant_and_the_owner_allow_them` |
| FR-012 access reviews with removal when not confirmed | Met | `SecurityAccessReviewTests.Sponsors_and_managers_confirm_or_remove_the_access_they_are_responsible_for`, `SecurityAccessReviewTests.What_nobody_confirms_in_14_days_is_removed_and_the_next_review_opens_after_90_days`, `guests.spec.ts` |
| FR-013 and US5 alerts with tenant thresholds and handling | Met | `SecurityAlertsFullTests` (thresholds, guest Confidential access, refused app calls, volume spike), `SecurityAlertTests`, `security.spec.ts` (thresholds and handling) |
| FR-014 SIEM over HTTPS JSON and syslog over TLS, buffering and replay | Met | `SecuritySiemTests` (signed and in order, replay after an outage, RFC 5424 over TLS, test event and secret rotation, history, permissions and tenant isolation), `security.spec.ts` |
| FR-015 detection with built-in and tenant patterns, warn and block per location | Met | `SecurityDetectionTests` (settings, sample test, publish warn and block, guest space uploads blocked), `security.spec.ts` |
| FR-018 changes to guest settings, SIEM and detection audited with before and after, no secrets | Met | Audit assertions in `SecuritySiemTests`, `SecurityDetectionTests`, `SecurityGuestTests` and `SecurityAlertsFullTests` |
| FR-020 platform access only through break-glass, visible to security officers | Met | `ResidencyBreakGlassTests.Break_glass_is_audited_to_the_tenant_alerts_it_and_recovers_a_network_lockout` (tenant and platform audit streams, alert, officer audit view, posture check, cross-tenant refusal); the network rule reset is the recovery the edge cases name |
| FR-021 control library: ISO 27001:2022, SOC 2, NCA ECC and tenant frameworks | Met | `ComplianceProjectTests.A_project_starts_from_a_framework_of_the_library_and_follows_permissions_and_plan` |
| FR-022 controls with owners, status, due dates, links and evidence tasks | Met | `ComplianceProjectTests.Owners_move_their_controls_on_and_only_compliance_officers_review_them`, `compliance.spec.ts` |
| FR-023 evidence keeps the export reference and hash | Met | `ComplianceProjectTests.An_audit_export_attached_as_evidence_keeps_its_reference_and_hash_and_the_readiness_view_counts_it`, `compliance.spec.ts` |
| FR-024 projects shared read-only to guest auditor spaces | Met | `ComplianceProjectTests.A_project_shared_to_an_auditor_guest_space_is_read_only_to_its_guests` |
| Org boundary for compliance | Met | `ComplianceProjectTests.A_scoped_compliance_officer_sees_and_runs_only_their_subtree` |
| Package split (section 9) for R2 capabilities | Met | `SecurityGuestTests.The_package_limits_how_many_spaces_have_guests`, `SecuritySiemTests.Only_security_managers_on_a_plan_with_siem_...`, `SecurityDetectionTests.Settings_and_patterns_follow_permissions_plan_and_the_sample_test`, `ComplianceProjectTests.A_project_starts_from_...`, `ResidencyBreakGlassTests.Region_is_chosen_...` |
| US5.3 guest opens a Confidential item (R1 gap) | Met | `SecurityAlertsFullTests.A_guest_opening_a_Confidential_item_alerts_security_officers_or_the_sponsor_as_the_tenant_chose` |
| FR-013 threshold screen (R1 gap) | Met | `SecurityAlertsFullTests.Thresholds_are_read_with_the_alerts_and_changed_only_on_plans_with_advanced_alerts`, `security.spec.ts` |
| SC-003 auditor read-only access in under 10 minutes with removal at expiry | Met in steps | `compliance.spec.ts` and `guests.spec.ts` do the steps without timing a person; expiry in `SpaceGuestsTests.Guest_access_ends_on_its_date_...` |
| SC-001 and SC-002 trust pack and certificates | Not proven | Business outcomes outside the product |
| Cross-tenant denial | Met | Every class above has a case for another tenant |
| Arabic and English, RTL, accessibility | Met | The three e2e suites in en and ar with axe; catalogue and message checks in `npm run verify` |
| OpenAPI | Met | `docs/api/openapi.v1.json` regenerated, checked by `OpenApiContractTests` |

Known gaps for R3: customer-managed keys (FR-007), four-eyes approval (FR-016), tenant export and deletion certificate (FR-019). Break-glass offers only the network rule reset; reading tenant content stays closed.
