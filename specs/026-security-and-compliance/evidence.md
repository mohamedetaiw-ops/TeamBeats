# Spec 026 evidence (R1)

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
