# Implementation Plan: Self Services, Service Catalogue and Workflows

**Spec:** `specs/016-service-catalogue-and-workflows/spec.md` (v1.0, Approved) | **Date:** 2026-11-07
**Shared base:** ADR-0010, ADR-0013, ADR-0014, spec 024 plan (engine core), module map (`Services`)

## Summary
R1 builds the catalogue with internal form services, linked apps and module service registration (User Story 1), prefilled request forms (User Story 2), one task inbox with a header badge (User Story 3), delegation (User Story 4), and the basic no-code service builder with approval-only routes (User Story 7 R1 part, User Story 8 "approval-only routes in R1"). Multi-step routes, conditions, escalation, automatic steps, triggers, connector fulfilment and the template pack are R2.

R1 slices, in order:
1. **Catalogue:** categories, services (form, linked, module), audiences, favourites, recently used, catalogue contract for module services.
2. **Forms and requests:** form schema and versions, prefill, validation, attachments, request lifecycle, My requests, Details and Tracking.
3. **Inbox and approval:** one approval step on the spec 024 engine core, task inbox API and badge, approve, reject with reason, return for changes, delegation.
4. **Builder and pages:** service builder (names in each locale, category, audience, form, one approval step, fulfilment queue, preview), Services page, request pages, inbox page, in English and Arabic, RTL.

## Constitution check
| Principle | Pass | Evidence |
|---|---|---|
| I, II tenant isolation and org scope | Yes | All tables tenant-owned with forced RLS; a service owner scoped to a unit cannot publish an audience beyond it (US7 scenario 3); attachments under `tenants/{tenant_id}/services/` |
| III authorization | Yes | `service.manage`, `service.request_on_behalf`, `workflow.approve` through `IAuthorizationService`; acting on a task needs being the candidate or delegate plus `workflow.approve` (US3 scenario 3) |
| XIV modules | Yes | `Services` owns catalogue and requests; `Workflow` owns engine state; module services (spec 022 and others) register through `Services.Contracts` |
| XXI entitlements | Yes | `workflow.catalogue` for all; `workflow.max_services` as a quota; R2 keys refused until built |
| Rule 8 | Yes | Service names, field labels, options and policy notes stored per locale |

## Reporting line (dependency on planned spec 010)
Team requests and line manager approval need the reporting line (section 6: from the directory `manager` attribute or HR, never org unit membership). Directory sync (spec 003) does not read `manager` yet. The People plan (planned spec 010) adds it. Until it exists, R1 builds the approver rules "named users" and "role at the requester's unit" and the line manager rule ships with spec 010; Team requests ships with it too. Listed in the R1 implementation notes.

## Data model (schema `services`)
- `category`: names per locale, order.
- `service`: `kind` (form, linked, module), names and descriptions per locale, synonyms per locale, `category_id`, `audience jsonb` (tenant, org unit with subtree, group, membership type), `owner_org_unit_id`, `classification`, `url` (linked, http(s) only, FR-002), `sso bool`, `current_version`, `status`.
- `service_version`: `form jsonb` (FR-003 field types, rules, prefill sources), `route jsonb` (R1: none or one approval step with approver rule), `fulfilment_queue_group_id`, `published_at`.
- `request`: `number` (business key), `service_id`, `service_version`, `requester_user_id`, `on_behalf_of_user_id`, `status`, `data jsonb`, `classification`, `process_instance_id`, `idempotency_key`, timestamps.
- `request_event`: the Tracking timeline.
- `attachment`: object key, name, size, content type, scan state.
- `favourite`, `recent_use`, `delegation` (`user_id`, `delegate_user_id`, `from`, `to`).

## Forms
The form is a JSON schema of fields with labels per locale, validated on save (field types of FR-003, rules limited to required, comparisons with constants and today plus or minus days, and conditions on other fields). No expressions beyond that fixed set. The same validator runs on submit. Prefill sources: profile fields from `Identity` user lookup (name, email, job title, department) and the unit names from `Tenancy`; directory-owned fields are read-only (FR-004). More profile fields arrive with spec 010.

## Attachments and malware scanning (spec gap)
US2 scenario 4 asks for malware scanning; the platform has no scanner. Default for R1: attachments are stored with `scan_state = pending` behind an `IMalwareScanner` port, are not downloadable until clean, and the first adapter is ClamAV (GPL, run as a separate container, not linked), added to the dev stack and staging. ClamAV is new infrastructure, so it is listed for approval; the alternative is to defer attachments to R2.

## Inbox and delegation
- `GET /api/v1/tasks` merges engine user tasks where the viewer is a candidate or active delegate, with type, requester, age and due date; `GET /api/v1/tasks/count` feeds the header badge (spec 008 shell slot), cached per user for 30 seconds and invalidated by `UserTaskCreated` and `UserTaskCompleted`.
- Actions `POST /api/v1/tasks/{id}/approve | reject {reason} | return {comment}` call `IProcessEngine.CompleteTaskAsync`; `Services` updates the request on `ProcessInstanceCompleted`.
- Delegation contract `IDelegations.ActiveDelegateAsync(userId, at)` in `Services.Contracts`, used by the engine; nobody approves their own request, directly or as delegate (US4 scenario 2).
- Module services (FR-010) register `ServiceRegistration(id, names, audience, launchRoute, taskSource)` at startup; their tasks come through the same engine or a task source contract.

## API (v1)
- Catalogue: `GET /api/v1/services?q=&category=`, `GET /api/v1/services/{id}`, `PUT/DELETE /api/v1/services/{id}/favourite`.
- Admin: `GET/POST /api/v1/services/admin`, `PUT /api/v1/services/admin/{id}/draft`, `POST .../publish`, categories endpoints.
- Requests: `POST /api/v1/requests` (idempotency key), `GET /api/v1/requests?mine=true`, `GET /api/v1/requests/{id}`, `POST /api/v1/requests/{id}/cancel`, `POST /api/v1/requests/{id}/resubmit`, attachments upload and download.
- Tasks and delegation as above; `GET/PUT/DELETE /api/v1/me/delegation`.

## Events
`RequestSubmitted`, `RequestCompleted`, `TaskCompleted` with IDs only. Search (planned spec 012) indexes services from `ServicePublished`.

## Tests
- Integration per user story: US1 scenarios 1, 2 and 4 (3 with search), US2 scenarios 1 to 5, US3 scenarios 1 to 3 (4 with spec 010), US4 scenarios 1 to 3, US7 scenarios 1 to 3.
- Boundaries: request, task, queue and attachment of another tenant not found; Acme Gulf service invisible and unusable in Acme Africa; a user who can see a request cannot approve another person's task.
- Entitlement: `workflow.max_services` quota error shape; R2 route features refused.
- Idempotent submit; attachment not downloadable before a clean scan.
- Acceptance (e2e): find a service, submit a prefilled request, approve from the inbox with the badge, set a delegate, in English and Arabic with axe.

## R2 plan

R2 completes User Stories 5, 6, 7 (full) and 8, FR-006, FR-011 and FR-013 to FR-016, the starter templates of section 12 and the R2 AI items of section 11. It builds on the spec 024 R2 engine (graph instances, the executor, the sandbox and the React Flow canvas) instead of a second engine.

### Route model
- A service version's route becomes a **step graph** (`route.graph`): steps of the US8 types (approval, task, set field, notify, connector action, webhook, wait, condition, parallel split and join, start another service, end) and the flows between them, with names per locale. The R1 shape (one approval and connector actions) stays readable and is converted to a graph when a draft is next saved.
- `Services` compiles the graph to BPMN 2.0 with the `teambeats` extension namespace and hands it to `Workflow` through `IProcessEngine` (new `PublishGraphAsync(key, bpmn)`, `ValidateGraphAsync`, `TestGraphAsync`), under the definition key `service:<serviceId>`. `Services` never reads engine tables (CLAUDE.md rule 5). Each published service version publishes the next engine version; open requests finish on theirs (FR-009, US5 scenario 3).
- Step types map to the spec 024 subset: approval and task are user tasks (an approval is followed by an outcome gateway; reject and return end the route with that result); connector and webhook are service tasks; wait is a timer or message catch; condition, split and join are exclusive, inclusive and parallel gateways; start another service, notify and set field are service tasks bound to built-in product actions (below); end events carry the request result (`completed`, `approved`, `rejected`).
- Conditions use the fixed rule set of the forms (a form field compared with a constant), compiled to FEEL by `Services`; owners never write expressions (no custom code, US8 scenario 1).

### Engine additions (spec 024, Workflow module)
- **Candidates by group or queue** (`tb:assignment group`), besides users, permission at a unit, line manager and manager level (FR-006).
- **Product actions** for service tasks (`tb:product action="notify|set|start_service"`): run inside the product with no connector, idempotent per token. Notify goes through `INotificationSender` (in-app, email, Teams per spec 015); set writes a variable; start service publishes `ServiceStartRequested`, which `Services` handles.
- **End results**: `tb:result` on end events becomes the instance outcome, so `ProcessInstanceCompleted` tells `Services` how the request ended.
- **Escalation**: a user task's `tb:due` gains `remindAfter` and `escalateAfter` (working days) and an escalation target; the due job reminds the candidates and, after the escalation time, adds the target to the candidates (US5 scenario 2).

### Triggers (FR-014)
- `service_trigger`: manual (default), schedule (cron subset: monthly or quarterly on a day, weekly on weekdays) and product events `UserProvisioned`, `UserDeprovisioned`, `OrganizationMembershipChanged` and `RequestCompleted` of a named service. Event triggers start one request per event ID (`unique (service_id, trigger_event_id)`), for the event's user, filed by the service's system requester; schedule triggers run from the engine's timer start jobs.
- Requester leaves (edge case): on `UserDeprovisioned`, open requests of that requester are cancelled with "requester left", except on services marked `continueAfterExit`.

### Service levels and auto-approve (FR-015)
- `service_version.service_level_days` (working days on the unit calendar) and per-step due times. A request is at risk at 75% of its target and breached after it; both are request events, alert the queue and the service owners through spec 015, and the breach is counted for spec 021 (`request.sla_breached`, unkeyed).
- Auto-approve rules on an approval step: all of a list of field comparisons; a match completes the step as the system with "auto-approved by rule" on Tracking and the audit `request.auto_approved`.

### Fulfilment (US6)
- The fulfilment queue is a task for the queue group: any member picks it (claims it), completes it or rejects it with a reason. A connector step shows its external reference on the request (built in R1 slice of spec 017); a failure alerts the queue and owners and waits for retry, skip or cancel.

### Designer and test mode (US7, US8 scenarios 1, 6 and 7)
- The route designer reuses the React Flow canvas of the process modeler with the service step palette, a properties panel per step, the keyboard actions and the text outline. Publishing is blocked while the compiled model has problems, shown on the steps.
- Test mode runs the compiled draft in the spec 024 sandbox as a sample employee and shows each step's resolved assignee and the branches taken.

### Templates and AI
- The 13 starter templates of section 12 ship as a seed pack the tenant switches on per template; switching one on creates an unpublished draft service the owner edits. Templates that need a module capability (expenses, journeys) say so and are offered only without it, as the spec says.
- Request summary for approvers goes through the AI gateway (spec 013) with the request's own fields only; agent-requested tasks carry `requestedByAgent` and show "requested by an AI agent on behalf of" in the inbox (spec 027 US4).

### R2 entitlements
As section 9 sets: `workflow.advanced` for parallel steps, conditions and escalation; `workflow.connector_fulfilment` for connector and webhook steps; `workflow.automation` for event and schedule triggers, product actions and auto-approve rules. Workplace keeps manual submit with approval and task steps in sequence, so the designer and the templates work there with those step types only.

### R2 tests
- Integration per scenario: US5 1 to 3, US6 1 to 3, US7 2 and 3 on graphs, US8 1 to 7; triggers idempotent per event; requester leaves; cross-tenant and org scope for triggers, queues and services; entitlement refusals.
- e2e: build a multi-step service in the designer, test it, publish, submit, approve, fulfil from the queue, in English and Arabic with axe.
