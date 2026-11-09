# Tasks: Analytics and Insights

Depends on 004, 006, 008, 009, 025, 032; 010 (location breakdown), 015 (reminders). R1 covers the event pipeline, item counts (User Story 1, R1 part), adoption basics and the R1 part of User Story 9.

## Phase 1: Pipeline
- [x] T001 `Analytics` and `Analytics.Contracts` projects; migration for events (monthly partitions), daily and weekly aggregates and the tenant analytics policy (forced RLS)
- [x] T002 Permission `analytics.view`; audit `analytics.policy_changed`
- [x] T003 [P] Tests: per-tenant pseudonymous keys; declared schemas only; nonessential off; retention job
- [x] T004 Event contract, web batch endpoint, aggregation job

## Phase 2: User Story 1, item reach and acknowledgement (P1, R1 counts)
- [x] T005 [P] [US1] Tests: counts and trend; minimum group size merged into "other"; remind this group only reaches non-readers; rights checked through the owning module
- [x] T006 [US1] Item analytics endpoint and the analytics panel on content and policy pages

## Phase 3: Adoption and experience quality
- [ ] T007 Adoption basics endpoint and admin page with table views
- [ ] T008 [US9] Frontend error reports with release, component version and correlation ID; support list

## Final phase
- [ ] T009 OpenAPI; R1 implementation notes (location breakdown); spec evidence
