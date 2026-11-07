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
