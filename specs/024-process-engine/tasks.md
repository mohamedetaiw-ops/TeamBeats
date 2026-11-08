# Tasks: Process Engine (BPMN and DMN)

Depends on 004 (authorization), 006 (audit), 022 (working-day contract), ADR-0006 (outbox), ADR-0010. R1 covers the engine core for approval routes only; the modeler, DMN, messages and monitoring are R2.

## Phase 1: Engine core for approval routes (R1)
- [x] T001 `Workflow` and `Workflow.Contracts` projects; migration for definitions, instances, user tasks, jobs and history (forced RLS)
- [x] T002 [P] Unit tests: route compiler to BPMN 2.0 XML with the `teambeats` namespace and to the executable graph; token moves; outcome gateway
- [x] T003 Compiler and executor for start, sequential user tasks, exclusive gateway on outcome, end
- [x] T004 [P] Integration tests: start, approve, reject, withdraw; duplicate job delivery has no second effect; starter excluded from candidates; sibling role holder denied; cross-tenant start, complete and query fail
- [x] T005 `IProcessEngine` contract, events `UserTaskCreated`, `UserTaskCompleted`, `ProcessInstanceCompleted`, `ProcessInstanceWithdrawn`; audit of task completion
- [x] T006 Working-day due times on user tasks with reminder jobs and recalculation on `WorkingCalendarChanged`; `UserTaskDue` event
- [ ] T007 Delegation lookup through the spec 016 contract; "on behalf of" in history
- [ ] T008 R1 implementation notes; spec evidence for the R1 part
