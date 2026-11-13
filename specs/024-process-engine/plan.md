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

## R2 plan (2026-11-13)

R2 adds the modeler, the BPMN subset of section 12 (R2 rows), DMN decision tables, messages, timers, error and escalation events, incidents for every automatic step, the operator view and monitoring. Compensation, migration, import from other tools and the external engine adapter stay in R3.

### Model and versions
- A process model is BPMN 2.0 XML with TeamBeats extension elements (`teambeats:` namespace for assignment, due times, connector bindings, mappings and names per locale). The server parses it with external entities off and a 2 MB limit, keeps only the subset, and compiles it to an executable graph (`compiled jsonb`) of elements and flows. Route specs of R1 keep their own compiler and executor, so every R1 route runs unchanged.
- New tables: `process_model` (draft per key, owner unit, names per locale), `process_definition` gains `kind` (`route` or `graph`) and `source = 'process_model'`; `decision_definition` (DMN XML, compiled table, version); `token` (instance, element, scope, state, loop count); `message_subscription` and `message_buffer`; `timer` rows reuse `engine_job` with kind `timer`. Instance variables stay in `process_instance.variables` with the limits of the edge cases (256 KB per variable, 2 MB per instance).
- Validation (FR-002) runs on save and blocks publish: reachability from a start event, every parallel split matched by a join, user tasks with an assignment, timers and FEEL that parse, service tasks with a connector action or an allowlisted webhook, call activity targets that exist, no call to itself without a condition. Problems name the element ID so the diagram marks them.

### FEEL subset (FR-005)
A hand-written parser and interpreter in the module, no reflection and no host functions beyond the listed set: literals, names and paths into variables, comparisons, `and`, `or`, `not()`, arithmetic, ranges and lists, `in`, `if then else`, string functions (`contains`, `starts with`, `ends with`, `upper case`, `lower case`, `string length`, `substring`), `date()`, `date and time()`, `duration()`, `today()`, `now()`, and unary tests for DMN cells. Each evaluation has a step budget and a 50 ms limit, and results over 256 KB fail.

### Execution
- Token semantics: tokens move through the graph inside one transaction until each one waits (user task, receive task, message or timer catch, event-based gateway, service task job) or ends. Parallel joins wait for a token on every incoming flow; inclusive joins wait for the flows that were activated by the matching split (tracked per token scope). Embedded sub-processes and multi-instance bodies open a scope; event sub-processes start inside their scope on a timer or message; call activities start a child instance on the current version of the called key and wait for it, with depth at most 10.
- Automatic steps (service tasks, async continuations, timers) run as `engine_job` rows on the worker through the outbox (ADR-0006), at least once, with idempotent completion.
- A token passing the same element more than 1,000 times raises an incident.
- User tasks use the R1 candidate rules plus assignee and candidate group expressions, manager chain level (spec 010) and a fallback assignee; they appear in the spec 016 inbox through `ITaskSource` as today.

### DMN (FR-007)
Decision tables only. Inputs are FEEL expressions, cells are unary tests, outputs are FEEL. Hit policies unique, first, any, priority, collect (with sum, min, max, count), rule order and output order. Validation reports overlaps under unique. A test endpoint evaluates input rows before publish. A business rule task evaluates the current version when it is reached, writes the result to the named variable and records the matched rules in history.

### Messages and timers (FR-008, FR-009)
- Messages correlate by name and key within the tenant, are idempotent per message ID, and wait in `message_buffer` for their time-to-live (default 1 hour) when nothing is waiting. They come only from product events (spec 016 triggers), connectors (spec 017) and a signed webhook endpoint per tenant (`POST /api/v1/workflow/messages`, HMAC with a tenant key and a 5 minute replay window).
- Timers take ISO 8601 dates, durations and cycles (cycles at least 1 hour apart), and working-day durations through `IWorkingCalendar`. A missed cycle fires once. Jobs of a suspended tenant wait and run after reactivation.

### Errors, escalation and incidents (FR-010, FR-011, FR-013)
Business errors from connector actions map to BPMN error codes and follow a matching error boundary event; technical failures retry with backoff and then raise an incident, as service tasks already do for routes. Escalation events throw and catch within scopes. Operators (`process.operate`) search instances in their scope, read variables (Restricted ones masked), and retry, skip, cancel or change variables on an incident with a reason; each is audited, and a second operator acting on a resolved incident is refused.

### Monitoring (FR-014)
Per process version: active, completed, cancelled and incident counts, per-element active tokens and incidents, and average and 90th percentile durations from history for a chosen period, with the slowest elements listed. Analysts (`process.monitor`) see numbers only; variables and names need `process.operate`. Counts go to spec 021 as a dataset.

### Modeler and pages (ADR-0017)
`@xyflow/react`, `bpmn-moddle` and `dmn-moddle`, `@dagrejs/dagre`. The palette lists only the subset; properties panels in Arabic and English; every action from the keyboard; a text outline lists elements and flows in order. The decision table editor is a design system table. Test run calls the server sandbox (no tasks, no outbound calls, timers fast-forwarded). Pages: Processes (list, modeler, versions, test), Decisions, Operations (instances, incidents) and Monitoring.

### Permissions and package
`process.design` (exists) for models and decisions at the owner's unit, `process.operate` and `process.monitor` (new). Capability `workflow.bpmn` (Workplace Plus and above) for models, DMN, messages, timers, incidents and monitoring; `workflow.max_active_instances` as a quota. Assignment targets are checked against the owner's scope at publish and at run time.

### Tests (R2)
- Unit: BPMN read and write round trip, validator cases, FEEL grammar and limits, every DMN hit policy, token moves for each element group.
- Conformance suite (SC-002): one reference model per supported element, run on every build.
- Integration: publish and versions, starts within the owner's scope, parallel and inclusive joins, sub-processes, call activity, multi-instance with completion condition, loop limit, variable limits, messages (correlation, buffer, message start once, cross-tenant key), timers (boundary interrupting and non-interrupting, event-based gateway, cycle once after an outage), error boundary and incidents, operator actions and their audit, monitoring numbers and analyst masking, package refusal, cross-tenant denial.
- Performance: 200 rule table under 20 ms p95; correlation under 1 second p95; worker kill test loses no job.
- e2e: model, test and publish a process with a decision; resolve an incident; read monitoring; English and Arabic with axe.
