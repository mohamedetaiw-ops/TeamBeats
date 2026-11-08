# Tasks: Self Services, Service Catalogue and Workflows

Depends on 003 (user lookup, groups), 004, 005, 006, 007, 008 (shell badge slot), 022 (working days), 024 (engine core); 010 (reporting line), 012 (search), 015 (notifications). R1 covers User Stories 1 to 4 and the R1 parts of 7 and 8.

## Phase 1: Foundations
- [x] T001 `Services` and `Services.Contracts` projects; migration for categories, services, versions, requests, events, attachments, favourites, recent use and delegations (forced RLS)
- [ ] T002 Permissions `service.manage`, `service.request_on_behalf`; audit actions `service.published`, `request.submitted`, `request.cancelled`, `task.approved`, `task.rejected`, `task.returned`, `delegation.changed`; capability `workflow.max_services` quota
- [x] T003 Form schema and validator with unit tests (field types of FR-003, required, comparisons, conditions, prefill sources)

## Phase 2: User Story 1, catalogue (P1)
- [x] T004 [P] [US1] Tests: audience filtering by unit, group and membership type; linked app http(s) only with external cue and SSO flag; contractor gets "not available to you"; favourites and recent; cross-tenant
- [x] T005 [US1] Catalogue endpoints and module service registration contract

## Phase 3: User Story 2, prefilled requests (P1)
- [x] T006 [P] [US2] Tests: prefill read-only for directory fields; policy note per locale; validation blocks submit; attachment stored under the tenant path and blocked until clean; My requests with status, step, assignee and Tracking; idempotent submit
- [x] T007 [US2] Request endpoints and lifecycle; `IMalwareScanner` port with ClamAV adapter and a fake for tests
- [x] T008 [US2] Attachments upload and download

## Phase 4: User Stories 3 and 4, inbox and delegation (P1)
- [ ] T009 [P] [US3] Tests: one inbox across sources with filters; approve, reject with reason, return; another user cannot act; badge count
- [ ] T010 [US3] One approval step on the engine core; task endpoints and count; request update on completion
- [ ] T011 [P] [US4] Tests: delegate receives "on behalf of"; requester as delegate skips; tasks return after the window
- [ ] T012 [US4] Delegation endpoints and `IDelegations` contract

## Phase 5: User Story 7, builder (P1, R1 basic)
- [ ] T013 [P] [US7] Tests: create, preview and publish; form version kept by submitted requests; scoped owner cannot reach beyond their unit
- [ ] T014 [US7] Builder endpoints

## Phase 6: Experience
- [ ] T015 Services page, request form, My requests, Details and Tracking, task inbox page, header badge, builder pages, in English and Arabic, RTL; e2e with axe

## Final phase
- [ ] T016 Line manager rule and Team requests once the reporting line exists (planned spec 010)
- [ ] T017 OpenAPI; R1 implementation notes (reporting line, scanner); spec evidence
