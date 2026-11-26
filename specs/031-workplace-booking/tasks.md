# Tasks: Workplace Booking

Depends on 002, 015, 016, 018, 022, 030. R2, and R3 below (also 011 scanning and 017).

## Phase 1: Module setup
- [x] T001 `Booking` module skeleton; migration with exclusion constraint (forced RLS); permissions, capability keys, audit actions

## Phase 2: User Stories 1 and 2 (P1)
- [x] T002 [P] [US1] [US2] Tests: resources and rules, M365 room refusal, search, parallel race, recurring skips, limits
- [x] T003 [US1] Locations, resources, rules, QR codes
- [x] T004 [US2] Search, booking, recurring series, .ics

## Phase 3: User Stories 3 and 4 (P1, P2)
- [x] T005 [P] [US3] [US4] Tests: tentative hold and expiry, approval outcomes, check-in window, auto release, no-show rule
- [x] T006 [US3] Approval route integration
- [x] T007 [US4] Check-in and release job

## Phase 4: User Stories 5 and 6 (P2)
- [x] T008 [US5] On behalf and team booking
- [x] T009 [US6] Utilization reports

## Final phase
- [x] T010 Performance tests; OpenAPI; R2 implementation notes; spec evidence

## R3

- [x] T011 [US7] Floor plan images and resource positions, availability map for a time, booking from the map; tests
- [x] T012 [US7] Floor plan page with its list alternative, and the plan editor for administrators; e2e
- [x] T013 [US8] Visits, visitor emails, reception check-in by code, host notice, 30 day purge; tests
- [ ] T014 [US8] Visitors page for hosts and Reception page; e2e
- [ ] T015 [US9] Occupancy connector pack and contract, sensor mapping, readings job, sensor check-in, sensed hours in reports; tests
- [ ] T016 OpenAPI, R3 implementation notes, spec evidence
