# Tasks: Employee Directory, Profiles and Org Chart

Depends on 002, 003, 004, 006, 007, 008, 032. All R1 user stories.

- [x] T001 `People` and `People.Contracts` projects; migration for profiles, reporting lines and field policy (forced RLS); module map confirmed
- [x] T002 Permissions `people.view`, `people.hr_view`, `people.manage`; audit actions of section 13; capability keys
- [x] T003 Directory sync reads the extra Graph attributes and publishes them; fake Graph support
- [x] T004 [P] [US2] Tests: manager from directory with history; non-member manager; org chart; My team; cycle keeps the previous line
- [x] T005 [US2] Reporting line consumer, `IReportingLine` contract, org chart and My team endpoints
- [x] T006 [P] [US1] [US3] Tests: search by name, title, department and extension in both languages; visibility in API and search; disabled and cross-tenant not found; own field edit; directory fields read-only; photo checks; policy change within 60 seconds
- [ ] T007 [US1] [US3] Profiles, visibility, search, own profile and photo endpoints
- [x] T008 [US4] Newcomers and the people component manifest
- [x] T009 Directory page, profile page, org chart with list view, My team, personal profile settings, in English and Arabic, RTL; e2e with axe
- [ ] T010 Wire the reporting line into spec 016 line manager approval and Team requests, spec 009 and 025 manager lists, and spec 018 availability visibility
- [ ] T011 Performance tests; OpenAPI; R1 implementation notes; spec evidence
