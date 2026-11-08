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
