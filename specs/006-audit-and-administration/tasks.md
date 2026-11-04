# Tasks: Audit Trail and Admin Shell

Depends on 001 Phase 1 and ADR-0008. `IAuditWriter` should land early because 001 to 005 call it.

## Phase 1: Foundations
- [ ] T001 Migration: `audit_event` (monthly partitions, RLS), `platform_audit_event`, `audit_chain_state`, `audit_retention`, `audit_export_job`; grants INSERT and SELECT only for `tb_app`
- [ ] T002 `IAuditWriter` implementation writing in the caller's transaction; sensitive field exclusion

## Phase 2: User Story 1, record (P1)
- [ ] T003 [P] [US1] Tests: required fields present; rolled-back transaction leaves no row; UPDATE/DELETE as `tb_app` fails
- [ ] T004 [P] [US1] Test: tampering a sealed row is detected at the right position
- [ ] T005 [US1] Sealer worker and integrity check

## Phase 3: User Story 2, search and export (P1)
- [ ] T006 [P] [US2] Tests: tenant-scoped search; org admin subtree predicate; cross-tenant empty; export audited; unauthorized export denied
- [ ] T007 [US2] Search endpoint with keyset pagination and rate limit
- [ ] T008 [US2] Export job, storage, 7-day expiry, download link

## Phase 4: User Story 3, retention (P2)
- [ ] T009 [P] [US3] Tests: retention below 365 rejected; purge removes aged rows and writes a purge record
- [ ] T010 [US3] Retention setting and purge function (legal hold deferred to R2)

## Phase 5: User Story 4, admin shell (P1)
- [ ] T011 [P] [US4] Tests: tenant admin sees all sections; org admin sees scoped sections; employee gets no-access page and API denial
- [ ] T012 [US4] `GET /api/v1/admin/navigation` and Next.js admin layout
- [ ] T013 [US4] Users and groups list and detail; disable membership action
- [ ] T014 Audit search UI (localized actions, user time zone, RTL)

## Final phase
- [ ] T015 Contract test listing every audit event named in specs 001 to 005; OpenAPI; spec evidence
