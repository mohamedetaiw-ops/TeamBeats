# Tasks: Workplace Booking

Depends on 002, 015, 016, 018, 022, 030. R2.

## Phase 1: Module setup
- [ ] T001 `Booking` module skeleton; migration with exclusion constraint (forced RLS); permissions, capability keys, audit actions

## Phase 2: User Stories 1 and 2 (P1)
- [ ] T002 [P] [US1] [US2] Tests: resources and rules, M365 room refusal, search, parallel race, recurring skips, limits
- [ ] T003 [US1] Locations, resources, rules, QR codes
- [ ] T004 [US2] Search, booking, recurring series, .ics

## Phase 3: User Stories 3 and 4 (P1, P2)
- [ ] T005 [P] [US3] [US4] Tests: tentative hold and expiry, approval outcomes, check-in window, auto release, no-show rule
- [ ] T006 [US3] Approval route integration
- [ ] T007 [US4] Check-in and release job

## Phase 4: User Stories 5 and 6 (P2)
- [ ] T008 [US5] On behalf and team booking
- [ ] T009 [US6] Utilization reports

## Final phase
- [ ] T010 Performance tests; OpenAPI; R2 implementation notes; spec evidence
