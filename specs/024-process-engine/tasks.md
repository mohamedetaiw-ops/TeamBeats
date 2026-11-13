# Tasks: Process Engine (BPMN and DMN)

Depends on 004 (authorization), 006 (audit), 022 (working-day contract), ADR-0006 (outbox), ADR-0010. R1 covers the engine core for approval routes only; the modeler, DMN, messages and monitoring are R2.

## Phase 1: Engine core for approval routes (R1)
- [x] T001 `Workflow` and `Workflow.Contracts` projects; migration for definitions, instances, user tasks, jobs and history (forced RLS)
- [x] T002 [P] Unit tests: route compiler to BPMN 2.0 XML with the `teambeats` namespace and to the executable graph; token moves; outcome gateway
- [x] T003 Compiler and executor for start, sequential user tasks, exclusive gateway on outcome, end
- [x] T004 [P] Integration tests: start, approve, reject, withdraw; duplicate job delivery has no second effect; starter excluded from candidates; sibling role holder denied; cross-tenant start, complete and query fail
- [x] T005 `IProcessEngine` contract, events `UserTaskCreated`, `UserTaskCompleted`, `ProcessInstanceCompleted`, `ProcessInstanceWithdrawn`; audit of task completion
- [x] T006 Working-day due times on user tasks with reminder jobs and recalculation on `WorkingCalendarChanged`; `UserTaskDue` event
- [x] T007 Delegation lookup through the spec 016 contract; "on behalf of" in history
- [x] T008 R1 implementation notes; spec evidence for the R1 part

## R2

## Phase 2: Model, expressions and execution (R2)
- [ ] T009 Process models: BPMN 2.0 read and write with the `teambeats` namespace, subset compiler to the executable graph, validator (FR-001, FR-002), drafts and immutable versions (FR-003); permission `process.operate` and `process.monitor`; capability `workflow.bpmn`; unit tests
- [ ] T010 FEEL subset parser and sandboxed interpreter with limits (FR-005); unit tests
- [ ] T011 Token executor for user, manual and send tasks, exclusive, parallel and inclusive gateways, none, terminate, error and escalation ends, embedded and event sub-processes, call activity, multi-instance, loop and variable limits (FR-004, FR-006); conformance suite (SC-002); integration tests

## Phase 3: Decisions, messages and timers (R2)
- [ ] T012 DMN decision tables: storage, versions, validation, hit policies, test rows, business rule task (FR-007); 200 rule performance test
- [ ] T013 Timers (ISO 8601 and working days, start, catch, boundary, event-based gateway) and messages (correlation, buffer, message start, signed webhook, product events) (FR-008, FR-009)

## Phase 4: Automatic steps and operations (R2)
- [ ] T014 Service tasks in models (connector actions and allowlisted webhooks with mappings), error boundary mapping, incidents (FR-010, FR-011)
- [ ] T015 Operator view (search, variables, retry, skip, cancel, change variables, audit) and monitoring (counts, heatmap, durations, spec 021 dataset) (FR-013, FR-014)

## Phase 5: Experience (R2)
- [ ] T016 Modeler (React Flow, bpmn-moddle), decision table editor, test run, Processes, Decisions, Operations and Monitoring pages in English and Arabic; e2e with axe

## R2 final phase
- [ ] T017 Correlation performance and worker kill test; OpenAPI; R2 implementation notes; spec evidence
