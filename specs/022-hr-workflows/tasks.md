# Tasks: Leave, Attendance and HR Workflows

Depends on 002 (hierarchy), 004 (authorization), 006 (audit and admin shell), 007 (localization). R1 covers User Story 2 only (FR-007); the rest is R2, planned in the R2 part of plan.md.

## Phase 1: Foundations
- [x] T001 `Hr` and `Hr.Contracts` projects; migration with `working_calendar`, `calendar_holiday`, `calendar_assignment` (forced RLS); module map row confirmed
- [x] T002 Permission `hr.calendar_manage` with built-in role HR officer and tenant administrator grants; audit action `hr.calendar_changed`

## Phase 2: User Story 2, calendars and working days (P1)
- [x] T003 [P] [US2] Unit tests: working-day arithmetic (weekends, holidays, half days, Sunday to Thursday, year end, tenant time zone)
- [x] T004 [P] [US2] Integration tests: tenant default applies with no calendar; nearest assigned ancestor wins; holiday change publishes `WorkingCalendarChanged`; sibling HR officer denied; cross-tenant IDs not found; default calendar cannot be deleted; audit
- [x] T005 [US2] `IWorkingCalendar` with resolved snapshots and cache invalidated by `WorkingCalendarChanged`
- [x] T006 [US2] Calendar, holiday and assignment endpoints with `If-Match`; working-day preview
- [x] T007 [US2] Calendars admin page with holiday table, working week picker, unit assignment and preview, in English and Arabic, RTL; e2e with axe

## Final phase
- [x] T008 OpenAPI; R1 implementation notes (calendar assignment default); spec evidence

# R2 tasks

## Slice 1: Leave (US1 scenarios 1 to 7, FR-001 to FR-005, FR-009)
- [x] T009 Migration for leave types, policies, ledger, requests and request days (forced RLS); capability keys of section 9; permission `hr.manage`; audit actions of section 13
- [x] T010 [P] [US1] Unit tests: day count with half days and holidays, accrual, carry-over, eligibility, ledger balance
- [x] T011 [P] [US1] Integration tests: preview, balance block, document rule, overlap names without sensitive types, replacement accept and decline, approval from the inbox, cancel and shorten restore the balance, own request not approvable, sibling HR officer denied, cross-tenant not found, package denied
- [x] T012 [US1] Leave types and policies admin API and page
- [x] T013 [US1] Requests, approval on the engine with an `ITaskSource`, accrual job, `LeaveApproved` and `LeaveCancelled`, Leave module service in the catalogue
- [x] T014 [US1] Leave page and team availability page, in English and Arabic, RTL; e2e with axe

## Slice 2: HR balances, recount and reduced hours (US1 scenario 8, FR-006, FR-008, US2 scenario 3)
- [x] T015 HR leave balance source and write queue (with spec 019 T013)
- [x] T016 Recount of approved leave on `WorkingCalendarChanged`, with notifications
- [x] T017 Reduced hours periods on calendars

## Slice 3: Journeys (US4, US5, FR-013 to FR-018)
- [x] T018 Journey templates, starts from spec 019 and spec 003 events and approved resignations, rescheduling
- [x] T019 Attestation steps, access removal checklist with escalation, exit interview, journeys dashboard, pre-boarding setting

## Slice 4: Attendance (US3, FR-010 to FR-012)
- [ ] T020 Sign in and out, clock data, working hours rules, exceptions, corrections, compensation days, monthly summary and export

## Slice 5: Letters and data change (US6, US7, FR-019 to FR-022)
- [ ] T021 Letter templates, issued PDFs with verification, public verification page
- [ ] T022 Data change requests with documents, HR write-back and all-channel notice

## Slice 6: Expenses (US8, FR-023 to FR-025)
- [ ] T023 Claims, limits, currency, duplicates, routes by amount, finance hand-off, AI receipt suggestions

## Final phase (R2)
- [ ] T024 Performance checks of section 15; OpenAPI; R2 implementation notes; spec evidence
