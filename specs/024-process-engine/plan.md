# Implementation Plan: Process Engine (BPMN and DMN)

**Spec:** `specs/024-process-engine/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0010 (own engine, approved with the R1 set on 2026-11-05), ADR-0006 (outbox), ADR-0009 (Npgsql), ADR-0013, module map (`Workflow`)

## Summary
R1 builds only the engine core that approval routes need (ADR-0010 rollout: "R1: approval-only routes on the same engine core"). It runs a fixed subset: start event, one or more user tasks in sequence, an exclusive gateway on each task outcome (approve continues, reject or return ends the instance with that outcome), end event, and a working-day due time timer on user tasks. Sequential steps are needed in R1 by policy routes (spec 025 US1: Legal, then CISO, then CEO) and controlled documents (spec 011 US3); spec 016 keeps one step until `workflow.advanced` in R2. Routes are compiled from the simple route settings of spec 016 (one approval step per service) and spec 009 (one step per site and content type) into BPMN 2.0 XML with the `teambeats` extension namespace, so the R2 modeler and the full subset run the same definitions. The modeler, DMN, messages, service tasks, incidents UI and monitoring are R2; compensation, migration and the external engine adapter are R3.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation and org scope | Yes | Definitions, instances, tasks, jobs and history tenant-owned with forced RLS; workers set the tenant context per job; candidates are resolved within the instance's org unit |
| III authorization | Yes | Completing a task needs the user to be a current candidate or delegate, checked by the engine, plus the permission the calling module demands; `process.operate` for operator actions |
| XIV modules | Yes | `Workflow` owns engine state; `Services` and `Content` call `IProcessEngine` from `Workflow.Contracts`; outcomes return as events |
| XVIII durability | Yes | Jobs and timers run through the outbox, at least once, with idempotent handlers (FR-012) |
| No script code | Yes | No expressions in R1 beyond the fixed outcome match; the FEEL subset is R2 |

## Data model (schema `workflow`)
- `process_definition`: `tenant_id`, `key`, `version`, `bpmn xml`, `compiled jsonb` (the executable graph), `source` (`service_route`, `content_route`), `published_at`. Immutable per version (FR-003).
- `process_instance`: `tenant_id`, `id`, `definition_key`, `definition_version`, `business_key` (unique per key), `org_unit_id`, `status`, `variables jsonb` (classification inherited), timestamps.
- `user_task`: `tenant_id`, `id`, `instance_id`, `element_id`, `candidates jsonb` (users, role at unit, line manager), `excluded_user_ids`, `assignee_user_id`, `due_at`, `status`, `outcome`, `comment`, `completed_by`, timestamps.
- `engine_job`: timers and continuations with `due_at`, `attempts`, `locked_until`; history rows in `instance_history`.

## Contract (`Workflow.Contracts`)
- `IProcessEngine.PublishRouteAsync(key, RouteSpec)` compiles and stores a new version; `StartAsync(key, businessKey, orgUnitId, variables)`; `CompleteTaskAsync(taskId, outcome, comment)`; `WithdrawAsync(instanceId)`; queries for tasks by candidate (used by the spec 016 inbox).
- Events: `UserTaskCreated`, `UserTaskCompleted`, `ProcessInstanceCompleted(outcome)`, `ProcessInstanceWithdrawn`. Callers react to the completion event.
- Candidate resolution through `IAuthorizationService` (users with a role at the unit) and the reporting line once spec 010 exists; the starter of an instance can be excluded (no self-approval, spec 009 US1 scenario 6 and spec 016).

## Timers (FR-008, R1 part)
A user task may carry a due time in working days. The job computes `due_at` with `IWorkingCalendar.AddWorkingDays` (spec 022) for the instance's org unit and creates a reminder job; `WorkingCalendarChanged` recalculates open due times. Reminders publish `UserTaskDue` for spec 015.

## Delegation
The engine asks the spec 016 delegation contract for active delegates when it lists or completes tasks, so a delegate sees and completes the task under their own name with "on behalf of" in history.

## Tests
- Unit: the compiler (route spec to BPMN XML and back to the executable graph), token moves through the R1 subset including sequential steps, outcome gateway.
- Integration: start, complete approve and reject, withdraw, idempotent duplicate job delivery, self-approval exclusion, due time on a Sunday to Thursday calendar and recalculation after a holiday change, candidate role at the unit and denial for a sibling unit's role holder.
- Cross-tenant: start, complete and query across tenants fail; a task ID from another tenant is not found.
- Architecture: `Workflow` tables are reached only from the module.
