# Tasks: HR System Integration and Payslips

Depends on 002, 003, 010, 016, 017, 022, 026. R2.

## Phase 1: Contract and storage
- [x] T001 Migration for match links, runs and held changes (forced RLS)
- [x] T002 Permissions `hr.integration_manage`; capability keys of section 9; audit actions of section 13
- [x] T003 `IHrRecords` contract, fake HR system with fixtures

## Phase 2: User Story 1, connect and match (P1)
- [x] T004 [P] [US1] Tests: match by email then number; conflicts not matched; manual link kept; HR fields read-only; threshold hold
- [x] T005 [US1] HR REST and SFTP packs, sync use case, results page

## Phase 3: User Stories 2 to 4, lifecycle (P1)
- [x] T006 [P] [US2] [US3] [US4] Tests: held hires and lead time; cancelled hire; transfer applies together; unmapped code holds one change; leaver disabled on time; immediate termination within 15 minutes; rehire
- [x] T007 Lifecycle events and consumers in 002, 003 and 010 (the 022 journey consumers are built with spec 022 R2)
- [x] T008 Org code mapping and proposed units, with their migration (forced RLS)

## Phase 4: User Stories 5 to 7 (P1, P2)
- [ ] T009 [P] [US5] Tests: prefill, read-only server values, classification check, live read when stale
- [ ] T010 [US5] Prefill source for 016 forms
- [ ] T011 [P] [US6] Tests: self only; manager and administrator denied; step-up; watermark; no indexing; kiosk hidden; failure shows nothing stale
- [ ] T012 [US6] My payslips page and streaming endpoint
- [ ] T013 [US7] HR leave balance source for 022

## Final phase
- [ ] T014 Performance test with 20,000 employees; OpenAPI; R2 implementation notes; spec evidence
