# Spec 024 evidence (R1)

R1 covers the engine core that approval routes need (plan, ADR-0010 rollout). The spec stays Approved, not Implemented, because the modeler, DMN, messages, service tasks, incidents and monitoring are R2, and compensation, migration and the external adapter are R3.

| Requirement | Status | Evidence |
|---|---|---|
| BPMN 2.0 XML with the `teambeats` namespace, read back by the same engine | Met for the R1 subset | `RouteCompilerTests` |
| Immutable versions; running instances stay on their version | Met | `WorkflowEngineTests.Running_instances_stay_on_their_version_and_republishing_the_same_route_adds_no_version` |
| Sequential user tasks with approve, reject and return | Met | `WorkflowEngineTests` approve, reject and return tests |
| No self-approval | Met | `WorkflowEngineTests` starter exclusion test |
| FR-006 candidates by unit, sibling unit denied | Met with a permission at the unit | `WorkflowEngineTests` sibling unit test; line manager waits for spec 016 (r1-implementation-notes) |
| FR-006 delegation | Open | Lands with spec 016 (T007) |
| FR-008 working-day due times from the unit's calendar | Met for user task due times | `WorkflowDueTests` (Sunday to Thursday calendar, holiday recalculation) |
| FR-012 jobs at least once with no second effect | Met | `WorkflowDueTests.The_reminder_publishes_UserTaskDue_once_even_when_the_job_is_delivered_twice`; a concurrent double completion in `WorkflowEngineTests` |
| Withdraw | Met | `WorkflowEngineTests` withdraw test |
| Tenant isolation | Met | `WorkflowEngineTests` cross-tenant test, `WorkflowDueTests.Jobs_run_only_in_their_own_tenant` |
| Audit of task completion | Met | `workflow.task_completed` asserted in `WorkflowEngineTests` |

## R2

R2 adds the modeler, the BPMN subset of section 12, DMN, messages, timers, service and webhook steps, incidents, the operator view and monitoring. The spec stays Approved, not Implemented: compensation, instance migration, import from other tools and the external engine adapter are R3. The spec 024 section of `docs/architecture/r2-implementation-notes.md` holds the defaults and deviations. Integration tests are in `tests/Integration/ProcessModelTests.cs` unless named otherwise; unit tests in `tests/Unit`; the e2e suite `process-operations.spec.ts` runs in English and Arabic with axe.

| Requirement | Status | Evidence |
|---|---|---|
| US1.1 and FR-002 palette offers only the subset, properties panel per element in Arabic and English | Met | `Modeler.tsx` palette from `PALETTE`; `process-operations.spec.ts` (designer draws, saves and publishes) |
| US1.2 and FR-002 problems block publishing and are marked on the diagram | Met | `A_draft_keeps_its_problems_publishing_makes_immutable_versions_and_owners_act_in_their_scope`, `ProcessGraphTests.Validation_marks_each_problem_on_its_element`, `Validation_refuses_bad_expressions_cycles_under_an_hour_self_calls_and_unsafe_xml`; problems are marked on shapes, in the panel and in the outline |
| US1.3 and FR-003 immutable versions, running instances keep theirs | Met | `A_draft_keeps_its_problems_publishing_makes_immutable_versions_and_owners_act_in_their_scope` |
| US1.4 test run in a sandbox | Met | `A_test_run_walks_the_draft_shows_who_would_get_each_task_and_creates_or_sends_nothing`, `process-operations.spec.ts` (test run after publishing) |
| US1.5 owner scope for starting and assignment | Met | `A_draft_keeps_its_problems_publishing_makes_immutable_versions_and_owners_act_in_their_scope`, `An_instance_moves_through_user_tasks_and_an_exclusive_gateway_with_candidates_at_its_unit` |
| FR-004 subset elements | Met | `ProcessConformanceTests.Every_supported_element_type_has_a_reference_model` (SC-002), `ProcessGraphTests` |
| FR-005 FEEL subset in a sandbox with limits | Met | `FeelTests`, `A_failing_expression_or_decision_raises_an_incident_and_retry_runs_the_element_again` |
| US2.1 to US2.5 and FR-007 decision tables | Met | `DecisionTableTests`, `Decision_owners_test_and_publish_tables_and_a_business_rule_task_routes_on_the_current_version`, `process-operations.spec.ts` (designer builds, tests and publishes a table) |
| US3.1 and FR-009 messages by name and key, from connectors and signed webhooks | Met | `Messages_correlate_by_name_and_key_once_and_wait_in_the_buffer_when_they_come_first`, `A_signed_inbound_webhook_correlates_a_message_once_and_a_webhook_step_posts_to_an_allowlisted_host`; product events come with spec 016 R2 triggers |
| US3.2 buffer for one hour | Met | `Messages_correlate_by_name_and_key_once_and_wait_in_the_buffer_when_they_come_first` |
| US3.3 message start once per message ID | Met | `A_timer_start_event_starts_one_instance_per_cycle_of_the_current_version_and_a_message_start_starts_one_per_message` |
| US3.4 and FR-008 timer boundaries, working days | Met | `A_timer_boundary_moves_an_overdue_task_on_and_a_task_with_nobody_to_do_it_raises_an_incident_an_operator_resolves`, `ProcessGraphTests.Timer_boundaries_interrupt_or_run_beside_the_task` |
| US3.5 event based gateway | Met | `ProcessGraphTests.An_event_based_gateway_takes_the_first_event_and_cancels_the_other` |
| US4.1 and US4.4 business errors and escalations | Met | `ProcessGraphTests.Business_errors_follow_error_boundaries_and_uncaught_errors_end_the_instance`, `A_non_interrupting_escalation_runs_its_path_while_the_sub_process_continues` |
| US4.2 and FR-010, FR-011 retries then an incident | Met | `ConnectorStepFinished` path in `A_signed_inbound_webhook_correlates_a_message_once_and_a_webhook_step_posts_to_an_allowlisted_host`; connector retries in spec 017 tests |
| FR-012 jobs at least once with no second effect, worker failure | Met | `ProcessEnginePerformanceTests.A_worker_that_dies_holding_a_due_timer_loses_nothing_and_the_next_worker_runs_it_once` |
| US5.1 to US5.5 and FR-013 operator view, incident actions audited, second resolve refused | Met | `Operators_search_their_instances_fix_variables_on_an_incident_retry_and_cancel_and_analysts_read_monitoring_numbers`, `process-operations.spec.ts` (operator skips a stopped step) |
| US5.3, US5.4 and FR-014 monitoring per element, analysts see no variables | Met | Same test; `process-operations.spec.ts` monitoring tab |
| Performance: correlation under 1 second at p95 | Met | `ProcessEnginePerformanceTests.A_message_correlates_with_one_of_hundreds_of_waiting_instances_in_under_one_second_at_p95` |
| Performance: decisions | Met | `DecisionTableTests.A_table_of_200_rules_decides_within_20_ms_at_the_95th_percentile` |
| Package split (section 9) | Met | `workflow.bpmn` refused on Workplace in `A_draft_keeps_its_problems_publishing_makes_immutable_versions_and_owners_act_in_their_scope` and the operator test; pages listed only with the capability |
| Tenant isolation | Met | Cross-tenant message in `Messages_correlate_by_name_and_key_once_and_wait_in_the_buffer_when_they_come_first` |
| Section 10 RTL, keyboard and text outline, accessibility | Met | `process-operations.spec.ts` in en and ar with axe; keyboard connect and outline used in the modeler test |
