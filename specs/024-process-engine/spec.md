# Feature Specification: Process Engine (BPMN and DMN)

| Field | Value |
|---|---|
| Spec ID | 024-process-engine |
| Area | workflows |
| Status | Proposed |
| Version | 0.1 |
| Owner | Mohamed |
| Approver | Mohamed |
| Target release | R1 (engine core for approval routes), R2 (modeler, BPMN subset, DMN, messages, timers, incidents, monitoring), R3 (compensation, instance migration, BPMN import from other tools, external engine adapter) |
| Last updated | 2026-11-05 |
| Requirement IDs | WF-002, WF-003, BPM-001 to BPM-016 (new) |
| Use cases | UC-08 |
| Depends on | ADR-0010 (workflow engine), 004 (authorization), 005 (entitlements), 006 (audit), 007 (localization), 015 (notifications), 016 (services and task inbox), 017 (connectors), 021 (analytics) |

## 1. Problem and outcome

**Problem:** Simple approval routes cover most employee services, but finance, HR, procurement and government customers also run long processes with decision rules, waits for other systems, deadlines, error paths and undo steps. Those customers know BPMN and DMN, often from Camunda, and expect to model, run, watch and change processes themselves. Running a separate BPMN product next to TeamBeats breaks tenant isolation, permissions, audit and the single task inbox, and adds licence and hosting cost on every install.

**Outcome:** TeamBeats has its own process engine inside the modular monolith. Process owners draw BPMN 2.0 diagrams and DMN decision tables in the browser, test them, and publish them. The engine runs them durably per tenant, puts every human step in the TeamBeats task inbox, talks to other systems only through connectors and allowlisted webhooks, and gives operators a live view of instances, incidents and bottlenecks. Customers that already run Camunda can connect it instead of replacing it.

## 2. Scope

**In scope:**
- Process modeler in the browser for the BPMN 2.0 subset in section 12, with properties panels for TeamBeats settings (assignment, forms, retries, timers, connectors).
- Model validation, test run, publish, versioning, .bpmn import and export.
- Execution of the subset: tokens, gateways, sub-processes, call activities, multi-instance, variables and expressions.
- DMN 1.x decision tables with a FEEL subset, versioned on their own and called from business rule tasks or from service rules in spec 016.
- Timers with ISO 8601 dates, durations and cycles, and working-day durations based on the tenant calendar.
- Message correlation from connectors, signed webhooks and product events.
- Error, escalation and compensation handling.
- Jobs, retries, incidents and operator actions.
- Monitoring: instance search, diagram heatmap, step durations, incidents, history.
- Instance migration between process versions.
- Engine API used by spec 016 (simple route designer compiles to BPMN), by module services such as leave (spec 022), and by the AI agents of spec 020 through permissioned tools.
- Adapter for an external BPMN engine (Camunda 8 first) that surfaces its user tasks in the TeamBeats inbox.

**Out of scope:**
- Script tasks, inline code or user-supplied expressions beyond the FEEL subset.
- CMMN case management.
- High-throughput straight-through processing (thousands of instances per second). The engine targets employee and back-office processes.
- Robotic process automation.
- Hosting or embedding Camunda, Flowable, Operaton or Elsa (ADR-0010).

## 3. Actors and permissions

| Actor | What they can do | Permission / scope |
|---|---|---|
| Process owner | Model, test, publish and retire processes and decisions in their area | `process.design` on a category or org scope |
| Decision owner | Edit and publish DMN tables without touching the process | `decision.design` on a category or org scope |
| Process operator | Search instances, resolve incidents, retry, skip, cancel, migrate | `process.operate` on a category or org scope |
| Process analyst | View monitoring and history without personal data | `process.analyze` |
| Task assignee | Complete user tasks assigned to them through the task inbox (spec 016) | Assignment rules |
| Tenant administrator | Webhook allowlist, connector access, retention, engine settings | Tenant |
| External system | Send messages that correlate to instances | Connector credentials or signed webhook key |

## 4. User scenarios

### User Story 1: Model and publish a process (Priority: P1, R2)

Mona in procurement draws "Purchase to pay" in the modeler: a user task for the request, a business rule task that picks the approval chain, a parallel approval, a receive task waiting for the supplier invoice, a service task that posts payment through the ERP connector, and an end event.

**Independent test:** model a process with one of each supported element group, validate, test run, publish, and start an instance.

**Acceptance scenarios:**
1. **Given** the modeler, **When** Mona adds an element, **Then** only elements from the supported subset are offered, and each has a properties panel in Arabic and English.
2. **Given** a model with an unreachable element, a parallel split without a matching join, a user task with no assignment, a timer with an invalid expression, or a service task with no connector action, **When** she validates or publishes, **Then** publishing is blocked and each problem is marked on the diagram.
3. **Given** a valid model, **When** she publishes, **Then** a new immutable version is created, new instances use it, and running instances stay on their version.
4. **Given** a draft model, **When** she runs a test with sample variables and a sample requester, **Then** the engine walks the model in a sandbox: user tasks show who they would be assigned to, decisions show the matched rules, timers can be fast-forwarded, connectors and webhooks are mocked, nothing leaves the product and no real tasks are created.
5. **Given** a process owner scoped to Acme Gulf, **When** she publishes, **Then** the process can only be started for requesters in Acme Gulf and its tasks can only be assigned to people the owner may target.

### User Story 2: Decide with DMN tables (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** a decision table "Approval chain" with inputs amount and category and hit policy "first", **When** a business rule task evaluates it, **Then** the output is written to the named process variable and Tracking shows the matched rule number.
2. **Given** hit policy "collect" with a sum aggregation, **When** several rules match, **Then** the outputs are aggregated as defined.
3. **Given** a decision owner, **When** they publish a new version of the table, **Then** instances that have not yet reached the business rule task use the new version; instances that already evaluated it keep their result.
4. **Given** a table with overlapping rules under hit policy "unique", **When** it is validated, **Then** the overlap is reported and publishing is blocked.
5. **Given** a table, **When** the owner tests it with a set of input rows, **Then** the result for each row is shown before publishing.

### User Story 3: Wait for other systems and for time (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** an instance at receive task "InvoiceReceived" with correlation key = PO number, **When** the ERP connector or a signed webhook sends that message with the same key, **Then** exactly that instance continues and the message payload is mapped to variables.
2. **Given** a message with no waiting instance, **When** it arrives, **Then** it is buffered for its time-to-live (default 1 hour), delivered if a matching instance reaches the wait in that window, and otherwise logged as unmatched.
3. **Given** a message start event, **When** a matching message arrives, **Then** a new instance starts, at most once per message ID.
4. **Given** a non-interrupting timer boundary event "every 2 working days" on a user task, **When** it fires, **Then** a reminder path runs and the user task stays open. An interrupting timer of 5 working days moves the instance to the escalation path and cancels the user task.
5. **Given** an event-based gateway waiting for either "Supplier confirmed" or a 10 day timer, **When** one occurs, **Then** the other is cancelled.

### User Story 4: Handle errors and undo work (Priority: P2, R2 errors, R3 compensation)

**Acceptance scenarios:**
1. **Given** a connector action that returns a business error mapped to BPMN error "BudgetExceeded", **When** an error boundary event catches it, **Then** the instance follows the error path and no incident is raised.
2. **Given** a technical failure (timeout, 5xx), **When** retries with backoff are used up, **Then** an incident is raised on that step with the error, and the instance waits there.
3. **Given** completed steps "Reserve budget" and "Create PO" with compensation handlers "Release budget" and "Cancel PO", **When** a compensation end event is reached, **Then** the handlers run in reverse order of completion and each appears in Tracking.
4. **Given** an escalation event thrown inside a sub-process, **When** a non-interrupting escalation boundary catches it, **Then** the escalation path runs while the sub-process continues.

### User Story 5: Operate running processes (Priority: P1, R2)

**Acceptance scenarios:**
1. **Given** the operator view, **When** an operator searches by process, version, state, business key, requester, org unit or date, **Then** matching instances are listed within their scope.
2. **Given** an instance with an incident, **When** the operator edits a variable and retries, **Then** the step runs again; when they skip with a reason, the instance moves on; when they cancel with a reason, the instance ends. Each action is audited.
3. **Given** a process diagram, **When** the operator opens monitoring, **Then** each element shows active instances, incidents and average and 90th percentile duration for the chosen period, and the slowest steps are listed.
4. **Given** a process analyst, **When** they open monitoring, **Then** they see counts and durations but no variables or names unless they also hold `process.operate`.
5. **Given** two operators acting on the same incident, **When** the second submits, **Then** it is refused because the incident is already resolved.

### User Story 6: Move running instances to a new version (Priority: P2, R3)

**Acceptance scenarios:**
1. **Given** version 2 of a process, **When** an operator creates a migration plan, **Then** the product proposes mappings for elements with the same ID and asks for the rest.
2. **Given** a plan, **When** the operator runs a dry run on selected instances, **Then** each instance is reported as migratable or not, with the reason.
3. **Given** an executed plan, **Then** migrated instances keep variables, history and open tasks (re-linked to the new element), and the run is audited with counts.
4. **Given** an instance inside a multi-instance or a sub-process whose structure changed, **When** no safe mapping exists, **Then** that instance is not migrated.

### User Story 7: Import a process from another tool (Priority: P2, R3)

**Acceptance scenarios:**
1. **Given** a .bpmn file exported from Camunda Modeler or another tool, **When** it is imported, **Then** supported elements are kept, vendor extensions that map to TeamBeats properties (assignee, candidate groups, due dates, retries) are converted, and anything unsupported is listed; the import creates a draft only if the list is empty.
2. **Given** any published process, **When** it is exported, **Then** the .bpmn and .dmn files open in standard BPMN tools, with TeamBeats settings in a `teambeats` extension namespace.

### User Story 8: Work with an existing Camunda (Priority: P3, R3)

**Acceptance scenarios:**
1. **Given** a tenant with Camunda 8 connected through the connector framework, **When** Camunda creates a user task for a TeamBeats user, **Then** it appears in that user's task inbox with a link to its form.
2. **Given** the user completes it in TeamBeats, **Then** the completion and variables are sent to Camunda, and a failure is shown on the task and retried.
3. TeamBeats stores only the task reference and its own audit; process state remains in Camunda.

### Edge cases
- Infinite loops: a token passing the same element more than 1,000 times in one instance raises an incident.
- Call activity depth over 10, or a call to the process itself without a stop condition: blocked at validation where detectable, incident at run time.
- Timer due during an outage fires once when workers return; missed cycle timers fire once, not once per missed cycle.
- Message arrives twice: idempotent per message ID.
- Variable too large (over 256 KB per variable or 2 MB per instance): the step fails with a clear incident; large content goes in attachments.
- Assignee disabled by directory sync before acting: the task goes to the element's fallback assignee, or raises an incident if none is set.
- A user task's form changes between versions: the task keeps the form version of its process version.
- Tenant suspended: timers and jobs pause and resume when the tenant is reactivated; nothing is lost.

## 5. Functional requirements

- **FR-001**: The engine MUST store process definitions as BPMN 2.0 XML and decisions as DMN 1.x XML, limited to the supported subset in section 12, with TeamBeats properties in a `teambeats` extension namespace. *(BPM-001)*
- **FR-002**: The modeler MUST offer only supported elements, validate models (reachability, matched gateways, assignments, timer and FEEL syntax, connector bindings) and block publishing on any error. *(BPM-002)*
- **FR-003**: Published processes and decisions MUST be immutable versions. Instances MUST run on the version they started with unless migrated. *(BPM-003)*
- **FR-004**: The engine MUST execute the subset with BPMN token semantics, including inclusive gateway joins, event sub-processes, call activities and sequential and parallel multi-instance with completion conditions. *(BPM-004)*
- **FR-005**: Expressions in conditions, assignments, timers and mappings MUST use a FEEL subset evaluated in a sandbox with no access to the network, file system or product internals, and a time and size limit. Script tasks MUST NOT exist. *(BPM-005)*
- **FR-006**: User tasks MUST be assigned by assignee expression, candidate users, candidate groups, org unit role, line manager or manager chain level (spec 010), and MUST appear in the task inbox of spec 016 with delegation applied. *(BPM-006, WF-002)*
- **FR-007**: The engine MUST evaluate DMN decision tables with hit policies unique, first, any, priority, collect (with sum, min, max, count), rule order and output order, and support decision table testing before publish. *(BPM-007)*
- **FR-008**: Timers MUST support ISO 8601 date, duration and cycle, plus working-day durations from the tenant calendar for the instance's org unit. *(BPM-008)*
- **FR-009**: Message correlation MUST match by message name and correlation key, be idempotent per message ID, buffer unmatched messages for a configurable time-to-live, and accept messages only from connectors, signed webhooks and product events. *(BPM-009)*
- **FR-010**: Service tasks MUST call only connector actions (spec 017) or allowlisted webhooks, with input and output mappings. Business errors MUST map to BPMN errors; technical failures MUST retry with backoff and then raise an incident. *(BPM-010)*
- **FR-011**: The engine MUST support error, escalation and compensation events, with compensation handlers run in reverse order of completion. *(BPM-011)*
- **FR-012**: Jobs (service tasks, timers, async continuations) MUST run on the worker processes through the transactional outbox (ADR-0006), at least once, with idempotent handlers so that a duplicate delivery has no second effect. *(BPM-012, WF-003)*
- **FR-013**: Operators MUST be able to search instances, view variables within their permission, and retry, skip, cancel or modify variables on incidents, each with a reason and an audit event. *(BPM-013)*
- **FR-014**: The product MUST provide monitoring per process version: active, completed, cancelled and incident counts, per-element heatmap, average and 90th percentile durations, and export to spec 021. *(BPM-014)*
- **FR-015**: Operators MUST be able to migrate selected running instances between versions using an explicit element mapping with a dry run. *(BPM-015)*
- **FR-016**: The engine MUST expose an internal API (start instance, correlate message, complete task, query instances) used by spec 016, module services and AI agent tools, all through central authorization; and an external engine adapter contract for Camunda 8 and others. *(BPM-016)*

## 6. Business and validation rules
- Business key: every instance has one, unique per process within a tenant (for example the request number from spec 016).
- History retention: instance history is kept as long as the linked request (spec 016, default 3 years after closure), then deleted except for audit events.
- Variable classification: a variable inherits the classification of its process unless set higher. Restricted variables are hidden from analysts and never sent to AI providers outside the policy of the AI Gateway.
- Only one published version of a process is "current" for new starts; older versions can be retired once no instance runs on them.

## 7. Tenant and organization scope
- Definitions, versions, instances, tokens, variables, jobs, timers, messages, incidents and history are tenant-owned with RLS. Workers set the tenant context before each job.
- Cross-tenant denial: start, correlate, query, operate and migrate across tenants all fail, including a message whose correlation key matches an instance in another tenant.
- Org boundary: process owners, operators and analysts act only within their scope; a migration plan cannot touch instances outside the operator's scope.

## 8. Data, ownership and classification

| Entity | System of record | Classification | Retention | Personal data? |
|---|---|---|---|---|
| Process and decision definitions | Product | Internal | Life of tenant, versions kept | No |
| Instance, variables, history | Product | Per process (Internal default) | Per linked request, default 3 years after closure | Yes |
| Jobs, timers, buffered messages | Product | Internal | Until done, then 30 days | Possibly |
| Incidents | Product | Internal | 1 year after resolution | Possibly |
| External engine task reference | External engine | Internal | Until task closed, then 30 days | Yes |

## 9. Package entitlements

| Capability key | Behavior |
|---|---|
| `workflow.engine_core` | All packages: engine running approval routes compiled by spec 016 |
| `workflow.bpmn` | Workplace Plus and above: modeler, full subset except R3 items, DMN, messages, timers, incidents, monitoring |
| `workflow.bpmn_advanced` | Enterprise & Government: compensation, instance migration, BPMN import from other tools, external engine adapter |
| `workflow.max_active_instances` | Quota per package (commercial data) |

## 10. UX, localization and accessibility
- The modeler canvas is mirrored in RTL for labels and panels; diagram flow direction stays as drawn.
- Element names, documentation and task names are stored per locale (Arabic and English in R1 and R2).
- Every modeler action is available from the keyboard (add element, connect, move, edit properties) and the diagram has a text outline view for screen readers listing elements and flows in order.
- Monitoring colours meet WCAG 2.2 AA contrast and are backed by numbers, never colour alone.

## 11. AI behavior
- Later (spec 014 and 020): the assistant can draft a BPMN model or DMN table from a description for the owner to edit, explain why an instance is stuck, and summarize an incident. It never publishes, migrates or resolves incidents by itself; those stay human actions.

## 12. Integrations, events and supported subset

Publishes `ProcessDeployed`, `ProcessInstanceStarted`, `ProcessInstanceCompleted`, `ProcessInstanceCancelled`, `UserTaskCreated`, `UserTaskCompleted`, `IncidentRaised`, `IncidentResolved`, `MessageUnmatched`. Consumes product events listed as message or start triggers in spec 016 (for example `UserProvisioned`).

Supported BPMN 2.0 subset:

| Group | Elements | Release |
|---|---|---|
| Tasks | User task, service task (connector or webhook only), business rule task (DMN), send task, receive task, manual task | R2 (user task R1) |
| Gateways | Exclusive, parallel, inclusive, event-based | R2 (exclusive and parallel R1) |
| Start and end events | None, timer, message start; none, error, terminate, escalation end | R2 (none R1) |
| Intermediate events | Timer catch, message catch and throw, escalation throw | R2 |
| Boundary events | Timer, message, error, escalation (interrupting and non-interrupting where BPMN allows) | R2 (timer R1) |
| Structure | Embedded sub-process, event sub-process, call activity, multi-instance (sequential and parallel) | R2 |
| Compensation | Compensation boundary, compensation throw and end events, compensation handlers | R3 |
| Not supported | Script task, complex gateway, conditional and signal events, link events, transactions and cancel events, ad hoc sub-process, choreography and conversation diagrams | n/a |

DMN: decision tables only (no decision requirement graphs with literal expressions in R2; added in R3 if needed). FEEL subset: comparisons, ranges, lists, `not()`, arithmetic, string and date functions, `date and time()`, `duration()`.

## 13. Audit

| Event | Type | Key fields |
|---|---|---|
| process.published / retired, decision.published / retired | audit | definition, version, actor |
| process.instance_started / cancelled | audit | instance, business key, actor or trigger |
| process.incident_raised / retried / skipped / variables_changed | audit | instance, element, actor, reason |
| process.migration_executed | audit | plan, from and to version, instance count, actor |
| process.message_correlated / unmatched | operational | message name, key hash, instance |

## 14. Security and abuse cases
- Code execution through expressions: FEEL sandbox only, no reflection, no host functions beyond the listed set, time and size limits (FR-005).
- Message spoofing to push an instance forward: messages accepted only from configured connectors, product events, or webhooks with a per-tenant signing key and replay window.
- Data exfiltration through a service task: connector actions and allowlisted webhooks only, with output mappings visible in the model and every call audited.
- Operator abuses variable edits to change an approval outcome: variable edits need a reason, are audited with old and new values (Restricted values masked), and cannot change completed task outcomes.
- Process owner assigns tasks outside their scope to harvest data: assignment targets are checked against the owner's scope at publish and at run time.
- Denial of service through runaway loops or timers: loop limit, cycle timer minimum interval of 1 hour, and per-tenant job rate limits.

## 15. Non-functional requirements
- Design-to scale per installation: 50,000 active instances, 1,000 timers firing per minute, 100 job completions per second.
- A user task created by a step is visible in the inbox within 10 seconds at p95.
- DMN table with 200 rules evaluates in under 20 ms at p95.
- Message correlation completes in under 1 second at p95.
- Worker restart loses no job, timer or message (verified by a kill test).

## 16. Success criteria
- **SC-001**: A process owner trained on BPMN can model, test and publish a 15 element process with one decision table in under half a day, without developer help.
- **SC-002**: A conformance suite of reference models (one per supported element) passes on every build.
- **SC-003**: Operators resolve 90% of incidents from the operator view without engineering support.

## 17. Open questions
1. Which modeler editor to build on (for example bpmn-js and dmn-js). Their licence must pass the CI licence gate; if not, an ADR is needed (ADR-0010).
2. Is Camunda 8 the first external engine for the adapter, or does a design partner use another engine? Default: Camunda 8.

## 18. Traceability

| Requirement ID | Covered by |
|---|---|
| WF-002 | FR-006 |
| WF-003 | FR-012 |
| BPM-001 to BPM-005 | FR-001 to FR-005, US1, US7 |
| BPM-006 | FR-006, US1 |
| BPM-007 | FR-007, US2 |
| BPM-008, BPM-009 | FR-008, FR-009, US3 |
| BPM-010, BPM-011 | FR-010, FR-011, US4 |
| BPM-012 | FR-012 |
| BPM-013, BPM-014 | FR-013, FR-014, US5 |
| BPM-015 | FR-015, US6 |
| BPM-016 | FR-016, US8 |
| UC-08 | US1, US5 |
