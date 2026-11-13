# Tasks: Self Services, Service Catalogue and Workflows

Depends on 003 (user lookup, groups), 004, 005, 006, 007, 008 (shell badge slot), 022 (working days), 024 (engine core); 010 (reporting line), 012 (search), 015 (notifications). R1 covers User Stories 1 to 4 and the R1 parts of 7 and 8.

## Phase 1: Foundations
- [x] T001 `Services` and `Services.Contracts` projects; migration for categories, services, versions, requests, events, attachments, favourites, recent use and delegations (forced RLS)
- [x] T002 Permissions `service.manage`, `service.request_on_behalf`; audit actions `service.published`, `request.submitted`, `request.cancelled`, `task.approved`, `task.rejected`, `task.returned`, `delegation.changed`; capability `workflow.max_services` quota
- [x] T003 Form schema and validator with unit tests (field types of FR-003, required, comparisons, conditions, prefill sources)

## Phase 2: User Story 1, catalogue (P1)
- [x] T004 [P] [US1] Tests: audience filtering by unit, group and membership type; linked app http(s) only with external cue and SSO flag; contractor gets "not available to you"; favourites and recent; cross-tenant
- [x] T005 [US1] Catalogue endpoints and module service registration contract

## Phase 3: User Story 2, prefilled requests (P1)
- [x] T006 [P] [US2] Tests: prefill read-only for directory fields; policy note per locale; validation blocks submit; attachment stored under the tenant path and blocked until clean; My requests with status, step, assignee and Tracking; idempotent submit
- [x] T007 [US2] Request endpoints and lifecycle; `IMalwareScanner` port with ClamAV adapter and a fake for tests
- [x] T008 [US2] Attachments upload and download

## Phase 4: User Stories 3 and 4, inbox and delegation (P1)
- [x] T009 [P] [US3] Tests: one inbox across sources with filters; approve, reject with reason, return; another user cannot act; badge count
- [x] T010 [US3] One approval step on the engine core; task endpoints and count; request update on completion
- [x] T011 [P] [US4] Tests: delegate receives "on behalf of"; requester as delegate skips; tasks return after the window
- [x] T012 [US4] Delegation endpoints and `IDelegations` contract

## Phase 5: User Story 7, builder (P1, R1 basic)
- [x] T013 [P] [US7] Tests: create, preview and publish; form version kept by submitted requests; scoped owner cannot reach beyond their unit
- [x] T014 [US7] Builder endpoints

## Phase 6: Experience
- [x] T015 Services page, request form, My requests, Details and Tracking, task inbox page, header badge, builder pages, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T016 Line manager rule and Team requests once the reporting line exists (planned spec 010)
- [x] T017 OpenAPI; R1 implementation notes (reporting line, scanner); spec evidence

## R2

### Slice 1: step graph routes on the process engine
- [x] T018 Engine additions (spec 024): group and queue candidates, product actions (notify, set, start service), end results, reminders and escalation on user tasks; unit and integration tests
- [x] T019 [US5] [US8] Step graph route model, conversion of R1 routes, compiler to BPMN, validation through `IProcessEngine`, publish per service version; requests run on graph routes and close on the end result
- [x] T020 [P] Tests: sequential and parallel steps with conditions, skipped steps on Tracking, open requests finish on their version, auto reject and return paths, cross-tenant and scope

### Slice 2: triggers
- [x] T021 [US8] Schedule and product event triggers, system requester, idempotent per event; start service steps start the other service's request (chain depth at most 5); requester leaves cancels open requests unless "continue after exit"; tests

### Slice 3: service levels and auto-approve
- [x] T022 [US5] [US8] Service level targets, at-risk and breach alerts and analytics, auto-approve rules; tests

### Slice 4: fulfilment
- [ ] T023 [US6] Queue tasks (pick, complete, reject with reason), hand-off failure alert to the queue and owners; tests

### Slice 5: designer and test mode
- [ ] T024 [US7] [US8] Route designer on the React Flow canvas, test mode as a sample employee, problems on steps, triggers, service level and auto-approve settings, in English and Arabic; e2e with axe

### Slice 6: templates and AI
- [ ] T025 Starter template pack (section 12), switched on per template as drafts
- [ ] T026 Request summary for approvers through the AI gateway; agent-requested tasks marked in the inbox (spec 027 US4)

### R2 final
- [ ] T027 OpenAPI; R2 implementation notes; spec evidence
