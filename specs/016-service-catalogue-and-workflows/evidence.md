# Spec 016 evidence (R1 and R2)

R1 covers User Stories 1 to 4, User Story 7 (basic) and approval-only routes of User Story 8. Multi-step routes, fulfilment queues, connectors, triggers, service levels, templates and the route designer followed in R2 (below).

| Requirement | Status | Evidence |
|---|---|---|
| US1.1 and FR-001 one catalogue by audience, categories, search, favourites, recent use | Met | `ServiceCatalogueTests.People_see_only_services_whose_audience_includes_them_and_a_contractor_is_told_it_is_not_available`, `Favourites_recent_use_search_and_categories_shape_the_catalogue`, `services.spec.ts` |
| US1.2 and FR-002 linked apps, external cue, SSO, http(s) only | Met | `Linked_apps_open_only_http_addresses_and_say_whether_sign_in_is_single_sign_on` |
| US1.3 services in global search | With spec 012 | Search indexes services from `ServicePublished` when spec 012 is built |
| US1.4 contractor refused, direct URL "not available to you" | Met | `People_see_only_services_whose_audience_includes_them_and_a_contractor_is_told_it_is_not_available` |
| US2.1 and FR-004 prefill, directory values read only | Met | `ServiceRequestTests.The_form_is_prefilled_and_directory_values_stay_read_only_even_when_the_client_sends_others` |
| US2.2 policy note in the user's language | Met | Same test (policy note in en and ar), `services.spec.ts` |
| US2.3 validation blocks submit with a localized message | Met | `Validation_blocks_submit_and_a_repeated_submit_returns_the_same_request`, `FormValidatorTests`, `services.spec.ts` |
| US2.4 attachments scanned, tenant path, classified | Met | `Attachments_are_scanned_stored_under_the_tenant_and_readable_only_through_the_request_once_clean`; staging uses the signature scanner (r1-implementation-notes) |
| US2.5 My requests with status, step, assignee, Tracking | Met | `TaskInboxTests.The_approver_sees_one_inbox_with_a_count_and_approving_closes_the_request_while_nobody_else_can_act`, `services.spec.ts` |
| FR-003 field types and rules | Met for R1 | `FormValidatorTests`; lookup lists keep their options in the field, service templates are R2 |
| FR-005 lifecycle, My requests, Team requests, Details and Tracking | Met | `LineManagerTests.Team_requests_show_direct_and_indirect_reports_only`, `The_requester_cancels_until_it_is_decided_and_the_open_task_is_cancelled_with_it`, `services.spec.ts` |
| US3.1 and FR-007 one inbox with a header count | Met | `The_approver_sees_one_inbox_with_a_count_and_approving_closes_the_request_while_nobody_else_can_act`, `services.spec.ts` (badge) |
| US3.2 approve, reject with reason, return; requester told; Tracking and audit | Met | `Rejecting_needs_a_reason_and_returning_lets_the_requester_change_and_submit_again`, `LineManagerTests.The_manager_is_told_a_request_waits_and_the_requester_is_told_each_decision` |
| US3.3 another user cannot approve | Met | `The_approver_sees_one_inbox_with_a_count_and_approving_closes_the_request_while_nobody_else_can_act` |
| US3.4 Team requests by reporting line only | Met | `Team_requests_show_direct_and_indirect_reports_only` |
| FR-006 line manager and org unit role assignment, approve, reject, return, history | Met for R1 | `LineManagerTests`, `TaskInboxTests`; manager chain level, groups, queues, parallel steps, conditions, reassign and escalation are R2 |
| Edge case: no manager falls back | Met | `Without_a_manager_or_when_the_manager_filed_it_the_step_falls_back_to_the_permission`; the service owner alert waits for US8 alerts |
| Edge case: manager change re-resolves | Met | `A_manager_change_during_approval_moves_the_open_step_to_the_new_manager` |
| US4.1 and FR-008 delegation window, on behalf of, delegator still sees it | Met | `A_delegate_acts_on_behalf_of_the_approver_inside_the_window_and_tasks_come_back_after_it`, `The_managers_delegate_acts_on_their_behalf_and_a_fallback_permission_is_validated` |
| US4.2 nobody approves their own request, directly or as delegate | Met | `A_requester_who_is_the_delegate_never_approves_their_own_request_and_permission_steps_delegate_too`, `On_behalf_requests_need_the_permission_and_the_person_it_is_for_never_approves_it` |
| US4.3 tasks come back after the window | Met | `A_delegate_acts_on_behalf_of_the_approver_inside_the_window_and_tasks_come_back_after_it` |
| US7.1 build, preview and publish in Arabic and English | Met for R1 | `An_owner_previews_the_draft_prefilled_for_themselves_and_a_submitted_request_keeps_its_form_version`, `services.spec.ts`; templates and fulfilment queues are R2 |
| US7.2 and FR-009 versions, requests keep theirs | Met | `Publishing_creates_versions_requests_keep_theirs_and_an_approval_step_is_published_to_the_engine` |
| US7.3 owner scope limits the audience | Met | `An_owner_scoped_to_a_unit_keeps_the_audience_inside_it_and_never_sees_other_units_services` |
| US8 approval-only routes | Met for R1 | One approval step on the process engine (spec 024 evidence); the rest of US8 is R2 |
| FR-010 module services in the catalogue and inbox | Met for R1 | Module services register through `Services.Contracts`; their tasks come through the engine into the same inbox |
| FR-011 steps on the process engine | Met | Spec 024 evidence |
| FR-012 to FR-016 | R2 | Reporting (spec 021), designer, triggers, service levels and automatic steps |
| Entitlements: catalogue and quota | Met | `Services_stop_at_the_plan_quota` |
| Tenant isolation | Met | `Services_categories_and_favourites_of_another_tenant_are_not_found`, `Requests_of_another_tenant_are_not_found_and_a_service_of_another_tenant_cannot_be_requested`, `Tasks_and_delegations_of_another_tenant_are_not_found`, `Team_requests_show_direct_and_indirect_reports_only`, `CrossTenantSuite` |
| Arabic and English, RTL, accessibility | Met | `services.spec.ts` in en and ar with axe on every page |

## R2

R2 adds User Stories 5, 6 and 8, the full User Story 7 and FR-012 to FR-016. With R2 every user story of the spec is built; the agent-requested approvals of section 11 arrive with spec 027 R2.

| Requirement | Status | Evidence |
|---|---|---|
| US5.1 sequential and parallel steps with conditions, skipped steps in Tracking | Met | `ServiceRouteGraphTests.Each_step_goes_to_its_assignee_the_condition_picks_the_path_and_steps_not_taken_show_as_skipped`, `Parallel_steps_join_and_a_rejection_or_return_ends_the_whole_route` |
| US5.2 due time reminder and escalation | Met | Steps compile to the spec 024 due and escalation timers (`ProcessModelTests`, `ProcessEnginePerformanceTests`); route checks in `A_route_that_cannot_run_is_not_published_and_shapes_are_checked_when_saved` |
| US5.3 open requests finish on their route version | Met | `Open_requests_finish_on_the_route_version_they_started_with` |
| US6.1 queue members pick, complete, or reject with a reason | Met | `ServiceQueueTests.One_member_picks_a_queue_task_the_rest_stop_seeing_it_and_only_the_picker_completes_it`, `A_pick_lapses_when_the_picker_leaves_the_queue` |
| US6.2 connector fulfilment with retry, idempotency and external reference | Met | `ServiceAutomationTests.After_approval_the_step_sends_only_the_mapped_fields_and_the_request_completes_with_the_ticket_number`, `A_timeout_on_an_idempotent_action_is_retried_with_the_same_key_and_acts_once` |
| US6.3 fulfilment failed, queue alerted, retry | Met | `A_failed_automatic_step_alerts_the_fulfilment_queue_and_the_owners_once`, `An_unknown_outcome_pauses_the_request_until_the_owner_retries_skips_or_cancels` |
| US7 full: templates, queue, designer | Met | `ServiceTemplateTests` (3), `ServiceRouteTestModeTests.Owners_list_the_tenant_groups_for_queues_and_others_cannot`, `route-designer.spec.ts`, `service-templates.spec.ts` |
| US8.1 and FR-013 designer with the step types, no custom code | Met | `RouteGraphCheck` and `RouteGraphCompiler`; `route-designer.spec.ts` in en and ar with axe |
| US8.2 and FR-014 triggers, idempotent per event | Met | `ServiceTriggerTests.A_product_event_starts_one_request_for_the_person_in_their_and_their_managers_lists`, `A_schedule_starts_one_request_per_period_for_its_requester_and_never_for_the_past` |
| US8.3 and FR-015 service level at risk and breach, counted for spec 021 | Met | `ServiceLevelTests.A_service_level_target_goes_at_risk_then_breached_once_each_and_a_request_closed_in_time_has_met_it` |
| US8.4 auto-approve rule, "auto-approved by rule" in Tracking | Met | `An_auto_approve_rule_completes_the_step_as_the_system_and_others_wait_for_the_manager`, `An_auto_approved_step_in_a_parallel_path_lets_the_join_go_on_when_the_other_path_is_done` |
| US8.5 failed automatic step pauses, owner alerted, retry, skip or cancel | Met | `An_unknown_outcome_pauses_the_request_until_the_owner_retries_skips_or_cancels`, `A_failed_automatic_step_alerts_the_fulfilment_queue_and_the_owners_once` |
| US8.6 test mode as a sample employee, nothing created | Met | `ServiceRouteTestModeTests.A_test_run_shows_assignees_and_the_branch_taken_and_creates_nothing`, `route-designer.spec.ts` |
| US8.7 unreachable steps or unmatched joins block publishing, shown on the diagram | Met | `A_route_that_cannot_run_is_not_published_and_shapes_are_checked_when_saved`; problems on steps in the designer |
| FR-012 reporting | With spec 021 | `request.sla_breached` analytics event without the person; resignation reasons are a structured choice |
| FR-016 automatic steps only through connectors or registered webhooks, owner-selected fields | Met | `Publishing_checks_each_step_against_the_actions_declared_fields_and_errors`, `After_approval_the_step_sends_only_the_mapped_fields_and_the_request_completes_with_the_ticket_number` |
| Edge case: requester leaves, continue after exit | Met | `A_requester_who_leaves_has_open_requests_cancelled_unless_the_service_continues_after_exit` |
| Edge case: trigger loops stop past depth 5 | Met | `A_start_service_step_starts_the_other_service_and_a_loop_stops_past_depth_five` |
| Section 11 request summary for approvers | Met | `RequestSummaryTests` (2), including the AI policy refusing confidential content |
| Section 12 starter templates | Met | `Every_available_template_switches_on_as_a_valid_draft_once`, `A_switched_on_template_publishes_and_a_colleague_files_it_with_prefill` |
| Entitlements | Met | `Workplace_runs_approval_and_task_steps_in_sequence_but_not_conditions_or_automatic_steps`, `Triggers_need_workflow_automation_valid_settings_and_a_requester_of_the_same_tenant`, `Rules_need_workflow_automation_an_approval_step_and_a_tenant_administrator_on_confidential_services`, `Automatic_steps_need_the_package_and_another_tenants_connection_or_request_is_not_found` |
| Tenant isolation | Met | Cross-tenant checks in `ServiceQueueTests`, `ServiceRouteTestModeTests`, `ServiceTemplateTests`, `RequestSummaryTests`, `ServiceAutomationTests` and `CrossTenantSuite` |

