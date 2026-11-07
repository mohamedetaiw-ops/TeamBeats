# Tasks: Leave, Attendance and HR Workflows

Depends on 002 (hierarchy), 004 (authorization), 006 (audit and admin shell), 007 (localization). R1 covers User Story 2 only (FR-007); the rest is R2.

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
