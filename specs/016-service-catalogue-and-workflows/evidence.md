# Spec 016 evidence (R1)

R1 covers User Stories 1 to 4, User Story 7 (basic) and approval-only routes of User Story 8. The spec stays Approved, not Implemented, because multi-step routes, fulfilment queues, connectors, triggers, service levels, templates and the route designer are R2.

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
