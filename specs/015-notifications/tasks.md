# Tasks: Notifications and Email

Depends on 004, 005, 006, 007, 008. All R1 user stories.

- [ ] T001 `Notifications` and `Notifications.Contracts` projects; migration (forced RLS); module map confirmed
- [ ] T002 Permission `notifications.manage`; audit actions; capability keys; platform mandatory categories
- [ ] T003 [P] [US1] Tests: in-app within 5 seconds; open marks read; isolation; dedupe; disabled members
- [ ] T004 [US1] Send contract, in-app centre endpoints, header count
- [ ] T005 [P] [US2] Tests: email per locale with sender name; off and mandatory; digest; retry 24 hours; bounce pauses
- [ ] T006 [US2] Email worker, `IEmailSender` with SMTP adapter and fake, templates, digest job
- [ ] T007 [US3] Tenant settings and relay test
- [ ] T008 Bell, centre panel and page, preferences page, dev inbox page, in English and Arabic, RTL; e2e with axe
- [ ] T009 Wire existing "notify" points (critical announcements, approvals, security alerts) once their modules exist; burst performance test; OpenAPI; spec evidence
